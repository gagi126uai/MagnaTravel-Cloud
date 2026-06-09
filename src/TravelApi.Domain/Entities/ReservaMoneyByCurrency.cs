using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TravelApi.Domain.Entities;

/// <summary>
/// ADR-021 §2.3.0 (multimoneda, 2026-06-08): tabla hija MATERIALIZADA con el detalle de plata
/// de una reserva SEPARADO por moneda. Una fila por cada moneda presente en la reserva (hoy a lo
/// sumo dos: ARS y USD).
///
/// <para><b>Por que existe</b> (decision B1 del review): los consumidores que suman/ordenan/filtran
/// saldos entre muchas reservas (cuenta corriente del cliente, reportes, tesoreria, top-N de
/// deudores) lo hacen en SQL. Un diccionario en memoria no es queryable en SQL: para hacer
/// <c>Where(Currency=="USD").Sum(Balance)</c> o un top-N por moneda hay que tener el detalle
/// PERSISTIDO en columnas reales. El escalar <c>Reserva.Balance</c> queda solo de semaforo
/// (¿tiene saldo si/no?); el monto real por moneda vive aca.</para>
///
/// <para><b>Es una PROYECCION, no una segunda fuente de verdad</b>: el calculo sigue siendo del
/// <c>ReservaMoneyCalculator</c>. Esta tabla se reescribe en cada recalculo (upsert por moneda,
/// borrar monedas ausentes) dentro de la MISMA <c>SaveChangesAsync</c> que persiste el escalar,
/// asi escalar y hija nunca divergen. Quien la escribe (rutina consolidada <c>ReservaMoneyPersister</c>)
/// es Capa 2/3; en Capa 1 esta tabla se crea vacia y se backfillea por recalculo (tambien Capa 2).</para>
/// </summary>
public class ReservaMoneyByCurrency
{
    public int Id { get; set; }

    /// <summary>FK a <see cref="Reserva"/> (tabla "TravelFiles"). Indexada unica junto con <see cref="Currency"/>.</summary>
    public int ReservaId { get; set; }
    public Reserva? Reserva { get; set; }

    /// <summary>Moneda de esta linea: "ARS" o "USD" (<c>Monedas.Soportadas</c>). Una fila por moneda presente.</summary>
    [MaxLength(3)]
    public string Currency { get; set; } = Monedas.ARS;

    /// <summary>Venta total (cotizada) de los servicios de esta moneda.</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal TotalSale { get; set; }

    /// <summary>Venta CONFIRMADA (servicios resueltos, ADR-020) de esta moneda. Es la base del saldo a cobrar.</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal ConfirmedSale { get; set; }

    /// <summary>Costo/inversion total de los servicios de esta moneda.</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal TotalCost { get; set; }

    /// <summary>Pagado imputado a ESTA moneda (suma de <c>ImputedAmount</c>, o <c>Amount</c> si el pago no cruzo).</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal TotalPaid { get; set; }

    /// <summary>
    /// Saldo de esta moneda = <see cref="ConfirmedSale"/> - <see cref="TotalPaid"/>. Puede ser
    /// negativo (saldo a favor del cliente en esta moneda). El saldo a favor de una moneda NO
    /// compensa la deuda de otra (decision §2.4). Indexado por (Currency, Balance) para los top-N.
    /// </summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal Balance { get; set; }
}
