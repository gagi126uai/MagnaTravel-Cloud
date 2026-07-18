using TravelApi.Application.DTOs;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Reservations;

namespace TravelApi.Infrastructure.Reservations;

/// <summary>
/// ADR-048 T5 (2026-07-17, hardening — materializacion de los ejes secundarios en la cabecera de la
/// reserva): calcula los DOS valores que se van a guardar en <see cref="Reserva.DerivedCollectionStatus"/>
/// y <see cref="Reserva.DerivedInvoicingStatus"/>.
///
/// <para><b>No es una regla de negocio nueva</b>: este proyector NO reimplementa el criterio de "que es
/// deuda" o "que es facturado neto" — llama a las MISMAS funciones puras que ya usan el detalle
/// (<c>ReservaService.GetReservaByIdAsync</c>) y el listado (<c>FillPorMonedaForListAsync</c> /
/// <c>FillInvoicingStatusForListAsync</c>): <see cref="ReservaCollectionStatus.Derive(IEnumerable{ReservaCollectionLine})"/>
/// y <see cref="ReservaInvoicingStatus.Derive"/> + <see cref="ReservaInvoicingCuadreCalculator.Calculate"/>.
/// Si mañana cambia el criterio de "que es deuda", cambia en UN solo lugar y automaticamente el
/// materializado y el en-vivo quedan de acuerdo — nunca puede haber DOS reglas divergentes (regla 8).</para>
///
/// <para><b>Por que no vive en <c>TravelApi.Domain</c></b>: las funciones que reusa
/// (<see cref="ReservaCollectionStatus"/>, <see cref="ReservaInvoicingStatus"/>) viven en
/// <c>TravelApi.Application</c> (capa de presentacion de reglas, no de dominio puro — decision heredada de
/// ADR-033/ADR-037). <c>Domain</c> no referencia <c>Application</c> (seria invertir la dependencia), pero
/// <c>Infrastructure</c> SI referencia ambas, asi que este proyector vive aca, junto a
/// <see cref="ReservaMoneyPersister"/> y <see cref="ReservaTerminalTransitionApplier"/> (sus dos hermanos
/// del mismo chokepoint).</para>
///
/// <para><b>Respeta B3 por construccion</b>: ninguna de las dos funciones que reusa mira
/// <c>Reserva.Status</c> — el eje de cobro sale del saldo por moneda, el de facturacion del cuadre de
/// comprobantes. Como NO hay una rama especial "si esta Cancelled hago X, si no Y", no existe el riesgo
/// que B3 advierte (materializar una mentira leyendo SOLO el estado <c>Cancelled</c> y dejando
/// <c>PendingOperatorRefund</c> con un valor viejo): ambos estados del par pasan por la MISMA cuenta, sin
/// distincion, cada vez que este proyector corre.</para>
/// </summary>
internal static class ReservaDerivedAxesProjector
{
    /// <summary>
    /// Calcula el eje de COBRO a partir del MISMO <see cref="ReservaMoneySummary"/> que
    /// <see cref="ReservaMoneyPersister"/> ya calculo para actualizar el escalar y la tabla hija — no hace
    /// ninguna query extra.
    /// </summary>
    public static string ProjectCollectionStatus(ReservaMoneySummary summary)
    {
        var lines = summary.PorMoneda.Values.Select(line => new ReservaCollectionLine(
            line.Balance,
            hasCharges: line.ConfirmedSale > 0m,
            hasPayments: line.TotalPaid > 0m));

        return ReservaCollectionStatus.Derive(lines);
    }

    /// <summary>
    /// Calcula el eje de FACTURACION a partir de los comprobantes YA CARGADOS de la reserva
    /// (<paramref name="invoices"/>, responsabilidad del caller incluirlos — ver el Include agregado en
    /// <see cref="ReservaMoneyPersister"/>) y de la venta total (<paramref name="totalSale"/>, el mismo
    /// escalar que el persister acaba de recalcular).
    /// </summary>
    public static string ProjectInvoicingStatus(decimal totalSale, IEnumerable<Invoice> invoices)
    {
        var cuadreLines = invoices.Select(invoice => new CuadreInvoiceLine(
            invoice.TipoComprobante,
            invoice.ImporteTotal,
            // Misma regla UNICA que el detalle y el listado: cuenta el CAE aprobado aunque este anulado
            // (ver el XML-doc de ReservaInvoicingCuadreCalculator.CountsInNetBilled).
            IsLive: ReservaInvoicingCuadreCalculator.CountsInNetBilled(invoice.Resultado)));

        var cuadre = ReservaInvoicingCuadreCalculator.Calculate(totalSale, cuadreLines);

        return ReservaInvoicingStatus.Derive(totalSale, cuadre.FacturadoNeto, cuadre.BrutoEmitido);
    }
}
