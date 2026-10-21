using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace ImageTo3D.Api.Tests.Infrastructure;

/// <summary>
/// Stand-in for the internet in reference-search tests: routes match on host and path, anything
/// else answers 404. Records every request so tests can assert what was (not) contacted.
/// </summary>
public sealed class FakeReferenceWeb : HttpMessageHandler
{
    private readonly ConcurrentDictionary<string, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _routes = new();

    public ConcurrentQueue<HttpRequestMessage> Requests { get; } = new();

    public int Count(string hostAndPath) => Requests.Count(r => Key(r.RequestUri!) == hostAndPath);

    /// <summary>Route for "host/path" (no query string).</summary>
    public void On(string hostAndPath, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) => _routes[hostAndPath] = handler;

    public void OnJson(string hostAndPath, string json, HttpStatusCode status = HttpStatusCode.OK) =>
        On(hostAndPath, (_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") }));

    public void OnStatus(string hostAndPath, HttpStatusCode status) =>
        On(hostAndPath, (_, _) => Task.FromResult(new HttpResponseMessage(status)));

    public void OnImage(string hostAndPath, byte[] bytes, string contentType = "image/png") =>
        On(hostAndPath, (_, _) =>
        {
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });

    public void OnRedirect(string hostAndPath, string location) =>
        On(hostAndPath, (_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri(location);
            return Task.FromResult(response);
        });

    /// <summary>Never answers until the caller gives up (timeout or cancellation).</summary>
    public void OnHang(string hostAndPath) =>
        On(hostAndPath, async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("unreachable");
        });

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Enqueue(request);
        return _routes.TryGetValue(Key(request.RequestUri!), out var handler)
            ? handler(request, cancellationToken)
            : Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    // Shared by every client the factory builds; the factory's handler rotation must not dispose it.
    protected override void Dispose(bool disposing)
    {
    }

    private static string Key(Uri uri) => uri.Host + uri.AbsolutePath;
}
