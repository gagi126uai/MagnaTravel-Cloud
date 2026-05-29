using TravelApi.Domain.Entities;

namespace TravelApi.Application.Interfaces;

/// <summary>
/// FC1.3 Fase 3 (ADR-010 R1, 2026-05-29): evalua la regla de bypass de 4-ojos
/// para agencias de UN SOLO admin (GR-005 / G5).
///
/// <para><b>Por que existe (DRY)</b>: la regla GR-005 estaba duplicada como metodo
/// privado <c>TryApplyGr005BypassAsync</c> en <c>BookingCancellationService</c> y se
/// usaba en dos lugares (EditLiquidation + OnApproved). La Fase 3 necesita la MISMA
/// regla en el cierre de casos de reconciliacion. Para no copiar-pegar la logica
/// (riesgo: que se desincronicen y una valide distinto que la otra), se extrae a
/// este servicio compartido e inyectable. Los tres call-sites usan exactamente la
/// misma evaluacion.</para>
///
/// <para><b>La regla (4-ojos)</b>: normalmente, quien aprueba/cierra una operacion
/// sensible NO puede ser la misma persona que la origino (cuatro ojos miran, no dos).
/// Pero una agencia con un solo administrador no tiene "otra persona" — quedaria
/// trabada. Para esos casos se permite saltarse la regla SI Y SOLO SI:
/// <list type="number">
///   <item>el setting <c>Allow4EyesBypassWhenSingleAdmin</c> esta en true, Y</item>
///   <item>el comentario justificatorio tiene al menos 100 caracteres (refuerzo G5), Y</item>
///   <item>hay EXACTAMENTE 1 admin activo en el sistema.</item>
/// </list>
/// Si las tres se cumplen, el bypass aplica. Si alguna falla, no.</para>
/// </summary>
public interface IFourEyesBypassEvaluator
{
    /// <summary>
    /// Devuelve <c>true</c> si el bypass de 4-ojos para single-admin aplica con el
    /// comentario y settings dados. El caller decide que hacer si devuelve <c>false</c>
    /// (tirar excepcion, loguear, o devolver 409) — este metodo no decide eso.
    /// </summary>
    /// <param name="comment">Comentario/motivo justificatorio. Debe tener >= 100 chars (trim) para que el bypass aplique.</param>
    /// <param name="settings">Settings operativos (de ahi sale <c>Allow4EyesBypassWhenSingleAdmin</c>).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> EvaluateAsync(
        string? comment,
        OperationalFinanceSettings settings,
        CancellationToken ct);
}
