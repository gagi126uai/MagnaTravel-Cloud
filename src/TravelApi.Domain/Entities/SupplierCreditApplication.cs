using System.ComponentModel.DataAnnotations;

namespace TravelApi.Domain.Entities;

/// <summary>
/// ADR-041 TANDA 3 (lado proveedor, 2026-06-27): movimiento de consumo (o reversa de consumo) de un
/// <see cref="SupplierCreditEntry"/>. Es child del entry, no aggregate root. Espejo conceptual de
/// <see cref="ClientCreditWithdrawal"/>.
///
/// <para><b>Que modela</b>: aplicar el saldo a favor del operador a OTRA reserva del MISMO operador y
/// MISMA moneda. Aplicar es NETO-CERO a nivel agregado: drena el pool (baja
/// <see cref="SupplierCreditEntry.RemainingBalance"/>) y baja la deuda-por-reserva de la reserva destino,
/// SIN generar caja nueva ni mover <c>SupplierBalanceByCurrency.TotalPaid</c>. Por eso una aplicacion
/// nunca mueve el <c>Balance</c> agregado del operador+moneda; solo reparte entre reservas.</para>
///
/// <para><b>Reversa (patron Void)</b>: no se borra nada. La reversa es una contra-fila inmutable
/// (<see cref="Kind"/> = <see cref="SupplierCreditApplicationKind.Reversed"/>) que repone el
/// <c>RemainingBalance</c> del entry, deshace la imputacion en la reserva destino y apunta a la
/// aplicacion original via <see cref="ReversesApplicationId"/>.</para>
/// </summary>
public class SupplierCreditApplication : IHasPublicId
{
    public int Id { get; set; }
    public Guid PublicId { get; set; } = Guid.NewGuid();

    /// <summary>FK al <see cref="SupplierCreditEntry"/> (bolsillo) del que se drena (o al que se repone).</summary>
    public int SupplierCreditEntryId { get; set; }
    public SupplierCreditEntry Entry { get; set; } = null!;

    /// <summary>
    /// Monto del movimiento. SIEMPRE positivo. El signo economico lo da <see cref="Kind"/>: una Applied
    /// baja el pool y la deuda destino; una Reversed los repone. Esto evita montos negativos en la BD.
    /// </summary>
    public decimal Amount { get; set; }

    /// <summary>Reserva destino a la que se aplico (o se reponia) el credito. Misma moneda que el entry.</summary>
    public int TargetReservaId { get; set; }
    public Reserva? TargetReserva { get; set; }

    public SupplierCreditApplicationKind Kind { get; set; }

    /// <summary>
    /// Solo en una <see cref="SupplierCreditApplicationKind.Reversed"/>: FK a la aplicacion original que
    /// esta deshaciendo. Permite ligar reversa->aplicacion y bloquear la doble-reversa. Null en una Applied.
    /// </summary>
    public int? ReversesApplicationId { get; set; }
    public SupplierCreditApplication? ReversesApplication { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Required]
    [MaxLength(450)]
    public string CreatedByUserId { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? CreatedByUserName { get; set; }

    /// <summary>Motivo de la reversa (>= 10 chars, exigido por el service). Null en una Applied.</summary>
    [MaxLength(500)]
    public string? ReversalReason { get; set; }
}

/// <summary>
/// Tipo de movimiento sobre un <see cref="SupplierCreditEntry"/>: aplicacion (consumo) o su reversa.
/// </summary>
public enum SupplierCreditApplicationKind
{
    /// <summary>Se aplico saldo a favor del operador a una reserva destino (drena el pool).</summary>
    Applied = 0,

    /// <summary>Se revirtio una aplicacion previa (repone el pool y deshace la imputacion en destino).</summary>
    Reversed = 1
}
