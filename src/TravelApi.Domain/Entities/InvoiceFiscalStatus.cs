namespace TravelApi.Domain.Entities;

/// <summary>
/// H2 (2026-06-24): estado CLARO de una factura electronica de cara al usuario, derivado del
/// campo crudo <see cref="Invoice.Resultado"/> que escribe el job que pide el CAE a ARCA.
///
/// <para><b>Por que existe</b>: la emision es ASINCRONA. El front hace POST /invoices, el backend
/// crea la factura en PENDING y un job (ProcessInvoiceJob) le pide el CAE a ARCA en segundo plano.
/// Hoy el front solo ve "comprobante encolado" y nunca se entera de como termino. Este enum traduce
/// el <c>Resultado</c> crudo ("PENDING" / "A" / "R") a tres estados que el front puede mostrar tal
/// cual, sin tener que conocer los codigos internos de ARCA.</para>
/// </summary>
public enum InvoiceFiscalStatus
{
    /// <summary>
    /// Encolada, esperando la respuesta de ARCA. Equivale a Resultado == "PENDING" (o null, que es
    /// el estado de una factura recien creada antes de que el job corra por primera vez).
    /// </summary>
    InProcess = 0,

    /// <summary>
    /// ARCA aprobo: hay numero de comprobante + CAE + vencimiento de CAE. Equivale a Resultado == "A".
    /// </summary>
    Issued = 1,

    /// <summary>
    /// ARCA rechazo definitivamente. Equivale a Resultado == "R". El motivo legible queda en
    /// <see cref="Invoice.Observaciones"/> (texto ya traducido por TranslateAfipError).
    /// </summary>
    Rejected = 2,
}

/// <summary>
/// H2 (2026-06-24): traduce el <see cref="Invoice.Resultado"/> crudo al estado claro de tres valores.
///
/// <para>Es un helper PURO (sin acceso a base ni a servicios): recibe el string crudo y devuelve el
/// enum. Vive en Domain para poder testearlo aislado y reusarlo desde el service y desde cualquier
/// proyeccion. El mapeo es la fuente unica de verdad de "que significa cada Resultado".</para>
/// </summary>
public static class InvoiceFiscalStatusMapper
{
    /// <summary>
    /// Mapea el <c>Resultado</c> persistido al estado claro.
    ///
    /// <list type="bullet">
    ///   <item><c>"A"</c> -> <see cref="InvoiceFiscalStatus.Issued"/> (aprobada por ARCA).</item>
    ///   <item><c>"R"</c> -> <see cref="InvoiceFiscalStatus.Rejected"/> (rechazada por ARCA).</item>
    ///   <item>cualquier otro valor (<c>"PENDING"</c>, null, vacio) -> <see cref="InvoiceFiscalStatus.InProcess"/>.</item>
    /// </list>
    ///
    /// <para>El default conservador es "en proceso": una factura recien creada todavia no tiene
    /// Resultado seteado por el job, y nunca queremos mostrarla como rechazada o emitida sin que ARCA
    /// lo haya confirmado. El rechazo ("R") solo lo escribe el job cuando ARCA responde explicitamente
    /// que rechaza (un error tecnico/red deja la factura en PENDING, no en "R").</para>
    /// </summary>
    public static InvoiceFiscalStatus FromResultado(string? resultado)
    {
        return resultado switch
        {
            "A" => InvoiceFiscalStatus.Issued,
            "R" => InvoiceFiscalStatus.Rejected,
            _ => InvoiceFiscalStatus.InProcess,
        };
    }
}
