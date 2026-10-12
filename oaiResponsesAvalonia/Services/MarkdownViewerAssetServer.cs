using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace oaiResponsesAvalonia.Services;

internal sealed class MarkdownViewerAssetServer : IAsyncDisposable
{
    private readonly HttpListener _listener;
    private readonly string _assetRoot;
    private readonly string _assetRootPrefix;
    private readonly string _requestPathPrefix;
    private readonly StringComparison _pathComparison;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _requestLoop;
    private bool _disposed;

    private MarkdownViewerAssetServer(HttpListener listener, string assetRoot, string requestPathPrefix, Uri baseUri)
    {
        _listener = listener;
        _assetRoot = assetRoot;
        _assetRootPrefix = assetRoot + Path.DirectorySeparatorChar;
        _requestPathPrefix = requestPathPrefix;
        _pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        BaseUri = baseUri;
        _requestLoop = ProcessRequestsAsync();
    }

    public Uri BaseUri { get; }

    public Uri TemplateUri => new(BaseUri, "template.html");

    public static MarkdownViewerAssetServer Start(string assetDirectory)
    {
        if (!Directory.Exists(assetDirectory))
            throw new DirectoryNotFoundException($"Markdown viewer assets were not found: {assetDirectory}");

        string assetRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(assetDirectory));
        string token = Guid.NewGuid().ToString("N");
        string requestPathPrefix = $"/{token}/";

        for (int attempt = 0; attempt < 5; attempt++)
        {
            int port = GetAvailablePort();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}{requestPathPrefix}");

            try
            {
                listener.Start();
                return new MarkdownViewerAssetServer(
                    listener,
                    assetRoot,
                    requestPathPrefix,
                    new Uri($"http://127.0.0.1:{port}{requestPathPrefix}"));
            }
            catch (HttpListenerException)
            {
                listener.Close();
            }
        }

        throw new InvalidOperationException("Could not start the Markdown viewer asset server on a local port.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        _stop.Cancel();
        _listener.Close();

        try
        {
            await _requestLoop.ConfigureAwait(false);
        }
        catch
        {
        }

        _stop.Dispose();
    }

    private static int GetAvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private async Task ProcessRequestsAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (HttpListenerException) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException) when (_stop.IsCancellationRequested)
            {
                return;
            }

            await ServeRequestAsync(context);
        }
    }

    private async Task ServeRequestAsync(HttpListenerContext context)
    {
        try
        {
            HttpListenerRequest request = context.Request;
            HttpListenerResponse response = context.Response;

            if (request.HttpMethod != "GET" && request.HttpMethod != "HEAD")
            {
                response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                return;
            }

            string requestPath = request.Url?.AbsolutePath ?? string.Empty;
            if (!requestPath.StartsWith(_requestPathPrefix, StringComparison.Ordinal))
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }

            string relativePath = Uri.UnescapeDataString(requestPath[_requestPathPrefix.Length..])
                .Replace('/', Path.DirectorySeparatorChar);
            string filePath = Path.GetFullPath(Path.Combine(_assetRoot, relativePath));
            if (!filePath.StartsWith(_assetRootPrefix, _pathComparison) || !File.Exists(filePath))
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }

            response.ContentType = GetContentType(filePath);
            response.Headers[HttpResponseHeader.CacheControl] = "no-store";
            response.Headers["X-Content-Type-Options"] = "nosniff";

            using var file = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            response.ContentLength64 = file.Length;
            if (request.HttpMethod == "GET")
                await file.CopyToAsync(response.OutputStream);
        }
        catch
        {
            try
            {
                context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
            }
            catch
            {
            }
        }
        finally
        {
            try
            {
                context.Response.Close();
            }
            catch
            {
            }
        }
    }

    private static string GetContentType(string filePath) => Path.GetExtension(filePath).ToLowerInvariant() switch
    {
        ".css" => "text/css; charset=utf-8",
        ".html" => "text/html; charset=utf-8",
        ".js" => "text/javascript; charset=utf-8",
        ".json" => "application/json; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".ico" => "image/x-icon",
        ".woff" => "font/woff",
        ".woff2" => "font/woff2",
        ".ttf" => "font/ttf",
        _ => "application/octet-stream"
    };
}
