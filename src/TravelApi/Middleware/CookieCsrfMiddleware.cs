using System.Security.Cryptography;
using System.Text;
using TravelApi.Application.Contracts.Auth;

namespace TravelApi.Middleware;

public class CookieCsrfMiddleware
{
    private const string HeaderName = "X-CSRF-Token";
    private readonly RequestDelegate _next;

    public CookieCsrfMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (HttpMethods.IsGet(context.Request.Method) ||
            HttpMethods.IsHead(context.Request.Method) ||
            HttpMethods.IsOptions(context.Request.Method) ||
            HttpMethods.IsTrace(context.Request.Method))
        {
            await _next(context);
            return;
        }

        if (context.Request.Path.StartsWithSegments("/hubs", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        if (context.Request.Path.Equals("/api/auth/login", StringComparison.OrdinalIgnoreCase) ||
            context.Request.Path.Equals("/api/auth/register", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        var hasSessionCookie = context.Request.Cookies.ContainsKey(AuthCookieNames.Access) ||
                               context.Request.Cookies.ContainsKey(AuthCookieNames.Refresh);
        if (!hasSessionCookie)
        {
            await _next(context);
            return;
        }

        if (!context.Request.Cookies.TryGetValue(AuthCookieNames.Csrf, out var csrfCookie) ||
            string.IsNullOrWhiteSpace(csrfCookie))
        {
            await RejectAsync(context);
            return;
        }

        var csrfHeader = context.Request.Headers[HeaderName].ToString();
        if (string.IsNullOrWhiteSpace(csrfHeader))
        {
            await RejectAsync(context);
            return;
        }

        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(csrfCookie), Encoding.UTF8.GetBytes(csrfHeader)))
        {
            await RejectAsync(context);
            return;
        }

        await _next(context);
    }

    private static Task RejectAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return context.Response.WriteAsJsonAsync(new
        {
            message = "La solicitud fue rechazada por validacion CSRF."
        });
    }
}
