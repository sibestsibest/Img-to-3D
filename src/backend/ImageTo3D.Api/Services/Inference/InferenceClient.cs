using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ImageTo3D.Core.Contracts;
using ImageTo3D.Core.Hosting;

namespace ImageTo3D.Api.Services.Inference;

public interface IInferenceClient
{
    Task<InferenceHealthDto?> GetHealthAsync(CancellationToken cancellationToken);

    Task<InferenceJobDto> SubmitAsync(byte[] png, string correlationId, CancellationToken cancellationToken);

    /// <summary>Returns null when the inference service no longer knows the job (e.g. it restarted).</summary>
    Task<InferenceJobDto?> GetJobAsync(Guid inferenceJobId, CancellationToken cancellationToken);

    Task<byte[]> DownloadArtifactAsync(Guid inferenceJobId, string name, CancellationToken cancellationToken);

    Task CancelAsync(Guid inferenceJobId, CancellationToken cancellationToken);

    Task DeleteAsync(Guid inferenceJobId, CancellationToken cancellationToken);
}

/// <summary>The inference service could not be reached or did not answer in time.</summary>
public sealed class InferenceUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The inference service answered with a non-success status.</summary>
public sealed class InferenceRequestException(HttpStatusCode status, string? code, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;

    public string? Code { get; } = code;
}

public sealed class InferenceClient(HttpClient http, ILogger<InferenceClient> logger) : IInferenceClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<InferenceHealthDto?> GetHealthAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync("health", cancellationToken);
            return await response.Content.ReadFromJsonAsync<InferenceHealthDto>(Json, cancellationToken);
        }
        catch (Exception ex) when (IsTransport(ex, cancellationToken))
        {
            return null;
        }
    }

    public async Task<InferenceJobDto> SubmitAsync(byte[] png, string correlationId, CancellationToken cancellationToken)
    {
        using var content = new MultipartFormDataContent();
        var image = new ByteArrayContent(png);
        image.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        content.Add(image, "image", "source.png");

        using var request = new HttpRequestMessage(HttpMethod.Post, "internal/jobs/") { Content = content };
        request.Headers.Add(CorrelationIdMiddleware.HeaderName, correlationId);
        return await SendForJsonAsync<InferenceJobDto>(request, cancellationToken)
               ?? throw new InferenceRequestException(HttpStatusCode.BadGateway, null, "Empty response from inference service.");
    }

    public async Task<InferenceJobDto?> GetJobAsync(Guid inferenceJobId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"internal/jobs/{inferenceJobId}");
        try
        {
            return await SendForJsonAsync<InferenceJobDto>(request, cancellationToken);
        }
        catch (InferenceRequestException ex) when (ex.Status == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<byte[]> DownloadArtifactAsync(Guid inferenceJobId, string name, CancellationToken cancellationToken)
    {
        if (!InferenceArtifactNames.All.Contains(name))
            throw new ArgumentException("Unknown artifact.", nameof(name));

        using var request = new HttpRequestMessage(HttpMethod.Get, $"internal/jobs/{inferenceJobId}/artifacts/{name}");
        using var response = await SendAsync(request, cancellationToken);
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    public async Task CancelAsync(Guid inferenceJobId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"internal/jobs/{inferenceJobId}/cancel");
        try
        {
            using var _ = await SendAsync(request, cancellationToken);
        }
        catch (InferenceRequestException ex) when (ex.Status is HttpStatusCode.NotFound or HttpStatusCode.Conflict)
        {
            // Already gone or already finished: nothing to cancel.
        }
    }

    public async Task DeleteAsync(Guid inferenceJobId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"internal/jobs/{inferenceJobId}");
        try
        {
            using var _ = await SendAsync(request, cancellationToken);
        }
        catch (InferenceRequestException ex) when (ex.Status == HttpStatusCode.NotFound)
        {
        }
    }

    private async Task<T?> SendForJsonAsync<T>(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(request, cancellationToken);
        return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken);
        }
        catch (Exception ex) when (IsTransport(ex, cancellationToken))
        {
            logger.LogWarning("Inference service call {Method} {Path} failed: {Error}", request.Method, request.RequestUri, ex.GetType().Name);
            throw new InferenceUnavailableException("The 3D generation service is unavailable. Please try again later.", ex);
        }

        if (response.IsSuccessStatusCode)
            return response;

        using (response)
        {
            string? code = null;
            var detail = $"Inference service returned {(int)response.StatusCode}.";
            try
            {
                var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Json, cancellationToken);
                if (problem.TryGetProperty("code", out var c))
                    code = c.GetString();
                if (problem.TryGetProperty("detail", out var d))
                    detail = d.GetString() ?? detail;
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
            }

            if (response.StatusCode is HttpStatusCode.ServiceUnavailable or HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout)
                throw new InferenceUnavailableException(code == InferenceErrorCodes.QueueFull ? "The generation queue is full. Please try again later." : "The 3D generation service is unavailable. Please try again later.");
            throw new InferenceRequestException(response.StatusCode, code, detail);
        }
    }

    /// <summary>Connection failures and HttpClient timeouts (TaskCanceled without caller cancellation).</summary>
    private static bool IsTransport(Exception ex, CancellationToken cancellationToken) =>
        ex is HttpRequestException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested);
}
