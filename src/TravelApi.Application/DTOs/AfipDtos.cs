namespace TravelApi.Application.DTOs;

public class AfipVoucherDetails
{
    public decimal ImporteTotal { get; set; }
    public decimal ImporteNeto { get; set; }
    public decimal ImporteIva { get; set; }
    public decimal ImporteTrib { get; set; }
    public List<VatDetail> VatDetails { get; set; } = new();
    public List<TributeDetail> TributeDetails { get; set; } = new();
}

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
