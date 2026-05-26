using System.ComponentModel.DataAnnotations;
using TravelApi.Domain.Entities;

namespace TravelApi.Application.DTOs;

public class OperationalFinanceSettingsDto
{
    public bool RequireFullPaymentForOperativeStatus { get; set; } = true;
    public bool RequireFullPaymentForVoucher { get; set; } = true;
    public string AfipInvoiceControlMode { get; set; } = "AllowAgentOverrideWithReason";
    public bool EnableUpcomingUnpaidReservationNotifications { get; set; } = true;
    public int UpcomingUnpaidReservationAlertDays { get; set; } = 7;
    /// <summary>
    /// B1.15 Fase 2a: tope de descuento (% sobre precio de referencia) que un
    /// vendedor puede aplicar sin permiso <c>reservas.discount_above_threshold</c>.
    /// Rango valido 0..100. Default 10%.
    /// </summary>
    [Range(0, 100, ErrorMessage = "MaxDiscountPercentWithoutOverride debe estar entre 0 y 100.")]
    public decimal MaxDiscountPercentWithoutOverride { get; set; } = 10m;

    // ============================================================
    // FC1.3 Fase 2 (plan tactico Fase 2 §FC1.3.F2.0, 2026-05-22):
    // 3 settings configurables desde el panel admin. Los 2 flags maestros
    // (EnablePartialCreditNoteRealEmission, EnableTotalPlusNewInvoiceAutoProcessing)
    // intencionalmente NO se exponen aca: se cambian via SQL/seed/migration igual
    // que los flags FC1.2/FC1.3 actuales (EnableNewCancellationFlow,
    // EnablePartialCreditNotes). El service valida las cross-field rules en runtime.
    //
    // El proyecto NO usa FluentValidation registrado (ver comentario en
    // Program.cs §FC1.3.2). Usamos DataAnnotations consistente con el resto del
    // DTO (MaxDiscountPercentWithoutOverride ya usa [Range]). El binding de
    // ASP.NET Core dispara ModelState invalido y devuelve HTTP 400 antes de
    // llegar al service — mismo patron que ya funciona en este DTO.
    //
    // B-002 fix (2026-05-26): los 3 campos son NULLABLE para evitar regresion
    // silenciosa via PUT full-replace. Mandar null o omitir el campo en el PUT
    // = no se modifica el valor actual. Solo se actualiza si viene con valor.
    // Razon: un cliente legacy o el frontend que olvide mandar uno de estos
    // 3 campos haria que el binder llene con el default del DTO y persista
    // sobre lo que el admin configuro. Cuando el contador conteste F1 round 3 y
    // un admin pase IvaProrrateoMode a PerItem, un PUT legacy lo revertiria
    // silenciosamente a ProportionalToNet. Con nullable, el campo omitido no
    // pisa nada. Los [Range] siguen aplicando — solo se evaluan si viene valor.
    // ============================================================

    /// <summary>
    /// FC1.3 Fase 2 (RH-005): modo de prorrateo de IVA en NC parcial. Configurable
    /// por admin segun respuesta del contador (pregunta F1 round 3). Default
    /// persistido en BD: <see cref="IvaProrrateoMode.ProportionalToNet"/>.
    /// Nullable en el DTO: enviar null o omitir en el PUT para no modificar el
    /// valor actual. Solo se actualiza si viene con valor.
    /// </summary>
    public IvaProrrateoMode? IvaProrrateoMode { get; set; }

    /// <summary>
    /// FC1.3 Fase 2: tolerancia maxima en la validacion defensiva pre-envio al
    /// ARCA (ImpNeto + ImpIVA + ImpTrib vs ImpTotal). Default persistido en BD:
    /// 0.01 (un centavo). Rango razonable 0..1 en la moneda original del comprobante.
    /// Nullable en el DTO: enviar null o omitir en el PUT para no modificar el
    /// valor actual. Solo se actualiza si viene con valor.
    /// </summary>
    [Range(0.0, 1.0, ErrorMessage = "PartialCreditNoteRoundingTolerance debe estar entre 0 y 1.")]
    public decimal? PartialCreditNoteRoundingTolerance { get; set; }

    /// <summary>
    /// FC1.3 Fase 2 (RH2-004): umbral en minutos para considerar huerfana una
    /// key en ArcaIdempotencyKeys. Default persistido en BD: 10. Rango razonable
    /// 1..60 (mas de 60 atrasa el recovery; menos de 1 declara "huerfanas" keys
    /// legitimas en vuelo).
    /// Nullable en el DTO: enviar null o omitir en el PUT para no modificar el
    /// valor actual. Solo se actualiza si viene con valor.
    /// </summary>
    [Range(1, 60, ErrorMessage = "IdempotencyKeyStaleThresholdMinutes debe estar entre 1 y 60.")]
    public int? IdempotencyKeyStaleThresholdMinutes { get; set; }
}
