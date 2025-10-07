namespace Dexter.WebApi.Infrastructure;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Dexter.WebApi.Common.Logging;
using Dexter.WebApi.Decks.Models;

internal sealed class OpenAiClient
{
    private readonly HttpClient _httpClient;

    public OpenAiClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<Dictionary<int, List<string>>> GenerateImageCaptionsAsync(
        DeckDto deck,
        string pptxPath,
        Guid deckId,
        SupabaseClient.Settings settings,
        CancellationToken cancellationToken)
    {
        var captionsBySlide = new Dictionary<int, List<string>>();
        var sw = Stopwatch.StartNew();
        int pictureCount = 0;
        int captionRequests = 0;

        if (string.IsNullOrWhiteSpace(settings.OpenAiKey))
        {
            OperationTimer.LogTiming("image captions skipped", TimeSpan.Zero, $"{deckId:n} (no OpenAI key)");
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
                {
                    continue;
                }

                var entryPath = picture.imgPath.TrimStart('/');
                if (captionCache.TryGetValue(entryPath, out var cached))
                {
                    picture.summary = cached;
                    AppendCaption(captionsBySlide, slide.index, cached);
                    continue;
                }

                var entry = archive.GetEntry(entryPath);
                if (entry is null)
                {
                    continue;
                }

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
        OperationTimer.LogTiming("image captions", sw.Elapsed, $"{deckId:n} pictures {pictureCount} requests {captionRequests} captions {totalCaptions}");
        return captionsBySlide;
    }

    public async Task GenerateTableSummariesAsync(
        DeckDto deck,
        SupabaseClient.Settings settings,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.OpenAiKey))
        {
            OperationTimer.LogTiming("table summaries skipped", TimeSpan.Zero, "no OpenAI key");
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
            {
                continue;
            }

            summaryRequests++;
            var summary = await RequestTableSummaryAsync(tableText, settings, cancellationToken);
            if (!string.IsNullOrWhiteSpace(summary))
            {
                table.summary = summary.Trim();
                summariesCreated++;
            }
        }

        OperationTimer.LogTiming("table summaries", sw.Elapsed, $"tables {tableCount} requests {summaryRequests} summaries {summariesCreated}");
    }

    public async Task<float[]?> TryGenerateEmbeddingAsync(
        string input,
        string apiKey,
        string model,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var payload = new
        {
            model,
            input
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/embeddings");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        var sw = Stopwatch.StartNew();
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var elapsed = sw.Elapsed;
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            Console.Error.WriteLine($"Embedding request failed ({(int)response.StatusCode}): {body}");
            OperationTimer.LogTiming("embedding failed", elapsed, $"chars {input.Length}");
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var embeddingResponse = await JsonSerializer.DeserializeAsync<OpenAiEmbeddingResponse>(stream, cancellationToken: cancellationToken);
        var vector = embeddingResponse?.Data?.FirstOrDefault()?.Embedding;
        OperationTimer.LogTiming("embedding", elapsed, $"chars {input.Length}");
        return vector?.ToArray();
    }

    private async Task<string?> RequestImageCaptionAsync(byte[] imageBytes, string contentType, SupabaseClient.Settings settings, CancellationToken cancellationToken)
    {
        if (imageBytes.Length == 0)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(settings.OpenAiKey))
        {
            return null;
        }

        var model = settings.VisionModel;
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

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

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.OpenAiKey);
        request.Headers.Add("OpenAI-Beta", "assistants=v2");

        var sw = Stopwatch.StartNew();
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var elapsed = sw.Elapsed;
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            Console.Error.WriteLine($"Vision caption request failed ({(int)response.StatusCode}): {error}");
            OperationTimer.LogTiming("vision caption failed", elapsed, $"bytes {imageBytes.Length}");
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        OperationTimer.LogTiming("vision caption", elapsed, $"bytes {imageBytes.Length}");

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

    private async Task<string?> RequestTableSummaryAsync(string tableText, SupabaseClient.Settings settings, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tableText))
        {
            return null;
        }

        var model = settings.VisionModel;
        if (string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(settings.OpenAiKey))
        {
            return null;
        }

        var systemPrompt = "Summarize the table for semantic retrieval.";

        var payload = new
        {
            model,
            input = new object[]
            {
                new
                {
                    role = "system",
                    content = new object[]
                    {
                        new { type = "input_text", text = systemPrompt }
                    }
                },
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "input_text", text = tableText }
                    }
                }
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.OpenAiKey);
        request.Headers.Add("OpenAI-Beta", "assistants=v2");

        var sw = Stopwatch.StartNew();
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var elapsed = sw.Elapsed;
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            Console.Error.WriteLine($"Table summary request failed ({(int)response.StatusCode}): {error}");
            OperationTimer.LogTiming("table summary failed", elapsed, $"chars {tableText.Length}");
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        OperationTimer.LogTiming("table summary", elapsed, $"chars {tableText.Length}");

        if (doc.RootElement.TryGetProperty("output_text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
        {
            return textEl.GetString();
        }

        if (doc.RootElement.TryGetProperty("output", out var outputEl) && outputEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in outputEl.EnumerateArray())
            {
                if (item.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var contentItem in contentEl.EnumerateArray())
                    {
                        if (contentItem.TryGetProperty("text", out var textContent) && textContent.ValueKind == JsonValueKind.String)
                        {
                            return textContent.GetString();
                        }
                    }
                }
            }
        }

        return null;
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

    private static string FormatTableForSummary(TableDto table)
    {
        if (table.cells is null || table.cells.Length == 0)
        {
            return string.Empty;
        }

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
                    if (cell is null)
                    {
                        continue;
                    }

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
            if (row is null)
            {
                continue;
            }

            var values = new List<string>();
            for (int c = 0; c < Math.Min(table.cols, row.Length); c++)
            {
                var cell = row[c];
                if (cell is null)
                {
                    values.Add(string.Empty);
                    continue;
                }

                var text = ExtractTableCellText(cell).Trim();
                if (hasHeaders && c < headers.Length && !string.IsNullOrWhiteSpace(headers[c]))
                {
                    values.Add($"{headers[c]}: {text}");
                }
                else
                {
                    values.Add(text);
                }
            }

            sb.AppendLine(string.Join(" | ", values.Where(static v => !string.IsNullOrWhiteSpace(v))));
        }

        return sb.ToString().Trim();
    }

    private static string ExtractTableCellText(TableCellDto cell)
    {
        if (cell.paragraphs is null || cell.paragraphs.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        foreach (var paragraph in cell.paragraphs)
        {
            if (string.IsNullOrWhiteSpace(paragraph.text))
            {
                continue;
            }

            if (sb.Length > 0)
            {
                sb.Append(' ');
            }

            sb.Append(paragraph.text.Trim());
        }

        return sb.ToString();
    }

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
