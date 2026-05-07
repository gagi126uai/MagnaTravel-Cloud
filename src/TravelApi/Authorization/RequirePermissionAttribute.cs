using Microsoft.AspNetCore.Authorization;

namespace TravelApi.Authorization;

/// <summary>
/// B1.15 Fase 1: requiere uno o mas permisos para acceder a un endpoint.
/// Multiples permisos en un mismo attribute = OR. Apilar attributes en el
/// mismo endpoint = AND.
///
/// Implementacion: hereda <see cref="AuthorizeAttribute"/> y codifica los
/// permisos en <c>Policy</c> con prefijo <see cref="PolicyPrefix"/> para que
/// <see cref="PermissionAuthorizationPolicyProvider"/> las construya on-demand.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public sealed class RequirePermissionAttribute : AuthorizeAttribute
{
    public const string PolicyPrefix = "PERM:";
    public const char Separator = '|';

    public RequirePermissionAttribute(params string[] permissions)
    {
        if (permissions is null || permissions.Length == 0)
        {
            throw new ArgumentException("Debe especificarse al menos un permiso.", nameof(permissions));
        }

        // Codificar en Policy: PERM:perm1|perm2 (OR entre los listados).
        Policy = PolicyPrefix + string.Join(Separator, permissions);
    }

    /// <summary>
    /// Decodifica una policy con prefijo PERM: y devuelve la lista de permisos.
    /// </summary>
    public static IReadOnlyList<string>? TryParsePolicyName(string policyName)
    {
        if (string.IsNullOrEmpty(policyName) || !policyName.StartsWith(PolicyPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var raw = policyName.Substring(PolicyPrefix.Length);
        var parts = raw.Split(Separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0 ? null : parts;
    }
}
