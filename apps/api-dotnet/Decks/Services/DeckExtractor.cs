namespace Dexter.WebApi.Decks.Services;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using Dexter.WebApi.Decks.Models;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

/// <summary>
/// PPTX parsing helpers: builds deck DTOs from OpenXML presentation parts.
/// </summary>
internal sealed class DeckExtractor
{
    public DeckDto Extract(Stream pptxStream, string fileName)
    {
        using var doc = PresentationDocument.Open(pptxStream, false);
        var presPart = doc.PresentationPart ?? throw new InvalidOperationException("Not a PPTX (no PresentationPart)");
        var pres = presPart.Presentation ?? throw new InvalidOperationException("Invalid PPTX (no Presentation)");

        var slideIds = pres.SlideIdList?.Elements<SlideId>()?.ToList() ?? new List<SlideId>();
        var slideSize = pres.SlideSize;
        long slideWidthEmu = slideSize?.Cx?.Value ?? 9144000L; // default 10"
        long slideHeightEmu = slideSize?.Cy?.Value ?? 6858000L; // default 7.5"

        var deck = new DeckDto
        {
            file = Path.GetFileName(fileName),
            slideCount = slideIds.Count,
            slideWidthEmu = slideWidthEmu,
            slideHeightEmu = slideHeightEmu
        };

        for (int i = 0; i < slideIds.Count; i++)
        {
            var relId = slideIds[i].RelationshipId!.Value!;
            var slidePart = (SlidePart)presPart.GetPartById(relId);
            var sld = slidePart.Slide!;
            var spTree = sld.CommonSlideData!.ShapeTree!;

            var slideDto = new SlideDto { index = i };

            int z = 0;
            foreach (var child in spTree.ChildElements)
            {
                ExtractElements(child, slidePart, slideDto.elements, ref z);
            }

            deck.slides.Add(slideDto);
        }

        return deck;
    }

    private static void ExtractElements(OpenXmlElement el, SlidePart slidePart, List<ElementDto> outList, ref int z)
    {
        if (el is P.Shape sp)
        {
            var paragraphs = ExtractParagraphs(sp.TextBody);
            if (paragraphs.Count > 0)
            {
                var nv = sp.NonVisualShapeProperties?.NonVisualDrawingProperties;
                var idV = nv?.Id?.Value;
                if (idV is uint id)
                {
                    outList.Add(new TextboxDto
                    {
                        key = MakeKey(slidePart, "textbox", id),
                        id = id,
                        name = nv?.Name,
                        bbox = ReadEffectiveBBox(sp.ShapeProperties, slidePart),
                        paragraphs = paragraphs,
                        z = z++
                    });
                }
            }
        }
        else if (el is P.Picture pic)
        {
            var nv = pic.NonVisualPictureProperties?.NonVisualDrawingProperties;
            var idV = nv?.Id?.Value;
            if (idV is uint id)
            {
                var bbox = ReadBBox(pic.ShapeProperties?.Transform2D);

                string? imgPath = null;
                long? bytes = null;

                var embedRId = pic.BlipFill?.Blip?.Embed?.Value;
                if (!string.IsNullOrEmpty(embedRId))
                {
                    if (slidePart.GetPartById(embedRId) is ImagePart imgPart)
                    {
                        imgPath = imgPart.Uri.OriginalString.TrimStart('/');
                        using var s = imgPart.GetStream(FileMode.Open, FileAccess.Read);
                        if (s.CanSeek)
                        {
                            bytes = s.Length;
                        }
                        else
                        {
                            long total = 0;
                            byte[] buf = new byte[81920];
                            int n;
                            while ((n = s.Read(buf, 0, buf.Length)) > 0) total += n;
                            bytes = total;
                        }
                    }
                }

                outList.Add(new PictureDto
                {
                    key = MakeKey(slidePart, "picture", id),
                    id = id,
                    name = nv?.Name,
                    bbox = bbox,
                    imgPath = imgPath,
                    bytes = bytes,
                    z = z++
                });
            }
        }
        else if (el is P.GraphicFrame gf)
        {
            var tbl = gf.Graphic?.GraphicData?.GetFirstChild<A.Table>();
            if (tbl != null)
            {
                var nv = gf.NonVisualGraphicFrameProperties?.NonVisualDrawingProperties;
                var idV = nv?.Id?.Value;
                if (idV is uint id)
                {
                    var bbox = ReadEffectiveBBox(gf, slidePart);

                    var tableDto = BuildTableDto(tbl, slidePart, id, nv?.Name, bbox);
                    tableDto.key = MakeKey(slidePart, "table", id);
                    tableDto.z = z++;

                    outList.Add(tableDto);
                }
            }
        }
        else if (el is P.GroupShape grp)
        {
            foreach (var child in grp.ChildElements)
            {
                ExtractElements(child, slidePart, outList, ref z);
            }
        }
    }

