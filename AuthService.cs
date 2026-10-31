using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace PclCmu.Admin;

/// <summary>
/// Minimal cookie authentication for the loopback-only admin listener.
///
/// Auth: an HttpOnly "pcl_cmu_admin" cookie holds a random session id that is
///       tracked in-memory (restarting the process invalidates sessions, which
///       is acceptable for a single-instance admin tool).
///
/// CSRF: a double-submit "pcl_cmu_csrf" cookie (non-HttpOnly, SameSite=Lax)
///       whose value is mirrored into every form as a hidden "_csrf" field and
///       compared on POST. Combined with SameSite=Lax this blocks cross-site
///       request forgery.
/// </summary>
public sealed class AuthService
{
    public const string AuthCookieName = "pcl_cmu_admin";
    public const string CsrfCookieName = "pcl_cmu_csrf";
    public const string CsrfFieldName = "_csrf";

    private static readonly TimeSpan SessionTtl = TimeSpan.FromHours(12);

    private sealed class Session
    {
        public DateTimeOffset ExpiresUtc;
    }

    private readonly ConcurrentDictionary<string, Session> _sessions = new();

    public void StartSession(HttpContext context)
    {
        var sessionId = RandomToken();
        _sessions[sessionId] = new Session { ExpiresUtc = DateTimeOffset.UtcNow + SessionTtl };
        context.Response.Cookies.Append(AuthCookieName, sessionId, AuthCookieOptions());
    }

    public void SignOut(HttpContext context)
    {
        var id = context.Request.Cookies[AuthCookieName];
        if (id is not null)
            _sessions.TryRemove(id, out _);

        context.Response.Cookies.Delete(AuthCookieName, new CookieOptions { Path = "/admin" });
    }

    public bool IsAuthenticated(HttpContext context)
    {
        var id = context.Request.Cookies[AuthCookieName];
        if (id is null || !_sessions.TryGetValue(id, out var session))
            return false;

        if (DateTimeOffset.UtcNow > session.ExpiresUtc)
        {
            _sessions.TryRemove(id, out _);
            return false;
        }

        return true;
    }

    /// <summary>Ensures a CSRF cookie exists and returns its value.</summary>
    public string EnsureCsrfCookie(HttpContext context)
    {
        var existing = context.Request.Cookies[CsrfCookieName];
        if (!string.IsNullOrEmpty(existing))
            return existing;

        var token = RandomToken();
        context.Response.Cookies.Append(CsrfCookieName, token, new CookieOptions
        {
            HttpOnly = false,
            SameSite = SameSiteMode.Lax,
            Path = "/admin",
            Secure = false,
        });
        return token;
    }

    public bool ValidateCsrf(HttpContext context, string? submitted)
    {
        var cookie = context.Request.Cookies[CsrfCookieName];
        if (string.IsNullOrEmpty(cookie) || string.IsNullOrEmpty(submitted))
            return false;

        var expected = Encoding.UTF8.GetBytes(cookie);
        var actual = Encoding.UTF8.GetBytes(submitted);
        return expected.Length == actual.Length
            && CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static CookieOptions AuthCookieOptions() => new()
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Lax,
        Path = "/admin",
        Secure = false,
    };

    private static string RandomToken()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
}
