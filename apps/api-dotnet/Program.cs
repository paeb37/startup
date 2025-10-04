// ASP.NET Core entry point and HTTP endpoints for PPTX extraction and redaction.
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.IO.Compression;
using System.Diagnostics;
using System.Text.Json.Serialization.Metadata;
using System.ComponentModel;
using Microsoft.Extensions.Configuration;
using DotNetEnv;
using Docnet.Core;
using Docnet.Core.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Formats.Png;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Dexter.WebApi.Decks;
using Dexter.WebApi.Decks.Models;
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

    internal static JsonSerializerOptions CreateDeckJsonOptions()
    {
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

        return new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            TypeInfoResolver = resolver
        };
    }


    internal static byte[] RenderSlideToPng(
        byte[] pdfBytes,
        int slideNumber,
        int targetWidth = 960)
    {
        var pageIndex = slideNumber - 1;
        if (pageIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(slideNumber));
        }

        var widthHint = targetWidth > 0 ? targetWidth : 1024;
        var heightHintCandidate = Math.Max(1, (int)Math.Round(widthHint * (3.0 / 4.0)));
        var heightHint = Math.Max(widthHint + 1, heightHintCandidate);

        using var docReader = DocLib.Instance.GetDocReader(pdfBytes, new PageDimensions(widthHint, heightHint));
        var pageCount = docReader.GetPageCount();
        if (pageIndex >= pageCount)
        {
            throw new ArgumentOutOfRangeException(nameof(slideNumber), $"Slide {slideNumber} does not exist (deck has {pageCount} slides).");
        }

        using var pageReader = docReader.GetPageReader(pageIndex);
        var rawBytes = pageReader.GetImage();
        var pageWidth = pageReader.GetPageWidth();
        var pageHeight = pageReader.GetPageHeight();
        var width = Math.Max(1, (int)Math.Round(Convert.ToDouble(pageWidth), MidpointRounding.AwayFromZero));
        var height = Math.Max(1, (int)Math.Round(Convert.ToDouble(pageHeight), MidpointRounding.AwayFromZero));

        using var image = Image.LoadPixelData<Bgra32>(rawBytes, width, height);

        if (targetWidth > 0 && image.Width > targetWidth)
        {
            var ratio = targetWidth / (double)image.Width;
            var newHeight = Math.Max(1, (int)Math.Round(image.Height * ratio));
            image.Mutate(ctx => ctx.Resize(targetWidth, newHeight));
        }

        using var output = new MemoryStream();
        image.Save(output, new PngEncoder
        {
            CompressionLevel = PngCompressionLevel.Level6
        });

        return output.ToArray();
    }

}
