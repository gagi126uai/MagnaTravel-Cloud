using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TravelApi.Tests.Fixtures;

/// <summary>
/// Handler de autenticacion para tests. Parametrizable via headers para que
/// cada test pueda emitir un user con identidad y permisos distintos.
///
/// Headers reconocidos (todos opcionales):
///  - "X-Test-User-Id"        => ClaimTypes.NameIdentifier (default: "test-user").
///  - "X-Test-User-Name"      => ClaimTypes.Name (default: "Test User").
///  - "X-Test-User-Roles"     => ClaimTypes.Role (CSV; default: "Admin").
///  - "X-Test-User-Permissions" => permission claims (CSV; sin default).
///
/// Default: Admin con identidad "test-user" — preserva el comportamiento
/// de los tests existentes (los 131 tests pre-B1.15).
/// </summary>
public class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string TestUserIdHeader = "X-Test-User-Id";
    public const string TestUserNameHeader = "X-Test-User-Name";
    public const string TestUserRolesHeader = "X-Test-User-Roles";
    public const string TestUserPermissionsHeader = "X-Test-User-Permissions";

    /// <summary>
    /// Claim type usado para los permisos en tests. NOTA: en runtime los permisos
    /// NO viajan en el JWT (decision B1.15 — invalidacion explicita por evento);
    /// este claim solo se emite aca para que la suite de tests pueda escribir
    /// scenarios sin levantar toda la infra de RolePermissions + cache.
    /// El handler real de autorizacion en runtime usa IUserPermissionResolver,
    /// no este claim.
    /// </summary>
    public const string PermissionClaimType = "permission";

    public TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        ISystemClock clock)
        : base(options, logger, encoder, clock)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var userId = Request.Headers.TryGetValue(TestUserIdHeader, out var userIdValue)
            && !string.IsNullOrWhiteSpace(userIdValue)
                ? userIdValue.ToString()
                : "test-user";

        var userName = Request.Headers.TryGetValue(TestUserNameHeader, out var userNameValue)
            && !string.IsNullOrWhiteSpace(userNameValue)
                ? userNameValue.ToString()
                : "Test User";

        var roles = Request.Headers.TryGetValue(TestUserRolesHeader, out var rolesValue)
            && !string.IsNullOrWhiteSpace(rolesValue)
                ? rolesValue.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : new[] { "Admin" };

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId),
            new(ClaimTypes.Name, userName),
        };

        foreach (var role in roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        if (Request.Headers.TryGetValue(TestUserPermissionsHeader, out var permsValue)
            && !string.IsNullOrWhiteSpace(permsValue))
        {
            var perms = permsValue.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var perm in perms)
            {
                claims.Add(new Claim(PermissionClaimType, perm));
            }
        }

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
