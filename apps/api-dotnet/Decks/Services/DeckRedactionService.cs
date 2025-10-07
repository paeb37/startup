namespace Dexter.WebApi.Decks.Services;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dexter.WebApi.Decks.Models;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

internal sealed class DeckRedactionService
{   
    /**
    Apply the rules to PPTX
    */
    public byte[] ApplyRuleActionsToPptx(
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

        foreach (var (slidePath, actionsForSlide) in slideActions)
        {
            if (!slideParts.TryGetValue(slidePath, out var slidePart))
            {
                continue;
            }

            var slide = slidePart.Slide;
            if (slide == null)
            {
                continue;
            }

            bool slideModified = false;
            foreach (var (info, action) in actionsForSlide)
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
        var sb = new System.Text.StringBuilder();
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

        var normalizedA = NormalizeWhitespace(RemoveListMarkers(a));
        var normalizedB = NormalizeWhitespace(RemoveListMarkers(b));

        return string.Equals(normalizedA, normalizedB, StringComparison.Ordinal);
    }

    private static string NormalizeWhitespace(string value)
    {
        var sb = new System.Text.StringBuilder(value.Length);
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

    private static string RemoveListMarkers(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value
            .Replace('\u2022', ' ')
            .Replace('\u2023', ' ')
            .Replace('\u25E6', ' ');
    }

    private static bool TryParseElementKey(string elementKey, out ElementKeyInfo info)
    {
        info = new ElementKeyInfo(string.Empty, string.Empty, 0);
        if (string.IsNullOrWhiteSpace(elementKey))
        {
            return false;
        }

        var parts = elementKey.Split('#');
        string slidePath;
        string elementType;
        string? idSegment;

        if (parts.Length == 2)
        {
            slidePath = parts[0].Trim();
            var typeAndId = parts[1].Split(':');
            if (typeAndId.Length != 2)
            {
                return false;
            }

            elementType = typeAndId[0].Trim();
            idSegment = typeAndId[1];
        }
        else if (parts.Length == 3)
        {
            slidePath = parts[0].Trim();
            elementType = parts[1].Trim();
            idSegment = parts[2];
        }
        else
        {
            return false;
        }

        if (!uint.TryParse(idSegment, out var elementId))
        {
            return false;
        }

        info = new ElementKeyInfo(slidePath, elementType, elementId);
        return true;
    }

    private readonly record struct ElementKeyInfo(string SlidePath, string ElementType, uint ElementId);
}
