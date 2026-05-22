namespace TravelApi.Application.Interfaces;

/// <summary>
/// FC1.3.3 (ADR-009 §2.3.4.bis N-002, 2026-05-21): servicio chico que cuenta
/// cuantos usuarios activos tienen rol "Admin".
///
/// <para><b>Por que existe como abstraccion separada</b>: la regla GR-005
/// (bypass de 4-ojos cuando hay un solo admin) necesita una query sobre
/// <c>UserManager&lt;ApplicationUser&gt;</c>. Mockear el <c>UserManager</c> en tests
/// unit es ruidoso (8+ dependencias, ctor protected). Esta abstraccion deja
/// el detalle de Identity en Infrastructure y los tests del
/// <c>BookingCancellationService</c> mockean SOLO esto.</para>
///
/// <para><b>Que cuenta</b>: usuarios con rol string canonico <c>"Admin"</c>
/// (mismo string que el <c>RoleSeeder</c>) Y <c>IsActive=true</c>. NO cuenta
/// permission strings (un rol "Colaborador" que tenga
/// <c>Permissions.Approvals.Review</c> no es admin a los efectos de GR-005).</para>
///
/// <para><b>Idempotente y barato</b>: una sola query por invocacion. La logica
/// de "1 solo admin" la decide el caller (compara count == 1 inline).</para>
/// </summary>
public interface IAdminUserCountService
{
    /// <summary>
    /// Devuelve cuantos usuarios con rol "Admin" estan activos
    /// (<c>IsActive=true</c>) en la BD en este momento.
    /// </summary>
    Task<int> CountActiveAdminsAsync(CancellationToken ct);
}
