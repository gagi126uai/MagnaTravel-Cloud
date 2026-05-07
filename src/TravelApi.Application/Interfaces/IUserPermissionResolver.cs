namespace TravelApi.Application.Interfaces;

/// <summary>
/// B1.15 Fase 1: resuelve los permisos efectivos de un usuario combinando los
/// permisos de todos sus roles. Cachea con TTL corto (15s) — los permisos
/// fiscales son sensibles y no toleran stale > 15s.
///
/// Invalidacion explicita por evento (cambio de permisos del rol, cambio de
/// rol del usuario, deactivation). NO se ponen permisos en el JWT para
/// evitar el problema de tokens vivos con permisos viejos.
/// </summary>
public interface IUserPermissionResolver
{
    /// <summary>
    /// Devuelve el conjunto de permisos efectivos del usuario. Vacio si no
    /// existe o no esta activo.
    /// </summary>
    Task<IReadOnlySet<string>> GetPermissionsAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Invalida la entrada cache de un usuario (post cambio de rol/permisos).
    /// </summary>
    void Invalidate(string userId);

    /// <summary>
    /// Invalida todas las entradas cache (por ejemplo, post seed o cambio masivo).
    /// </summary>
    void InvalidateAll();
}
