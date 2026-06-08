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

    // Flag operativo SOLO LECTURA. El flujo de cancelacion lo usa para mostrar/ocultar
    // toda la rama de la Nota de Debito por penalidad (ADR-013/014): la opcion de
    // "cargo propio de la agencia" en el modal de cancelacion, la bandeja de ND
    // pendientes y la pantalla de confirmacion diferida. Vive en OperationalFinanceSettings;
    // se proyecta aca igual que EnableMultiCurrencyInvoicing / EnableSoldToSettleStates
    // para no obligar al frontend a pegarle a un segundo endpoint.
    // Con OFF (default) la rama de ND es UI muerta y el backend es byte-identico a hoy.
    public bool EnableCancellationDebitNote { get; set; }

    // Flag operativo SOLO LECTURA (ADR-016 F0a). El frontend lo usara (a partir de F2) para
    // mostrar/ocultar el bloque de texto generado por IA en las alertas. Vive en
    // OperationalFinanceSettings; se proyecta aca igual que los demas flags para no obligar al
    // frontend a pegarle a un segundo endpoint. Con OFF (default) no hay nada de copiloto en UI.
    public bool EnableAiCopilot { get; set; }
}
