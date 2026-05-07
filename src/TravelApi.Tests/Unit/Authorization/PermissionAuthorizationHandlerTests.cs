using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Moq;
using TravelApi.Application.Interfaces;
using TravelApi.Authorization;
using Xunit;

namespace TravelApi.Tests.Unit.Authorization;

/// <summary>
/// B1.15 Fase 1 — handler de permisos.
///
/// Cubre:
///  - Admin bypass (succeed sin consultar el resolver).
///  - has perm single (succeed).
///  - has perm OR (al menos uno coincide).
///  - missing perm (no succeed; el authorization framework trata eso como fail).
///  - sin user identity / sin NameIdentifier (no succeed).
/// </summary>
public class PermissionAuthorizationHandlerTests
{
    private static ClaimsPrincipal BuildPrincipal(string? userId, params string[] roles)
    {
        var claims = new List<Claim>();
        if (!string.IsNullOrEmpty(userId))
        {
            claims.Add(new Claim(ClaimTypes.NameIdentifier, userId));
        }
        foreach (var r in roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, r));
        }
        var identity = new ClaimsIdentity(claims, authenticationType: "Test");
        return new ClaimsPrincipal(identity);
    }

    private static AuthorizationHandlerContext BuildContext(PermissionRequirement requirement, ClaimsPrincipal principal)
        => new(new[] { requirement }, principal, resource: null);

    [Fact]
    public async Task Admin_role_short_circuits_without_resolver_lookup()
    {
        var resolver = new Mock<IUserPermissionResolver>(MockBehavior.Strict);
        // Strict + ningun setup: si se llama a GetPermissionsAsync, falla el test.

        var handler = new PermissionAuthorizationHandler(resolver.Object);
        var requirement = new PermissionRequirement(new[] { "reservas.cancel" });
        var ctx = BuildContext(requirement, BuildPrincipal("admin-id", "Admin"));

        await handler.HandleAsync(ctx);

        Assert.True(ctx.HasSucceeded);
        resolver.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Has_single_permission_succeeds()
    {
        var resolver = new Mock<IUserPermissionResolver>();
        IReadOnlySet<string> perms = new HashSet<string> { "reservas.cancel" };
        resolver.Setup(r => r.GetPermissionsAsync("user-1", It.IsAny<CancellationToken>()))
                .ReturnsAsync(perms);

        var handler = new PermissionAuthorizationHandler(resolver.Object);
        var requirement = new PermissionRequirement(new[] { "reservas.cancel" });
        var ctx = BuildContext(requirement, BuildPrincipal("user-1", "Vendedor"));

        await handler.HandleAsync(ctx);

        Assert.True(ctx.HasSucceeded);
    }

    [Fact]
    public async Task Has_one_of_OR_permissions_succeeds()
    {
        var resolver = new Mock<IUserPermissionResolver>();
        IReadOnlySet<string> perms = new HashSet<string> { "cobranzas.invoice" };
        resolver.Setup(r => r.GetPermissionsAsync("user-2", It.IsAny<CancellationToken>()))
                .ReturnsAsync(perms);

        var handler = new PermissionAuthorizationHandler(resolver.Object);
        // OR entre dos permisos: alcanza con que tenga uno.
        var requirement = new PermissionRequirement(new[] { "cobranzas.invoice", "cobranzas.invoice_annul" });
        var ctx = BuildContext(requirement, BuildPrincipal("user-2", "Vendedor"));

        await handler.HandleAsync(ctx);

        Assert.True(ctx.HasSucceeded);
    }

    [Fact]
    public async Task Missing_permission_does_not_succeed()
    {
        var resolver = new Mock<IUserPermissionResolver>();
        IReadOnlySet<string> perms = new HashSet<string> { "reservas.view" };
        resolver.Setup(r => r.GetPermissionsAsync("user-3", It.IsAny<CancellationToken>()))
                .ReturnsAsync(perms);

        var handler = new PermissionAuthorizationHandler(resolver.Object);
        var requirement = new PermissionRequirement(new[] { "reservas.cancel" });
        var ctx = BuildContext(requirement, BuildPrincipal("user-3", "Vendedor"));

        await handler.HandleAsync(ctx);

        Assert.False(ctx.HasSucceeded);
    }

    [Fact]
    public async Task Empty_permissions_set_does_not_succeed()
    {
        var resolver = new Mock<IUserPermissionResolver>();
        IReadOnlySet<string> perms = new HashSet<string>();
        resolver.Setup(r => r.GetPermissionsAsync("user-4", It.IsAny<CancellationToken>()))
                .ReturnsAsync(perms);

        var handler = new PermissionAuthorizationHandler(resolver.Object);
        var requirement = new PermissionRequirement(new[] { "reservas.cancel" });
        var ctx = BuildContext(requirement, BuildPrincipal("user-4", "Vendedor"));

        await handler.HandleAsync(ctx);

        Assert.False(ctx.HasSucceeded);
    }

    [Fact]
    public async Task Unauthenticated_principal_does_not_succeed()
    {
        var resolver = new Mock<IUserPermissionResolver>(MockBehavior.Strict);
        var handler = new PermissionAuthorizationHandler(resolver.Object);
        var requirement = new PermissionRequirement(new[] { "reservas.cancel" });

        var anonymous = new ClaimsPrincipal(new ClaimsIdentity()); // no auth type => not authenticated
        var ctx = BuildContext(requirement, anonymous);

        await handler.HandleAsync(ctx);

        Assert.False(ctx.HasSucceeded);
        resolver.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Missing_NameIdentifier_does_not_succeed()
    {
        var resolver = new Mock<IUserPermissionResolver>(MockBehavior.Strict);
        var handler = new PermissionAuthorizationHandler(resolver.Object);
        var requirement = new PermissionRequirement(new[] { "reservas.cancel" });

        // Authenticated pero sin NameIdentifier ni rol Admin.
        var principal = BuildPrincipal(userId: null, roles: "Vendedor");
        var ctx = BuildContext(requirement, principal);

        await handler.HandleAsync(ctx);

        Assert.False(ctx.HasSucceeded);
        resolver.VerifyNoOtherCalls();
    }
}
