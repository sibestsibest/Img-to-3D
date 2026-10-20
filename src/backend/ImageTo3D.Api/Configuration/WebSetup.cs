using System.Threading.RateLimiting;
using ImageTo3D.Api.Services;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace ImageTo3D.Api.Configuration;

public static class RateLimitPolicies
{
    public const string Uploads = "uploads";

    public static void AddApiRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, cancellationToken) =>
            {
                var problem = new ProblemDetails
                {
                    Status = StatusCodes.Status429TooManyRequests,
                    Title = "Too many requests",
                    Detail = "Too many requests. Please wait a moment and try again.",
                };
                problem.Extensions["code"] = "rate_limited";
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                    context.HttpContext.Response.Headers.RetryAfter = ((int)retryAfter.TotalSeconds).ToString();
                await context.HttpContext.Response.WriteAsJsonAsync(problem, cancellationToken);
            };

            // Coarse per-client ceiling for all API calls (status polling included).
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = 600, Window = TimeSpan.FromMinutes(1) }));

            // Uploads are expensive: limit per anonymous owner.
            options.AddPolicy(Uploads, context =>
            {
                var limit = context.RequestServices.GetRequiredService<IOptions<UploadOptions>>().Value.UploadsPerMinute;
                var key = context.Items[OwnerCookieMiddleware.ItemKey] as string ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions { PermitLimit = limit, Window = TimeSpan.FromMinutes(1) });
            });
        });
    }
}

/// <summary>Maps exceptions to RFC 7807 responses with a stable "code"; never leaks stack traces or paths.</summary>
public sealed class ApiExceptionHandler(IProblemDetailsService problemDetails, ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, code, title, detail) = exception switch
        {
            ApiException api => (api.Status, api.Code, ReasonPhrase(api.Status), api.Message),
            BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge } =>
                (StatusCodes.Status413PayloadTooLarge, "file_too_large", "Payload too large", "The upload exceeds the maximum allowed size."),
            BadHttpRequestException bad => (bad.StatusCode, "bad_request", "Bad request", "The request could not be read."),
            InvalidDataException => (StatusCodes.Status400BadRequest, "bad_request", "Bad request", "The request body is malformed or exceeds form limits."),
            OperationCanceledException when httpContext.RequestAborted.IsCancellationRequested => (499, "client_closed", "Client closed request", ""),
            _ => (StatusCodes.Status500InternalServerError, "internal_error", "Internal server error", "An unexpected error occurred."),
        };

        if (status >= 500)
            logger.LogError(exception, "Unhandled exception");

        httpContext.Response.StatusCode = status;
        var problem = new ProblemDetails { Status = status, Title = title, Detail = detail };
        problem.Extensions["code"] = code;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext { HttpContext = httpContext, ProblemDetails = problem, Exception = exception });
    }

    private static string ReasonPhrase(int status) => Microsoft.AspNetCore.WebUtilities.ReasonPhrases.GetReasonPhrase(status) is { Length: > 0 } p ? p : "Error";
}

public static class ProblemDetailsSetup
{
    public static void AddApiProblemDetails(this IServiceCollection services)
    {
        services.AddProblemDetails(options =>
        {
            options.CustomizeProblemDetails = context =>
            {
                context.ProblemDetails.Extensions.Remove("exception");
                context.ProblemDetails.Extensions["correlationId"] = context.HttpContext.TraceIdentifier;
                context.ProblemDetails.Extensions.TryAdd("code", context.ProblemDetails.Status switch
                {
                    StatusCodes.Status400BadRequest => "validation_failed",
                    StatusCodes.Status404NotFound => "not_found",
                    StatusCodes.Status415UnsupportedMediaType => "unsupported_media_type",
                    _ => "error",
                });
            };
        });
        services.AddExceptionHandler<ApiExceptionHandler>();
        services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = 60L * 1024 * 1024);
    }
}
