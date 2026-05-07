using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace TravelApi.Authorization;

/// <summary>
/// B1.15 Fase 1: provider custom que construye policies on-demand para los
/// attributes <see cref="RequirePermissionAttribute"/> (prefijo PERM:).
/// Para policies que no tienen el prefijo (ej: AdminOnly), delega al default.
/// </summary>
public sealed class PermissionAuthorizationPolicyProvider : DefaultAuthorizationPolicyProvider
{
    public PermissionAuthorizationPolicyProvider(IOptions<AuthorizationOptions> options)
        : base(options)
    {
    }

    public override async Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        var permissions = RequirePermissionAttribute.TryParsePolicyName(policyName);
        if (permissions is not null)
        {
            return new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .AddRequirements(new PermissionRequirement(permissions))
                .Build();
        }

        return await base.GetPolicyAsync(policyName);
    }
}
