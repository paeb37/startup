using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Diagnostics;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using System.Text.Json.Serialization.Metadata;
using System.ComponentModel;

// ----------------------------- DTOs -----------------------------------------

public class DeckDto
{
    public string file { get; set; } = "";
    public int slideCount { get; set; }
    public List<SlideDto> slides { get; set; } = new();
}

public class SlideDto
{
    public int index { get; set; }
    // Mixed list: textboxes + pictures + tables
    public List<ElementDto> elements { get; set; } = new();
}

// Base element (shared fields)
// We'll serialize polymorphically with "type" as the discriminator.
public abstract class ElementDto
{
    public string key { get; set; } = "";
    public uint id { get; set; }             // cNvPr/@id (unique per slide)
    public string? name { get; set; }        // cNvPr/@name
    public BBox bbox { get; set; } = new();  // EMUs
    public int z { get; set; }               // z-order on the slide
}

// Textbox element -------------------------------------------------------------
public sealed class TextboxDto : ElementDto
{
    public List<ParagraphDto> paragraphs { get; set; } = new();
}

// Picture element -------------------------------------------------------------
public sealed class PictureDto : ElementDto
{
    public string? imgPath { get; set; }   // e.g., "ppt/media/image3.jpeg" (embedded)
    public long? bytes { get; set; }       // size of the image part stream
}

// Table element ---------------------------------------------------------------
public sealed class TableDto : ElementDto
{
    public int rows { get; set; }
    public int cols { get; set; }
    public long[]? colWidths { get; set; }
    public long[]? rowHeights { get; set; }
    public TableCellDto?[][] cells { get; set; } = Array.Empty<TableCellDto?[]>();
}

public class TableCellDto
{
    public int r { get; set; }
    public int c { get; set; }
    public int rowSpan { get; set; } = 1;
    public int colSpan { get; set; } = 1;
    public List<ParagraphDto> paragraphs { get; set; } = new();
    public BBox? cellBox { get; set; }
}

public class BBox
{
    public long? x { get; set; }
    public long? y { get; set; }
    public long? w { get; set; }
    public long? h { get; set; }
}

public class ParagraphDto
{
    public int level { get; set; }
    public string text { get; set; } = "";
}

// ----------------------------- Web app --------------------------------------

