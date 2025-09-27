// ASP.NET Core entry point and HTTP endpoints for PPTX extraction and redaction.
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Diagnostics;
using System.Net.Http;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using System.Text.Json.Serialization.Metadata;
using System.ComponentModel;

// ----------------------------- Web app --------------------------------------

public static partial class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
            p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

        var app = builder.Build();
        app.UseCors();

        // POST /api/extract: accept multipart "file", return deck JSON
        app.MapPost("/api/extract", async (HttpRequest req) =>
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

                // Configure polymorphic serialization for ElementDto
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

                if (string.IsNullOrWhiteSpace(instructions))
                {
                    return Results.Text(JsonSerializer.Serialize(deck, jsonOptions), "application/json");
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
                            export = exportInfo
                        };

                        return Results.Text(JsonSerializer.Serialize(result, jsonOptions), "application/json");
                    }

                    return Results.Text(JsonSerializer.Serialize(new
                    {
                        deck,
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

    // --------------------------- LibreOffice conversion ----------------------

    private static async Task ConvertPptxToPdfAsync(string inputPath, string outDir)
    {
        // Use SOFFICE_PATH env var if you want a fixed path; else rely on PATH
        var soffice = Environment.GetEnvironmentVariable("SOFFICE_PATH") ?? "soffice";

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
        psi.ArgumentList.Add("--convert-to");
        psi.ArgumentList.Add("pdf");
        psi.ArgumentList.Add("--outdir");
        psi.ArgumentList.Add(outDir);
        psi.ArgumentList.Add(inputPath);

        using var proc = Process.Start(psi)!;
        var stdOut = await proc.StandardOutput.ReadToEndAsync();
        var stdErr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();

        if (proc.ExitCode != 0)
        {
            throw new Exception($"LibreOffice conversion failed (code {proc.ExitCode}). stderr: {stdErr}");
        }
    }
}
