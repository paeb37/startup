// ASP.NET Core entry point and HTTP endpoints for PPTX extraction and redaction.
using Microsoft.Extensions.Configuration;
using DotNetEnv;
using Microsoft.Extensions.DependencyInjection;
using Dexter.WebApi.Decks;
using Dexter.WebApi.Decks.Services;
using Dexter.WebApi.Infrastructure;

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

        builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
            p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));
        builder.Services.AddMemoryCache();
        builder.Services.AddSingleton<DeckExtractor>();
        builder.Services.AddSingleton<DeckRedactionService>();
        builder.Services.AddScoped<DeckWorkflowService>();

        builder.Services.AddHttpClient<OpenAiClient>();
        builder.Services.AddHttpClient<SupabaseClient>();
        builder.Services.AddHttpClient<ConverterClient>();

        var app = builder.Build();
        app.UseCors();

        DeckEndpoints.MapDeckEndpoints(app);

        await app.RunAsync();
    }

}
