// DTOs for annotation responses, actions, and redaction summaries.
using System.Collections.Generic;

public class AnnotationResponse
{
    public string? Instructions { get; set; }
    public string? DeckId { get; set; }
    public List<ActionDto> Actions { get; set; } = new();
    public Dictionary<string, object?>? Meta { get; set; }
}

public class ActionDto
{
    public string? Id { get; set; }
    public string Type { get; set; } = "replace";
    public ActionScope Scope { get; set; } = new();
    public ActionMatch? Match { get; set; }
    public string? Replacement { get; set; }
    public RewriteInstruction? Rewrite { get; set; }
}

public class ActionScope
{
    public SlideScope Slides { get; set; } = new();
}

public class SlideScope
{
    public int? From { get; set; }
    public int? To { get; set; }
    public List<int>? List { get; set; }
}

public class ActionMatch
{
    public string? Mode { get; set; }
    public List<string>? Tokens { get; set; }
    public string? Pattern { get; set; }
}

public class RewriteInstruction
{
    public string? Instructions { get; set; }
    public double? MaxLengthRatio { get; set; }
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

