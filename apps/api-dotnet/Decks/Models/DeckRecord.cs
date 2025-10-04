namespace Dexter.WebApi.Decks.Models;

using System;
using System.Text.Json.Serialization;

public sealed class DeckRecord
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    [JsonPropertyName("deck_name")]
    public string? DeckName { get; init; }

    [JsonPropertyName("pptx_path")]
    public string? PptxPath { get; init; }

    [JsonPropertyName("redacted_pptx_path")]
    public string? RedactedPptxPath { get; init; }

    [JsonPropertyName("redacted_pdf_path")]
    public string? RedactedPdfPath { get; init; }

    [JsonPropertyName("redacted_json_path")]
    public string? RedactedJsonPath { get; init; }

    [JsonPropertyName("industry")]
    public string? Industry { get; init; }

    [JsonPropertyName("deck_type")]
    public string? DeckType { get; init; }

    [JsonPropertyName("slide_count")]
    public int? SlideCount { get; init; }
}
