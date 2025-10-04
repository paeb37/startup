namespace Dexter.WebApi.Infrastructure;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Dexter.WebApi.Common.Logging;
using Dexter.WebApi.Decks.Models;
using Dexter.WebApi.Decks.Services;
using Dexter.WebApi.Infrastructure.Options;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

internal sealed class SupabaseClient
{
    private readonly HttpClient _httpClient;
    private readonly OpenAiClient _openAi;
    private readonly ConverterClient _converter;
    private readonly IOptionsMonitor<SupabaseOptions> _supabaseOptions;
    private readonly IOptionsMonitor<OpenAiOptions> _openAiOptions;

    public SupabaseClient(
        HttpClient httpClient,
        OpenAiClient openAi,
        ConverterClient converter,
        IOptionsMonitor<SupabaseOptions> supabaseOptions,
        IOptionsMonitor<OpenAiOptions> openAiOptions)
    {
        _httpClient = httpClient;
        _openAi = openAi;
        _converter = converter;
        _supabaseOptions = supabaseOptions;
        _openAiOptions = openAiOptions;
    }

    public SupabaseSettings? GetSettings()
    {
        var supabase = _supabaseOptions.CurrentValue;
        if (!supabase.IsConfigured())
        {
            return null;
        }

        var openAi = _openAiOptions.CurrentValue;

        return new SupabaseSettings(
            Url: (supabase.Url ?? string.Empty).TrimEnd('/'),
            ServiceKey: supabase.ServiceKey!,
            DecksTable: supabase.DecksTable,
            SlidesTable: supabase.SlidesTable,
            RulesTable: supabase.RulesTable,
            RuleActionsTable: supabase.RuleActionsTable,
            OpenAiKey: openAi.ApiKey,
            EmbeddingModel: openAi.EmbeddingModel,
            StorageBucket: supabase.StorageBucket,
            StoragePathPrefix: supabase.StoragePathPrefix,
            VisionModel: openAi.VisionModel);
    }

