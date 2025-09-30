// DTOs describing the extracted presentation deck, slides, and elements.
using System;
using System.Collections.Generic;

public class DeckDto
{
    public string file { get; set; } = "";
    public int slideCount { get; set; }
    public long slideWidthEmu { get; set; }
    public long slideHeightEmu { get; set; }
    public List<SlideDto> slides { get; set; } = new();
}

public class SlideDto
{
    public int index { get; set; }
    public List<ElementDto> elements { get; set; } = new();
}

public abstract class ElementDto
{
    public string key { get; set; } = "";
    public uint id { get; set; }
    public string? name { get; set; }
    public BBox bbox { get; set; } = new();
    public int z { get; set; }
}

public sealed class TextboxDto : ElementDto
{
    public List<ParagraphDto> paragraphs { get; set; } = new();
}

public sealed class PictureDto : ElementDto
{
    public string? imgPath { get; set; }
    public long? bytes { get; set; }
    public string? summary { get; set; }
}

public sealed class TableDto : ElementDto
{
    public int rows { get; set; }
    public int cols { get; set; }
    public long[]? colWidths { get; set; }
    public long[]? rowHeights { get; set; }
    public TableCellDto?[][] cells { get; set; } = Array.Empty<TableCellDto?[]>();
    public string? summary { get; set; }
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
