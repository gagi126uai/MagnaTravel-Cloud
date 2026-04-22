using System.Net.Http.Headers;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace TravelApi.Infrastructure.Services.ReservationsServiceProxy;

public class ReservationsServiceAuthHandler : DelegatingHandler
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IOptions<ReservationsServiceOptions> _options;

    public ReservationsServiceAuthHandler(IHttpContextAccessor httpContextAccessor, IOptions<ReservationsServiceOptions> options)
    {
        _httpContextAccessor = httpContextAccessor;
        _options = options;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = _options.Value.InternalToken;
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.TryAddWithoutValidation("X-Internal-Service-Token", token);
        }

        var httpContext = _httpContextAccessor.HttpContext;
        if (httpContext != null)
        {
            var user = httpContext.User;
            var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
            var userName = user.Identity?.Name ?? user.FindFirstValue(ClaimTypes.Name);
            var roles = user.FindAll(ClaimTypes.Role).Select(role => role.Value).Where(role => !string.IsNullOrWhiteSpace(role)).ToArray();
            var correlationId = httpContext.Request.Headers["x-correlation-id"].FirstOrDefault() ?? httpContext.TraceIdentifier;

            if (!string.IsNullOrWhiteSpace(userId))
            {
                request.Headers.TryAddWithoutValidation("X-User-Id", userId);
            }

            if (!string.IsNullOrWhiteSpace(userName))
            {
                request.Headers.TryAddWithoutValidation("X-User-Name", userName);
            }

            if (roles.Length > 0)
            {
                request.Headers.TryAddWithoutValidation("X-User-Roles", string.Join(",", roles));
            }

            if (!string.IsNullOrWhiteSpace(correlationId))
            {
                request.Headers.TryAddWithoutValidation("X-Correlation-Id", correlationId);
            }
        }

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return base.SendAsync(request, cancellationToken);
    }
}
