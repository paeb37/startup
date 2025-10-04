namespace Dexter.WebApi.Infrastructure;

public sealed record SupabaseSettings(
    string Url,
    string ServiceKey,
    string DecksTable,
    string SlidesTable,
    string RulesTable,
    string RuleActionsTable,
    string? OpenAiKey,
    string EmbeddingModel,
    string StorageBucket,
    string StoragePathPrefix,
    string VisionModel);
