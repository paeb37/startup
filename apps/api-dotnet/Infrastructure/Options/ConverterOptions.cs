namespace Dexter.WebApi.Infrastructure.Options;

using System;

public class ConverterOptions
{
    public string? BaseUrl { get; set; }
    public int TimeoutSeconds { get; set; } = 60;

    public void ApplyEnvironmentFallbacks()
    {
        BaseUrl = Resolve(BaseUrl, "LIBRE_CONVERTER_URL")?.Trim();
        if (!string.IsNullOrWhiteSpace(BaseUrl))
        {
            BaseUrl = BaseUrl.TrimEnd('/');
        }
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
