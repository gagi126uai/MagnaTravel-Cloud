using System.Globalization;

namespace TravelApi.Domain.Reservations;

/// <summary>
/// Hallazgo auditoria ERP #9 (2026-06-13): compara cuanto se esta facturando (suma de los items de la
/// factura) contra cuanto vale lo VENDIDO Y CONFIRMADO de la reserva en ESA misma moneda
/// (<c>ConfirmedSale</c> de la linea de moneda). Si no cuadra, produce un texto de AVISO.
///
/// <para><b>No bloquea</b>: la decision del dueño es AVISAR, no impedir. A veces se factura distinto a
/// proposito (factura parcial, ajuste, concepto agrupado distinto). El service usa este aviso para
/// poblar un campo informativo en la respuesta y deja emitir igual. Mismo espiritu que el <c>warning</c>
/// de <c>AddServiceAsync</c> ("se vende a perdida") que tampoco frena el alta.</para>
///
/// <para><b>Tolerancia de centavo</b>: comparamos con <see cref="ToleranceArs"/> = 0.01 para no avisar
/// por diferencias de redondeo de 1 centavo entre la suma de lineas y el agregado. Una diferencia real
/// (el operador facturo de mas o de menos a proposito o por error) supera ese umbral y se avisa.</para>
///
/// <para>Clase PURA (sin EF, sin DB). El service le pasa los dos numeros ya resueltos (suma de items y
/// ConfirmedSale de la moneda de la factura); esta clase solo decide si hay aviso y arma el texto.</para>
/// </summary>
public static class InvoiceMismatchChecker
{
    /// <summary>
    /// Tolerancia para considerar que "cuadra". 1 centavo: cubre el redondeo entre la suma de lineas
    /// y el total agregado sin tapar diferencias reales (que son de pesos, no de centavos).
    /// </summary>
    public const decimal ToleranceArs = 0.01m;

    /// <summary>
    /// Devuelve un texto de aviso si la suma de los items facturados NO cuadra con la venta confirmada
    /// de la reserva en esa moneda; <c>null</c> si cuadra (dentro de la tolerancia de centavo).
    ///
    /// <para><paramref name="currency"/> es la moneda de la factura (ISO: "ARS"/"USD"), solo para el
    /// texto del aviso. <paramref name="invoicedItemsTotal"/> es la suma de los <c>Total</c> de los items
    /// del request. <paramref name="confirmedSaleForCurrency"/> es el <c>ConfirmedSale</c> de la linea de
    /// esa moneda de la reserva (0 si la reserva no tiene venta confirmada en esa moneda).</para>
    /// </summary>
    public static string? BuildMismatchWarning(
        string currency,
        decimal invoicedItemsTotal,
        decimal confirmedSaleForCurrency)
    {
        decimal invoiced = Math.Round(invoicedItemsTotal, 2);
        decimal confirmed = Math.Round(confirmedSaleForCurrency, 2);

        decimal difference = invoiced - confirmed;
        if (Math.Abs(difference) <= ToleranceArs)
        {
            return null; // cuadra (o difiere solo por redondeo de centavo)
        }

        string currencyLabel = string.IsNullOrWhiteSpace(currency) ? Entities.Monedas.ARS : currency;
        string invoicedText = invoiced.ToString("N2", CultureInfo.InvariantCulture);
        string confirmedText = confirmed.ToString("N2", CultureInfo.InvariantCulture);

        // El signo de la diferencia le dice al operador si esta facturando de MAS o de MENOS frente a
        // lo vendido confirmado. Es informativo: puede ser intencional (factura parcial, ajuste).
        string sentido = difference > 0m ? "mas" : "menos";

        return
            $"Aviso: la factura suma {currencyLabel} {invoicedText}, que no coincide con lo vendido " +
            $"confirmado de la reserva en {currencyLabel} ({confirmedText}). Estas facturando {sentido} " +
            "de lo vendido. Si es a proposito, podes emitir igual.";
    }
}
