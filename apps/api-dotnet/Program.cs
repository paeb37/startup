// ASP.NET Core entry point and HTTP endpoints for PPTX extraction and redaction.
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.IO.Compression;
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
using DotNetEnv;

public static partial class Program
{
    private static readonly HttpClient SharedHttpClient = new();

    public static async Task Main(string[] args)
    {
        try
        {
            Env.TraversePath().Load();
        }
        catch (FileNotFoundException)
        {
            // .env is optional; ignore if not found.
        }

        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
            p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

        var app = builder.Build();
        app.UseCors();

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

                var supabaseSettings = ReadSupabaseSettings(app.Configuration);
                if (supabaseSettings is null)
                {
                    return Results.Problem(
                        detail: "Supabase storage is not configured. Set SUPABASE_URL, SUPABASE_SERVICE_ROLE_KEY, and SUPABASE_STORAGE_BUCKET.",
                        statusCode: 500
                    );
                }

                var deckId = Guid.NewGuid();

                var artifacts = await PersistArtifactsAsync(
                    ms,
                    deck,
                    jsonOptions,
                    file.FileName,
                    deckId,
                    supabaseSettings,
                    req.HttpContext.RequestAborted);

                var pdfInfo = new
                {
                    fileName = artifacts.PdfFileName,
                    base64 = Convert.ToBase64String(artifacts.PdfBytes)
                };

                try
                {
                    await PersistDeckToSupabaseAsync(
                        deck,
                        deckId,
                        artifacts,
                        supabaseSettings,
                        req.HttpContext.RequestAborted);
                }
                catch (Exception supabaseEx)
                {
                    Console.Error.WriteLine($"Supabase persistence failed: {supabaseEx.Message}");
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
                    deckId = deckIdForAnnotation,
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
                        var annotation = JsonSerializer.Deserialize<AnnotationResponse>(responseBody, jsonOptions);

                        if (annotation?.Actions is null)
                        {
                            return Results.Text(JsonSerializer.Serialize(new
                            {
                                deck,
                                pdf = pdfInfo,
                                annotation = new
                                {
                                    error = "semantic_service_invalid_response",
                                    body = responseBody
                                }
                            }, jsonOptions), "application/json");
                        }

                        var actions = annotation.Actions;
                        var redactions = ApplyActions(deck, actions, out var actionWarnings);

                        var stats = new
                        {
                            redactionCount = redactions.Sum(r => r.Matches.Count),
                            modifiedSlides = redactions.Select(r => r.Slide).Distinct().Order().ToArray()
                        };

                        var meta = annotation.Meta != null
                            ? new Dictionary<string, object?>(annotation.Meta)
                            : new Dictionary<string, object?>();

                        if (!string.IsNullOrWhiteSpace(annotation.DeckId))
                        {
                            meta["deckId"] = annotation.DeckId;
                        }

                        if (actionWarnings.Count > 0)
                        {
                            meta["pipelineWarnings"] = actionWarnings;
                        }

                        object? exportInfo;
                        try
                        {
                            ms.Position = 0;
                            var sanitizedBytes = CreateSanitizedCopy(ms, deck, actions);
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
                            actions,
                            deck,
                            redactions,
                            stats,
                            export = exportInfo,
                            pdf = pdfInfo,
                            meta
                        };

                        return Results.Text(JsonSerializer.Serialize(result, jsonOptions), "application/json");
                    }

                    return Results.Text(JsonSerializer.Serialize(new
                    {
                        deck,
                        pdf = pdfInfo,
                        annotation = new
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
                        annotation = new
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

                await ConvertPptxToPdfAsync(inPath, tmpDir);

                if (!File.Exists(outPdf))
                    return Results.StatusCode(500);

                var pdfBytes = await File.ReadAllBytesAsync(outPdf);
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
                try { Directory.Delete(tmpDir, recursive: true); } catch { /* ignore */ }
            }
        });

        await app.RunAsync();
    }

