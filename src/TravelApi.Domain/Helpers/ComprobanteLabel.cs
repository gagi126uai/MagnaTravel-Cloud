namespace TravelApi.Domain.Helpers;

/// <summary>
/// Etiqueta legible de un comprobante fiscal para mostrarle a una persona ("Factura B 0001-00012345"),
/// sin exponer IDs internos (reglas T-5 / P-17). El numero se formatea punto de venta (4 digitos) +
/// numero (8 digitos), tal cual salen impresos los comprobantes.
///
/// <para><b>Por que vive en el Dominio</b>: el mismo texto lo arman hoy la pantalla de anulaciones y el
/// reporte de facturas en dolares. Que dos lugares escriban "Factura B" con criterios propios es como
/// termina un sistema diciendo "Factura B" en una pantalla y "FC B" en la de al lado (regla T-6: el
/// texto se fija una unica vez).</para>
/// </summary>
public static class ComprobanteLabel
{
    /// <summary>
    /// Arma la etiqueta completa: tipo + punto de venta + numero.
    /// </summary>
    /// <param name="tipoComprobante">Codigo de tipo de comprobante del organismo fiscal (1 = Factura A, 6 = B, 11 = C...).</param>
    public static string Format(int tipoComprobante, int puntoDeVenta, long numeroComprobante)
        => $"{FormatTipo(tipoComprobante)} {puntoDeVenta:D4}-{numeroComprobante:D8}";

    /// <summary>
    /// Solo el nombre del tipo de comprobante. Un codigo que no conocemos cae en "Comprobante": nunca se
    /// muestra el numero crudo del tipo, que no le dice nada a nadie fuera del sistema.
    /// </summary>
    public static string FormatTipo(int tipoComprobante) => tipoComprobante switch
    {
        1 => "Factura A",
        6 => "Factura B",
        11 => "Factura C",
        51 => "Factura M",
        3 => "Nota de credito A",
        8 => "Nota de credito B",
        13 => "Nota de credito C",
        53 => "Nota de credito M",
        2 => "Nota de debito A",
        7 => "Nota de debito B",
        12 => "Nota de debito C",
        52 => "Nota de debito M",
        _ => "Comprobante",
    };

    /// <summary>
    /// <c>true</c> si el codigo corresponde a una FACTURA de venta (no a una nota de credito ni de
    /// debito). Lo usa el reporte de facturas en dolares para no mezclar en la misma tabla la venta con
    /// las notas que la corrigen.
    /// </summary>
    public static bool IsSaleInvoice(int tipoComprobante)
        => tipoComprobante is 1 or 6 or 11 or 51;
}
