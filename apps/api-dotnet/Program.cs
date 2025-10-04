// ASP.NET Core entry point and HTTP endpoints for PPTX extraction and redaction.
using Microsoft.Extensions.Configuration;
using DotNetEnv;
using Microsoft.Extensions.DependencyInjection;
using Dexter.WebApi.Decks;
using Dexter.WebApi.Decks.Services;
using Dexter.WebApi.Infrastructure;
using Dexter.WebApi.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace Dexter.WebApi;

public static partial class Program
{
    public static async Task Main(string[] args)
    {
        try
        {
            Env.TraversePath().Load();
        }
        catch (FileNotFoundException)
        {
            // .env is optional; ignore if not found.
        }

        Console.WriteLine($"[env] SUPABASE_URL={Environment.GetEnvironmentVariable("SUPABASE_URL") ?? "<null>"}");
        Console.WriteLine($"[env] SUPABASE_SERVICE_ROLE_KEY={(Environment.GetEnvironmentVariable("SUPABASE_SERVICE_ROLE_KEY") is { Length: > 0 } ? "<set>" : "<null>")}");
        Console.WriteLine($"[env] SUPABASE_STORAGE_BUCKET={Environment.GetEnvironmentVariable("SUPABASE_STORAGE_BUCKET") ?? "<null>"}");

        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddOptions<SupabaseOptions>()
            .Bind(builder.Configuration.GetSection("Supabase"))
            .PostConfigure(options => options.ApplyEnvironmentFallbacks());

        builder.Services.AddOptions<OpenAiOptions>()
            .Bind(builder.Configuration.GetSection("OpenAI"))
            .PostConfigure(options => options.ApplyEnvironmentFallbacks());

        builder.Services.AddOptions<ConverterOptions>()
            .Bind(builder.Configuration.GetSection("Converter"))
            .PostConfigure(options => options.ApplyEnvironmentFallbacks());

        builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
            p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));
        builder.Services.AddMemoryCache();
        builder.Services.AddSingleton<DeckExtractor>();
        builder.Services.AddSingleton<DeckRedactionService>();
        builder.Services.AddScoped<DeckWorkflowService>();

        builder.Services.AddHttpClient<OpenAiClient>();
        builder.Services.AddHttpClient<SupabaseClient>();
        builder.Services.AddHttpClient<ConverterClient>((sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<ConverterOptions>>().Value;
            if (!string.IsNullOrWhiteSpace(options.BaseUrl) && Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var baseUri))
            {
                client.BaseAddress = baseUri;
            }

            if (options.TimeoutSeconds > 0)
            {
                client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
            }
        });

        var app = builder.Build();
        app.UseCors();

        DeckEndpoints.MapDeckEndpoints(app);

        await app.RunAsync();
    }

}