public static class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
            p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

        var app = builder.Build();
        app.UseCors();

        // POST /api/extract: accept multipart "file", return deck JSON
        app.MapPost("/api/extract", async (HttpRequest req) =>
        {
            if (!req.HasFormContentType)
                return Results.BadRequest(new { error = "multipart/form-data required" });

            var form = await req.ReadFormAsync();
            var file = form.Files.GetFile("file");
            if (file is null || file.Length == 0)
                return Results.BadRequest(new { error = "file missing" });

            try
            {
                await using var ms = new MemoryStream();
                await file.CopyToAsync(ms);
                ms.Position = 0;

                var deck = ExtractDeck(ms, file.FileName);

                // Configure polymorphic serialization for ElementDto
                var resolver = new DefaultJsonTypeInfoResolver();
                resolver.Modifiers.Add(info =>
                {
                    if (info.Type == typeof(ElementDto))
                    {
                        info.PolymorphismOptions = new JsonPolymorphismOptions
                        {
                            TypeDiscriminatorPropertyName = "type",
                            IgnoreUnrecognizedTypeDiscriminators = true
                        };
                        info.PolymorphismOptions.DerivedTypes.Add(new JsonDerivedType(typeof(TextboxDto), "textbox"));
                        info.PolymorphismOptions.DerivedTypes.Add(new JsonDerivedType(typeof(PictureDto), "picture"));
                        info.PolymorphismOptions.DerivedTypes.Add(new JsonDerivedType(typeof(TableDto), "table"));
                    }
                });

                var jsonOptions = new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = true,
                    TypeInfoResolver = resolver
                };
                return Results.Text(JsonSerializer.Serialize(deck, jsonOptions), "application/json");
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // POST /api/render: accept multipart "file", return a PDF for inline viewing
        app.MapPost("/api/render", async (HttpRequest req) =>
        {
            if (!req.HasFormContentType)
                return Results.BadRequest(new { error = "multipart/form-data required" });

            var form = await req.ReadFormAsync();
            var file = form.Files.GetFile("file");
            if (file is null || file.Length == 0)
                return Results.BadRequest(new { error = "file missing" });

            var tmpDir = Path.Combine(Path.GetTempPath(), "dexter-preview", Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(tmpDir);
            var inPath = Path.Combine(tmpDir, Path.GetFileName(file.FileName));
            var outPdf = Path.Combine(tmpDir, Path.GetFileNameWithoutExtension(file.FileName) + ".pdf");

            try
            {
                await using (var fs = File.Create(inPath))
                {
                    await file.CopyToAsync(fs);
                }

                await ConvertPptxToPdfAsync(inPath, tmpDir); // produces outPdf

                if (!System.IO.File.Exists(outPdf))
                    return Results.StatusCode(500);

                var pdfBytes = await System.IO.File.ReadAllBytesAsync(outPdf);
                var cd = new System.Net.Mime.ContentDisposition
                {
                    Inline = true,
                    FileName = Path.GetFileName(outPdf)
                };
                var headers = new HeaderDictionary();
                headers["Content-Disposition"] = cd.ToString();

                return Results.File(pdfBytes, "application/pdf", enableRangeProcessing: true);
            }
            catch (Win32Exception)
            {
                return Results.Problem(
                    statusCode: 501,
                    title: "Not Implemented",
                    detail: "PDF rendering requires LibreOffice (soffice) on the server (or set SOFFICE_PATH)."
                );
            }

            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            finally
            {
                // Best-effort cleanup
                try { Directory.Delete(tmpDir, recursive: true); } catch { /* ignore */ }
            }
        });

        await app.RunAsync();
    }

    // --------------------------- LibreOffice conversion ----------------------

    private static async Task ConvertPptxToPdfAsync(string inputPath, string outDir)
    {
        // Use SOFFICE_PATH env var if you want a fixed path; else rely on PATH
        var soffice = Environment.GetEnvironmentVariable("SOFFICE_PATH") ?? "soffice";

        var psi = new ProcessStartInfo
        {
            FileName = soffice,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        // soffice --headless --convert-to pdf --outdir <outDir> <inputPath>
        psi.ArgumentList.Add("--headless");
        psi.ArgumentList.Add("--convert-to");
        psi.ArgumentList.Add("pdf");
        psi.ArgumentList.Add("--outdir");
        psi.ArgumentList.Add(outDir);
        psi.ArgumentList.Add(inputPath);

        using var proc = Process.Start(psi)!;
        var stdOut = await proc.StandardOutput.ReadToEndAsync();
        var stdErr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();

        if (proc.ExitCode != 0)
        {
            throw new Exception($"LibreOffice conversion failed (code {proc.ExitCode}). stderr: {stdErr}");
        }
    }

    // --------------------------- Extraction ----------------------------------

    private static DeckDto ExtractDeck(Stream pptxStream, string fileName)
    {
        using var doc = PresentationDocument.Open(pptxStream, false);
        var presPart = doc.PresentationPart ?? throw new InvalidOperationException("Not a PPTX (no PresentationPart)");
        var pres = presPart.Presentation ?? throw new InvalidOperationException("Invalid PPTX (no Presentation)");

        var slideIds = pres.SlideIdList?.Elements<SlideId>()?.ToList() ?? new List<SlideId>();
        var deck = new DeckDto { file = Path.GetFileName(fileName), slideCount = slideIds.Count };

        for (int i = 0; i < slideIds.Count; i++)
        {
            var relId = slideIds[i].RelationshipId!.Value!;
            var slidePart = (SlidePart)presPart.GetPartById(relId);
            var sld = slidePart.Slide!;
            var spTree = sld.CommonSlideData!.ShapeTree!;

            var slideDto = new SlideDto { index = i };

            int z = 0; // increment as we encounter elements (top-most last)
            foreach (var child in spTree.ChildElements)
                ExtractElements(child, slidePart, slideDto.elements, ref z);

            deck.slides.Add(slideDto);
        }

        return deck;
    }

    // Walk the shape tree; add textboxes, pictures, tables; recurse into groups
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
                        bbox = ReadEffectiveBBox(sp, slidePart),
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
                        imgPath = imgPart.Uri.OriginalString.TrimStart('/'); // "ppt/media/imageN.ext"
                        using var s = imgPart.GetStream(System.IO.FileMode.Open, System.IO.FileAccess.Read);
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
                ExtractElements(child, slidePart, outList, ref z);
        }
    }

    // ---------------------------- Helpers ------------------------------------

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

                for (int rr = r; rr < Math.Min(r + rowSpan, rows); rr++)
                    for (int cc = cOut; cc < Math.Min(cOut + colSpan, cols); cc++)
                        covered[rr, cc] = true;

                cOut += colSpan;
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

    private static long Sum(long[] arr, int start, int count)
    {
        long total = 0;
        int end = Math.Min(start + count, arr.Length);
        for (int i = start; i < end; i++) total += arr[i];
        return total;
    }

    private static string MakeKey(SlidePart slidePart, string type, uint id)
    {
        var uri = slidePart.Uri.OriginalString.TrimStart('/'); // "ppt/slides/slide5.xml"
        return $"{uri}#{type}:{id}";
    }

    // For tables/graphic frames
    private static BBox ReadEffectiveBBox(P.GraphicFrame gf, SlidePart slidePart)
    {
        var t = gf.Transform;
        if (t != null) return ReadBBox(t);

        var ph = gf.NonVisualGraphicFrameProperties?
                .ApplicationNonVisualDrawingProperties?
                .GetFirstChild<P.PlaceholderShape>();
        if (ph == null) return new BBox();

        var layoutTree = slidePart.SlideLayoutPart?.SlideLayout?.CommonSlideData?.ShapeTree;
        var fromLayout = FindGraphicFramePlaceholderTransform(layoutTree, ph);
        if (fromLayout != null) return ReadBBox(fromLayout);

        var masterTree = slidePart.SlideLayoutPart?.SlideMasterPart?.SlideMaster?.CommonSlideData?.ShapeTree;
        var fromMaster = FindGraphicFramePlaceholderTransform(masterTree, ph);
        if (fromMaster != null) return ReadBBox(fromMaster);

        return new BBox();
    }

    private static P.Transform? FindGraphicFramePlaceholderTransform(P.ShapeTree? tree, P.PlaceholderShape phTarget)
    {
        if (tree == null) return null;

        var targetType = phTarget.Type?.Value ?? P.PlaceholderValues.Body;
        var targetIdx  = phTarget.Index?.Value;

        foreach (var g in tree.Descendants<P.GraphicFrame>())
        {
            var ph = g.NonVisualGraphicFrameProperties?
                    .ApplicationNonVisualDrawingProperties?
                    .GetFirstChild<P.PlaceholderShape>();
            if (ph == null) continue;

            var candType = ph.Type?.Value ?? P.PlaceholderValues.Body;
            var candIdx  = ph.Index?.Value;

            bool typeMatch = candType == targetType;
            bool idxMatch  = targetIdx == null || candIdx == targetIdx;

            if (typeMatch && idxMatch)
                return g.Transform;
        }
        return null;
    }

    private static BBox ReadBBox(P.Transform? t)
    {
        if (t == null) return new BBox();
        return new BBox
        {
            x = (long?)t.Offset?.X,
            y = (long?)t.Offset?.Y,
            w = (long?)t.Extents?.Cx,
            h = (long?)t.Extents?.Cy
        };
    }

    // Slide-level transform for shapes/pictures
    private static BBox ReadBBox(A.Transform2D? xfrm)
    {
        if (xfrm == null) return new BBox();
        return new BBox
        {
            x = (long?)xfrm.Offset?.X,
            y = (long?)xfrm.Offset?.Y,
            w = (long?)xfrm.Extents?.Cx,
            h = (long?)xfrm.Extents?.Cy
        };
    }

    // For textboxes only: slide override, else inherit from layout/master placeholder
    private static BBox ReadEffectiveBBox(P.Shape sp, SlidePart slidePart)
    {
        var xfrm = sp.ShapeProperties?.Transform2D;
        if (xfrm != null) return ReadBBox(xfrm);

        var ph = sp.NonVisualShapeProperties?
                   .ApplicationNonVisualDrawingProperties?
                   .GetFirstChild<P.PlaceholderShape>();
        if (ph == null) return new BBox();

        var layoutTree = slidePart.SlideLayoutPart?.SlideLayout?.CommonSlideData?.ShapeTree;
        var fromLayout = FindPlaceholderTransform(layoutTree, ph);
        if (fromLayout != null) return ReadBBox(fromLayout);

        var masterTree = slidePart.SlideLayoutPart?.SlideMasterPart?.SlideMaster?.CommonSlideData?.ShapeTree;
        var fromMaster = FindPlaceholderTransform(masterTree, ph);
        if (fromMaster != null) return ReadBBox(fromMaster);

        return new BBox();
    }

    private static A.Transform2D? FindPlaceholderTransform(P.ShapeTree? tree, P.PlaceholderShape phTarget)
    {
        if (tree == null) return null;

        var targetType = phTarget.Type?.Value ?? P.PlaceholderValues.Body;
        var targetIdx = phTarget.Index?.Value;

        foreach (var s in tree.Descendants<P.Shape>())
        {
            var ph = s.NonVisualShapeProperties?
                       .ApplicationNonVisualDrawingProperties?
                       .GetFirstChild<P.PlaceholderShape>();
            if (ph == null) continue;

            var candType = ph.Type?.Value ?? P.PlaceholderValues.Body;
            var candIdx = ph.Index?.Value;

            bool typeMatch = candType == targetType;
            bool idxMatch = targetIdx == null || candIdx == targetIdx;

            if (typeMatch && idxMatch)
                return s.ShapeProperties?.Transform2D;
        }
        return null;
    }

    private static List<ParagraphDto> ExtractParagraphs(A.TextBody? tb)
    {
        var list = new List<ParagraphDto>();
        if (tb == null) return list;

        foreach (var p in tb.Elements<A.Paragraph>())
        {
            var sb = new StringBuilder();
            foreach (var node in p.ChildElements)
            {
                if (node is A.Run run) sb.Append(run.Text?.Text ?? "");
                else if (node is A.Break) sb.Append('\n');
                else if (node is A.Field fld) sb.Append(fld.Text?.Text ?? "");
            }

            var text = sb.ToString().TrimEnd();
            if (string.IsNullOrWhiteSpace(text)) continue;

            int level = (int?)p.ParagraphProperties?.Level?.Value ?? 0;
            list.Add(new ParagraphDto { level = level, text = text });
        }
        return list;
    }

    private static List<ParagraphDto> ExtractParagraphs(P.TextBody? tb)
    {
        var list = new List<ParagraphDto>();
        if (tb == null) return list;

        foreach (var p in tb.Elements<A.Paragraph>())
        {
            var sb = new StringBuilder();
            foreach (var node in p.ChildElements)
            {
                if (node is A.Run run) sb.Append(run.Text?.Text ?? "");
                else if (node is A.Break) sb.Append('\n');
                else if (node is A.Field fld) sb.Append(fld.Text?.Text ?? "");
            }

            var text = sb.ToString().TrimEnd();
            if (string.IsNullOrWhiteSpace(text)) continue;

            int level = (int?)p.ParagraphProperties?.Level?.Value ?? 0;
            list.Add(new ParagraphDto { level = level, text = text });
        }
        return list;
    }
}