    private static async Task<DeckArtifactUploadResult> PersistArtifactsAsync(
        Stream pptxStream,
        DeckDto deck,
        JsonSerializerOptions jsonOptions,
        string originalFileName,
        Guid deckId,
        SupabaseSettings settings,
        CancellationToken cancellationToken)
    {
        var baseName = SanitizeBaseName(originalFileName);
        var tempRoot = Path.Combine(Path.GetTempPath(), "dexter-artifacts", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempRoot);

        var pptxPath = Path.Combine(tempRoot, $"{baseName}.pptx");
        var jsonPath = Path.Combine(tempRoot, $"{baseName}.json");
        var pdfPath = Path.Combine(tempRoot, $"{baseName}.pdf");

        try
        {
            pptxStream.Position = 0;
            await using (var fileStream = File.Create(pptxPath))
            {
                await pptxStream.CopyToAsync(fileStream, cancellationToken);
            }

            await ConvertPptxToPdfAsync(pptxPath, tempRoot);

            if (!File.Exists(pdfPath))
            {
                var generated = Directory.GetFiles(tempRoot, "*.pdf").FirstOrDefault();
                if (generated != null)
                {
                    if (File.Exists(pdfPath))
                    {
                        File.Delete(pdfPath);
                    }
                    File.Move(generated, pdfPath);
                }
            }

            if (!File.Exists(pdfPath))
            {
                throw new InvalidOperationException("PDF conversion failed (no output file).");
            }

            var pdfBytes = await File.ReadAllBytesAsync(pdfPath, cancellationToken);

            var imageCaptions = await GenerateImageCaptionsAsync(
                deck,
                pptxPath,
                deckId,
                settings,
                cancellationToken);

            await GenerateTableSummariesAsync(
                deck,
                settings,
                cancellationToken);

            var deckJson = JsonSerializer.Serialize(deck, jsonOptions);
            await File.WriteAllTextAsync(jsonPath, deckJson, cancellationToken);

            var deckFolder = deckId.ToString("n");
            var objectPrefix = string.IsNullOrWhiteSpace(settings.StoragePathPrefix)
                ? deckFolder
                : $"{settings.StoragePathPrefix.TrimEnd('/')}/{deckFolder}";

            var pptxObjectPath = await UploadToSupabaseStorageAsync(
                pptxPath,
                "application/vnd.openxmlformats-officedocument.presentationml.presentation",
                settings,
                $"{objectPrefix}/{Path.GetFileName(pptxPath)}",
                cancellationToken);

            var jsonObjectPath = await UploadToSupabaseStorageAsync(
                jsonPath,
                "application/json",
                settings,
                $"{objectPrefix}/{Path.GetFileName(jsonPath)}",
                cancellationToken);

            var pdfObjectPath = await UploadToSupabaseStorageAsync(
                pdfPath,
                "application/pdf",
                settings,
                $"{objectPrefix}/{Path.GetFileName(pdfPath)}",
                cancellationToken);

            pptxStream.Position = 0;

            return new DeckArtifactUploadResult(
                BaseName: baseName,
                PptxStoragePath: pptxObjectPath,
                JsonStoragePath: jsonObjectPath,
                PdfStoragePath: pdfObjectPath,
                PdfFileName: Path.GetFileName(pdfPath),
                PdfBytes: pdfBytes,
                ImageCaptions: imageCaptions);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { /* ignore */ }
        }
    }

    private static SupabaseSettings? ReadSupabaseSettings(IConfiguration configuration)
    {
        var url = configuration["SUPABASE_URL"] ?? Environment.GetEnvironmentVariable("SUPABASE_URL");
        var serviceKey = configuration["SUPABASE_SERVICE_ROLE_KEY"] ?? Environment.GetEnvironmentVariable("SUPABASE_SERVICE_ROLE_KEY");
        var storageBucket = configuration["SUPABASE_STORAGE_BUCKET"] ?? Environment.GetEnvironmentVariable("SUPABASE_STORAGE_BUCKET");

        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(serviceKey) || string.IsNullOrWhiteSpace(storageBucket))
            return null;

        var decksTable = configuration["SUPABASE_DECKS_TABLE"] ?? Environment.GetEnvironmentVariable("SUPABASE_DECKS_TABLE") ?? "decks";
        var slidesTable = configuration["SUPABASE_SLIDES_TABLE"] ?? Environment.GetEnvironmentVariable("SUPABASE_SLIDES_TABLE") ?? "slides";
        var embeddingModel = configuration["OPENAI_EMBEDDING_MODEL"] ?? Environment.GetEnvironmentVariable("OPENAI_EMBEDDING_MODEL") ?? "text-embedding-3-small";
        var openAiKey = configuration["OPENAI_API_KEY"] ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        var storagePrefix = configuration["SUPABASE_STORAGE_PREFIX"] ?? Environment.GetEnvironmentVariable("SUPABASE_STORAGE_PREFIX");
        var visionModel = configuration["OPENAI_VISION_MODEL"] ?? Environment.GetEnvironmentVariable("OPENAI_VISION_MODEL") ?? "gpt-4o-mini";

