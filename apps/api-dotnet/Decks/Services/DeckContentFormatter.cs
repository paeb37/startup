namespace Dexter.WebApi.Decks.Services;

using System.Collections.Generic;
using System.Text;
using Dexter.WebApi.Decks.Models;

internal static class DeckContentFormatter
{
    public static string CombineSlideContent(
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
                {
                    continue;
                }

                if (sb.Length > 0)
                {
                    sb.AppendLine();
                }

                sb.Append("Image summary: ").Append(caption.Trim());
            }
        }

        if (tableSummaries != null)
        {
            foreach (var summary in tableSummaries)
            {
                if (string.IsNullOrWhiteSpace(summary))
                {
                    continue;
                }

                if (sb.Length > 0)
                {
                    sb.AppendLine();
                }

                sb.Append("Table summary: ").Append(summary.Trim());
            }
        }

        return sb.ToString().Trim();
    }

    public static string ExtractSlidePlainText(SlideDto slide)
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
                        if (row is null)
                        {
                            continue;
                        }

                        foreach (var cell in row)
                        {
                            if (cell is null)
                            {
                                continue;
                            }

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
            {
                continue;
            }

            sb.AppendLine(paragraph.text.Trim());
        }
    }
}
