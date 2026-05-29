namespace TravelApi.Application.DTOs;

public class AfipSettingsResponse
{
    public long Cuit { get; set; }
    public int PuntoDeVenta { get; set; }
    public bool IsProduction { get; set; }
    public string TaxCondition { get; set; } = "Responsable Inscripto";
    public bool HasCertificate { get; set; }
    public string? CertificateFileName { get; set; }
    public bool HasAuthToken { get; set; }
    public bool HasPadronToken { get; set; }
    
    public bool HasProdCertificate { get; set; }
    public string? ProdCertificateFileName { get; set; }
    public bool HasProdAuthToken { get; set; }
    public bool HasProdPadronToken { get; set; }

    // Flag operativo SOLO LECTURA. El modal de emision de factura lo usa para decidir
    // si muestra el selector de moneda (MVP ADR-012 facturar en USD). Vive en
    // OperationalFinanceSettings, no en AfipSettings; se proyecta aca para no obligar
    // al frontend a pegarle a un segundo endpoint. No se setea desde POST /afip/settings.
    public bool EnableMultiCurrencyInvoicing { get; set; }
}