        return new SupabaseSettings(
            Url: url.TrimEnd('/'),
            ServiceKey: serviceKey,
            DecksTable: decksTable,
            SlidesTable: slidesTable,
            OpenAiKey: openAiKey,
            EmbeddingModel: embeddingModel,
            StorageBucket: storageBucket,
            StoragePathPrefix: storagePrefix ?? string.Empty,
            VisionModel: visionModel);
    }

    private static async Task PersistDeckToSupabaseAsync(
        DeckDto deck,
        Guid deckId,
        DeckArtifactUploadResult artifacts,
        SupabaseSettings settings,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;

        var deckRecord = new
        {
            id = deckId,
            deck_name = artifacts.BaseName,
            original_filename = deck.file,
            pptx_path = artifacts.PptxStoragePath,
            json_path = artifacts.JsonStoragePath,
            pdf_path = artifacts.PdfStoragePath,
            slide_count = deck.slideCount,
            created_at = now,
            updated_at = now
        };

        await PostgrestInsertAsync(settings, settings.DecksTable, deckRecord, returnRepresentation: false, cancellationToken);

        var slidePayload = new List<object>();
        foreach (var slide in deck.slides)
        {
            var text = ExtractSlidePlainText(slide);
            var captions = artifacts.ImageCaptions.TryGetValue(slide.index, out var captionList) ? captionList : null;
            var tableSummaries = slide.elements
                .OfType<TableDto>()
                .Select(t => t.summary)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Cast<string>()
                .ToList();

            var combined = CombineSlideContent(text, captions, tableSummaries);

            float[]? embedding = null;
            if (!string.IsNullOrWhiteSpace(settings.OpenAiKey) && !string.IsNullOrWhiteSpace(combined))
            {
                embedding = await TryGenerateEmbeddingAsync(combined, settings.OpenAiKey!, settings.EmbeddingModel, cancellationToken);
            }

            slidePayload.Add(new
            {
                id = Guid.NewGuid(),
                deck_id = deckId,
                slide_no = slide.index,
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

    private static async Task<string> UploadToSupabaseStorageAsync(
        string localFilePath,
        string contentType,
        SupabaseSettings settings,
        string objectPath,
        CancellationToken cancellationToken)
    {
        var normalizedPath = NormalizeStoragePath(objectPath);
        var requestUri = $"{settings.Url}/storage/v1/object/{settings.StorageBucket}/{normalizedPath}";

        using var request = new HttpRequestMessage(HttpMethod.Put, requestUri);
        request.Headers.Add("apikey", settings.ServiceKey);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ServiceKey);
        request.Headers.Add("x-upsert", "true");

        var fileBytes = await File.ReadAllBytesAsync(localFilePath, cancellationToken);
        request.Content = new ByteArrayContent(fileBytes);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);

        using var response = await SharedHttpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new Exception($"Supabase storage upload failed ({(int)response.StatusCode}): {body}");
        }

        return normalizedPath;
    }

    private static string NormalizeStoragePath(string path)
    {
        return path.Trim('/');
    }

    private static async Task<Dictionary<int, List<string>>> GenerateImageCaptionsAsync(
        DeckDto deck,
        string pptxPath,
        Guid deckId,
        SupabaseSettings settings,
        CancellationToken cancellationToken)
    {
        var captionsBySlide = new Dictionary<int, List<string>>();

        if (string.IsNullOrWhiteSpace(settings.OpenAiKey))
            return captionsBySlide;

        using var archive = ZipFile.OpenRead(pptxPath);
        var captionCache = new Dictionary<string, string>();

        foreach (var slide in deck.slides)
        {
            foreach (var picture in slide.elements.OfType<PictureDto>())
            {
                if (string.IsNullOrWhiteSpace(picture.imgPath))
                    continue;

                var entryPath = picture.imgPath.TrimStart('/');
                if (captionCache.TryGetValue(entryPath, out var cached))
                {
                    picture.summary = cached;
                    AppendCaption(captionsBySlide, slide.index, cached);
                    continue;
                }

                var entry = archive.GetEntry(entryPath);
                if (entry is null)
                    continue;

                await using var entryStream = entry.Open();
                using var ms = new MemoryStream();
                await entryStream.CopyToAsync(ms, cancellationToken);
                var imageBytes = ms.ToArray();

                var contentType = GetMimeTypeForExtension(Path.GetExtension(entryPath));

                var caption = await RequestImageCaptionAsync(imageBytes, contentType, settings, cancellationToken);
                if (!string.IsNullOrWhiteSpace(caption))
                {
                    caption = caption.Trim();
                    picture.summary = caption;
                    captionCache[entryPath] = caption;
                    AppendCaption(captionsBySlide, slide.index, caption);
                }
            }
        }

        return captionsBySlide;
    }

    private static void AppendCaption(Dictionary<int, List<string>> captionsBySlide, int slideIndex, string caption)
    {
        if (!captionsBySlide.TryGetValue(slideIndex, out var list))
        {
            list = new List<string>();
            captionsBySlide[slideIndex] = list;
        }
        list.Add(caption);
    }

    private static async Task GenerateTableSummariesAsync(
        DeckDto deck,
        SupabaseSettings settings,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.OpenAiKey))
            return;

        foreach (var table in deck.slides.SelectMany(s => s.elements).OfType<TableDto>())
        {
            var tableText = FormatTableForSummary(table);
            if (string.IsNullOrWhiteSpace(tableText))
                continue;

            var summary = await RequestTableSummaryAsync(tableText, settings, cancellationToken);
            if (!string.IsNullOrWhiteSpace(summary))
            {
                table.summary = summary.Trim();
            }
        }
    }

    private static string FormatTableForSummary(TableDto table)
    {
        if (table.cells is null || table.cells.Length == 0)
            return string.Empty;

        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(table.name))
        {
            sb.Append("Table ").Append(table.name.Trim()).AppendLine();
        }

        var headers = new string[table.cols];
        var hasHeaders = false;
        if (table.rows > 0)
        {
            var headerRow = table.cells[0];
            if (headerRow != null)
            {
                for (int c = 0; c < Math.Min(table.cols, headerRow.Length); c++)
                {
                    var cell = headerRow[c];
                    if (cell is null) continue;
                    var text = ExtractTableCellText(cell);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        headers[c] = text.Trim();
                        hasHeaders = true;
                    }
                }
            }
        }

        for (int r = 0; r < Math.Min(table.rows, table.cells.Length); r++)
        {
            var row = table.cells[r];
            if (row is null) continue;

            var values = new List<string>();
            for (int c = 0; c < Math.Min(table.cols, row.Length); c++)
            {
                var cell = row[c];
                if (cell is null) continue;
                var text = ExtractTableCellText(cell);
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                var label = headers[c];
                if (string.IsNullOrWhiteSpace(label))
                {
                    label = $"Column {c + 1}";
                }

                if (cell.rowSpan > 1 || cell.colSpan > 1)
                {
                    label += $" (spans {cell.rowSpan}x{cell.colSpan})";
                }

                values.Add($"{label}: {text.Trim()}");
            }

            if (values.Count > 0)
            {
                var rowLabel = hasHeaders && r == 0 ? "Header" : $"Row {r + 1}";
                sb.Append(rowLabel).Append(": ").Append(string.Join("; ", values)).AppendLine();
            }
        }

        var result = sb.ToString().Trim();
        if (result.Length > 8000)
        {
            result = result.Substring(0, 8000);
        }

        return result;
    }

    private static string ExtractTableCellText(TableCellDto cell)
    {
        if (cell.paragraphs is null || cell.paragraphs.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        foreach (var paragraph in cell.paragraphs)
        {
            if (string.IsNullOrWhiteSpace(paragraph.text))
                continue;
            if (sb.Length > 0)
                sb.Append(' ');
            sb.Append(paragraph.text.Trim());
        }

        return sb.ToString();
    }

    private static async Task<string?> RequestImageCaptionAsync(byte[] imageBytes, string contentType, SupabaseSettings settings, CancellationToken cancellationToken)
    {
        if (imageBytes.Length == 0)
            return null;

        if (string.IsNullOrWhiteSpace(settings.OpenAiKey))
            return null;

        var model = settings.VisionModel;
        if (string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(settings.OpenAiKey))
            return null;

        var base64 = Convert.ToBase64String(imageBytes);
        var imageUrl = $"data:{contentType};base64,{base64}";

        var payload = new
        {
            model,
            input = new object[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "input_text", text = "Describe this slide image succinctly for semantic search. Avoid speculation." },
                        new { type = "input_image", image_url = imageUrl }
                    }
                }
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.OpenAiKey);
        request.Headers.Add("OpenAI-Beta", "assistants=v2");
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var response = await SharedHttpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            Console.Error.WriteLine($"Vision caption request failed ({(int)response.StatusCode}): {error}");
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (doc.RootElement.TryGetProperty("output_text", out var outputText) && outputText.ValueKind == JsonValueKind.String)
        {
            return outputText.GetString();
        }

        if (doc.RootElement.TryGetProperty("output", out var outputEl) && outputEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in outputEl.EnumerateArray())
            {
                if (item.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var contentItem in contentEl.EnumerateArray())
                    {
                        if (contentItem.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
                        {
                            return textEl.GetString();
                        }
                    }
                }
            }
        }

        return null;
    }

    private static string GetMimeTypeForExtension(string? extension)
    {
        return extension?.ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".tif" or ".tiff" => "image/tiff",
            _ => "image/png"
        };
    }

    private static async Task<string?> RequestTableSummaryAsync(string tableText, SupabaseSettings settings, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tableText))
            return null;

        var model = settings.VisionModel;
        if (string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(settings.OpenAiKey))
            return null;

        var prompt = "Summarize the following slide table in one or two sentences focusing on the key comparisons or insights. Avoid speculation.";
        if (tableText.Length > 6000)
        {
            tableText = tableText.Substring(0, 6000);
        }

        var payload = new
        {
            model,
            input = new object[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "input_text", text = $"{prompt}\n\n{tableText}" }
                    }
                }
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.OpenAiKey);
        request.Headers.Add("OpenAI-Beta", "assistants=v2");
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var response = await SharedHttpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            Console.Error.WriteLine($"Table summary request failed ({(int)response.StatusCode}): {error}");
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (doc.RootElement.TryGetProperty("output_text", out var outputText) && outputText.ValueKind == JsonValueKind.String)
        {
            return outputText.GetString();
        }

        if (doc.RootElement.TryGetProperty("output", out var outputEl) && outputEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in outputEl.EnumerateArray())
            {
                if (item.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var contentItem in contentEl.EnumerateArray())
                    {
                        if (contentItem.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
                        {
                            return textEl.GetString();
                        }
                    }
                }
            }
        }

        return null;
    }

    private static string CombineSlideContent(
        string text,
        IReadOnlyList<string>? captions,
        IReadOnlyList<string>? tableSummaries)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(text))
        {
            sb.AppendLine(text.Trim());
        }

        if (captions != null)
        {
            foreach (var caption in captions)
            {
                if (string.IsNullOrWhiteSpace(caption))
                    continue;
                if (sb.Length > 0)
                    sb.AppendLine();
                sb.Append("Image summary: ").Append(caption.Trim());
            }
        }

        if (tableSummaries != null)
        {
            foreach (var summary in tableSummaries)
            {
                if (string.IsNullOrWhiteSpace(summary))
                    continue;
                if (sb.Length > 0)
                    sb.AppendLine();
                sb.Append("Table summary: ").Append(summary.Trim());
            }
        }

        return sb.ToString().Trim();
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

    private static async Task ConvertPptxToPdfAsync(string inputPath, string outDir)
    {
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
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            Console.Error.WriteLine($"Embedding request failed ({(int)response.StatusCode}): {body}");
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var embeddingResponse = await JsonSerializer.DeserializeAsync<OpenAiEmbeddingResponse>(stream, cancellationToken: cancellationToken);
        var vector = embeddingResponse?.Data?.FirstOrDefault()?.Embedding;
        return vector?.ToArray();
    }

    private sealed record DeckArtifactUploadResult(
        string BaseName,
        string PptxStoragePath,
        string JsonStoragePath,
        string PdfStoragePath,
        string PdfFileName,
        byte[] PdfBytes,
        Dictionary<int, List<string>> ImageCaptions);

    private sealed record SupabaseSettings(
        string Url,
        string ServiceKey,
        string DecksTable,
        string SlidesTable,
        string? OpenAiKey,
        string EmbeddingModel,
        string StorageBucket,
        string StoragePathPrefix,
        string VisionModel);

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
}
