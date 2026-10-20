using System.Security.Cryptography;
using System.Text;

namespace ImageTo3D.Api.Services;

/// <summary>
/// Anonymous ownership for the local, account-less version: each browser gets a random
/// 256-bit HttpOnly cookie, and jobs are scoped to the SHA-256 of that value. This keeps one
/// browser's jobs and downloads private from another, but it is NOT user authentication;
/// see README "Before exposing to the internet".
/// </summary>
public sealed class OwnerCookieMiddleware(RequestDelegate next)
{
    public const string CookieName = "i3d_owner";
    public const string ItemKey = "OwnerKeyHash";

    public Task InvokeAsync(HttpContext context)
    {
        var value = context.Request.Cookies[CookieName];
        if (value is null || value.Length != 43)
        {
            value = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            context.Response.Cookies.Append(CookieName, value, new CookieOptions
            {
                HttpOnly = true,
                Secure = context.Request.IsHttps,
                SameSite = SameSiteMode.Strict,
                IsEssential = true,
                MaxAge = TimeSpan.FromDays(365),
                Path = "/",
            });
        }

        context.Items[ItemKey] = Hash(value);
        return next(context);
    }

    public static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string Base64UrlEncode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public interface IOwnerAccessor
{
    string OwnerKeyHash { get; }
}

public sealed class HttpOwnerAccessor(IHttpContextAccessor accessor) : IOwnerAccessor
{
    public string OwnerKeyHash =>
        accessor.HttpContext?.Items[OwnerCookieMiddleware.ItemKey] as string
        ?? throw new InvalidOperationException("Owner middleware has not run for this request.");
}
