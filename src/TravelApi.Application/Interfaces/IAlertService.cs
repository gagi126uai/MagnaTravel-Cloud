namespace TravelApi.Application.Interfaces;

/// <summary>
/// Identidad del usuario que pide las alertas, resuelta en el controller a partir
/// de los claims. Existe para que el gating de buckets sea SERVER-SIDE (Fuga 2,
/// ADR-017 §2.7, F1b) y para que la fase F3 pueda sumar buckets por-vendedor
/// (filtrando por <see cref="UserId"/>) sin volver a cambiar la firma.
/// </summary>
/// <param name="UserId">Id del usuario (claim NameIdentifier). Puede ser null si el token no lo trae.</param>
/// <param name="IsAdmin">True si el caller tiene el rol "Admin".</param>
/// <param name="CanSeeCost">
/// ADR-017 F1.4 (§2.8/D8b): true si el caller puede ver costos (rol Admin o permiso
/// <c>cobranzas.see_cost</c>). Gatea el bucket <c>CostsToConfirm</c> server-side. Default false
/// (fail-closed): los callers/tests que no lo seteen quedan sin ver el bucket de costos.
/// </param>
public sealed record AlertCallerContext(string? UserId, bool IsAdmin, bool CanSeeCost = false);

public interface IAlertService
{
    Task<object> GetAlertsAsync(AlertCallerContext caller, CancellationToken cancellationToken);
}
