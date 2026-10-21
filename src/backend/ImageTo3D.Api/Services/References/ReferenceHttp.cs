using System.Net;
using System.Text.Json;
using ImageTo3D.Api.Configuration;
using Microsoft.Extensions.Options;

namespace ImageTo3D.Api.Services.References;

/// <summary>A source call that failed in a known way; <see cref="Code"/> is shown per source (rate_limited, bad_response, ...).</summary>
public sealed class ReferenceFetchException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Outbound HTTP for reference search. Redirects are followed by hand (at most 3) so every hop
/// is checked against the caller's host rule: addresses come from third-party responses and must
/// never reach other hosts (or the local network). Downloads are size-capped while streaming.
/// Timeouts come from the caller's token; this class adds bounded retries only.
/// </summary>
public sealed class ReferenceHttp(IHttpClientFactory factory, IOptions<ReferenceSearchOptions> options, ILogger<ReferenceHttp> logger)
{
    public const string ClientName = "references";

    private const int MaxRedirects = 3;

    private const string JsonAccept = "application/json";

    // The formats ImageDecoder reads, then anything: Openverse's thumbnail endpoint answers 406 to a
    // bare "image/*" (checked 2026-10-21). The response's Content-Type is still required to be an image.
    private const string ImageAccept = "image/jpeg, image/png, image/webp, */*;q=0.5";

    /// <summary>GET JSON from a source API. Network errors and 5xx are retried; 429 and other 4xx are not.</summary>
    public async Task<JsonDocument> GetJsonAsync(Uri uri, Func<Uri, bool> allowed, CancellationToken cancellationToken)
    {
        var o = options.Value;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var response = await SendAsync(uri, allowed, JsonAccept, cancellationToken);
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    throw new ReferenceFetchException("rate_limited", "The source is limiting requests. Try again later.");
                if ((int)response.StatusCode >= 500)
                    throw new HttpRequestException($"Server error {(int)response.StatusCode}.", null, response.StatusCode);
                if (!response.IsSuccessStatusCode)
                    throw new ReferenceFetchException("bad_response", $"The source answered {(int)response.StatusCode}.");

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                try
                {
                    return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                }
                catch (JsonException)
                {
                    throw new ReferenceFetchException("bad_response", "The source returned something that is not JSON.");
                }
            }
            catch (HttpRequestException ex) when (attempt < o.MaxRetries)
            {
                logger.LogInformation("Reference source call to {Host} failed ({Error}); retrying", uri.Host, ex.Message);
                await Task.Delay(o.RetryDelay * (attempt + 1), cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                throw new ReferenceFetchException("unavailable", ex.StatusCode is { } s ? $"The source answered {(int)s}." : "The source could not be reached.");
            }
        }
    }

    /// <summary>Download an image of at most <paramref name="maxBytes"/>; the response must say it is an image.</summary>
    public async Task<byte[]> DownloadImageAsync(Uri uri, Func<Uri, bool> allowed, long maxBytes, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await SendAsync(uri, allowed, ImageAccept, cancellationToken);
        }
        catch (HttpRequestException)
        {
            throw new ReferenceFetchException("unavailable", "The image could not be reached.");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new ReferenceFetchException("bad_response", $"The image address answered {(int)response.StatusCode}.");
            if (response.Content.Headers.ContentType?.MediaType is not { } type || !type.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                throw new ReferenceFetchException("not_an_image", "The address did not return an image.");
            if (response.Content.Headers.ContentLength > maxBytes)
                throw new ReferenceFetchException("too_large", $"The image is larger than {maxBytes / 1024} KB.");

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + read > maxBytes)
                    throw new ReferenceFetchException("too_large", $"The image is larger than {maxBytes / 1024} KB.");
                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        }
    }

    private async Task<HttpResponseMessage> SendAsync(Uri uri, Func<Uri, bool> allowed, string accept, CancellationToken cancellationToken)
    {
        var client = factory.CreateClient(ClientName);
        var current = uri;
        for (var hop = 0; ; hop++)
        {
            if (!allowed(current))
                throw new ReferenceFetchException("untrusted_host", $"Refused to contact {current.Host}: not an address this source uses.");

            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.Accept.ParseAdd(accept);
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if ((int)response.StatusCode is < 300 or >= 400 || response.StatusCode == HttpStatusCode.NotModified)
                return response;

            var location = response.Headers.Location;
            response.Dispose();
            if (location is null || hop >= MaxRedirects)
                throw new ReferenceFetchException("bad_response", "Too many or invalid redirects.");
            current = location.IsAbsoluteUri ? location : new Uri(current, location);
        }
    }
}
