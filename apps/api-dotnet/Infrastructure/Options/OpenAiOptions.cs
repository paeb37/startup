namespace Dexter.WebApi.Infrastructure.Options;

using System;

public class OpenAiOptions
{
    public string? ApiKey { get; set; }
    public string EmbeddingModel { get; set; } = "text-embedding-3-small";
    public string VisionModel { get; set; } = "gpt-4o-mini";

    public void ApplyEnvironmentFallbacks()
    {
        ApiKey = Resolve(ApiKey, "OPENAI_API_KEY")?.Trim();
        EmbeddingModel = (Resolve(EmbeddingModel, "OPENAI_EMBEDDING_MODEL") ?? EmbeddingModel).Trim();
        VisionModel = (Resolve(VisionModel, "OPENAI_VISION_MODEL") ?? VisionModel).Trim();
    }

    private static string? Resolve(string? current, string envVar)
    {
        if (!string.IsNullOrWhiteSpace(current))
        {
            return current;
        }

        return Environment.GetEnvironmentVariable(envVar);
    }
}