    private static TableDto BuildTableDto(A.Table tbl, SlidePart slidePart, uint id, string? name, BBox bbox)
    {
        var gridCols = tbl.TableGrid?.Elements<A.GridColumn>().ToList() ?? new List<A.GridColumn>();
        var cols = gridCols.Count;
        long[]? colWidths = cols > 0 ? gridCols.Select(c => (long?)(c.Width?.Value) ?? 0L).ToArray() : null;

        var rowsList = tbl.Elements<A.TableRow>().ToList();
        var rows = rowsList.Count;
        long[]? rowHeights = rows > 0 ? rowsList.Select(r => (long?)(r.Height?.Value) ?? 0L).ToArray() : null;

        var cells = new TableCellDto?[rows][];
        for (int r = 0; r < rows; r++) cells[r] = new TableCellDto?[cols];

        var covered = new bool[rows, Math.Max(cols, 1)];

        for (int r = 0; r < rows; r++)
        {
            var row = rowsList[r];
            int cOut = 0;

            foreach (var tc in row.Elements<A.TableCell>())
            {
                while (cOut < cols && covered[r, cOut]) cOut++;
                if (cOut >= cols && cols > 0) break;

                int colSpan = (tc.GridSpan?.Value) ?? 1;
                int rowSpan = (tc.RowSpan?.Value) ?? 1;

                var paragraphs = ExtractParagraphs(tc.TextBody);

                BBox? cellBox = null;
                if (colWidths != null && rowHeights != null && cols > 0 && rows > 0)
                {
                    long x = Sum(colWidths, 0, cOut);
                    long y = Sum(rowHeights, 0, r);
                    long w = Sum(colWidths, cOut, colSpan);
                    long h = Sum(rowHeights, r, rowSpan);
                    cellBox = new BBox { x = x, y = y, w = w, h = h };
                }

                if (cols == 0)
                {
                    cols = 1;
                    colWidths = null;
                    cells[r] = new TableCellDto?[1];
                }

                cells[r][cOut] = new TableCellDto
                {
                    r = r,
                    c = cOut,
                    rowSpan = rowSpan,
                    colSpan = colSpan,
                    paragraphs = paragraphs,
                    cellBox = cellBox
                };

                for (int rr = r; rr < Math.Min(rows, r + rowSpan); rr++)
                {
                    for (int cc = cOut; cc < Math.Min(cols, cOut + colSpan); cc++)
                    {
                        covered[rr, cc] = true;
                    }
                }

                cOut++;
            }
        }

        return new TableDto
        {
            id = id,
            name = name,
            bbox = bbox,
            rows = rows,
            cols = cols,
            colWidths = colWidths,
            rowHeights = rowHeights,
            cells = cells
        };
    }

    private static List<ParagraphDto> ExtractParagraphs(OpenXmlCompositeElement? body)
    {
        var result = new List<ParagraphDto>();
        if (body == null) return result;

        foreach (var para in body.Elements<A.Paragraph>())
        {
            var text = new System.Text.StringBuilder();
            foreach (var child in para.ChildElements)
            {
                switch (child)
                {
                    case A.Run run when run.Text != null:
                        text.Append(run.Text.Text);
                        break;
                    case A.Break:
                        text.Append('\n');
                        break;
                    case A.Field field when field.Text != null:
                        text.Append(field.Text.Text);
                        break;
                }
            }

            var trimmed = text.ToString().TrimEnd();
            if (trimmed.Length == 0) continue;

            var level = para.ParagraphProperties?.Level?.Value ?? 0;
            result.Add(new ParagraphDto { level = level, text = trimmed });
        }

        return result;
    }

    private static BBox ReadEffectiveBBox(P.ShapeProperties? sp, SlidePart _)
    {
        return sp?.Transform2D != null
            ? ReadBBox(sp.Transform2D)
            : new BBox();
    }

    private static BBox ReadEffectiveBBox(P.GraphicFrame frame, SlidePart _)
    {
        return frame.Transform != null
            ? ReadBBox(frame.Transform)
            : new BBox();
    }

    private static BBox ReadBBox(A.Transform2D? transform)
    {
        var offset = transform?.Offset;
        var extents = transform?.Extents;
        return new BBox
        {
            x = offset?.X?.Value,
            y = offset?.Y?.Value,
            w = extents?.Cx?.Value,
            h = extents?.Cy?.Value
        };
    }

    private static BBox ReadBBox(P.Transform? transform)
    {
        var offset = transform?.Offset;
        var extents = transform?.Extents;
        return new BBox
        {
            x = offset?.X?.Value,
            y = offset?.Y?.Value,
            w = extents?.Cx?.Value,
            h = extents?.Cy?.Value
        };
    }

    private static string MakeKey(SlidePart slidePart, string type, uint id)
        => $"{slidePart.Uri.OriginalString.TrimStart('/')}{'#'}{type}{'#'}{id}";

    private static long Sum(long[]? values, int start, int count)
    {
        if (values == null || values.Length == 0 || count <= 0)
        {
            return 0;
        }

        long total = 0;
        for (int i = 0; i < count && start + i < values.Length; i++)
        {
            total += values[start + i];
        }

        return total;
    }
}
