namespace TravelApi.Application.DTOs;

public class PackageBookingDto
{
    public Guid PublicId { get; set; }
    public Guid SupplierPublicId { get; set; }
    public string SupplierName { get; set; } = string.Empty;
    public Guid? RatePublicId { get; set; }
    public string PackageName { get; set; } = string.Empty;
    public string Destination { get; set; } = string.Empty;
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public int Nights { get; set; }
    public string Status { get; set; } = "Pendiente";
    public string? ConfirmationNumber { get; set; }
    public decimal SalePrice { get; set; }
    public decimal NetCost { get; set; }
    /// <summary>
    /// Moneda en que se cotizo el servicio (trazabilidad, copiada del tarifario).
    /// Null = legacy / no informado. NO se usa todavia en calculos de saldo.
    /// </summary>
    public string? Currency { get; set; }
    public bool IsPriceSynced { get; set; } = true;
    public string SourceKind { get; set; } = "Package";
    public string WorkflowStatus { get; set; } = "Solicitado";
}
