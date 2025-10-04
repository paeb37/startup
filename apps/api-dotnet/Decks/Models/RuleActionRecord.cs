namespace Dexter.WebApi.Decks.Models;

using System;
using System.Text.Json.Serialization;

public sealed class RuleActionRecord
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    [JsonPropertyName("rule_id")]
    public Guid RuleId { get; init; }

    [JsonPropertyName("deck_id")]
    public Guid DeckId { get; init; }

    [JsonPropertyName("slide_no")]
    public int SlideNo { get; init; }

    [JsonPropertyName("element_key")]
    public string ElementKey { get; init; } = string.Empty;

    [JsonPropertyName("bbox")]
    public BBox? BBox { get; init; }

    [JsonPropertyName("original_text")]
    public string? OriginalText { get; init; }

    [JsonPropertyName("new_text")]
    public string? NewText { get; init; }
}
