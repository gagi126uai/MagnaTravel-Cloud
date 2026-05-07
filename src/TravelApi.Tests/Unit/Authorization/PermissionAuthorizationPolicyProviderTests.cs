using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using TravelApi.Authorization;
using Xunit;

namespace TravelApi.Tests.Unit.Authorization;

/// <summary>
/// B1.15 Fase 1 — provider custom de policies.
/// Cubre que parsea el prefijo "PERM:" y que delega al default provider para
/// otros nombres de policy (ej: "AdminOnly").
/// </summary>
public class PermissionAuthorizationPolicyProviderTests
{
    private static PermissionAuthorizationPolicyProvider BuildProvider(AuthorizationOptions? configure = null)
    {
        var options = new AuthorizationOptions();
        if (configure is not null)
        {
            options.AddPolicy("AdminOnly", p => p.RequireRole("Admin"));
        }
        var snapshot = Options.Create(options);
        return new PermissionAuthorizationPolicyProvider(snapshot);
    }

    [Fact]
    public async Task Parses_PERM_prefix_with_single_permission()
    {
        var provider = BuildProvider();

        var policy = await provider.GetPolicyAsync("PERM:reservas.cancel");

        Assert.NotNull(policy);
        var requirement = Assert.Single(policy!.Requirements.OfType<PermissionRequirement>());
        Assert.Equal(new[] { "reservas.cancel" }, requirement.Permissions);
    }

    [Fact]
    public async Task Parses_PERM_prefix_with_OR_permissions()
    {
        var provider = BuildProvider();

        var policy = await provider.GetPolicyAsync("PERM:cobranzas.invoice|cobranzas.invoice_annul");

        Assert.NotNull(policy);
        var requirement = Assert.Single(policy!.Requirements.OfType<PermissionRequirement>());
        Assert.Equal(2, requirement.Permissions.Count);
        Assert.Contains("cobranzas.invoice", requirement.Permissions);
        Assert.Contains("cobranzas.invoice_annul", requirement.Permissions);
    }

    [Fact]
    public async Task Falls_back_to_default_for_non_PERM_policy()
    {
        var options = new AuthorizationOptions();
        options.AddPolicy("AdminOnly", p => p.RequireRole("Admin"));
        var provider = new PermissionAuthorizationPolicyProvider(Options.Create(options));

        var policy = await provider.GetPolicyAsync("AdminOnly");

        Assert.NotNull(policy);
        // No debe ser un PermissionRequirement; debe venir de la config base.
        Assert.DoesNotContain(policy!.Requirements, r => r is PermissionRequirement);
    }

    [Fact]
    public async Task Returns_null_for_unknown_non_PERM_policy()
    {
        var provider = BuildProvider();

        var policy = await provider.GetPolicyAsync("PoliticaInexistente");

        Assert.Null(policy);
    }
}
