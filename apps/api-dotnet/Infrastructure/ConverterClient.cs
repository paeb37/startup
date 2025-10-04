namespace Dexter.WebApi.Infrastructure;

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dexter.WebApi.Common.Logging;
using Dexter.WebApi.Infrastructure.Options;
using Microsoft.Extensions.Options;

internal sealed class ConverterClient
{
    private readonly HttpClient _httpClient;
    private readonly IOptionsMonitor<ConverterOptions> _options;

    public ConverterClient(HttpClient httpClient, IOptionsMonitor<ConverterOptions> options)
    {
        _httpClient = httpClient;
        _options = options;
    }

    public async Task ConvertPptxToPdfAsync(string inputPath, string outDir, CancellationToken cancellationToken, int? slide = null)
    {
        var baseUrl = _options.CurrentValue.BaseUrl;
        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            try
            {
                await ConvertPptxToPdfRemoteAsync(baseUrl!, inputPath, outDir, cancellationToken, slide);
                return;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Remote converter failed: {ex.Message}. Falling back to local LibreOffice conversion.");
            }
        }

        await ConvertPptxToPdfLocalAsync(inputPath, outDir);
    }

    private async Task ConvertPptxToPdfRemoteAsync(string baseUrl, string inputPath, string outDir, CancellationToken cancellationToken, int? slide)
    {
        var requestUrl = new StringBuilder(baseUrl);
        requestUrl.Append("/export?fmt=pdf");
        if (slide.HasValue)
        {
            requestUrl.Append("&slide=").Append(slide.Value);
        }

        var pptxBytes = await File.ReadAllBytesAsync(inputPath, cancellationToken);
        using var content = new MultipartFormDataContent();
        var fileName = Path.GetFileName(inputPath);
        var pptxContent = new ByteArrayContent(pptxBytes)
        {
            Headers =
            {
                ContentType = new MediaTypeHeaderValue("application/vnd.openxmlformats-officedocument.presentationml.presentation")
            }
        };
        content.Add(pptxContent, "file", fileName);

        using var request = new HttpRequestMessage(HttpMethod.Post, requestUrl.ToString())
        {
            Content = content
        };

        var sw = Stopwatch.StartNew();
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var elapsed = sw.Elapsed;

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            OperationTimer.LogTiming("remote converter failed", elapsed, Path.GetFileName(inputPath));
            throw new Exception($"Converter service returned {(int)response.StatusCode}: {error}");
        }

        var pdfPath = Path.Combine(outDir, Path.GetFileNameWithoutExtension(inputPath) + ".pdf");
        await using (var target = File.Create(pdfPath))
        {
            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await responseStream.CopyToAsync(target, cancellationToken);
        }

        if (response.Headers.TryGetValues("X-Convert-Time", out var convertHeader) && convertHeader.FirstOrDefault() is { Length: > 0 } headerValue)
        {
            OperationTimer.LogTiming("remote converter", elapsed, $"{Path.GetFileName(inputPath)} | {headerValue}");
        }
        else
        {
            OperationTimer.LogTiming("remote converter", elapsed, Path.GetFileName(inputPath));
        }
    }

    private static async Task ConvertPptxToPdfLocalAsync(string inputPath, string outDir)
    {
        var soffice = Environment.GetEnvironmentVariable("SOFFICE_PATH") ?? "soffice";

        var profileDir = Path.Combine(Path.GetTempPath(), "libreoffice-profile", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(profileDir);
        var profileUri = new Uri(profileDir.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
            ? profileDir
            : profileDir + Path.DirectorySeparatorChar).AbsoluteUri;

        var psi = new ProcessStartInfo
        {
            FileName = soffice,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add("--headless");
        psi.ArgumentList.Add("--nologo");
        psi.ArgumentList.Add("--nofirststartwizard");
        psi.ArgumentList.Add("--norestore");
        psi.ArgumentList.Add($"-env:UserInstallation={profileUri}");
        psi.ArgumentList.Add("--convert-to");
        psi.ArgumentList.Add("pdf");
        psi.ArgumentList.Add("--outdir");
        psi.ArgumentList.Add(outDir);
        psi.ArgumentList.Add(inputPath);

        var sw = Stopwatch.StartNew();
        using var proc = Process.Start(psi)!;
        var stdOut = await proc.StandardOutput.ReadToEndAsync();
        var stdErr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        var elapsed = sw.Elapsed;

        TryDeleteDirectory(profileDir);

        if (proc.ExitCode != 0)
        {
            OperationTimer.LogTiming("libreoffice convert failed", elapsed, Path.GetFileName(inputPath));
            throw new Exception($"LibreOffice conversion failed (code {proc.ExitCode}). stderr: {stdErr}. stdout: {stdOut}");
        }

        OperationTimer.LogTiming("libreoffice convert", elapsed, Path.GetFileName(inputPath));
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // best effort cleanup
        }
    }
}
