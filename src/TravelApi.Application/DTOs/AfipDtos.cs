namespace TravelApi.Application.DTOs;

public class AfipVoucherDetails
{
    public decimal ImporteTotal { get; set; }
    public decimal ImporteNeto { get; set; }
    public decimal ImporteIva { get; set; }
    public decimal ImporteTrib { get; set; }
    public List<VatDetail> VatDetails { get; set; } = new();
    public List<TributeDetail> TributeDetails { get; set; } = new();

    // ---- FC1.3.F2.2 (RH3-001 round 4 Camino A, 2026-05-27): campos OPCIONALES ----
    //
    // Estos campos los llena la etapa 3 (extension del parseo SOAP de FECompConsultar).
    // Sirven para el "stale key recovery": cuando un job de NC parcial pudo haber
    // emitido el comprobante al ARCA pero el proceso murio antes de guardar la
    // respuesta, el reintento consulta ARCA y deriva el CAE de aca en vez de
    // re-emitir y generar un CAE duplicado.
    //
    // CRITICO: son NULLABLE a proposito. Los callers existentes de GetVoucherDetails
    // (hoy solo InvoiceService:723, que lee ImporteTotal/VatDetails/TributeDetails/
    // ImporteNeto via object-initializer) NO se rompen: agregar propiedades nullable
    // a una clase no cambia ningun constructor ni deconstruccion. El parseo SOAP los
    // deja en null cuando el nodo no viene en el response.

    /// <summary>CAE del comprobante (nodo <c>CodAutorizacion</c> del response ARCA). Null si no esta presente.</summary>
    public string? Cae { get; set; }

    /// <summary>
    /// Comprobante asociado (primer item del array <c>CbtesAsoc</c> del response).
    /// Por contrato, una NC parcial Fase 2 tiene exactamente 1 CbteAsoc apuntando a la
    /// factura origen. Si el array viene vacio o con &gt;1 items, el parseo deja esto en
    /// null (la capa de recovery lo trata como mismatch y reintenta limpio).
    /// </summary>
    public int? CbteAsoc { get; set; }

    /// <summary>Fecha de emision del comprobante (nodo <c>CbteFch</c>, formato yyyyMMdd). Null si no esta presente.</summary>
    public DateTime? IssuedAt { get; set; }

    /// <summary>Moneda del comprobante (nodo <c>MonId</c>, ej. "PES", "DOL"). Null si no esta presente.</summary>
    public string? MonId { get; set; }

    /// <summary>Cotizacion de la moneda (nodo <c>MonCotiz</c>). Null si no esta presente.</summary>
    public decimal? MonCotiz { get; set; }
}

/// <summary>
/// FC1.3.F2.2 (RH3-001 round 4 Camino A, plan tactico sub-tarea A.1, 2026-05-27):
/// resultado de la consulta compuesta a ARCA que usa el "stale key recovery" de la NC parcial.
///
/// <para><b>Por que existe</b>: cuando Hangfire reintenta un job de NC parcial, no sabemos
/// si el intento anterior llego a emitir el comprobante al ARCA o murio antes. Este DTO
/// resume la respuesta de <c>QueryLastAuthorizedWithDetailsAsync</c> (que combina
/// <c>FECompUltimoAutorizado</c> + <c>FECompConsultar</c>) para arbitrar:</para>
/// <list type="bullet">
///   <item><c>Found=false</c> -> el numerador ARCA no avanzo desde el snapshot previo: el
///     POST nunca viajo. Se borra la idempotency key huerfana y se reintenta limpio.</item>
///   <item><c>Found=true</c> + <c>CbteAsoc</c> matchea la factura origen + <c>ImporteTotal</c>
///     coincide -> el POST si viajo: se deriva el <c>Cae</c> sin re-emitir.</item>
/// </list>
///
/// <para><b>Inmutable</b> (record posicional con init-only). Casi todos los campos son
/// nullable porque cuando <c>Found=false</c> no hay comprobante del cual leer detalle.</para>
/// </summary>
/// <param name="Found">true si el numerador ARCA avanzo y hay un comprobante para inspeccionar.</param>
/// <param name="LastNumero">Ultimo numero de comprobante autorizado por ARCA para ese PV+tipo. Util incluso cuando <c>Found=false</c> para diagnostico.</param>
/// <param name="Cae">CAE del comprobante encontrado. Null si <c>Found=false</c>.</param>
/// <param name="CbteAsoc">Comprobante asociado (factura origen) derivado del response. Null si no aplica o el array no traia exactamente 1 item.</param>
/// <param name="IssuedAt">Fecha de emision del comprobante encontrado. Null si <c>Found=false</c>.</param>
/// <param name="ImporteTotal">Total del comprobante encontrado, para comparar contra el monto esperado. Null si <c>Found=false</c>.</param>
/// <param name="MonId">Moneda del comprobante encontrado. Null si <c>Found=false</c> o no presente.</param>
/// <param name="MonCotiz">Cotizacion del comprobante encontrado. Null si <c>Found=false</c> o no presente.</param>
public record ArcaCompoundQueryResult(
    bool Found,
    int? LastNumero,
    string? Cae,
    int? CbteAsoc,
    DateTime? IssuedAt,
    decimal? ImporteTotal,
    string? MonId,
    decimal? MonCotiz);

public class VatDetail
{
    public int Id { get; set; }
    public decimal BaseImp { get; set; }
    public decimal Importe { get; set; }
}

public class TributeDetail
{
    public int Id { get; set; }
    public string Desc { get; set; } = string.Empty;
    public decimal BaseImp { get; set; }
    public decimal Alic { get; set; }
    public decimal Importe { get; set; }
}

public class AfipSettingsDto
{
    public long Cuit { get; set; }
    public int PuntoDeVenta { get; set; }
    public bool IsProduction { get; set; }
    public string TaxCondition { get; set; } = "Responsable Inscripto";
    public bool HasCertificate { get; set; }
    public bool HasProdCertificate { get; set; }
    public string? CertificatePath { get; set; }
    public string? ProdCertificatePath { get; set; }
}
