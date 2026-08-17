using FikaHeadlessManager.Models;
using Microsoft.Extensions.Logging;
using System.IO.Compression;
using System.Text.Json;

namespace FikaHeadlessManager.Server;

public sealed class ServerClient : IDisposable
{
    private const string PresenceEndpoint = "fika/presence/get";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly ILogger<ServerClient> _logger;
    private readonly HttpClient _client;
    private readonly Uri _backendUrl;

    public ServerClient(ILogger<ServerClient> logger, Settings settings)
    {
        _logger = logger;
        _backendUrl = settings.BackendUrl!;

        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
            UseProxy = false
        };

        _client = new HttpClient(handler);
    }

    public async Task<bool> IsAccessibleAsync(CancellationToken token = default)
    {
        try
        {
            using var response = await _client.SendAsync(BuildJsonRequest(PresenceEndpoint), token);

            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            _logger.LogError("Could not access {Url}\nEnsure Fika Server mod is installed. Please review the installation process in the documentation.",
                BuildUrl(PresenceEndpoint));
            return false;
        }
        catch
        {
            _logger.LogError("Could not reach SPT.Server at {BackendUrl}\nPlease ensure SPT.Server is running and accessible.", _backendUrl);
            return false;
        }
    }

    public async Task<T?> GetJsonAsync<T>(string endpoint, CancellationToken token)
    {
        using var response = await _client.SendAsync(BuildJsonRequest(endpoint), token);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsByteArrayAsync(token);

        await using var json = body.Length > 0 && body[0] == 0x78
            ? new ZLibStream(new MemoryStream(body), CompressionMode.Decompress)
            : (Stream)new MemoryStream(body);

        return await JsonSerializer.DeserializeAsync<T>(json, JsonOptions, token);
    }

    public async Task DownloadFileAsync(string endpoint, string destinationPath, Action<long>? onProgress, CancellationToken token)
    {
        var directory = Path.GetDirectoryName(destinationPath);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Write to a sibling temp file and move on success, so an interrupted download can never be
        // mistaken for a complete bundle
        var tempPath = $"{destinationPath}.part";

        try
        {
            using var response = await _client.GetAsync(BuildUrl(endpoint), HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();

            await using (var source = await response.Content.ReadAsStreamAsync(token))
            await using (var destination = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long written = 0;
                int read;

                while ((read = await source.ReadAsync(buffer, token)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), token);
                    written += read;
                    onProgress?.Invoke(written);
                }
            }

            File.Move(tempPath, destinationPath, true);
        }
        catch
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            throw;
        }
    }

    private HttpRequestMessage BuildJsonRequest(string endpoint)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, BuildUrl(endpoint));

        // Skip Zlib compression
        request.Headers.Add("responsecompressed", "0");

        return request;
    }

    private string BuildUrl(string endpoint)
    {
        return $"{_backendUrl.OriginalString.TrimEnd('/')}/{endpoint.TrimStart('/')}";
    }

    public void Dispose()
    {
        _client.Dispose();
    }
}
