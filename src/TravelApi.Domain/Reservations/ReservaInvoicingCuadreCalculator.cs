using System.Collections.Generic;
using TravelApi.Domain.Entities;

namespace TravelApi.Domain.Reservations;

/// <summary>
/// Una linea de comprobante para el calculo del cuadre: su tipo AFIP, su importe total
/// y si esta "vivo" (CAE aprobado y no anulado). El llamador arma esta lista desde las
/// Invoices de la reserva.
/// </summary>
public readonly record struct CuadreInvoiceLine(int TipoComprobante, decimal ImporteTotal, bool IsLive);

/// <summary>
/// Resultado del cuadre de facturacion de una reserva: cuanto se vendio, cuanto se
/// facturo NETO (facturas + notas de debito - notas de credito) y cuanto queda
/// disponible para facturar. <see cref="Excedido"/> es true cuando ya se facturo
/// MAS de lo vendido (over-invoicing) — el caso que queremos avisar.
/// </summary>
public readonly record struct ReservaInvoicingCuadre(
    decimal Vendido,
    decimal FacturadoNeto,
    decimal Disponible,
    bool Excedido,
    decimal Exceso);

/// <summary>
/// Calculador PURO del cuadre entre lo VENDIDO en la reserva y lo FACTURADO al cliente.
///
/// <para>Existe porque hoy la factura se arma con montos cargados a mano y NADIE compara
/// el total facturado contra lo vendido en la reserva: se puede facturar $100.000 una
/// reserva de $80.000 sin que el sistema lo note. Este calculador es la cuenta unica del
/// cuadre; la UI la usa para AVISAR (no bloquea) cuando se factura de mas.</para>
///
/// <para>Sin EF ni base de datos: funcion pura, testeable sin Postgres. Reusa
/// <see cref="InvoiceComprobanteHelpers"/> para clasificar (facturas/ND suman, NC restan).</para>
/// </summary>
public static class ReservaInvoicingCuadreCalculator
{
    /// <summary>
    /// Calcula el cuadre. <paramref name="vendido"/> es el total vendido de la reserva
    /// (TotalSale, la fuente unica de verdad). <paramref name="lines"/> son los comprobantes
    /// de la reserva; SOLO los <c>IsLive</c> (CAE aprobado y no anulados) cuentan.
    /// </summary>
    public static ReservaInvoicingCuadre Calculate(decimal vendido, IEnumerable<CuadreInvoiceLine> lines)
    {
        decimal facturadoNeto = 0m;

        foreach (var line in lines)
        {
            if (!line.IsLive)
                continue;

            switch (InvoiceComprobanteHelpers.Categorize(line.TipoComprobante))
            {
                case InvoiceComprobanteCategory.Invoice:
                case InvoiceComprobanteCategory.DebitNote:
                    // Factura y Nota de Debito SUMAN lo que se le facturo al cliente.
                    facturadoNeto += line.ImporteTotal;
                    break;
                case InvoiceComprobanteCategory.CreditNote:
                    // Nota de Credito RESTA (devuelve / anula parte de lo facturado).
                    facturadoNeto -= line.ImporteTotal;
                    break;
                default:
                    // Tipo desconocido (dato sucio): no lo contamos para no inventar.
                    break;
            }
        }

        decimal disponible = vendido - facturadoNeto;
        bool excedido = facturadoNeto > vendido;
        decimal exceso = excedido ? facturadoNeto - vendido : 0m;

        return new ReservaInvoicingCuadre(
            Vendido: vendido,
            FacturadoNeto: facturadoNeto,
            Disponible: disponible,
            Excedido: excedido,
            Exceso: exceso);
    }
}
