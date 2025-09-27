// PPTX export helpers: writes redacted deck content into a sanitized copy.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

public static partial class Program
{
    private static byte[] CreateSanitizedCopy(Stream originalStream, DeckDto deck, IReadOnlyList<ActionDto> actions)
    {
        var working = new MemoryStream();
        originalStream.Position = 0;
        originalStream.CopyTo(working);
        working.Position = 0;

        if (actions == null || actions.Count == 0)
        {
            return working.ToArray();
        }

        using (var document = PresentationDocument.Open(working, true))
        {
            var presentationPart = document.PresentationPart ?? throw new InvalidOperationException("Invalid PPTX (no PresentationPart)");
            var slidePartsByUri = presentationPart.SlideParts.ToDictionary(sp => sp.Uri.OriginalString.TrimStart('/'));

            var replaceActions = new List<(ActionDto action, List<(Regex regex, string label, string replacement)> patterns)>();
            var scratchWarnings = new List<string>();

            foreach (var action in actions)
            {
                if (action is null) continue;
                if (!string.Equals(action.Type?.Trim(), "replace", StringComparison.OrdinalIgnoreCase))
                    continue;

                var replacement = string.IsNullOrWhiteSpace(action.Replacement) ? "[REDACTED]" : action.Replacement!;
                var patterns = BuildPatternsForAction(action, replacement, scratchWarnings);
                if (patterns.Count > 0)
                {
                    replaceActions.Add((action, patterns));
                }
            }

            if (replaceActions.Count == 0)
            {
                return working.ToArray();
            }

            var touchedSlides = new HashSet<P.Slide>();

            foreach (var slide in deck.slides)
            {
                foreach (var element in slide.elements)
                {
                    if (string.IsNullOrEmpty(element.key)) continue;
                    var keyParts = element.key.Split('#');
                    if (keyParts.Length != 2) continue;

                    if (!slidePartsByUri.TryGetValue(keyParts[0], out var slidePart) || slidePart.Slide == null) continue;

                    var slideDom = slidePart.Slide;
                    bool modified = false;

                    foreach (var (action, patterns) in replaceActions)
                    {
                        var totalSlides = deck.slideCount > 0 ? deck.slideCount : deck.slides.Count;
                        if (!ShouldApplyToSlide(action.Scope, slide.index, totalSlides))
                            continue;

                        modified |= element switch
                        {
                            TextboxDto textbox => ApplyPatternsToTextbox(slideDom, textbox, patterns),
                            TableDto table => ApplyPatternsToTable(slideDom, table, patterns),
                            _ => false
                        };
                    }

                    if (modified)
                    {
                        touchedSlides.Add(slideDom);
                    }
                }
            }

            foreach (var slideDom in touchedSlides)
            {
                slideDom.Save();
            }
        }

        return working.ToArray();
    }

    private static bool ApplyPatternsToTextbox(P.Slide slideDom, TextboxDto textbox, List<(Regex regex, string label, string replacement)> patterns)
    {
        var shape = slideDom.Descendants<P.Shape>()
            .FirstOrDefault(s => s.NonVisualShapeProperties?.NonVisualDrawingProperties?.Id?.Value == textbox.id);
        if (shape?.TextBody == null) return false;

        return ApplyPatternsToTextBody(shape.TextBody, patterns);
    }

    private static bool ApplyPatternsToTable(P.Slide slideDom, TableDto table, List<(Regex regex, string label, string replacement)> patterns)
    {
        var frame = slideDom.Descendants<P.GraphicFrame>()
            .FirstOrDefault(g => g.NonVisualGraphicFrameProperties?.NonVisualDrawingProperties?.Id?.Value == table.id);
        if (frame == null) return false;

        var tablePart = frame.Graphic?.GraphicData?.GetFirstChild<A.Table>();
        if (tablePart == null) return false;

        bool modified = false;

        foreach (var rowArray in table.cells)
        {
            if (rowArray == null) continue;

            foreach (var cellDto in rowArray)
            {
                if (cellDto == null) continue;

                var tableCell = FindTableCell(tablePart, cellDto.r, cellDto.c);
                if (tableCell?.TextBody == null) continue;

                if (ApplyPatternsToTextBody(tableCell.TextBody, patterns))
                {
                    modified = true;
                }
            }
        }

        return modified;
    }

    private static bool ApplyPatternsToTextBody(OpenXmlCompositeElement textBody, List<(Regex regex, string label, string replacement)> patterns)
    {
        bool modified = false;

        foreach (var paragraph in textBody.Elements<A.Paragraph>())
        {
            if (ApplyPatternsToParagraph(paragraph, patterns))
            {
                modified = true;
            }
        }

        return modified;
    }

    private static bool ApplyPatternsToParagraph(A.Paragraph paragraph, List<(Regex regex, string label, string replacement)> patterns)
    {
        bool modified = false;

        foreach (var run in paragraph.Descendants<A.Run>())
        {
            if (ApplyPatternsToRun(run, patterns))
            {
                modified = true;
            }
        }

        return modified;
    }

    private static bool ApplyPatternsToRun(A.Run run, List<(Regex regex, string label, string replacement)> patterns)
    {
        var textElement = run.GetFirstChild<A.Text>();
        if (textElement == null) return false;

        var text = textElement.Text ?? string.Empty;
        var updated = text;

        foreach (var (regex, _, replacement) in patterns)
        {
            updated = regex.Replace(updated, replacement);
        }

        if (updated == text) return false;

        textElement.Text = updated;
        return true;
    }

    private static A.TableCell? FindTableCell(A.Table table, int targetRow, int targetCol)
    {
        var rows = table.Elements<A.TableRow>().ToList();
        if (targetRow < 0 || targetRow >= rows.Count) return null;

        var cols = Math.Max(table.TableGrid?.Elements<A.GridColumn>().Count() ?? 0, 1);
        var covered = new bool[rows.Count, cols];

        for (int r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            int cOut = 0;

            foreach (var tc in row.Elements<A.TableCell>())
            {
                while (cOut < cols && covered[r, cOut]) cOut++;
                if (cOut >= cols) break;

                int colSpan = tc.GridSpan?.Value ?? 1;
                int rowSpan = tc.RowSpan?.Value ?? 1;

                if (r == targetRow && cOut == targetCol)
                {
                    return tc;
                }

                for (int rr = r; rr < Math.Min(rows.Count, r + rowSpan); rr++)
                    for (int cc = cOut; cc < Math.Min(cols, cOut + colSpan); cc++)
                        covered[rr, cc] = true;

                cOut += colSpan;
            }
        }

        return null;
    }
}

