using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TravelApi.Domain.Entities;

/// <summary>
/// Estados de un <see cref="CommissionAccrual"/> (comision de vendedor). Strings persistidos asi en BD.
/// </summary>
public static class CommissionAccrualStatus
{
    /// <summary>La comision se gano (la reserva quedo totalmente cobrada) pero todavia NO se pago al vendedor.</summary>
    public const string Devengada = "Devengada";

    /// <summary>La comision ya se le pago/liquido al vendedor. La pone la pantalla de liquidacion (futuro front).</summary>
    public const string Liquidada = "Liquidada";
}

/// <summary>
/// Auditoria ERP 2026-06-12 (hallazgo #1, decision del dueño): comision del VENDEDOR devengada por una
/// reserva, separada POR MONEDA (consistente con ADR-021). Una fila por combinacion
/// (Reserva + Vendedor + Moneda).
///
/// <para><b>Que representa</b>: cuanto le toca de comision al vendedor responsable de la reserva, calculado
/// como un % (de <see cref="CommissionRule"/>) sobre la GANANCIA de los servicios CONFIRMADOS de esa moneda.
/// NO confundir con <c>HotelBooking.Commission</c> (que es la ganancia del servicio, no la comision del
/// vendedor) ni con el % suelto de la calculadora de <c>CommissionsController</c> (que nunca se aplicaba a
/// una reserva).</para>
///
/// <para><b>Cuando se devenga</b>: cuando la reserva queda totalmente cobrada (<c>Balance &lt;= 0</c>) por
/// primera vez. Si despues se cancela o el saldo vuelve a positivo, el <c>Amount</c> se revierte a 0 (TOPE
/// CERO, nunca negativo). El recalculo es idempotente: se reescribe esta fila en cada recalculo de plata de
/// la reserva, no se duplica (la unicidad la garantiza el indice (ReservaId, SellerUserId, Currency)).</para>
///
/// <para><b>Dato sensible (tipo costo)</b>: la comision revela margen/ganancia. El endpoint de lectura se
/// gatea con <c>cobranzas.see_cost</c>, igual que los montos de costo del resto del sistema.</para>
/// </summary>
public class CommissionAccrual : IHasPublicId
{
    public int Id { get; set; }
    public Guid PublicId { get; set; } = Guid.NewGuid();

    /// <summary>Vendedor al que se le atribuye la comision = <see cref="Reserva.ResponsibleUserId"/>.</summary>
    [Required]
    [MaxLength(200)]
    public string SellerUserId { get; set; } = string.Empty;

    /// <summary>
    /// Snapshot del nombre del vendedor al momento de devengar (espejo de <see cref="Reserva.ResponsibleUserName"/>).
    /// Se guarda denormalizado para no depender de Identity al listar comisiones.
    /// </summary>
    [MaxLength(200)]
    public string? SellerName { get; set; }

    /// <summary>FK a <see cref="Reserva"/> (tabla "TravelFiles"). Indexada unica junto con vendedor y moneda.</summary>
    public int ReservaId { get; set; }
    public Reserva? Reserva { get; set; }

    /// <summary>Moneda de esta comision: "ARS" o "USD" (<c>Monedas.Soportadas</c>). Una fila por moneda devengada.</summary>
    [MaxLength(3)]
    public string Currency { get; set; } = Monedas.ARS;

    /// <summary>
    /// Monto de la comision en <see cref="Currency"/>. Nunca negativo (tope cero): si la reserva deja de estar
    /// cobrada o se cancela, vuelve a 0. Es la suma, por servicio confirmado de esta moneda, de
    /// <c>ganancia_del_servicio * porcentaje_de_regla / 100</c>.
    /// </summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal Amount { get; set; }

    /// <summary>
    /// Porcentaje EFECTIVO de comision sobre la ganancia de esta moneda (Amount / ganancia confirmada * 100),
    /// guardado para trazabilidad/listado. Si distintos servicios de la misma moneda tienen reglas con %
    /// distinto, este es el % promedio ponderado por ganancia. 0 si no hubo ganancia o no aplica regla.
    /// </summary>
    [Column(TypeName = "decimal(7,4)")]
    public decimal RatePercent { get; set; }

    /// <summary>Estado de la comision (ver <see cref="CommissionAccrualStatus"/>). Default "Devengada".</summary>
    [MaxLength(20)]
    public string Status { get; set; } = CommissionAccrualStatus.Devengada;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Ultima vez que el recalculo idempotente toco esta fila (monto, estado, etc.).</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
