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
/// ADR-037 / cuadre POR MONEDA (2026-06-22): igual que <see cref="CuadreInvoiceLine"/> pero con la
/// MONEDA ISO ("ARS"/"USD") del comprobante. El llamador la deriva de <c>Invoice.MonId</c> (codigo ARCA
/// "PES"/"DOL") con <c>ArcaCurrencyMapper.ToIso</c>; si MonId viene vacio o no se reconoce, normaliza a
/// ARS (regla legacy: factura sin moneda = pesos). Asi el cuadre se agrupa por moneda sin mezclar.
/// </summary>
public readonly record struct CuadreInvoiceLineByCurrency(
    string Currency, int TipoComprobante, decimal ImporteTotal, bool IsLive);

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
            // Reusa la MISMA regla de signo (factura/ND suma, NC resta, vivo, tipo conocido) que el
            // calculo por moneda, para que el escalar y el detalle nunca diverjan.
            facturadoNeto += SignedNetAmount(line.TipoComprobante, line.ImporteTotal, line.IsLive);
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

    /// <summary>
    /// ADR-037 / cuadre POR MONEDA (2026-06-22): calcula el FACTURADO NETO de cada moneda por separado
    /// (facturas + ND - NC vivas), agrupando los comprobantes por su moneda ISO. Devuelve un diccionario
    /// {moneda ISO -> facturado neto}. SOLO incluye monedas que tienen al menos un comprobante (vivo o no);
    /// el llamador combina este resultado con sus lineas de venta por moneda para armar "facturado" y
    /// "falta facturar" de cada moneda — una moneda con venta y sin facturas no aparece aca y se trata
    /// como facturado 0; una factura en una moneda sin venta aparece aca con facturado &gt; 0.
    ///
    /// <para>Existe como metodo aparte (no un overload de <see cref="Calculate"/>) porque el resultado es
    /// distinto: el escalar produce UN cuadre (vendido/disponible/excedido); este produce el facturado neto
    /// CRUDO por moneda, y el "disponible" por moneda lo arma el llamador con SU venta de esa moneda
    /// (TotalSale), no con un "vendido" unico. Comparte la regla de signo con el escalar via
    /// <see cref="SignedNetAmount"/>, asi no hay drift.</para>
    /// </summary>
    public static IReadOnlyDictionary<string, decimal> CalculatePerCurrency(
        IEnumerable<CuadreInvoiceLineByCurrency> lines)
    {
        // Ordinal: las monedas ya vienen normalizadas en mayuscula ("ARS"/"USD") desde el llamador.
        var facturadoNetoPorMoneda = new Dictionary<string, decimal>(System.StringComparer.Ordinal);

        foreach (var line in lines)
        {
            decimal signed = SignedNetAmount(line.TipoComprobante, line.ImporteTotal, line.IsLive);

            // Una factura no viva o de tipo desconocido aporta 0; igual creamos la entrada de su moneda,
            // porque el hecho de que exista un comprobante en esa moneda es informacion para el llamador
            // (le dice "esta moneda tuvo facturacion", aunque neta sea 0). No inventa montos: solo el 0.
            if (facturadoNetoPorMoneda.TryGetValue(line.Currency, out var acumulado))
            {
                facturadoNetoPorMoneda[line.Currency] = acumulado + signed;
            }
            else
            {
                facturadoNetoPorMoneda[line.Currency] = signed;
            }
        }

        return facturadoNetoPorMoneda;
    }

    /// <summary>
    /// Regla de signo UNICA del cuadre: una factura/ND viva suma su importe, una NC viva lo resta, y todo
    /// lo demas (no vivo, tipo desconocido) aporta 0. La comparten el calculo escalar y el por-moneda para
    /// que nunca tengan criterios distintos de "que cuenta y con que signo".
    /// </summary>
    private static decimal SignedNetAmount(int tipoComprobante, decimal importeTotal, bool isLive)
    {
        if (!isLive)
        {
            // Comprobante anulado/rechazado (sin CAE vivo): no cuenta.
            return 0m;
        }

        switch (InvoiceComprobanteHelpers.Categorize(tipoComprobante))
        {
            case InvoiceComprobanteCategory.Invoice:
            case InvoiceComprobanteCategory.DebitNote:
                // Factura y Nota de Debito SUMAN lo que se le facturo al cliente.
                return importeTotal;
            case InvoiceComprobanteCategory.CreditNote:
                // Nota de Credito RESTA (devuelve / anula parte de lo facturado).
                return -importeTotal;
            default:
                // Tipo desconocido (dato sucio): no lo contamos para no inventar.
                return 0m;
        }
    }
}
