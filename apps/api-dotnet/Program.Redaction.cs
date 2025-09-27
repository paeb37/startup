// Redaction pipeline: applies inferred rules to deck text content and records matches.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

public static partial class Program
{
    private static List<RedactionEntry> ApplyRedactions(DeckDto deck, RedactionRules rules)
    {
        var patterns = BuildPatterns(rules);
        var redactions = new List<RedactionEntry>();
        if (patterns.Count == 0) return redactions;

        foreach (var slide in deck.slides)
        {
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

        return redactions;
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

    private static List<(Regex regex, string label, string replacement)> BuildPatterns(RedactionRules rules)
    {
        var list = new List<(Regex regex, string label, string replacement)>();

        if (rules.MaskClientNames)
        {
            list.Add((
                new Regex(@"\b([A-Z][a-z]+(?:\s[A-Z][a-z]+)+)\b", RegexOptions.CultureInvariant),
                "client_name",
                "[REDACTED_CLIENT]"
            ));
        }

        if (rules.MaskRevenue)
        {
            list.Add((
                new Regex(@"\b\$?\d[\d,]*(?:\.\d+)?\b", RegexOptions.CultureInvariant),
                "revenue",
                "[REDACTED_REVENUE]"
            ));
        }

        if (rules.MaskEmails)
        {
            list.Add((
                new Regex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", RegexOptions.CultureInvariant),
                "email",
                "[REDACTED_EMAIL]"
            ));
        }

        if (rules.Keywords.Count > 0)
        {
            var keywords = rules.Keywords
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Select(k => k.Trim())
                .Where(k => k.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (var keyword in keywords)
            {
                list.Add((
                    new Regex(Regex.Escape(keyword), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
                    $"keyword:{keyword}",
                    "[REDACTED]"
                ));
            }
        }

        return list;
    }
}

