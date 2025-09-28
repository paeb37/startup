// Redaction pipeline: applies inferred actions to deck text content and records matches.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

public static partial class Program
{
    private static List<RedactionEntry> ApplyActions(DeckDto deck, IReadOnlyList<ActionDto>? actions, out List<string> warnings)
    {
        warnings = new List<string>();
        var redactions = new List<RedactionEntry>();
        if (actions == null || actions.Count == 0)
            return redactions;

        foreach (var action in actions)
        {
            if (action is null)
                continue;

            var type = action.Type?.Trim().ToLowerInvariant() ?? "replace";
            switch (type)
            {
                case "replace":
                    ApplyReplaceAction(deck, action, redactions, warnings);
                    break;
                case "rewrite":
                    warnings.Add($"rewrite action '{action.Id ?? "(unnamed)"}' not yet supported");
                    break;
                default:
                    warnings.Add($"unknown action type '{action.Type}' ignored");
                    break;
            }
        }

        return redactions;
    }

    private static void ApplyReplaceAction(DeckDto deck, ActionDto action, List<RedactionEntry> redactions, List<string> warnings)
    {
        if (deck.slides.Count == 0)
            return;

        var replacement = string.IsNullOrWhiteSpace(action.Replacement) ? "[REDACTED]" : action.Replacement!;
        var patterns = BuildPatternsForAction(action, replacement, warnings);
        if (patterns.Count == 0)
            return;

        var totalSlides = deck.slides.Count;
        foreach (var slide in deck.slides)
        {
            if (!ShouldApplyToSlide(action.Scope, slide.index, totalSlides))
                continue;

            foreach (var element in slide.elements)
            {
                switch (element)
                {
                    case TextboxDto textbox when textbox.paragraphs.Count > 0:
                        ApplyPatternsToParagraphs(textbox.paragraphs, slide.index, textbox.key, patterns, redactions, null);
                        break;
                    case TableDto table when table.cells.Length > 0:
                        ApplyPatternsToTable(table, slide.index, patterns, redactions);
                        break;
                }
            }
        }
    }

    private static void ApplyPatternsToTable(TableDto table, int slideIndex, List<(Regex regex, string label, string replacement)> patterns, List<RedactionEntry> redactions)
    {
        var cells = table.cells;
        for (int r = 0; r < cells.Length; r++)
        {
            var row = cells[r];
            if (row == null) continue;

            for (int c = 0; c < row.Length; c++)
            {
                var cell = row[c];
                if (cell?.paragraphs == null || cell.paragraphs.Count == 0) continue;

                var cellRef = new TableCellRef { Row = cell.r, Col = cell.c };
                ApplyPatternsToParagraphs(cell.paragraphs, slideIndex, table.key, patterns, redactions, cellRef);
            }
        }
    }

    private static void ApplyPatternsToParagraphs(List<ParagraphDto> paragraphs, int slideIndex, string elementKey, List<(Regex regex, string label, string replacement)> patterns, List<RedactionEntry> redactions, TableCellRef? cellRef)
    {
        for (int i = 0; i < paragraphs.Count; i++)
        {
            var paragraph = paragraphs[i];
            var text = paragraph.text ?? string.Empty;
            if (text.Length == 0) continue;

            var original = text;
            var matches = new List<RedactionMatch>();

            foreach (var (regex, label, replacement) in patterns)
            {
                var found = regex.Matches(text);
                if (found.Count == 0) continue;

                text = regex.Replace(text, replacement);
                foreach (Match match in found)
                {
                    matches.Add(new RedactionMatch
                    {
                        Type = label,
                        Original = match.Value
                    });
                }
            }

            if (matches.Count == 0 || text == original) continue;

            paragraph.text = text;

            var entry = new RedactionEntry
            {
                Slide = slideIndex,
                ElementKey = elementKey,
                Paragraph = i,
                Matches = matches
            };

            if (cellRef != null)
            {
                entry.Cell = new TableCellRef { Row = cellRef.Row, Col = cellRef.Col };
            }

            redactions.Add(entry);
        }
    }

    private static bool ShouldApplyToSlide(ActionScope? scope, int slideIndexZeroBased, int totalSlides)
    {
        if (scope == null || scope.Slides == null)
            return true;

        var slides = scope.Slides;
        var hasList = slides.List is { Count: > 0 };
        var hasRange = slides.From.HasValue || slides.To.HasValue;

        if (!hasList && !hasRange)
            return true;

        if (hasList)
        {
            foreach (var raw in slides.List!)
            {
                var idx = raw - 1;
                if (idx == slideIndexZeroBased)
                    return true;
            }
        }

        if (hasRange)
        {
            var from = slides.From.HasValue ? Math.Max(0, slides.From.Value - 1) : 0;
            var to = slides.To.HasValue ? Math.Min(totalSlides - 1, slides.To.Value - 1) : totalSlides - 1;
            if (slideIndexZeroBased >= from && slideIndexZeroBased <= to)
                return true;
        }

        return false;
    }

    private static List<(Regex regex, string label, string replacement)> BuildPatternsForAction(ActionDto action, string replacement, List<string>? warnings)
    {
        var list = new List<(Regex regex, string label, string replacement)>();
        var labelPrefix = !string.IsNullOrWhiteSpace(action.Id) ? action.Id! : $"action:{action.Type}";

        var match = action.Match;
        var mode = match?.Mode?.Trim().ToLowerInvariant();

        if (mode is null or "keyword")
        {
            var tokens = match?.Tokens?
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t.Trim())
                .Where(t => t.Length > 0)
                .ToList();

            if (tokens == null || tokens.Count == 0)
            {
                warnings?.Add($"replace action '{labelPrefix}' missing tokens");
                return list;
            }

            foreach (var token in tokens)
            {
                list.Add((
                    new Regex(Regex.Escape(token), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
                    $"{labelPrefix}:keyword",
                    replacement));
            }
        }
        else if (mode == "regex")
        {
            if (string.IsNullOrWhiteSpace(match?.Pattern))
            {
                warnings?.Add($"replace action '{labelPrefix}' missing pattern");
                return list;
            }

            try
            {
                list.Add((
                    new Regex(match.Pattern!, RegexOptions.CultureInvariant),
                    $"{labelPrefix}:regex",
                    replacement));
            }
            catch (ArgumentException ex)
            {
                warnings?.Add($"invalid regex for action '{labelPrefix}': {ex.Message}");
            }
        }
        else if (!string.IsNullOrWhiteSpace(match?.Pattern))
        {
            try
            {
                list.Add((
                    new Regex(match.Pattern!, RegexOptions.CultureInvariant),
                    $"{labelPrefix}:regex",
                    replacement));
            }
            catch (ArgumentException ex)
            {
                warnings?.Add($"invalid regex for action '{labelPrefix}': {ex.Message}");
            }
        }
        else
        {
            warnings?.Add($"replace action '{labelPrefix}' missing match criteria");
        }

        return list;
    }
}

