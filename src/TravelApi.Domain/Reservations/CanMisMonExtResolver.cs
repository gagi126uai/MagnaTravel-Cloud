namespace TravelApi.Domain.Reservations;

/// <summary>
/// ADR-042 §3.3.1 (2026-07-01): deriva el valor fiscal <c>CanMisMonExt</c> ("Cancela en Misma
/// Moneda Extranjera", RG ARCA 5616/2024) que se CONGELA en el comprobante al momento de emitir.
///
/// <para><b>Regla</b>: el discriminador es lo que la FACTURA DECLARA (su moneda), NO como se cobro.
/// Que el cliente pague un dolar en pesos NO cambia retroactivamente este dato.
/// <list type="bullet">
///   <item>comprobante en pesos (PES)  -> <c>null</c> (no se emite el nodo; byte-identico al historico)</item>
///   <item>comprobante en divisa       -> <c>"N"</c> (criterio firme para esta agencia: factura USD, cobra pesos)</item>
/// </list></para>
///
/// <para><b>Por que "N" y no "S"</b>: si el comprobante declara "S", ARCA FUERZA <c>MonCotiz</c> = TC
/// BNA vendedor del dia habil anterior a la emision de la NC, lo que es INCOMPATIBLE con heredar el
/// <c>MonCotiz</c> del comprobante original (regla de herencia ya decidida). El camino "S" (recalcular
/// TC por BNA en la NC) queda pendiente de firma matriculada y NO se construye ahora.</para>
///
/// <para><b>Espejado estricto</b>: la NC/ND NO redecide este valor: ESPEJA el congelado de su
/// comprobante original. Una NC con distinto <c>CanMisMonExt</c> que su original rompe el par en el
/// libro IVA y puede rebotar el CAE.</para>
/// </summary>
public static class CanMisMonExtResolver
{
    /// <summary>
    /// Deriva el valor a congelar al emitir un comprobante, a partir de su codigo de moneda ARCA.
    /// </summary>
    /// <param name="comprobanteMonId">Codigo de moneda ARCA ("PES", "DOL", ...).</param>
    /// <returns><c>null</c> para pesos (no aplica el nodo); <c>"N"</c> para moneda extranjera.</returns>
    public static string? Resolve(string? comprobanteMonId)
    {
        // Pesos (o moneda no informada): el nodo no aplica; null congela "no se emite".
        if (string.IsNullOrWhiteSpace(comprobanteMonId)
            || string.Equals(comprobanteMonId, "PES", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // Moneda extranjera: criterio firme "N" (factura en USD, cobra en pesos). Ver doc de la clase.
        return "N";
    }
}
