// DTOs for instruction-derived redaction rules and result summaries.
using System.Collections.Generic;

public class RedactionRules
{
    public bool MaskClientNames { get; set; }
    public bool MaskRevenue { get; set; }
    public bool MaskEmails { get; set; }
    public List<string> Keywords { get; set; } = new();
}

public class AnnotateResponse
{
    public string? Instructions { get; set; }
    public RedactionRules? Rules { get; set; }
}

public class RedactionMatch
{
    public string Type { get; set; } = "";
    public string Original { get; set; } = "";
}

public class RedactionEntry
{
    public int Slide { get; set; }
    public string? ElementKey { get; set; }
    public int Paragraph { get; set; }
    public List<RedactionMatch> Matches { get; set; } = new();
    public TableCellRef? Cell { get; set; }
}

public class TableCellRef
{
    public int? Row { get; set; }
    public int? Col { get; set; }
}