    public async Task<InitialDeckArtifacts> GenerateInitialArtifactsAsync(
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

            await _converter.ConvertPptxToPdfAsync(pptxPath, tempRoot, cancellationToken);

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
            OperationTimer.LogTiming("read pdf bytes", pdfReadSw.Elapsed, $"bytes {pdfBytes.Length}");

            var imageCaptions = await _openAi.GenerateImageCaptionsAsync(
                deck,
                pptxPath,
                deckId,
                settings,
                cancellationToken);

            await _openAi.GenerateTableSummariesAsync(
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

            OperationTimer.LogTiming("initial artifacts pipeline", totalSw.Elapsed, deckId.ToString("n"));
            return result;
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    public async Task InsertDeckRecordAsync(
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
    }

    public async Task<RedactedArtifacts> PersistRedactedArtifactsAsync(
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

        await _converter.ConvertPptxToPdfAsync(localPptxPath, tempDir, cancellationToken);

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

        var imageCaptions = await _openAi.GenerateImageCaptionsAsync(
            deck,
            localPptxPath,
            deckId,
            settings,
            cancellationToken);

        await _openAi.GenerateTableSummariesAsync(
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
        OperationTimer.LogTiming("write deck json", jsonSerializeSw.Elapsed, deckId.ToString("n"));

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

        OperationTimer.LogTiming("persist redacted artifacts", totalSw.Elapsed, deckId.ToString("n"));
        return result;
    }

    public async Task DeleteSlidesForDeckAsync(Guid deckId, SupabaseSettings settings, CancellationToken cancellationToken)
    {
        var requestUri = $"{settings.Url}/rest/v1/{settings.SlidesTable}?deck_id=eq.{deckId}";
        using var request = new HttpRequestMessage(HttpMethod.Delete, requestUri);
        request.Headers.Add("apikey", settings.ServiceKey);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ServiceKey);
        request.Headers.Add("Prefer", "return=minimal");

        var sw = Stopwatch.StartNew();
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var elapsed = sw.Elapsed;
        if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            OperationTimer.LogTiming("supabase slide delete failed", elapsed, deckId.ToString("n"));
            throw new Exception($"Supabase slide delete failed ({(int)response.StatusCode}): {body}");
        }

        OperationTimer.LogTiming("supabase slide delete", elapsed, deckId.ToString("n"));
    }

    public async Task ReplaceSlideEmbeddingsAsync(
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
            var text = DeckContentFormatter.ExtractSlidePlainText(slide);
            var captions = imageCaptions.TryGetValue(slide.index, out var captionList) ? captionList : null;
            var tableSummaries = slide.elements
                .OfType<TableDto>()
                .Select(t => t.summary)
                .Where(static s => !string.IsNullOrWhiteSpace(s))
                .Cast<string>()
                .ToList();

            var combined = DeckContentFormatter.CombineSlideContent(text, captions, tableSummaries);

            float[]? embedding = null;
            if (!string.IsNullOrWhiteSpace(settings.OpenAiKey) && !string.IsNullOrWhiteSpace(combined))
            {
                embeddingAttempts++;
                embedding = await _openAi.TryGenerateEmbeddingAsync(
                    combined,
                    settings.OpenAiKey!,
                    settings.EmbeddingModel,
                    cancellationToken);
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

        OperationTimer.LogTiming("replace slide embeddings", totalSw.Elapsed, $"{deckId:n} slides {deck.slides.Count} attempts {embeddingAttempts} success {embeddingSuccess}");
    }

    public async Task UpdateDeckWithRedactedArtifactsAsync(
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
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var elapsed = sw.Elapsed;
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            OperationTimer.LogTiming("supabase deck update failed", elapsed, deckId.ToString("n"));
            throw new Exception($"Supabase deck update failed ({(int)response.StatusCode}): {body}");
        }

        OperationTimer.LogTiming("supabase deck update", elapsed, deckId.ToString("n"));
    }

    public async Task<string?> FetchDeckPdfInfoAsync(Guid deckId, SupabaseSettings settings, CancellationToken cancellationToken)
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
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var elapsed = sw.Elapsed;
        if (!response.IsSuccessStatusCode)
        {
            OperationTimer.LogTiming("supabase pdf info failed", elapsed, deckId.ToString("n"));
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        OperationTimer.LogTiming("supabase pdf info", elapsed, deckId.ToString("n"));
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

    public async Task<DeckRecord?> FetchDeckRecordAsync(
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
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var elapsed = sw.Elapsed;
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            OperationTimer.LogTiming("supabase deck fetch failed", elapsed, deckId.ToString("n"));
            throw new Exception($"Supabase deck fetch failed ({(int)response.StatusCode}): {body}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        OperationTimer.LogTiming("supabase deck fetch", elapsed, deckId.ToString("n"));

        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
        {
            return null;
        }

        return JsonSerializer.Deserialize<DeckRecord>(doc.RootElement[0].GetRawText());
    }

    public async Task<List<RuleActionRecord>> FetchRuleActionsAsync(
        Guid deckId,
        SupabaseSettings settings,
        CancellationToken cancellationToken,
        int? slideFilter = null)
    {
        var query = new Dictionary<string, string?>
        {
            ["select"] = "id,rule_id,deck_id,slide_no,element_key,original_text,new_text",
            ["deck_id"] = $"eq.{deckId}",
            ["order"] = "created_at.asc"
        };

        if (slideFilter.HasValue)
        {
            query["slide_no"] = $"eq.{slideFilter.Value}";
        }

        var requestUri = QueryHelpers.AddQueryString($"{settings.Url}/rest/v1/{settings.RuleActionsTable}", query);
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Add("apikey", settings.ServiceKey);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ServiceKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new Exception($"Supabase rule actions fetch failed ({(int)response.StatusCode}): {body}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var actions = await JsonSerializer.DeserializeAsync<List<RuleActionRecord>>(stream, cancellationToken: cancellationToken);
        return actions ?? new List<RuleActionRecord>();
    }

    public async Task<byte[]?> DownloadObjectAsync(
        SupabaseSettings settings,
        string objectPath,
        CancellationToken cancellationToken)
    {
        var normalizedPath = NormalizeStoragePath(objectPath);
        var requestUri = $"{settings.Url}/storage/v1/object/{settings.StorageBucket}/{normalizedPath}";

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Add("apikey", settings.ServiceKey);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ServiceKey);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    public string NormalizeStoragePath(string path) => path.Trim('/');

    public string GetStorageDirectory(string path)
    {
        var normalized = NormalizeStoragePath(path);
        var lastSlash = normalized.LastIndexOf('/');
        if (lastSlash <= 0)
        {
            return string.Empty;
        }

        return normalized[..lastSlash];
    }

    public async Task<string?> CreateSignedUrlAsync(
        SupabaseSettings settings,
        string objectPath,
        int expiresInSeconds,
        CancellationToken cancellationToken)
    {
        var requestUri = $"{settings.Url}/storage/v1/object/sign/{settings.StorageBucket}/{NormalizeStoragePath(objectPath)}";
        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri);
        request.Headers.Add("apikey", settings.ServiceKey);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ServiceKey);

        var payload = new
        {
            expiresIn = expiresInSeconds
        };

        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (doc.RootElement.TryGetProperty("signedURL", out var signedUrlElement)
            && signedUrlElement.ValueKind == JsonValueKind.String)
        {
            return signedUrlElement.GetString();
        }

        return null;
    }

    private async Task<string> UploadToSupabaseStorageAsync(
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
        request.Content = new ByteArrayContent(fileBytes)
        {
            Headers =
            {
                ContentType = new MediaTypeHeaderValue(contentType)
            }
        };

        var sw = Stopwatch.StartNew();
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var elapsed = sw.Elapsed;
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            OperationTimer.LogTiming("supabase upload failed", elapsed, $"{normalizedPath} bytes {fileBytes.Length}");
            throw new Exception($"Supabase storage upload failed ({(int)response.StatusCode}): {body}");
        }

        OperationTimer.LogTiming("supabase upload", elapsed, $"{normalizedPath} bytes {fileBytes.Length}");
        return normalizedPath;
    }

    private async Task PostgrestInsertAsync(
        SupabaseSettings settings,
        string table,
        object payload,
        bool returnRepresentation,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{settings.Url}/rest/v1/{table}");
        request.Headers.Add("apikey", settings.ServiceKey);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ServiceKey);
        request.Headers.Add("Prefer", returnRepresentation ? "return=representation" : "return=minimal");

        var json = JsonSerializer.Serialize(payload);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        var payloadSize = Encoding.UTF8.GetByteCount(json);
        var sw = Stopwatch.StartNew();
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var elapsed = sw.Elapsed;
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            OperationTimer.LogTiming("supabase insert failed", elapsed, $"{table} bytes {payloadSize}");
            throw new Exception($"Supabase insert into '{table}' failed ({(int)response.StatusCode}): {body}");
        }

        OperationTimer.LogTiming("supabase insert", elapsed, $"{table} bytes {payloadSize}");
    }

    private static string SanitizeBaseName(string originalFileName)
    {
        var name = Path.GetFileNameWithoutExtension(originalFileName);
        if (string.IsNullOrWhiteSpace(name))
        {
            name = "deck";
        }

        var sanitized = Regex.Replace(name.Trim(), "[^A-Za-z0-9-_]+", "-").Trim('-');
        return string.IsNullOrWhiteSpace(sanitized) ? "deck" : sanitized.ToLowerInvariant();
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
