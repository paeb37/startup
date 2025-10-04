namespace Dexter.WebApi.Decks.Services;

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Dexter.WebApi.Common.Logging;
using Dexter.WebApi.Decks.Models;
using Dexter.WebApi.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using static Dexter.WebApi.Program;
using Docnet.Core;
using Docnet.Core.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Formats.Png;

internal sealed class DeckWorkflowService
{
    private static readonly TimeSpan CacheExpiration = TimeSpan.FromMinutes(5);

    private readonly IMemoryCache _cache;
    private readonly IConfiguration _configuration;
    private readonly DeckExtractor _extractor;
    private readonly DeckRedactionService _redaction;
    private readonly SupabaseClient _supabase;
    private readonly ConverterClient _converter;
    private readonly IHttpClientFactory _httpClientFactory;

    public DeckWorkflowService(
        IMemoryCache cache,
        IConfiguration configuration,
        DeckExtractor extractor,
        DeckRedactionService redaction,
        SupabaseClient supabase,
        ConverterClient converter,
        IHttpClientFactory httpClientFactory)
    {
        _cache = cache;
        _configuration = configuration;
        _extractor = extractor;
            _redaction = redaction;
            _supabase = supabase;
            _converter = converter;
            _httpClientFactory = httpClientFactory;
    }

    internal static JsonSerializerOptions CreateDeckJsonOptions()
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

