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
using Docnet.Core;
using Docnet.Core.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Formats.Png;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

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

        Console.WriteLine($"[env] SUPABASE_URL={Environment.GetEnvironmentVariable("SUPABASE_URL") ?? "<null>"}");
        Console.WriteLine($"[env] SUPABASE_SERVICE_ROLE_KEY={(Environment.GetEnvironmentVariable("SUPABASE_SERVICE_ROLE_KEY") is { Length: > 0 } ? "<set>" : "<null>")}");
        Console.WriteLine($"[env] SUPABASE_STORAGE_BUCKET={Environment.GetEnvironmentVariable("SUPABASE_STORAGE_BUCKET") ?? "<null>"}");

        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
            p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));
        builder.Services.AddMemoryCache();

        var app = builder.Build();
        app.UseCors();

        var deckCache = app.Services.GetRequiredService<IMemoryCache>();
        var cacheExpiration = TimeSpan.FromMinutes(5);

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

            var industry = form.TryGetValue("industry", out var industryValues)
                ? industryValues.ToString()
                : null;
            industry = string.IsNullOrWhiteSpace(industry) ? null : industry!.Trim();

            var deckType = form.TryGetValue("deckType", out var deckTypeValues)
                ? deckTypeValues.ToString()
                : null;
            deckType = string.IsNullOrWhiteSpace(deckType) ? null : deckType!.Trim();

            Guid deckId = Guid.Empty;
            var requestStopwatch = Stopwatch.StartNew();

            try
            {
                await using var ms = new MemoryStream();
                await file.CopyToAsync(ms);
                ms.Position = 0;

                var extractSw = Stopwatch.StartNew();
                var deck = ExtractDeck(ms, file.FileName);
                LogTiming("extract deck", extractSw.Elapsed, $"slides {deck.slideCount}");

                var jsonOptions = CreateDeckJsonOptions();

                var supabaseSettings = ReadSupabaseSettings(app.Configuration);
                if (supabaseSettings is null)
                {
                    return Results.Problem(
                        detail: "Supabase storage is not configured. Set SUPABASE_URL, SUPABASE_SERVICE_ROLE_KEY, and SUPABASE_STORAGE_BUCKET.",
                        statusCode: 500
                    );
                }

                deckId = Guid.NewGuid();

                var artifactsSw = Stopwatch.StartNew();
                var artifacts = await GenerateInitialArtifactsAsync(
                    ms,
                    deck,
                    file.FileName,
                    deckId,
                    supabaseSettings,
                    req.HttpContext.RequestAborted);
                LogTiming("initial artifacts", artifactsSw.Elapsed, deckId.ToString("n"));

                var originalBytes = ms.ToArray();
                deckCache.Set(GetDeckCacheKey(deckId), originalBytes, new MemoryCacheEntryOptions
                {
                    SlidingExpiration = cacheExpiration
                });

                var pdfInfo = new
                {
                    fileName = artifacts.PdfFileName,
                    base64 = ConvertToBase64WithTiming(artifacts.PdfBytes)
                };

                try
                {
                    var insertSw = Stopwatch.StartNew();
                    await InsertDeckRecordAsync(
                        deck,
                        deckId,
                        artifacts,
                        industry,
                        deckType,
                        supabaseSettings,
                        req.HttpContext.RequestAborted);
                    LogTiming("insert deck record", insertSw.Elapsed, deckId.ToString("n"));
                }
                catch (Exception supabaseEx)
                {
                    Console.Error.WriteLine($"Supabase persistence failed: {supabaseEx.Message}");
                }

                object? ruleInfo = null;

                if (!string.IsNullOrWhiteSpace(instructions))
                {
                    var semanticUrl = app.Configuration["SEMANTIC_URL"]
                        ?? Environment.GetEnvironmentVariable("SEMANTIC_URL")
                        ?? "http://localhost:8000";

                    var redactUrl = semanticUrl.TrimEnd('/') + "/redact";

                    using var client = new HttpClient();

                    var payload = new
                    {
                        deckId = deckId,
                        instructions,
                        deck
                    };

                    var payloadJson = JsonSerializer.Serialize(payload, jsonOptions);
                    using var content = new StringContent(payloadJson, Encoding.UTF8, "application/json");

                    try
                    {
                        var semanticSw = Stopwatch.StartNew();
                        var response = await client.PostAsync(redactUrl, content);
                        var responseBody = await response.Content.ReadAsStringAsync();
                        LogTiming("semantic redact call", semanticSw.Elapsed, deckId.ToString("n"));

                        if (response.IsSuccessStatusCode)
                        {
                            try
                            {
                                ruleInfo = JsonSerializer.Deserialize<JsonElement>(responseBody, jsonOptions);
                            }
                            catch
                            {
                                ruleInfo = new { success = true, raw = responseBody };
                            }
                        }
                        else
                        {
                            ruleInfo = new
                            {
                                success = false,
                                error = "semantic_service_failed",
                                status = (int)response.StatusCode,
                                body = responseBody
                            };
                        }
                    }
                    catch (Exception httpEx)
                    {
                        ruleInfo = new
                        {
                            success = false,
                            error = "semantic_service_unavailable",
                            detail = httpEx.Message
                        };
                    }
                }

                var resultPayload = new
                {
                    deckId,
                    deck,
                    industry,
                    deckType,
                    pdf = pdfInfo,
                    rule = ruleInfo,
                    instructions
                };

                LogTiming("upload handler", requestStopwatch.Elapsed, deckId.ToString("n"));
                return Results.Text(JsonSerializer.Serialize(resultPayload, jsonOptions), "application/json");
            }
            catch (Exception ex)
            {
                LogTiming("upload handler failed", requestStopwatch.Elapsed, deckId == Guid.Empty ? "no-deck" : deckId.ToString("n"));
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapGet("/api/decks", async (HttpRequest request, CancellationToken cancellationToken) =>
        {
            var settings = ReadSupabaseSettings(app.Configuration);
            if (settings is null)
            {
                return Results.Problem("Supabase storage is not configured", statusCode: 500);
            }

            var limit = 6;
            if (request.Query.TryGetValue("limit", out var limitValues) && int.TryParse(limitValues.FirstOrDefault(), out var parsedLimit))
            {
                limit = Math.Clamp(parsedLimit, 1, 50);
            }

            var query = new Dictionary<string, string?>
            {
                ["select"] = "id,deck_name,pptx_path,redacted_pptx_path,redacted_pdf_path,redacted_json_path,industry,deck_type,slide_count,created_at,updated_at",
                ["order"] = "created_at.desc",
                ["limit"] = limit.ToString()
            };

            var requestUri = QueryHelpers.AddQueryString($"{settings.Url}/rest/v1/{settings.DecksTable}", query);
            using var httpRequest = new HttpRequestMessage(HttpMethod.Get, requestUri);
            httpRequest.Headers.Add("apikey", settings.ServiceKey);
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ServiceKey);
            httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await SharedHttpClient.SendAsync(httpRequest, cancellationToken);
            var payload = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return Results.Problem(
                    detail: payload,
                    statusCode: (int)response.StatusCode,
                    title: $"Supabase decks query failed ({(int)response.StatusCode})");
            }

            return Results.Content(payload, "application/json");
        });

        app.MapPost("/api/decks/{deckId:guid}/redact", async (Guid deckId, CancellationToken cancellationToken) =>
        {
            var handlerSw = Stopwatch.StartNew();
            var handlerStatus = "redact handler failed";
            var handlerDetail = deckId.ToString("n");

            try
            {
                var settings = ReadSupabaseSettings(app.Configuration);
                if (settings is null)
                {
                    handlerDetail = "settings-missing";
                    return Results.Problem(
                        detail: "Supabase storage is not configured. Set SUPABASE_URL, SUPABASE_SERVICE_ROLE_KEY, and SUPABASE_STORAGE_BUCKET.",
                        statusCode: 500);
                }

                DeckRecord? deckRecord;
                try
                {
                    deckRecord = await FetchDeckRecordAsync(deckId, settings, cancellationToken);
                }
                catch (Exception ex)
                {
                    handlerDetail = "fetch-deck";
                    return Results.Problem($"Failed to load deck metadata: {ex.Message}", statusCode: 500);
                }

                if (deckRecord is null || string.IsNullOrWhiteSpace(deckRecord.PptxPath))
                {
                    handlerDetail = "deck-pptx-missing";
                    return Results.NotFound(new { error = "Deck PPTX not found" });
                }

                var cacheKey = GetDeckCacheKey(deckId);
                if (!deckCache.TryGetValue(cacheKey, out byte[]? originalPptx))
                {
                    originalPptx = await DownloadSupabaseObjectAsync(settings, deckRecord.PptxPath!, cancellationToken);
                    if (originalPptx != null && originalPptx.Length > 0)
                    {
                        deckCache.Set(cacheKey, originalPptx, new MemoryCacheEntryOptions
                        {
                            SlidingExpiration = cacheExpiration
                        });
                    }
                }
                else
                {
                    LogTiming("deck cache hit", TimeSpan.Zero, deckId.ToString("n"));
                }

                if (originalPptx is null || originalPptx.Length == 0)
                {
                    handlerDetail = "download-missing";
                    return Results.NotFound(new { error = "Unable to download original deck" });
                }

                List<RuleActionRecord> ruleActions;
                try
                {
                    ruleActions = await FetchRuleActionsAsync(deckId, settings, cancellationToken);
                }
                catch (Exception ex)
                {
                    handlerDetail = "rule-actions";
                    return Results.Problem($"Failed to load rule actions: {ex.Message}", statusCode: 500);
                }

                int appliedCount = 0;
                byte[] redactedBytes;

                if (ruleActions.Count > 0)
                {
                    try
                    {
                        var applySw = Stopwatch.StartNew();
                        redactedBytes = ApplyRuleActionsToPptx(originalPptx, ruleActions, out appliedCount);
                        LogTiming("apply rule actions", applySw.Elapsed, $"{deckId:n} actions {ruleActions.Count}");
                    }
                    catch (Exception ex)
                    {
                        var detail = ex is AggregateException agg ? string.Join("; ", agg.InnerExceptions.Select(e => e.Message)) : ex.Message;
                        handlerDetail = "apply-failed";
                        return Results.Json(new
                        {
                            error = "failed_to_apply_redactions",
                            message = detail,
                            stack = ex.StackTrace,
                        }, statusCode: 500);
                    }
                }
                else
                {
                    redactedBytes = originalPptx;
                }

                var jsonOptions = CreateDeckJsonOptions();

                var originalPath = NormalizeStoragePath(deckRecord.PptxPath!);
                var originalFileName = Path.GetFileNameWithoutExtension(originalPath);
                var redactedBaseName = string.IsNullOrWhiteSpace(originalFileName)
                    ? $"{deckId.ToString("n")}-redacted"
                    : $"{originalFileName}-redacted";

                var tempRoot = Path.Combine(Path.GetTempPath(), "dexter-redacted", Guid.NewGuid().ToString("n"));
                Directory.CreateDirectory(tempRoot);

                try
                {
                    var redactedLocalPptx = Path.Combine(tempRoot, $"{redactedBaseName}.pptx");
                    await File.WriteAllBytesAsync(redactedLocalPptx, redactedBytes, cancellationToken);

                    await using var redactedStream = new MemoryStream(redactedBytes, writable: false);
                    var extractSw = Stopwatch.StartNew();
                    var redactedDeck = ExtractDeck(redactedStream, Path.GetFileName(deckRecord.PptxPath) ?? $"{redactedBaseName}.pptx");
                    LogTiming("extract deck (redacted)", extractSw.Elapsed, $"{deckId:n} slides {redactedDeck.slideCount}");

                    var persistSw = Stopwatch.StartNew();
                    var redactedArtifacts = await PersistRedactedArtifactsAsync(
                        redactedLocalPptx,
                        redactedDeck,
                        redactedBaseName,
                        deckId,
                        settings,
                        jsonOptions,
                        cancellationToken);
                    LogTiming("persist redacted artifacts (handler)", persistSw.Elapsed, deckId.ToString("n"));

                    try
                    {
                        await DeleteSlidesForDeckAsync(deckId, settings, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"Slide cleanup failed: {ex.Message}");
                    }

                    await ReplaceSlideEmbeddingsAsync(
                        redactedDeck,
                        deckId,
                        redactedArtifacts.ImageCaptions,
                        settings,
                        cancellationToken);

                    try
                    {
                        var updateSw = Stopwatch.StartNew();
                        await UpdateDeckWithRedactedArtifactsAsync(
                            deckId,
                            redactedArtifacts.PptxPath,
                            redactedArtifacts.PdfPath,
                            redactedArtifacts.JsonPath,
                            redactedDeck.slideCount,
                            settings,
                            cancellationToken);
                        LogTiming("update deck record", updateSw.Elapsed, deckId.ToString("n"));
                    }
                    catch (Exception ex)
                    {
                        handlerDetail = "update-deck";
                        return Results.Problem($"Failed to update deck record: {ex.Message}", statusCode: 500);
                    }

                    string? signedUrl = null;
                    try
                    {
                        var signedSw = Stopwatch.StartNew();
                        signedUrl = await CreateSupabaseSignedUrlAsync(settings, redactedArtifacts.PptxPath, 3600, cancellationToken);
                        if (signedUrl != null)
                        {
                            LogTiming("create signed url", signedSw.Elapsed, deckId.ToString("n"));
                        }
                    }
                    catch
                    {
                        signedUrl = null;
                    }

                    handlerStatus = "redact handler";
                    return Results.Json(new
                    {
                        success = true,
                        actionsApplied = appliedCount,
                        path = redactedArtifacts.PptxPath,
                        fileName = Path.GetFileName(redactedArtifacts.PptxPath),
                        downloadUrl = signedUrl,
                        generated = ruleActions.Count > 0
                    });
                }
                finally
                {
                    try { Directory.Delete(tempRoot, recursive: true); } catch { /* ignore */ }
                }
            }
            finally
            {
                LogTiming(handlerStatus, handlerSw.Elapsed, handlerDetail);
            }
        });

        app.MapGet("/api/decks/{deckId:guid}/download", async (Guid deckId, HttpRequest request, CancellationToken cancellationToken) =>
        {
            var settings = ReadSupabaseSettings(app.Configuration);
            if (settings is null)
            {
                return Results.Problem(
                    detail: "Supabase storage is not configured. Set SUPABASE_URL, SUPABASE_SERVICE_ROLE_KEY, and SUPABASE_STORAGE_BUCKET.",
                    statusCode: 500);
            }

            DeckRecord? deckRecord;
            try
            {
                deckRecord = await FetchDeckRecordAsync(deckId, settings, cancellationToken);
            }
            catch (Exception ex)
            {
                return Results.Problem($"Failed to load deck metadata: {ex.Message}", statusCode: 500);
            }

            if (deckRecord is null)
            {
                return Results.NotFound(new { error = "deck_not_found" });
            }

            var variantRaw = request.Query.TryGetValue("variant", out var variantValues)
                ? variantValues.FirstOrDefault()
                : null;

            var variant = string.IsNullOrWhiteSpace(variantRaw)
                ? "redacted"
                : variantRaw!.Trim().ToLowerInvariant();

            string? selectedPath = variant switch
            {
                "original" => deckRecord.PptxPath,
                "pdf" => deckRecord.RedactedPdfPath ?? deckRecord.PptxPath,
                "json" => deckRecord.RedactedJsonPath,
                _ => deckRecord.RedactedPptxPath ?? deckRecord.PptxPath,
            };

            string resolvedVariant = variant switch
            {
                "original" => "original",
                "pdf" => deckRecord.RedactedPdfPath != null ? "pdf" : "original",
                "json" => "json",
                _ => deckRecord.RedactedPptxPath != null ? "redacted" : "original",
            };

            if (string.IsNullOrWhiteSpace(selectedPath))
            {
                return Results.NotFound(new { error = "asset_missing", variant = resolvedVariant });
            }

            byte[]? fileBytes;
            try
            {
                fileBytes = await DownloadSupabaseObjectAsync(settings, selectedPath, cancellationToken);
            }
            catch (Exception ex)
            {
                return Results.Problem($"Failed to download deck: {ex.Message}", statusCode: 500);
            }

            if (fileBytes is null || fileBytes.Length == 0)
            {
                return Results.NotFound(new { error = "asset_not_found", variant = resolvedVariant });
            }

            var extension = Path.GetExtension(selectedPath)?.ToLowerInvariant() ?? string.Empty;
            var contentType = extension switch
            {
                ".pdf" => "application/pdf",
                ".json" => "application/json",
                _ => "application/vnd.openxmlformats-officedocument.presentationml.presentation"
            };

            var fileName = Path.GetFileName(selectedPath);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                var safeName = deckRecord.DeckName;
                if (string.IsNullOrWhiteSpace(safeName))
                {
                    safeName = deckId.ToString("n");
                }

                fileName = resolvedVariant switch
                {
                    "pdf" => $"{safeName}.pdf",
                    "json" => $"{safeName}.json",
                    _ => $"{safeName}.pptx",
                };
            }

            return Results.File(fileBytes, contentType, fileName);
        });

        app.MapGet("/api/decks/{deckId:guid}/slides/{slideNo:int}", async (Guid deckId, int slideNo, HttpRequest request, CancellationToken cancellationToken) =>
        {
            if (slideNo < 1)
            {
                return Results.BadRequest(new { error = "slideNo must be >= 1" });
            }

            var settings = ReadSupabaseSettings(app.Configuration);
            if (settings is null)
            {
                return Results.Problem("Supabase storage is not configured", statusCode: 500);
            }

            var targetWidth = 960;
            if (request.Query.TryGetValue("width", out var widthValues) && int.TryParse(widthValues.FirstOrDefault(), out var parsedWidth))
            {
                if (parsedWidth > 0)
                {
                    targetWidth = Math.Clamp(parsedWidth, 120, 2048);
                }
            }

            var pdfPath = await FetchDeckPdfInfoAsync(deckId, settings, cancellationToken);
            if (string.IsNullOrWhiteSpace(pdfPath))
            {
                return Results.NotFound(new { error = "Deck PDF not found" });
            }

            var pdfBytes = await DownloadSupabaseObjectAsync(settings, pdfPath!, cancellationToken);
            if (pdfBytes is null || pdfBytes.Length == 0)
            {
                return Results.NotFound(new { error = "Unable to download deck PDF" });
            }

            byte[] pngBytes;
            try
            {
                pngBytes = RenderSlideToPng(pdfBytes, slideNo, targetWidth);
            }
            catch (ArgumentOutOfRangeException)
            {
                return Results.NotFound(new { error = "Slide not found" });
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message, statusCode: 500);
            }

            return Results.File(pngBytes, "image/png");
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

    private static async Task<InitialDeckArtifacts> GenerateInitialArtifactsAsync(
        Stream pptxStream,
        DeckDto deck,
        string originalFileName,
        Guid deckId,
        SupabaseSettings settings,
        CancellationToken cancellationToken)
    {
        var totalSw = Stopwatch.StartNew();
        var baseName = SanitizeBaseName(originalFileName);
        var tempRoot = Path.Combine(Path.GetTempPath(), "dexter-artifacts", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempRoot);

        var pptxPath = Path.Combine(tempRoot, $"{baseName}.pptx");
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

            var pdfReadSw = Stopwatch.StartNew();
            var pdfBytes = await File.ReadAllBytesAsync(pdfPath, cancellationToken);
            LogTiming("read pdf bytes", pdfReadSw.Elapsed, $"bytes {pdfBytes.Length}");

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

            pptxStream.Position = 0;

            var result = new InitialDeckArtifacts(
                BaseName: baseName,
                PptxStoragePath: pptxObjectPath,
                PdfFileName: Path.GetFileName(pdfPath),
                PdfBytes: pdfBytes,
                ImageCaptions: imageCaptions);
            LogTiming("initial artifacts pipeline", totalSw.Elapsed, deckId.ToString("n"));
            return result;
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { /* ignore */ }
        }
    }

    private static JsonSerializerOptions CreateDeckJsonOptions()
    {
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

        return new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            TypeInfoResolver = resolver
        };
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

        var rulesTable = configuration["SUPABASE_RULES_TABLE"] ?? Environment.GetEnvironmentVariable("SUPABASE_RULES_TABLE") ?? "rules";
        var ruleActionsTable = configuration["SUPABASE_RULE_ACTIONS_TABLE"] ?? Environment.GetEnvironmentVariable("SUPABASE_RULE_ACTIONS_TABLE") ?? "rule_actions";

        return new SupabaseSettings(
            Url: url.TrimEnd('/'),
            ServiceKey: serviceKey,
            DecksTable: decksTable,
            SlidesTable: slidesTable,
            RulesTable: rulesTable,
            RuleActionsTable: ruleActionsTable,
            OpenAiKey: openAiKey,
            EmbeddingModel: embeddingModel,
            StorageBucket: storageBucket,
            StoragePathPrefix: storagePrefix ?? string.Empty,
            VisionModel: visionModel);
    }

    private static async Task InsertDeckRecordAsync(
        DeckDto deck,
        Guid deckId,
        InitialDeckArtifacts artifacts,
        string? industry,
        string? deckType,
        SupabaseSettings settings,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;

        var deckRecord = new
        {
            id = deckId,
            deck_name = artifacts.BaseName,
            pptx_path = artifacts.PptxStoragePath,
            industry,
            deck_type = deckType,
            slide_count = deck.slideCount,
            created_at = now,
            updated_at = now
        };

        await PostgrestInsertAsync(settings, settings.DecksTable, deckRecord, returnRepresentation: false, cancellationToken);

        // No slide embeddings at upload time; they will be created once the redacted deck is finalized.
    }

    private static async Task<RedactedArtifacts> PersistRedactedArtifactsAsync(
        string localPptxPath,
        DeckDto deck,
        string baseName,
        Guid deckId,
        SupabaseSettings settings,
        JsonSerializerOptions jsonOptions,
        CancellationToken cancellationToken)
    {
        var totalSw = Stopwatch.StartNew();
        var tempDir = Path.GetDirectoryName(localPptxPath)!;
        var deckFolder = deckId.ToString("n");
        var objectPrefix = string.IsNullOrWhiteSpace(settings.StoragePathPrefix)
            ? deckFolder
            : $"{settings.StoragePathPrefix.TrimEnd('/')}/{deckFolder}";

        var pptxObjectPath = await UploadToSupabaseStorageAsync(
            localPptxPath,
            "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            settings,
            $"{objectPrefix}/{Path.GetFileName(localPptxPath)}",
            cancellationToken);

        await ConvertPptxToPdfAsync(localPptxPath, tempDir);

        var pdfPath = Path.Combine(tempDir, $"{baseName}.pdf");
        if (!File.Exists(pdfPath))
        {
            var generated = Directory.GetFiles(tempDir, "*.pdf").FirstOrDefault();
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
            throw new InvalidOperationException("Redacted PDF conversion failed (no output file).");
        }

        var imageCaptions = await GenerateImageCaptionsAsync(
            deck,
            localPptxPath,
            deckId,
            settings,
            cancellationToken);

        await GenerateTableSummariesAsync(
            deck,
            settings,
            cancellationToken);

        var pdfObjectPath = await UploadToSupabaseStorageAsync(
            pdfPath,
            "application/pdf",
            settings,
            $"{objectPrefix}/{Path.GetFileName(pdfPath)}",
            cancellationToken);

        var jsonTempPath = Path.Combine(tempDir, $"{baseName}.json");
        var jsonSerializeSw = Stopwatch.StartNew();
        var deckJson = JsonSerializer.Serialize(deck, jsonOptions);
        await File.WriteAllTextAsync(jsonTempPath, deckJson, cancellationToken);
        LogTiming("write deck json", jsonSerializeSw.Elapsed, deckId.ToString("n"));

        var jsonObjectPath = await UploadToSupabaseStorageAsync(
            jsonTempPath,
            "application/json",
            settings,
            $"{objectPrefix}/{Path.GetFileName(jsonTempPath)}",
            cancellationToken);

        var result = new RedactedArtifacts(
            PptxPath: pptxObjectPath,
            PdfPath: pdfObjectPath,
            JsonPath: jsonObjectPath,
            ImageCaptions: imageCaptions);
        LogTiming("persist redacted artifacts", totalSw.Elapsed, deckId.ToString("n"));
        return result;
    }

    private static async Task DeleteSlidesForDeckAsync(
        Guid deckId,
        SupabaseSettings settings,
        CancellationToken cancellationToken)
    {
        var requestUri = $"{settings.Url}/rest/v1/{settings.SlidesTable}?deck_id=eq.{deckId}";
        using var request = new HttpRequestMessage(HttpMethod.Delete, requestUri);
        request.Headers.Add("apikey", settings.ServiceKey);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ServiceKey);
        request.Headers.Add("Prefer", "return=minimal");

        var sw = Stopwatch.StartNew();
        using var response = await SharedHttpClient.SendAsync(request, cancellationToken);
        var elapsed = sw.Elapsed;
        if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            LogTiming("supabase slide delete failed", elapsed, deckId.ToString("n"));
            throw new Exception($"Supabase slide delete failed ({(int)response.StatusCode}): {body}");
        }

        LogTiming("supabase slide delete", elapsed, deckId.ToString("n"));
    }

    private static async Task ReplaceSlideEmbeddingsAsync(
        DeckDto deck,
        Guid deckId,
        Dictionary<int, List<string>> imageCaptions,
        SupabaseSettings settings,
        CancellationToken cancellationToken)
    {
        var totalSw = Stopwatch.StartNew();
        var now = DateTime.UtcNow;
        var slidePayload = new List<object>();
        int embeddingAttempts = 0;
        int embeddingSuccess = 0;

        foreach (var slide in deck.slides)
        {
            var text = ExtractSlidePlainText(slide);
            var captions = imageCaptions.TryGetValue(slide.index, out var captionList) ? captionList : null;
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
                embeddingAttempts++;
                embedding = await TryGenerateEmbeddingAsync(combined, settings.OpenAiKey!, settings.EmbeddingModel, cancellationToken);
                if (embedding != null)
                {
                    embeddingSuccess++;
                }
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

        LogTiming("replace slide embeddings", totalSw.Elapsed, $"{deckId:n} slides {deck.slides.Count} attempts {embeddingAttempts} success {embeddingSuccess}");
    }

    private static async Task UpdateDeckWithRedactedArtifactsAsync(
        Guid deckId,
        string redactedPptxPath,
        string redactedPdfPath,
        string redactedJsonPath,
        int slideCount,
        SupabaseSettings settings,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            redacted_pptx_path = redactedPptxPath,
            redacted_pdf_path = redactedPdfPath,
            redacted_json_path = redactedJsonPath,
            slide_count = slideCount,
            updated_at = DateTime.UtcNow
        };

        var requestUri = $"{settings.Url}/rest/v1/{settings.DecksTable}?id=eq.{deckId}";
        using var request = new HttpRequestMessage(HttpMethod.Patch, requestUri);
        request.Headers.Add("apikey", settings.ServiceKey);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ServiceKey);
        request.Headers.Add("Prefer", "return=minimal");
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        var sw = Stopwatch.StartNew();
        using var response = await SharedHttpClient.SendAsync(request, cancellationToken);
        var elapsed = sw.Elapsed;
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            LogTiming("supabase deck update failed", elapsed, deckId.ToString("n"));
            throw new Exception($"Supabase deck update failed ({(int)response.StatusCode}): {body}");
        }

        LogTiming("supabase deck update", elapsed, deckId.ToString("n"));
    }

    private static async Task PostgrestInsertAsync(SupabaseSettings settings, string table, object payload, bool returnRepresentation, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{settings.Url}/rest/v1/{table}");
        request.Headers.Add("apikey", settings.ServiceKey);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ServiceKey);
        request.Headers.Add("Prefer", returnRepresentation ? "return=representation" : "return=minimal");

        var json = JsonSerializer.Serialize(payload);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        var payloadSize = Encoding.UTF8.GetByteCount(json);
        var sw = Stopwatch.StartNew();
        using var response = await SharedHttpClient.SendAsync(request, cancellationToken);
        var elapsed = sw.Elapsed;
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            LogTiming("supabase insert failed", elapsed, $"{table} bytes {payloadSize}");
            throw new Exception($"Supabase insert into '{table}' failed ({(int)response.StatusCode}): {body}");
        }

        LogTiming("supabase insert", elapsed, $"{table} bytes {payloadSize}");
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

        var sw = Stopwatch.StartNew();
        using var response = await SharedHttpClient.SendAsync(request, cancellationToken);
        var elapsed = sw.Elapsed;
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            LogTiming("supabase upload failed", elapsed, $"{normalizedPath} bytes {fileBytes.Length}");
            throw new Exception($"Supabase storage upload failed ({(int)response.StatusCode}): {body}");
        }

        LogTiming("supabase upload", elapsed, $"{normalizedPath} bytes {fileBytes.Length}");
        return normalizedPath;
    }

    private static async Task<string> UploadBytesToSupabaseStorageAsync(
        byte[] content,
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

        request.Content = new ByteArrayContent(content);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);

        var sw = Stopwatch.StartNew();
        using var response = await SharedHttpClient.SendAsync(request, cancellationToken);
        var elapsed = sw.Elapsed;
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            LogTiming("supabase upload failed", elapsed, $"{normalizedPath} bytes {content.Length}");
            throw new Exception($"Supabase storage upload failed ({(int)response.StatusCode}): {body}");
        }

        LogTiming("supabase upload", elapsed, $"{normalizedPath} bytes {content.Length}");
        return normalizedPath;
    }

    private static string NormalizeStoragePath(string path)
    {
        return path.Trim('/');
    }

    private static string GetStorageDirectory(string path)
    {
        var normalized = NormalizeStoragePath(path);
        var lastSlash = normalized.LastIndexOf('/');
        if (lastSlash <= 0)
        {
            return string.Empty;
        }

        return normalized.Substring(0, lastSlash);
    }

    private static async Task<string?> FetchDeckPdfInfoAsync(
        Guid deckId,
        SupabaseSettings settings,
        CancellationToken cancellationToken)
    {
        var query = new Dictionary<string, string?>
        {
            ["select"] = "redacted_pdf_path",
            ["id"] = $"eq.{deckId}",
            ["limit"] = "1"
        };

        var requestUri = QueryHelpers.AddQueryString($"{settings.Url}/rest/v1/{settings.DecksTable}", query);
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Add("apikey", settings.ServiceKey);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ServiceKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var sw = Stopwatch.StartNew();
        using var response = await SharedHttpClient.SendAsync(request, cancellationToken);
        var elapsed = sw.Elapsed;
        if (!response.IsSuccessStatusCode)
        {
            LogTiming("supabase pdf info failed", elapsed, deckId.ToString("n"));
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        LogTiming("supabase pdf info", elapsed, deckId.ToString("n"));
        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
        {
            return null;
        }

        var first = doc.RootElement[0];
        var pdfPath = first.TryGetProperty("redacted_pdf_path", out var pdfPathElement) && pdfPathElement.ValueKind == JsonValueKind.String
            ? pdfPathElement.GetString()
            : null;

        return pdfPath;
    }

    private static async Task<DeckRecord?> FetchDeckRecordAsync(
        Guid deckId,
        SupabaseSettings settings,
        CancellationToken cancellationToken)
    {
        var query = new Dictionary<string, string?>
        {
            ["select"] = "id,deck_name,pptx_path,redacted_pptx_path,redacted_pdf_path,redacted_json_path,industry,deck_type,slide_count",
            ["id"] = $"eq.{deckId}",
            ["limit"] = "1"
        };

        var requestUri = QueryHelpers.AddQueryString($"{settings.Url}/rest/v1/{settings.DecksTable}", query);
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Add("apikey", settings.ServiceKey);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ServiceKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var sw = Stopwatch.StartNew();
        using var response = await SharedHttpClient.SendAsync(request, cancellationToken);
        var elapsed = sw.Elapsed;
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            LogTiming("supabase deck fetch failed", elapsed, deckId.ToString("n"));
            throw new Exception($"Supabase deck fetch failed ({(int)response.StatusCode}): {body}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        LogTiming("supabase deck fetch", elapsed, deckId.ToString("n"));
        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
        {
            return null;
        }

        var first = doc.RootElement[0];

        Guid id = deckId;
        if (first.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String && Guid.TryParse(idElement.GetString(), out var parsedId))
        {
            id = parsedId;
        }

        return new DeckRecord
        {
            Id = id,
            DeckName = first.TryGetProperty("deck_name", out var deckNameElement) && deckNameElement.ValueKind == JsonValueKind.String ? deckNameElement.GetString() : null,
            PptxPath = first.TryGetProperty("pptx_path", out var pptxElement) && pptxElement.ValueKind == JsonValueKind.String ? pptxElement.GetString() : null,
            RedactedPptxPath = first.TryGetProperty("redacted_pptx_path", out var redPptxElement) && redPptxElement.ValueKind == JsonValueKind.String ? redPptxElement.GetString() : null,
            RedactedPdfPath = first.TryGetProperty("redacted_pdf_path", out var redPdfElement) && redPdfElement.ValueKind == JsonValueKind.String ? redPdfElement.GetString() : null,
            RedactedJsonPath = first.TryGetProperty("redacted_json_path", out var redJsonElement) && redJsonElement.ValueKind == JsonValueKind.String ? redJsonElement.GetString() : null,
            Industry = first.TryGetProperty("industry", out var industryElement) && industryElement.ValueKind == JsonValueKind.String ? industryElement.GetString() : null,
            DeckType = first.TryGetProperty("deck_type", out var deckTypeElement) && deckTypeElement.ValueKind == JsonValueKind.String ? deckTypeElement.GetString() : null,
            SlideCount = first.TryGetProperty("slide_count", out var slideCountElement) && slideCountElement.ValueKind == JsonValueKind.Number ? slideCountElement.GetInt32() : (int?)null
        };
    }

    private static async Task<List<RuleActionRecord>> FetchRuleActionsAsync(
        Guid deckId,
        SupabaseSettings settings,
        CancellationToken cancellationToken)
    {
        var query = new Dictionary<string, string?>
        {
            ["select"] = "id,rule_id,deck_id,slide_no,element_key,bbox,original_text,new_text,created_at",
            ["deck_id"] = $"eq.{deckId}",
            ["order"] = "created_at.asc"
        };

        var requestUri = QueryHelpers.AddQueryString($"{settings.Url}/rest/v1/{settings.RuleActionsTable}", query);
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Add("apikey", settings.ServiceKey);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ServiceKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var sw = Stopwatch.StartNew();
        using var response = await SharedHttpClient.SendAsync(request, cancellationToken);
        var elapsed = sw.Elapsed;
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            LogTiming("supabase rule actions failed", elapsed, deckId.ToString("n"));
            throw new Exception($"Supabase rule_actions fetch failed ({(int)response.StatusCode}): {body}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        LogTiming("supabase rule actions", elapsed, deckId.ToString("n"));

        var list = new List<RuleActionRecord>();
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var row in doc.RootElement.EnumerateArray())
        {
            if (!row.TryGetProperty("element_key", out var keyElement) || keyElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var key = keyElement.GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(key)) continue;

            int slideNo = 0;
            if (row.TryGetProperty("slide_no", out var slideElement) && slideElement.ValueKind == JsonValueKind.Number)
            {
                slideElement.TryGetInt32(out slideNo);
            }

            BBox? bbox = null;
            if (row.TryGetProperty("bbox", out var bboxElement) && bboxElement.ValueKind == JsonValueKind.Object)
            {
                bbox = new BBox
                {
                    x = TryGetJsonInt64(bboxElement, "x"),
                    y = TryGetJsonInt64(bboxElement, "y"),
                    w = TryGetJsonInt64(bboxElement, "w"),
                    h = TryGetJsonInt64(bboxElement, "h")
                };
            }

            Guid id = Guid.Empty;
            if (row.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String)
            {
                Guid.TryParse(idElement.GetString(), out id);
            }

            Guid ruleId = Guid.Empty;
            if (row.TryGetProperty("rule_id", out var ruleIdElement) && ruleIdElement.ValueKind == JsonValueKind.String)
            {
                Guid.TryParse(ruleIdElement.GetString(), out ruleId);
            }

            var originalText = row.TryGetProperty("original_text", out var originalElement) && originalElement.ValueKind == JsonValueKind.String
                ? originalElement.GetString()
                : null;
            var newText = row.TryGetProperty("new_text", out var newElement) && newElement.ValueKind == JsonValueKind.String
                ? newElement.GetString()
                : null;

            list.Add(new RuleActionRecord
            {
                Id = id,
                RuleId = ruleId,
                DeckId = deckId,
                SlideNo = slideNo,
                ElementKey = key,
                BBox = bbox,
                OriginalText = originalText,
                NewText = newText
            });
        }

        return list;
    }

    private static long? TryGetJsonInt64(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number)
        {
            if (value.TryGetInt64(out var longValue))
            {
                return longValue;
            }

            if (value.TryGetDouble(out var doubleValue))
            {
                return (long)doubleValue;
            }
        }

        return null;
    }

    private static byte[] ApplyRuleActionsToPptx(
        byte[] originalBytes,
        IReadOnlyList<RuleActionRecord> actions,
        out int appliedCount)
    {
        appliedCount = 0;
        if (actions.Count == 0)
        {
            return originalBytes;
        }

        using var stream = new MemoryStream(originalBytes.Length + 4096);
        stream.Write(originalBytes, 0, originalBytes.Length);
        stream.Position = 0;

        using var document = PresentationDocument.Open(stream, true);

        var presentationPart = document.PresentationPart ?? throw new InvalidOperationException("Invalid PPTX: missing PresentationPart");
        var presentation = presentationPart.Presentation ?? throw new InvalidOperationException("Invalid PPTX: missing Presentation");

        var slideParts = new Dictionary<string, SlidePart>(StringComparer.OrdinalIgnoreCase);
        var slideActions = new Dictionary<string, List<(ElementKeyInfo Info, RuleActionRecord Action)>>(StringComparer.OrdinalIgnoreCase);

        var slideIds = presentation.SlideIdList?.Elements<SlideId>() ?? Enumerable.Empty<SlideId>();
        foreach (var slideId in slideIds)
        {
            if (slideId.RelationshipId?.Value is not string relId)
            {
                continue;
            }

            if (presentationPart.GetPartById(relId) is SlidePart slidePart)
            {
                var slideKey = slidePart.Uri.OriginalString.TrimStart('/');
                slideParts[slideKey] = slidePart;
            }
        }

        foreach (var action in actions)
        {
            if (string.IsNullOrWhiteSpace(action.ElementKey))
            {
                continue;
            }

            if (!TryParseElementKey(action.ElementKey, out var info))
            {
                continue;
            }

            if (!slideActions.TryGetValue(info.SlidePath, out var list))
            {
                list = new List<(ElementKeyInfo, RuleActionRecord)>();
                slideActions[info.SlidePath] = list;
            }

            list.Add((info, action));
        }

        var modifiedSlides = new HashSet<SlidePart>();

        foreach (var kvp in slideActions)
        {
            if (!slideParts.TryGetValue(kvp.Key, out var slidePart))
            {
                continue;
            }

            var slide = slidePart.Slide;
            if (slide == null)
            {
                continue;
            }

            bool slideModified = false;
            foreach (var (info, action) in kvp.Value)
            {
                if (!string.Equals(info.ElementType, "textbox", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var shape = slide.Descendants<P.Shape>()
                    .FirstOrDefault(s => s.NonVisualShapeProperties?.NonVisualDrawingProperties?.Id?.Value == info.ElementId);

                if (shape == null)
                {
                    continue;
                }

                if (TryApplyActionToShape(shape, action))
                {
                    appliedCount++;
                    slideModified = true;
                }
            }

            if (slideModified)
            {
                slide.Save();
                modifiedSlides.Add(slidePart);
            }
        }

        document.Save();
        stream.Position = 0;
        return stream.ToArray();
    }

    private static bool TryApplyActionToShape(P.Shape shape, RuleActionRecord action)
    {
        if (shape.TextBody == null)
        {
            return false;
        }

        var original = (action.OriginalText ?? string.Empty).Replace("\r\n", "\n");
        var updated = (action.NewText ?? string.Empty).Replace("\r\n", "\n");
        if (string.IsNullOrWhiteSpace(original))
        {
            return false;
        }

        foreach (var paragraph in shape.TextBody.Elements<A.Paragraph>())
        {
            var paragraphText = ReadParagraphText(paragraph);
            if (AreEquivalentText(paragraphText, original))
            {
                RewriteParagraph(paragraph, updated);
                return true;
            }
        }

        return false;
    }

    private static string ReadParagraphText(A.Paragraph paragraph)
    {
        var sb = new StringBuilder();
        foreach (var child in paragraph.ChildElements)
        {
            switch (child)
            {
                case A.Run run when run.Text != null:
                    sb.Append(run.Text.Text);
                    break;
                case A.Break:
                    sb.Append('\n');
                    break;
                case A.Field field when field.Text != null:
                    sb.Append(field.Text.Text);
                    break;
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static void RewriteParagraph(A.Paragraph paragraph, string replacement)
    {
        replacement ??= string.Empty;
        var normalized = replacement.Replace("\r\n", "\n");
        var segments = normalized.Split('\n');
        if (segments.Length == 0)
        {
            segments = new[] { string.Empty };
        }

        var existingRuns = paragraph.Elements<A.Run>().ToList();
        var baseRun = existingRuns.FirstOrDefault();
        if (baseRun == null)
        {
            baseRun = new A.Run();
            paragraph.PrependChild(baseRun);
        }

        var templateRun = (A.Run)baseRun.CloneNode(true);
        templateRun.RemoveAllChildren<A.Text>();

        foreach (var run in existingRuns.Skip(1))
        {
            run.Remove();
        }

        foreach (var br in paragraph.Elements<A.Break>().ToList())
        {
            br.Remove();
        }

        baseRun.RemoveAllChildren<A.Text>();
        baseRun.AppendChild(new A.Text(segments[0]));

        OpenXmlElement insertAfter = baseRun;
        for (int i = 1; i < segments.Length; i++)
        {
            var br = new A.Break();
            paragraph.InsertAfter(br, insertAfter);
            insertAfter = br;

            var newRun = (A.Run)templateRun.CloneNode(true);
            newRun.AppendChild(new A.Text(segments[i]));
            paragraph.InsertAfter(newRun, insertAfter);
            insertAfter = newRun;
        }
    }

    private static bool AreEquivalentText(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.Ordinal))
        {
            return true;
        }

        if (string.Equals(a.Trim(), b.Trim(), StringComparison.Ordinal))
        {
            return true;
        }

        return string.Equals(NormalizeWhitespace(a), NormalizeWhitespace(b), StringComparison.Ordinal);
    }

    private static string NormalizeWhitespace(string value)
    {
        var sb = new StringBuilder(value.Length);
        bool inWhitespace = false;
        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!inWhitespace)
                {
                    sb.Append(' ');
                    inWhitespace = true;
                }
            }
            else
            {
                sb.Append(ch);
                inWhitespace = false;
            }
        }

        return sb.ToString().Trim();
    }

    private static bool TryParseElementKey(string elementKey, out ElementKeyInfo info)
    {
        info = new ElementKeyInfo(string.Empty, string.Empty, 0);
        if (string.IsNullOrWhiteSpace(elementKey))
        {
            return false;
        }

        var parts = elementKey.Split('#');
        if (parts.Length != 2)
        {
            return false;
        }

        var slidePath = parts[0].Trim();
        var typeAndId = parts[1].Split(':');
        if (typeAndId.Length != 2)
        {
            return false;
        }

        if (!uint.TryParse(typeAndId[1], out var elementId))
        {
            return false;
        }

        info = new ElementKeyInfo(slidePath, typeAndId[0], elementId);
        return true;
    }

    private static async Task<string?> CreateSupabaseSignedUrlAsync(
        SupabaseSettings settings,
        string objectPath,
        int expiresInSeconds,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeStoragePath(objectPath);
        var bucketPrefix = settings.StorageBucket.Trim('/');
        if (!string.IsNullOrEmpty(bucketPrefix) && normalized.StartsWith(bucketPrefix + "/", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized.Substring(bucketPrefix.Length + 1);
        }
        var requestUri = $"{settings.Url}/storage/v1/object/sign/{settings.StorageBucket}/{normalized}";

        var payload = JsonSerializer.Serialize(new { expiresIn = expiresInSeconds });
        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri);
        request.Headers.Add("apikey", settings.ServiceKey);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ServiceKey);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        var sw = Stopwatch.StartNew();
        using var response = await SharedHttpClient.SendAsync(request, cancellationToken);
        var elapsed = sw.Elapsed;
        if (!response.IsSuccessStatusCode)
        {
            LogTiming("supabase signed url failed", elapsed, objectPath);
            return null;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("signedURL", out var urlElement) || urlElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var signedUrl = urlElement.GetString();
        if (string.IsNullOrWhiteSpace(signedUrl))
        {
            return null;
        }

        LogTiming("supabase signed url", elapsed, objectPath);
        if (Uri.TryCreate(signedUrl, UriKind.Absolute, out var absolute))
        {
            var path = absolute.AbsolutePath;
            var query = absolute.Query.TrimStart('?');

            if (!path.StartsWith("/storage/v1/", StringComparison.OrdinalIgnoreCase))
            {
                path = $"/storage/v1/{path.TrimStart('/')}";
            }

            var builder = new UriBuilder(settings.Url)
            {
                Path = path,
                Query = query,
            };
            return builder.Uri.ToString();
        }

        var raw = signedUrl.Trim();
        string rawPath = raw;
        string rawQuery = string.Empty;
        var questionIndex = raw.IndexOf('?');
        if (questionIndex >= 0)
        {
            rawPath = raw[..questionIndex];
            rawQuery = raw[(questionIndex + 1)..];
        }

        if (!rawPath.StartsWith("/storage/v1/", StringComparison.OrdinalIgnoreCase))
        {
            rawPath = $"/storage/v1/{rawPath.TrimStart('/')}";
        }

        var baseBuilder = new UriBuilder(settings.Url)
        {
            Path = rawPath,
            Query = rawQuery,
        };
        return baseBuilder.Uri.ToString();
    }

    private sealed record ElementKeyInfo(string SlidePath, string ElementType, uint ElementId);

    private sealed class DeckRecord
    {
        public Guid Id { get; init; }
        public string? DeckName { get; init; }
        public string? PptxPath { get; init; }
        public string? RedactedPptxPath { get; init; }
        public string? RedactedPdfPath { get; init; }
        public string? RedactedJsonPath { get; init; }
        public string? Industry { get; init; }
        public string? DeckType { get; init; }
        public int? SlideCount { get; init; }
    }

    private sealed class RuleActionRecord
    {
        public Guid Id { get; init; }
        public Guid RuleId { get; init; }
        public Guid DeckId { get; init; }
        public int SlideNo { get; init; }
        public string ElementKey { get; init; } = string.Empty;
        public BBox? BBox { get; init; }
        public string? OriginalText { get; init; }
        public string? NewText { get; init; }
    }

    private static async Task<byte[]?> DownloadSupabaseObjectAsync(
        SupabaseSettings settings,
        string storagePath,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeStoragePath(storagePath);
        var requestUri = $"{settings.Url}/storage/v1/object/{settings.StorageBucket}/{normalized}";

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Add("apikey", settings.ServiceKey);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ServiceKey);

        var sw = Stopwatch.StartNew();
        using var response = await SharedHttpClient.SendAsync(request, cancellationToken);
        var elapsed = sw.Elapsed;
        if (!response.IsSuccessStatusCode)
        {
            LogTiming("supabase download failed", elapsed, normalized);
            return null;
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        LogTiming("supabase download", elapsed, $"{normalized} bytes {bytes.Length}");
        return bytes;
    }

    private static byte[] RenderSlideToPng(
        byte[] pdfBytes,
        int slideNumber,
        int targetWidth = 960)
    {
        var pageIndex = slideNumber - 1;
        if (pageIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(slideNumber));
        }

        var widthHint = targetWidth > 0 ? targetWidth : 1024;
        var heightHintCandidate = Math.Max(1, (int)Math.Round(widthHint * (3.0 / 4.0)));
        var heightHint = Math.Max(widthHint + 1, heightHintCandidate);

        using var docReader = DocLib.Instance.GetDocReader(pdfBytes, new PageDimensions(widthHint, heightHint));
        var pageCount = docReader.GetPageCount();
        if (pageIndex >= pageCount)
        {
            throw new ArgumentOutOfRangeException(nameof(slideNumber), $"Slide {slideNumber} does not exist (deck has {pageCount} slides).");
        }

        using var pageReader = docReader.GetPageReader(pageIndex);
        var rawBytes = pageReader.GetImage();
        var pageWidth = pageReader.GetPageWidth();
        var pageHeight = pageReader.GetPageHeight();
        var width = Math.Max(1, (int)Math.Round(Convert.ToDouble(pageWidth), MidpointRounding.AwayFromZero));
        var height = Math.Max(1, (int)Math.Round(Convert.ToDouble(pageHeight), MidpointRounding.AwayFromZero));

        using var image = Image.LoadPixelData<Bgra32>(rawBytes, width, height);

        if (targetWidth > 0 && image.Width > targetWidth)
        {
            var ratio = targetWidth / (double)image.Width;
            var newHeight = Math.Max(1, (int)Math.Round(image.Height * ratio));
            image.Mutate(ctx => ctx.Resize(targetWidth, newHeight));
        }

        using var output = new MemoryStream();
        image.Save(output, new PngEncoder
        {
            CompressionLevel = PngCompressionLevel.Level6
        });

        return output.ToArray();
    }

    private static async Task<Dictionary<int, List<string>>> GenerateImageCaptionsAsync(
        DeckDto deck,
        string pptxPath,
        Guid deckId,
        SupabaseSettings settings,
        CancellationToken cancellationToken)
    {
        var captionsBySlide = new Dictionary<int, List<string>>();
        var sw = Stopwatch.StartNew();
        int pictureCount = 0;
        int captionRequests = 0;

        if (string.IsNullOrWhiteSpace(settings.OpenAiKey))
        {
            LogTiming("image captions skipped", TimeSpan.Zero, $"{deckId:n} (no OpenAI key)");
            return captionsBySlide;
        }

        using var archive = ZipFile.OpenRead(pptxPath);
        var captionCache = new Dictionary<string, string>();

        foreach (var slide in deck.slides)
        {
            foreach (var picture in slide.elements.OfType<PictureDto>())
            {
                pictureCount++;
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

                captionRequests++;
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

        var totalCaptions = captionsBySlide.Sum(kvp => kvp.Value.Count);
        LogTiming("image captions", sw.Elapsed, $"{deckId:n} pictures {pictureCount} requests {captionRequests} captions {totalCaptions}");
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
        {
            LogTiming("table summaries skipped", TimeSpan.Zero, "no OpenAI key");
            return;
        }

        var sw = Stopwatch.StartNew();
        int tableCount = 0;
        int summaryRequests = 0;
        int summariesCreated = 0;

        foreach (var table in deck.slides.SelectMany(s => s.elements).OfType<TableDto>())
        {
            tableCount++;
            var tableText = FormatTableForSummary(table);
            if (string.IsNullOrWhiteSpace(tableText))
                continue;

            summaryRequests++;
            var summary = await RequestTableSummaryAsync(tableText, settings, cancellationToken);
            if (!string.IsNullOrWhiteSpace(summary))
            {
                table.summary = summary.Trim();
                summariesCreated++;
            }
        }

        LogTiming("table summaries", sw.Elapsed, $"tables {tableCount} requests {summaryRequests} summaries {summariesCreated}");
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

        var sw = Stopwatch.StartNew();
        using var response = await SharedHttpClient.SendAsync(request, cancellationToken);
        var elapsed = sw.Elapsed;
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            Console.Error.WriteLine($"Vision caption request failed ({(int)response.StatusCode}): {error}");
            LogTiming("vision caption failed", elapsed, $"bytes {imageBytes.Length}");
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        LogTiming("vision caption", elapsed, $"bytes {imageBytes.Length}");

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

        var sw = Stopwatch.StartNew();
        using var response = await SharedHttpClient.SendAsync(request, cancellationToken);
        var elapsed = sw.Elapsed;
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            Console.Error.WriteLine($"Table summary request failed ({(int)response.StatusCode}): {error}");
            LogTiming("table summary failed", elapsed, $"chars {tableText.Length}");
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        LogTiming("table summary", elapsed, $"chars {tableText.Length}");

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

        var sw = Stopwatch.StartNew();
        using var proc = Process.Start(psi)!;
        var stdOut = await proc.StandardOutput.ReadToEndAsync();
        var stdErr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        var elapsed = sw.Elapsed;

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
            LogTiming("libreoffice convert failed", elapsed, Path.GetFileName(inputPath));
            throw new Exception($"LibreOffice conversion failed (code {proc.ExitCode}). stderr: {stdErr}. stdout: {stdOut}");
        }

        LogTiming("libreoffice convert", elapsed, Path.GetFileName(inputPath));
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

        var sw = Stopwatch.StartNew();
        using var response = await SharedHttpClient.SendAsync(request, cancellationToken);
        var elapsed = sw.Elapsed;
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            Console.Error.WriteLine($"Embedding request failed ({(int)response.StatusCode}): {body}");
            LogTiming("embedding failed", elapsed, $"chars {input.Length}");
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var embeddingResponse = await JsonSerializer.DeserializeAsync<OpenAiEmbeddingResponse>(stream, cancellationToken: cancellationToken);
        var vector = embeddingResponse?.Data?.FirstOrDefault()?.Embedding;
        LogTiming("embedding", elapsed, $"chars {input.Length}");
        return vector?.ToArray();
    }

    private static string GetDeckCacheKey(Guid deckId) => $"deck:pptx:{deckId:n}";

    private static string ConvertToBase64WithTiming(byte[] data)
    {
        var sw = Stopwatch.StartNew();
        var base64 = Convert.ToBase64String(data);
        LogTiming("pdf base64 encode", sw.Elapsed, $"bytes {data.Length}");
        return base64;
    }

    private static void LogTiming(string operation, TimeSpan elapsed, string? detail = null)
    {
        var suffix = string.IsNullOrWhiteSpace(detail) ? string.Empty : $" :: {detail}";
        Console.WriteLine($"[timing] {operation}{suffix} took {elapsed.TotalMilliseconds:F0} ms");
    }

    private sealed record InitialDeckArtifacts(
        string BaseName,
        string PptxStoragePath,
        string PdfFileName,
        byte[] PdfBytes,
        Dictionary<int, List<string>> ImageCaptions);

    private sealed record RedactedArtifacts(
        string PptxPath,
        string PdfPath,
        string JsonPath,
        Dictionary<int, List<string>> ImageCaptions);

    private sealed record SupabaseSettings(
        string Url,
        string ServiceKey,
        string DecksTable,
        string SlidesTable,
        string RulesTable,
        string RuleActionsTable,
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
