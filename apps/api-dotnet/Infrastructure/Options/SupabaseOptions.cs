namespace Dexter.WebApi.Infrastructure.Options;

using System;

public class SupabaseOptions
{
    public string? Url { get; set; }
    public string? ServiceKey { get; set; }
    public string StorageBucket { get; set; } = string.Empty;
    public string StoragePathPrefix { get; set; } = string.Empty;
    public string DecksTable { get; set; } = "decks";
    public string SlidesTable { get; set; } = "slides";
    public string RulesTable { get; set; } = "rules";
    public string RuleActionsTable { get; set; } = "rule_actions";

    public void ApplyEnvironmentFallbacks()
    {
        Url = (Resolve(Url, "SUPABASE_URL") ?? string.Empty).Trim().TrimEnd('/');
        ServiceKey = Resolve(ServiceKey, "SUPABASE_SERVICE_ROLE_KEY")?.Trim();
        StorageBucket = (Resolve(StorageBucket, "SUPABASE_STORAGE_BUCKET") ?? string.Empty).Trim();
        StoragePathPrefix = (Resolve(StoragePathPrefix, "SUPABASE_STORAGE_PREFIX") ?? string.Empty).Trim().Trim('/');
        DecksTable = string.IsNullOrWhiteSpace(DecksTable) ? "decks" : DecksTable;
        SlidesTable = string.IsNullOrWhiteSpace(SlidesTable) ? "slides" : SlidesTable;
        RulesTable = string.IsNullOrWhiteSpace(RulesTable) ? "rules" : RulesTable;
        RuleActionsTable = string.IsNullOrWhiteSpace(RuleActionsTable) ? "rule_actions" : RuleActionsTable;
    }

    public bool IsConfigured()
        => !string.IsNullOrWhiteSpace(Url)
           && !string.IsNullOrWhiteSpace(ServiceKey)
           && !string.IsNullOrWhiteSpace(StorageBucket);

    private static string? Resolve(string? current, string envVar)
    {
        if (!string.IsNullOrWhiteSpace(current))
        {
            return current;
        }

        return Environment.GetEnvironmentVariable(envVar);
    }
}
