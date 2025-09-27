// ASP.NET Core entry point and HTTP endpoints for PPTX extraction and redaction.
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using System.Text.Json.Serialization.Metadata;
using System.ComponentModel;
using Microsoft.Extensions.Configuration;


// ----------------------------- Web app --------------------------------------

public static partial class Program
{
    private static readonly HttpClient SharedHttpClient = new();

    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
            p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

        var storageRoot = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "storage"));

        var app = builder.Build();
        app.UseCors();

        // POST /api/upload: accept multipart "file" (optional "instructions"), persist artifacts, return deck + PDF
        app.MapPost("/api/upload", async (HttpRequest req) =>
        {
            if (!req.HasFormContentType)
                return Results.BadRequest(new { error = "multipart/form-data required" });

            var form = await req.ReadFormAsync();
            var file = form.Files.GetFile("file");
            if (file is null || file.Length == 0)
                return Results.BadRequest(new { error = "file missing" });

            var instructions = form.TryGetValue("instructions", out var instructionValues)
                ? instructionValues.ToString()
                : null;
            instructions = string.IsNullOrWhiteSpace(instructions) ? null : instructions!.Trim();

            try
            {
                await using var ms = new MemoryStream();
                await file.CopyToAsync(ms);
                ms.Position = 0;

                var deck = ExtractDeck(ms, file.FileName);

                var resolver = new DefaultJsonTypeInfoResolver();
                resolver.Modifiers.Add(info =>
                {
                    if (info.Type == typeof(ElementDto))
                    {
                        info.PolymorphismOptions = new JsonPolymorphismOptions
                        {
                            TypeDiscriminatorPropertyName = "type",
                            IgnoreUnrecognizedTypeDiscriminators = true
                        };
                        info.PolymorphismOptions.DerivedTypes.Add(new JsonDerivedType(typeof(TextboxDto), "textbox"));
                        info.PolymorphismOptions.DerivedTypes.Add(new JsonDerivedType(typeof(PictureDto), "picture"));
                        info.PolymorphismOptions.DerivedTypes.Add(new JsonDerivedType(typeof(TableDto), "table"));
                    }
                });

                var jsonOptions = new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = true,
                    TypeInfoResolver = resolver
                };

                var artifacts = await PersistArtifactsAsync(ms, deck, jsonOptions, file.FileName, storageRoot);

                if (!System.IO.File.Exists(artifacts.PdfPath))
                    return Results.StatusCode(500);

                var pdfBytes = await System.IO.File.ReadAllBytesAsync(artifacts.PdfPath);
                var pdfInfo = new
                {
                    fileName = Path.GetFileName(artifacts.PdfPath),
                    base64 = Convert.ToBase64String(pdfBytes)
                };

                var supabaseSettings = ReadSupabaseSettings(app.Configuration);
                if (supabaseSettings != null)
                {
                    try
                    {
                        await PersistDeckToSupabaseAsync(
                            deck,
                            instructions,
                            artifacts,
                            supabaseSettings,
                            storageRoot,
                            req.HttpContext.RequestAborted);
                    }
                    catch (Exception supabaseEx)
                    {
                        Console.Error.WriteLine($"Supabase persistence failed: {supabaseEx.Message}");
                    }
                }

                if (string.IsNullOrWhiteSpace(instructions))
                {
                    var result = new
                    {
                        deck,
                        pdf = pdfInfo
                    };

                    return Results.Text(JsonSerializer.Serialize(result, jsonOptions), "application/json");
                }

                var semanticUrl = app.Configuration["SEMANTIC_URL"]
                    ?? Environment.GetEnvironmentVariable("SEMANTIC_URL")
                    ?? "http://localhost:8000";

                var annotateUrl = semanticUrl.TrimEnd('/') + "/annotate";

                using var client = new HttpClient();

                var payload = new
                {
                    instructions
                };

                var payloadJson = JsonSerializer.Serialize(payload, jsonOptions);
                using var content = new StringContent(payloadJson, Encoding.UTF8, "application/json");

                try
                {
                    var response = await client.PostAsync(annotateUrl, content);
                    var responseBody = await response.Content.ReadAsStringAsync();

                    if (response.IsSuccessStatusCode)
                    {
                        var ruleResponse = JsonSerializer.Deserialize<AnnotateResponse>(responseBody, jsonOptions);

                        if (ruleResponse?.Rules is null)
                        {
                            return Results.Text(JsonSerializer.Serialize(new
                            {
                                deck,
                                pdf = pdfInfo,
                                redaction = new
                                {
                                    error = "semantic_service_invalid_response",
                                    body = responseBody
                                }
                            }, jsonOptions), "application/json");
                        }

                        ruleResponse.Rules.Keywords ??= new List<string>();

                        var redactions = ApplyRedactions(deck, ruleResponse.Rules);

                        var stats = new
                        {
                            redactionCount = redactions.Sum(r => r.Matches.Count),
                            modifiedSlides = redactions.Select(r => r.Slide).Distinct().Order().ToArray()
                        };

                        object? exportInfo;
                        try
                        {
                            ms.Position = 0;
                            var sanitizedBytes = CreateSanitizedCopy(ms, deck, ruleResponse.Rules);
                            var sanitizedBase64 = Convert.ToBase64String(sanitizedBytes);
                            var sanitizedFile = Path.GetFileNameWithoutExtension(deck.file) + "_redacted.pptx";
                            exportInfo = new
                            {
                                fileName = sanitizedFile,
                                pptxBase64 = sanitizedBase64
                            };
                        }
                        catch (Exception exportEx)
                        {
                            exportInfo = new
                            {
                                error = "sanitized_export_failed",
                                detail = exportEx.Message
                            };
                        }

                        var result = new
                        {
                            instructions,
                            rules = ruleResponse.Rules,
                            deck,
                            redactions,
                            stats,
                            export = exportInfo,
                            pdf = pdfInfo
                        };

                        return Results.Text(JsonSerializer.Serialize(result, jsonOptions), "application/json");
                    }

                    return Results.Text(JsonSerializer.Serialize(new
                    {
                        deck,
                        pdf = pdfInfo,
                        redaction = new
                        {
                            error = "semantic_service_failed",
                            status = (int)response.StatusCode,
                            body = responseBody
                        }
                    }, jsonOptions), "application/json");
                }
                catch (Exception httpEx)
                {
                    return Results.Text(JsonSerializer.Serialize(new
                    {
                        deck,
                        pdf = pdfInfo,
                        redaction = new
                        {
                            error = "semantic_service_unavailable",
                            detail = httpEx.Message
                        }
                    }, jsonOptions), "application/json");
                }
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // POST /api/render: accept multipart "file", return a PDF for inline viewing
        app.MapPost("/api/render", async (HttpRequest req) =>
        {
            if (!req.HasFormContentType)
                return Results.BadRequest(new { error = "multipart/form-data required" });

            var form = await req.ReadFormAsync();
            var file = form.Files.GetFile("file");
            if (file is null || file.Length == 0)
                return Results.BadRequest(new { error = "file missing" });

            var tmpDir = Path.Combine(Path.GetTempPath(), "dexter-preview", Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(tmpDir);
            var inPath = Path.Combine(tmpDir, Path.GetFileName(file.FileName));
            var outPdf = Path.Combine(tmpDir, Path.GetFileNameWithoutExtension(file.FileName) + ".pdf");

            try
            {
                await using (var fs = File.Create(inPath))
                {
                    await file.CopyToAsync(fs);
                }

                await ConvertPptxToPdfAsync(inPath, tmpDir); // produces outPdf

                if (!System.IO.File.Exists(outPdf))
                    return Results.StatusCode(500);

                var pdfBytes = await System.IO.File.ReadAllBytesAsync(outPdf);
                var cd = new System.Net.Mime.ContentDisposition
                {
                    Inline = true,
                    FileName = Path.GetFileName(outPdf)
                };
                var headers = new HeaderDictionary();
                headers["Content-Disposition"] = cd.ToString();

                return Results.File(pdfBytes, "application/pdf", enableRangeProcessing: true);
            }
            catch (Win32Exception)
            {
                return Results.Problem(
                    statusCode: 501,
                    title: "Not Implemented",
                    detail: "PDF rendering requires LibreOffice (soffice) on the server (or set SOFFICE_PATH)."
                );
            }

            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            finally
            {
                // Best-effort cleanup
                try { Directory.Delete(tmpDir, recursive: true); } catch { /* ignore */ }
            }
        });

        await app.RunAsync();
    }

    private static async Task<DeckArtifactPaths> PersistArtifactsAsync(Stream pptxStream, DeckDto deck, JsonSerializerOptions jsonOptions, string originalFileName, string storageRoot)
    {
        var baseName = SanitizeBaseName(originalFileName);
        Directory.CreateDirectory(storageRoot);

        var targetDir = Path.Combine(storageRoot, baseName);
        Directory.CreateDirectory(targetDir);

        var pptxPath = Path.Combine(targetDir, $"{baseName}.pptx");
        pptxStream.Position = 0;
        await using (var fileStream = File.Create(pptxPath))
        {
            await pptxStream.CopyToAsync(fileStream);
        }

        var jsonPath = Path.Combine(targetDir, $"{baseName}.json");
        var deckJson = JsonSerializer.Serialize(deck, jsonOptions);
        await File.WriteAllTextAsync(jsonPath, deckJson);

        await ConvertPptxToPdfAsync(pptxPath, targetDir);

        var expectedPdf = Path.Combine(targetDir, $"{baseName}.pdf");
        if (!File.Exists(expectedPdf))
        {
            var generated = Directory.GetFiles(targetDir, "*.pdf").FirstOrDefault();
            if (generated != null)
            {
                if (File.Exists(expectedPdf))
                {
                    File.Delete(expectedPdf);
                }
                File.Move(generated, expectedPdf);
            }
        }

        pptxStream.Position = 0;

        return new DeckArtifactPaths(
            BaseName: baseName,
            PptxPath: pptxPath,
            JsonPath: jsonPath,
            PdfPath: expectedPdf);
    }

    private static SupabaseSettings? ReadSupabaseSettings(IConfiguration configuration)
    {
        var url = configuration["SUPABASE_URL"] ?? Environment.GetEnvironmentVariable("SUPABASE_URL");
        var serviceKey = configuration["SUPABASE_SERVICE_ROLE_KEY"] ?? Environment.GetEnvironmentVariable("SUPABASE_SERVICE_ROLE_KEY");

        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(serviceKey))
            return null;

        var decksTable = configuration["SUPABASE_DECKS_TABLE"] ?? Environment.GetEnvironmentVariable("SUPABASE_DECKS_TABLE") ?? "decks";
        var slidesTable = configuration["SUPABASE_SLIDES_TABLE"] ?? Environment.GetEnvironmentVariable("SUPABASE_SLIDES_TABLE") ?? "slides";
        var embeddingModel = configuration["OPENAI_EMBEDDING_MODEL"] ?? Environment.GetEnvironmentVariable("OPENAI_EMBEDDING_MODEL") ?? "text-embedding-3-small";
        var openAiKey = configuration["OPENAI_API_KEY"] ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");

        return new SupabaseSettings(
            Url: url.TrimEnd('/'),
            ServiceKey: serviceKey,
            DecksTable: decksTable,
            SlidesTable: slidesTable,
            OpenAiKey: openAiKey,
            EmbeddingModel: embeddingModel);
    }

    private static async Task PersistDeckToSupabaseAsync(
        DeckDto deck,
        string? instructions,
        DeckArtifactPaths artifacts,
        SupabaseSettings settings,
        string storageRoot,
        CancellationToken cancellationToken)
    {
        var deckId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        var pptxRelative = ToRelativeStoragePath(storageRoot, artifacts.PptxPath);
        var jsonRelative = ToRelativeStoragePath(storageRoot, artifacts.JsonPath);
        var pdfRelative = ToRelativeStoragePath(storageRoot, artifacts.PdfPath);

        var deckRecord = new
        {
            id = deckId,
            deck_name = artifacts.BaseName,
            original_filename = deck.file,
            pptx_path = pptxRelative,
            json_path = jsonRelative,
            pdf_path = pdfRelative,
            pdf_url = ToPublicFileUrl(pdfRelative),
            instructions,
            slide_count = deck.slideCount,
            created_at = now,
            updated_at = now
        };

        await PostgrestInsertAsync(settings, settings.DecksTable, deckRecord, returnRepresentation: false, cancellationToken);

        var slidePayload = new List<object>();
        foreach (var slide in deck.slides)
        {
            var text = ExtractSlidePlainText(slide);
            if (string.IsNullOrWhiteSpace(text))
                continue;

            float[]? embedding = null;
            if (!string.IsNullOrWhiteSpace(settings.OpenAiKey))
            {
                embedding = await TryGenerateEmbeddingAsync(text, settings.OpenAiKey!, settings.EmbeddingModel, cancellationToken);
            }

            slidePayload.Add(new
            {
                id = Guid.NewGuid(),
                deck_id = deckId,
                slide_no = slide.index,
                content = text,
                embedding,
                created_at = now,
                updated_at = now
            });
        }

        if (slidePayload.Count > 0)
        {
            await PostgrestInsertAsync(settings, settings.SlidesTable, slidePayload, returnRepresentation: false, cancellationToken);
        }
    }

    private static async Task PostgrestInsertAsync(SupabaseSettings settings, string table, object payload, bool returnRepresentation, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{settings.Url}/rest/v1/{table}");
        request.Headers.Add("apikey", settings.ServiceKey);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ServiceKey);
        request.Headers.Add("Prefer", returnRepresentation ? "return=representation" : "return=minimal");

        var json = JsonSerializer.Serialize(payload);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await SharedHttpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new Exception($"Supabase insert into '{table}' failed ({(int)response.StatusCode}): {body}");
        }
    }

    private static string ToRelativeStoragePath(string storageRoot, string fullPath)
    {
        try
        {
            var relative = Path.GetRelativePath(storageRoot, fullPath);
            if (string.IsNullOrWhiteSpace(relative) || relative.StartsWith(".."))
            {
                relative = Path.GetFileName(fullPath);
            }

            return relative.Replace(Path.DirectorySeparatorChar, '/');
        }
        catch
        {
            return fullPath.Replace(Path.DirectorySeparatorChar, '/');
        }
    }

    private static string ToPublicFileUrl(string relativeStoragePath)
    {
        if (string.IsNullOrWhiteSpace(relativeStoragePath))
            return string.Empty;

        return "/files/" + relativeStoragePath.TrimStart('/');
    }

    private static string ExtractSlidePlainText(SlideDto slide)
    {
        var sb = new StringBuilder();

        foreach (var element in slide.elements)
        {
            switch (element)
            {
                case TextboxDto textbox:
                    AppendParagraphs(sb, textbox.paragraphs);
                    break;
                case TableDto table:
                    foreach (var row in table.cells)
                    {
                        if (row is null) continue;
                        foreach (var cell in row)
                        {
                            if (cell is null) continue;
                            AppendParagraphs(sb, cell.paragraphs);
                        }
                    }
                    break;
                case PictureDto picture when !string.IsNullOrWhiteSpace(picture.name):
                    sb.AppendLine(picture.name);
                    break;
            }
        }

        return sb.ToString().Trim();
    }

    private static void AppendParagraphs(StringBuilder sb, IEnumerable<ParagraphDto> paragraphs)
    {
        foreach (var paragraph in paragraphs)
        {
            if (string.IsNullOrWhiteSpace(paragraph.text))
                continue;

            sb.AppendLine(paragraph.text.Trim());
        }
    }

    private static async Task<float[]?> TryGenerateEmbeddingAsync(string input, string apiKey, string model, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        var payload = new
        {
            model,
            input
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/embeddings");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var response = await SharedHttpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // Swallow embedding failures but log for observability.
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            Console.Error.WriteLine($"Embedding request failed ({(int)response.StatusCode}): {body}");
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var embeddingResponse = await JsonSerializer.DeserializeAsync<OpenAiEmbeddingResponse>(stream, cancellationToken: cancellationToken);
        var vector = embeddingResponse?.Data?.FirstOrDefault()?.Embedding;
        return vector?.ToArray();
    }

    private sealed record DeckArtifactPaths(string BaseName, string PptxPath, string JsonPath, string PdfPath);

    private sealed record SupabaseSettings(string Url, string ServiceKey, string DecksTable, string SlidesTable, string? OpenAiKey, string EmbeddingModel);

    private sealed class OpenAiEmbeddingResponse
    {
        [JsonPropertyName("data")]
        public List<OpenAiEmbeddingDatum> Data { get; set; } = new();
    }

    private sealed class OpenAiEmbeddingDatum
    {
        [JsonPropertyName("embedding")]
        public List<float> Embedding { get; set; } = new();
    }

    private static string SanitizeBaseName(string originalFileName)
    {
        var baseName = Path.GetFileNameWithoutExtension(originalFileName);
        if (string.IsNullOrWhiteSpace(baseName))
            return "deck";

        var invalidChars = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(baseName.Length);
        foreach (var ch in baseName)
        {
            if (invalidChars.Contains(ch) || char.IsWhiteSpace(ch))
            {
                builder.Append('_');
            }
            else
            {
                builder.Append(ch);
            }
        }

        var sanitized = builder.ToString().Trim('_');
        return string.IsNullOrWhiteSpace(sanitized) ? "deck" : sanitized;
    }

    // --------------------------- LibreOffice conversion ----------------------

    private static async Task ConvertPptxToPdfAsync(string inputPath, string outDir)
    {
        // Use SOFFICE_PATH env var if you want a fixed path; else rely on PATH
        var soffice = Environment.GetEnvironmentVariable("SOFFICE_PATH") ?? "soffice";

        var profileDir = Path.Combine(Path.GetTempPath(), "libreoffice-profile", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(profileDir);
        var profileUri = new Uri(profileDir.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
            ? profileDir
            : profileDir + Path.DirectorySeparatorChar).AbsoluteUri;

        var psi = new ProcessStartInfo
        {
            FileName = soffice,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        // soffice --headless --convert-to pdf --outdir <outDir> <inputPath>
        psi.ArgumentList.Add("--headless");
        psi.ArgumentList.Add("--nologo");
        psi.ArgumentList.Add("--nofirststartwizard");
        psi.ArgumentList.Add("--norestore");
        psi.ArgumentList.Add($"-env:UserInstallation={profileUri}");
        psi.ArgumentList.Add("--convert-to");
        psi.ArgumentList.Add("pdf");
        psi.ArgumentList.Add("--outdir");
        psi.ArgumentList.Add(outDir);
        psi.ArgumentList.Add(inputPath);

        using var proc = Process.Start(psi)!;
        var stdOut = await proc.StandardOutput.ReadToEndAsync();
        var stdErr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();

        try
        {
            Directory.Delete(profileDir, recursive: true);
        }
        catch
        {
            // best effort cleanup
        }

        if (proc.ExitCode != 0)
        {
            throw new Exception($"LibreOffice conversion failed (code {proc.ExitCode}). stderr: {stdErr}. stdout: {stdOut}");
        }
    }
}