    public async Task<IResult> HandleUploadAsync(HttpRequest req, CancellationToken cancellationToken)
    {
        if (!req.HasFormContentType)
        {
            return Results.BadRequest(new { error = "multipart/form-data required" });
        }

        var form = await req.ReadFormAsync(cancellationToken);
        var file = form.Files.GetFile("file");
        if (file is null || file.Length == 0)
        {
            return Results.BadRequest(new { error = "file missing" });
        }

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
            await file.CopyToAsync(ms, cancellationToken);
            ms.Position = 0;

            var extractSw = Stopwatch.StartNew();
            var deck = _extractor.Extract(ms, file.FileName);
                OperationTimer.LogTiming("extract deck", extractSw.Elapsed, $"slides {deck.slideCount}");

            var jsonOptions = CreateDeckJsonOptions();

            var supabaseSettings = _supabase.GetSettings();
            if (supabaseSettings is null)
            {
                return Results.Problem(
                    detail: "Supabase storage is not configured. Set SUPABASE_URL, SUPABASE_SERVICE_ROLE_KEY, and SUPABASE_STORAGE_BUCKET.",
                    statusCode: 500);
            }

            deckId = Guid.NewGuid();

            var artifactsSw = Stopwatch.StartNew();
            var artifacts = await _supabase.GenerateInitialArtifactsAsync(
                ms,
                deck,
                file.FileName,
                deckId,
                supabaseSettings,
                cancellationToken);
                OperationTimer.LogTiming("initial artifacts", artifactsSw.Elapsed, deckId.ToString("n"));

            var originalBytes = ms.ToArray();
            _cache.Set(GetDeckCacheKey(deckId), originalBytes, new MemoryCacheEntryOptions
            {
                SlidingExpiration = CacheExpiration
            });

            var pdfInfo = new
            {
                fileName = artifacts.PdfFileName,
                base64 = ConvertToBase64WithTiming(artifacts.PdfBytes)
            };

            try
            {
                var insertSw = Stopwatch.StartNew();
                await _supabase.InsertDeckRecordAsync(
                    deck,
                    deckId,
                    artifacts,
                    industry,
                    deckType,
                    supabaseSettings,
                    cancellationToken);
                    OperationTimer.LogTiming("insert deck record", insertSw.Elapsed, deckId.ToString("n"));
            }
            catch (Exception supabaseEx)
            {
                Console.Error.WriteLine($"Supabase persistence failed: {supabaseEx.Message}");
            }

            object? ruleInfo = null;

            if (!string.IsNullOrWhiteSpace(instructions))
            {
                var semanticUrl = _configuration["SEMANTIC_URL"]
                    ?? Environment.GetEnvironmentVariable("SEMANTIC_URL")
                    ?? "http://localhost:8000";

                var redactUrl = semanticUrl.TrimEnd('/') + "/redact";

                using var client = new HttpClient();

                var payload = new
                {
                    deckId,
                    instructions,
                    deck
                };

                var payloadJson = JsonSerializer.Serialize(payload, jsonOptions);
                using var content = new StringContent(payloadJson, Encoding.UTF8, "application/json");

                try
                {
                    var semanticSw = Stopwatch.StartNew();
                    var response = await client.PostAsync(redactUrl, content, cancellationToken);
                    var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                        OperationTimer.LogTiming("semantic redact call", semanticSw.Elapsed, deckId.ToString("n"));

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

            OperationTimer.LogTiming("upload handler", requestStopwatch.Elapsed, deckId.ToString("n"));
            return Results.Text(JsonSerializer.Serialize(resultPayload, jsonOptions), "application/json");
        }
        catch (Exception ex)
        {
            OperationTimer.LogTiming("upload handler failed", requestStopwatch.Elapsed, deckId == Guid.Empty ? "no-deck" : deckId.ToString("n"));
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    public async Task<IResult> GetDecksAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        var settings = _supabase.GetSettings();
        if (settings is null)
        {
            return Results.Problem("Supabase storage is not configured", statusCode: 500);
        }

        var limit = 6;
        if (request.Query.TryGetValue("limit", out var limitValues)
            && int.TryParse(limitValues.FirstOrDefault(), out var parsedLimit))
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

        var httpClient = _httpClientFactory.CreateClient();
        using var response = await httpClient.SendAsync(httpRequest, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return Results.Problem(
                detail: payload,
                statusCode: (int)response.StatusCode,
                title: $"Supabase decks query failed ({(int)response.StatusCode})");
        }

        return Results.Content(payload, "application/json");
    }

    public async Task<IResult> PreviewDeckAsync(Guid deckId, PreviewRequest? payload, CancellationToken cancellationToken)
    {
        if (payload is null || payload.Slide < 1)
        {
            return Results.BadRequest(new { error = "slide must be >= 1" });
        }

        var settings = _supabase.GetSettings();
        if (settings is null)
        {
            return Results.Problem(
                detail: "Supabase storage is not configured. Set SUPABASE_URL, SUPABASE_SERVICE_ROLE_KEY, and SUPABASE_STORAGE_BUCKET.",
                statusCode: 500);
        }

        var cacheKey = GetDeckCacheKey(deckId);
        if (!_cache.TryGetValue(cacheKey, out byte[]? originalPptx))
        {
            DeckRecord? deckRecord;
            try
            {
                deckRecord = await _supabase.FetchDeckRecordAsync(deckId, settings, cancellationToken);
            }
            catch (Exception ex)
            {
                return Results.Problem($"Failed to load deck metadata: {ex.Message}", statusCode: 500);
            }

            if (deckRecord is null || string.IsNullOrWhiteSpace(deckRecord.PptxPath))
            {
                return Results.NotFound(new { error = "Deck PPTX not found" });
            }

            originalPptx = await _supabase.DownloadObjectAsync(settings, deckRecord.PptxPath!, cancellationToken);
            if (originalPptx != null && originalPptx.Length > 0)
            {
                _cache.Set(cacheKey, originalPptx, new MemoryCacheEntryOptions { SlidingExpiration = CacheExpiration });
            }
        }
        else
        {
            OperationTimer.LogTiming("deck cache hit", TimeSpan.Zero, deckId.ToString("n"));
        }

        if (originalPptx is null || originalPptx.Length == 0)
        {
            return Results.NotFound(new { error = "Unable to load deck PPTX" });
        }

        List<RuleActionRecord> ruleActions;
        try
        {
            ruleActions = await _supabase.FetchRuleActionsAsync(deckId, settings, cancellationToken, payload.Slide);
        }
        catch (Exception ex)
        {
            return Results.Problem($"Failed to load rule actions: {ex.Message}", statusCode: 500);
        }

        byte[] workingBytes = originalPptx;
        if (ruleActions.Count > 0)
        {
            try
            {
                workingBytes = _redaction.ApplyRuleActionsToPptx(originalPptx, ruleActions, out _);
            }
            catch (Exception ex)
            {
                var detail = ex is AggregateException agg
                    ? string.Join("; ", agg.InnerExceptions.Select(e => e.Message))
                    : ex.Message;
                return Results.Json(new { error = "preview_apply_failed", message = detail }, statusCode: 500);
            }
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), "dexter-preview", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var baseName = $"{deckId:n}-preview";
            var pptxPath = Path.Combine(tempRoot, baseName + ".pptx");
            await File.WriteAllBytesAsync(pptxPath, workingBytes, cancellationToken);

            await _converter.ConvertPptxToPdfAsync(pptxPath, tempRoot, cancellationToken, payload.Slide);

            var pdfPath = Path.Combine(tempRoot, baseName + ".pdf");
            if (!File.Exists(pdfPath))
            {
                var generated = Directory.GetFiles(tempRoot, "*.pdf").FirstOrDefault();
                if (generated != null)
                {
                    pdfPath = generated;
                }
            }

            if (!File.Exists(pdfPath))
            {
                return Results.StatusCode(500);
            }

            var pdfBytes = await File.ReadAllBytesAsync(pdfPath, cancellationToken);
            return Results.File(pdfBytes, "application/pdf");
        }
        finally
        {
            try
            {
                Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
                // ignore cleanup failures
            }
        }
    }

    public async Task<IResult> RedactDeckAsync(Guid deckId, CancellationToken cancellationToken)
    {
        var handlerSw = Stopwatch.StartNew();
        var handlerStatus = "redact handler failed";
        var handlerDetail = deckId.ToString("n");

        try
        {
            var settings = _supabase.GetSettings();
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
                deckRecord = await _supabase.FetchDeckRecordAsync(deckId, settings, cancellationToken);
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
            if (!_cache.TryGetValue(cacheKey, out byte[]? originalPptx))
            {
                originalPptx = await _supabase.DownloadObjectAsync(settings, deckRecord.PptxPath!, cancellationToken);
                if (originalPptx != null && originalPptx.Length > 0)
                {
                    _cache.Set(cacheKey, originalPptx, new MemoryCacheEntryOptions { SlidingExpiration = CacheExpiration });
                }
            }
            else
            {
                OperationTimer.LogTiming("deck cache hit", TimeSpan.Zero, deckId.ToString("n"));
            }

            if (originalPptx is null || originalPptx.Length == 0)
            {
                handlerDetail = "download-missing";
                return Results.NotFound(new { error = "Unable to download original deck" });
            }

            List<RuleActionRecord> ruleActions;
            try
            {
                ruleActions = await _supabase.FetchRuleActionsAsync(deckId, settings, cancellationToken);
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
                    redactedBytes = _redaction.ApplyRuleActionsToPptx(originalPptx, ruleActions, out appliedCount);
                    OperationTimer.LogTiming("apply rule actions", applySw.Elapsed, $"{deckId:n} actions {ruleActions.Count}");
                }
                catch (Exception ex)
                {
                    var detail = ex is AggregateException agg
                        ? string.Join("; ", agg.InnerExceptions.Select(e => e.Message))
                        : ex.Message;
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

            var originalPath = _supabase.NormalizeStoragePath(deckRecord.PptxPath!);
            var originalFileName = Path.GetFileNameWithoutExtension(originalPath);
            var redactedBaseName = string.IsNullOrWhiteSpace(originalFileName)
                ? $"{deckId:n}-redacted"
                : $"{originalFileName}-redacted";

            var tempRoot = Path.Combine(Path.GetTempPath(), "dexter-redacted", Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(tempRoot);

            try
            {
                var redactedLocalPptx = Path.Combine(tempRoot, $"{redactedBaseName}.pptx");
                await File.WriteAllBytesAsync(redactedLocalPptx, redactedBytes, cancellationToken);

                await using var redactedStream = new MemoryStream(redactedBytes, writable: false);
                var extractSw = Stopwatch.StartNew();
                var redactedDeck = _extractor.Extract(redactedStream, Path.GetFileName(deckRecord.PptxPath) ?? $"{redactedBaseName}.pptx");
                OperationTimer.LogTiming("extract deck (redacted)", extractSw.Elapsed, $"{deckId:n} slides {redactedDeck.slideCount}");

                var persistSw = Stopwatch.StartNew();
                var redactedArtifacts = await _supabase.PersistRedactedArtifactsAsync(
                    redactedLocalPptx,
                    redactedDeck,
                    redactedBaseName,
                    deckId,
                    settings,
                    jsonOptions,
                    cancellationToken);
                OperationTimer.LogTiming("persist redacted artifacts (handler)", persistSw.Elapsed, deckId.ToString("n"));

                try
                {
                    await _supabase.DeleteSlidesForDeckAsync(deckId, settings, cancellationToken);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Slide cleanup failed: {ex.Message}");
                }

                await _supabase.ReplaceSlideEmbeddingsAsync(
                    redactedDeck,
                    deckId,
                    redactedArtifacts.ImageCaptions,
                    settings,
                    cancellationToken);

                try
                {
                    var updateSw = Stopwatch.StartNew();
                    await _supabase.UpdateDeckWithRedactedArtifactsAsync(
                        deckId,
                        redactedArtifacts.PptxPath,
                        redactedArtifacts.PdfPath,
                        redactedArtifacts.JsonPath,
                        redactedDeck.slideCount,
                        settings,
                        cancellationToken);
                    OperationTimer.LogTiming("update deck record", updateSw.Elapsed, deckId.ToString("n"));
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
                    signedUrl = await _supabase.CreateSignedUrlAsync(settings, redactedArtifacts.PptxPath, 3600, cancellationToken);
                    if (signedUrl != null)
                    {
                        OperationTimer.LogTiming("create signed url", signedSw.Elapsed, deckId.ToString("n"));
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
                TryDeleteDirectory(tempRoot);
            }
        }
        finally
        {
            OperationTimer.LogTiming(handlerStatus, handlerSw.Elapsed, handlerDetail);
        }
    }

    public async Task<IResult> DownloadDeckAsync(Guid deckId, HttpRequest request, CancellationToken cancellationToken)
    {
        var settings = _supabase.GetSettings();
        if (settings is null)
        {
            return Results.Problem("Supabase storage is not configured", statusCode: 500);
        }

        DeckRecord? deckRecord;
        try
        {
            deckRecord = await _supabase.FetchDeckRecordAsync(deckId, settings, cancellationToken);
        }
        catch (Exception ex)
        {
            return Results.Problem($"Failed to load deck metadata: {ex.Message}", statusCode: 500);
        }

        if (deckRecord is null)
        {
            return Results.NotFound(new { error = "deck_not_found" });
        }

        var variant = request.Query.TryGetValue("variant", out var variantValues)
            ? variantValues.FirstOrDefault()
            : null;

        string? selectedPath = variant switch
        {
            "redacted" or "pptx" => deckRecord.RedactedPptxPath ?? deckRecord.PptxPath,
            "pdf" => deckRecord.RedactedPdfPath,
            "json" => deckRecord.RedactedJsonPath,
            _ => deckRecord.PptxPath
        };

        var resolvedVariant = variant?.ToLowerInvariant() switch
        {
            "redacted" => "pptx",
            "pptx" => "pptx",
            "pdf" => "pdf",
            "json" => "json",
            _ => "pptx"
        };

        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            return Results.NotFound(new { error = "variant_not_available", variant = resolvedVariant });
        }

        byte[]? fileBytes;
        try
        {
            fileBytes = await _supabase.DownloadObjectAsync(settings, selectedPath, cancellationToken);
        }
        catch (Exception ex)
        {
            return Results.Problem($"Failed to download deck asset: {ex.Message}", statusCode: 500);
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
    }

    public async Task<IResult> GetSlideAsync(Guid deckId, int slideNo, HttpRequest request, CancellationToken cancellationToken)
    {
        if (slideNo < 1)
        {
            return Results.BadRequest(new { error = "slideNo must be >= 1" });
        }

        var settings = _supabase.GetSettings();
        if (settings is null)
        {
            return Results.Problem("Supabase storage is not configured", statusCode: 500);
        }

        var targetWidth = 960;
        if (request.Query.TryGetValue("width", out var widthValues)
            && int.TryParse(widthValues.FirstOrDefault(), out var parsedWidth)
            && parsedWidth > 0)
        {
            targetWidth = Math.Clamp(parsedWidth, 120, 2048);
        }

        var pdfPath = await _supabase.FetchDeckPdfInfoAsync(deckId, settings, cancellationToken);
        if (string.IsNullOrWhiteSpace(pdfPath))
        {
            return Results.NotFound(new { error = "Deck PDF not found" });
        }

        var pdfBytes = await _supabase.DownloadObjectAsync(settings, pdfPath!, cancellationToken);
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
    }

    internal static byte[] RenderSlideToPng(
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

    public async Task<IResult> RenderAsync(HttpRequest req, CancellationToken cancellationToken)
    {
        if (!req.HasFormContentType)
        {
            return Results.BadRequest(new { error = "multipart/form-data required" });
        }

        var form = await req.ReadFormAsync(cancellationToken);
        var file = form.Files.GetFile("file");
        if (file is null || file.Length == 0)
        {
            return Results.BadRequest(new { error = "file missing" });
        }

        var tmpDir = Path.Combine(Path.GetTempPath(), "dexter-preview", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tmpDir);
        var inPath = Path.Combine(tmpDir, Path.GetFileName(file.FileName));
        var outPdf = Path.Combine(tmpDir, Path.GetFileNameWithoutExtension(file.FileName) + ".pdf");

        try
        {
            await using (var fs = File.Create(inPath))
            {
                await file.CopyToAsync(fs, cancellationToken);
            }

            await _converter.ConvertPptxToPdfAsync(inPath, tmpDir, cancellationToken);

            if (!File.Exists(outPdf))
            {
                return Results.StatusCode(500);
            }

            var pdfBytes = await File.ReadAllBytesAsync(outPdf, cancellationToken);
            var cd = new System.Net.Mime.ContentDisposition
            {
                Inline = true,
                FileName = Path.GetFileName(outPdf)
            };

            var headers = new HeaderDictionary
            {
                ["Content-Disposition"] = cd.ToString()
            };

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
            try
            {
                Directory.Delete(tmpDir, recursive: true);
            }
            catch
            {
                // ignore cleanup failures
            }
        }
    }

    private static string GetDeckCacheKey(Guid deckId)
        => $"deck:pptx:{deckId:n}";

    private static string ConvertToBase64WithTiming(byte[] data)
    {
        var sw = Stopwatch.StartNew();
        var base64 = Convert.ToBase64String(data);
        OperationTimer.LogTiming("pdf base64 encode", sw.Elapsed, $"bytes {data.Length}");
        return base64;
    }

    private static void TryDeleteDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // best effort cleanup
        }
    }
}
