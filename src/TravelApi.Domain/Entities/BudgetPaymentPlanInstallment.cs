using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TravelApi.Domain.Entities;

/// <summary>
/// UNA fila del "plan de pagos" del TOTAL del presupuesto (obra "PDF ronda 2", spec §6, decisión firmada
/// del dueño, 2026-08-14): ej. "Al confirmar la reserva — 500 USD", "10 de enero de 2027 — 1.200 USD".
///
/// <para><b>Es INFORMATIVO</b>: el PDF lo dibuja tal cual se cargó, en el orden de <see cref="Position"/>.
/// NO toca cobranzas, cuenta corriente ni el saldo real del cliente — es la misma filosofía que
/// <see cref="HotelBooking.InstallmentsCount"/> (plan de cuotas de un servicio), pero a nivel del TOTAL
/// de la reserva en vez de un servicio puntual.</para>
///
/// <para><b>Por qué una tabla hija y no un JSON en la reserva</b>: mismo criterio que
/// <see cref="ReservaPendingChange"/> — el vendedor reemplaza LA LISTA ENTERA en cada edición (arma de
/// nuevo el plan completo, no corrige una fila suelta), así que un DELETE+INSERT sobre una tabla con
/// columnas tipadas es más simple de leer/auditar que parsear un JSON. Se borra en cascada con la reserva.</para>
/// </summary>
public class BudgetPaymentPlanInstallment
{
    public int Id { get; set; }

    /// <summary>FK a <see cref="Reserva"/> (ojo: la tabla real de Reserva se llama "TravelFiles"). Cascade: al borrar la reserva se borra su plan de pagos.</summary>
    public int ReservaId { get; set; }
    public Reserva? Reserva { get; set; }

    /// <summary>
    /// Orden de impresión (1, 2, 3...). El endpoint que reemplaza el plan completo las numera en el
    /// orden en que llegan en el request — no hay forma de "insertar en el medio", el vendedor manda la
    /// lista entera ya ordenada.
    /// </summary>
    public int Position { get; set; }

    /// <summary>Texto de CUÁNDO se paga, tal como lo escribe el vendedor ("Al confirmar la reserva", "10 de enero de 2027").</summary>
    [Required]
    [MaxLength(200)]
    public string DueText { get; set; } = string.Empty;

    [Column(TypeName = "decimal(18,2)")]
    public decimal Amount { get; set; }

    [MaxLength(3)]
    public string Currency { get; set; } = Monedas.ARS;
}
