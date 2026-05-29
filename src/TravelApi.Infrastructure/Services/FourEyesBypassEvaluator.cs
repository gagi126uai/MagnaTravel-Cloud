using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;

namespace TravelApi.Infrastructure.Services;

/// <summary>
/// FC1.3 Fase 3 (ADR-010 R1, 2026-05-29): implementacion de la regla GR-005.
///
/// <para>Esta logica es BYTE-EQUIVALENTE al metodo privado original
/// <c>TryApplyGr005BypassAsync</c> que vivia en <c>BookingCancellationService</c>.
/// Se extrajo aca (mismo orden de chequeos, mismos umbrales) para que el cierre de
/// la bandeja de reconciliacion (Fase 3) use exactamente la misma evaluacion y no
/// haya dos copias que se puedan desincronizar.</para>
///
/// <para>Depende solo de <see cref="IAdminUserCountService"/> (cuenta admins activos),
/// que ya existia y es trivial de mockear en tests.</para>
/// </summary>
public class FourEyesBypassEvaluator : IFourEyesBypassEvaluator
{
    private readonly IAdminUserCountService _adminUserCount;

    public FourEyesBypassEvaluator(IAdminUserCountService adminUserCount)
    {
        _adminUserCount = adminUserCount;
    }

    /// <inheritdoc />
    public async Task<bool> EvaluateAsync(
        string? comment,
        OperationalFinanceSettings settings,
        CancellationToken ct)
    {
        // Chequeo 1: el setting tiene que habilitar el bypass. Default false = 4-ojos
        // estricto (la opcion segura).
        if (!settings.Allow4EyesBypassWhenSingleAdmin)
            return false;

        // Chequeo 2: comentario justificatorio de al menos 100 chars (refuerzo G5).
        // Trim para que 100 espacios no cuenten como justificacion.
        if (string.IsNullOrWhiteSpace(comment) || comment.Trim().Length < 100)
            return false;

        // Chequeo 3: tiene que haber EXACTAMENTE 1 admin activo. Si hay 2 o mas, el
        // 4-ojos es posible (que lo haga otro) y el bypass no corresponde.
        var activeAdminCount = await _adminUserCount.CountActiveAdminsAsync(ct);
        return activeAdminCount == 1;
    }
}
