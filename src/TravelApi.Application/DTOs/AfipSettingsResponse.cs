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
}
