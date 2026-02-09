using System.Xml.Serialization;

namespace TravelApi.Models.Afip;

// ==================== WSAA ====================
[XmlRoot("loginTicketResponse")]
public class LoginTicketResponse
{
    [XmlElement("header")]
    public LoginHeader? Header { get; set; }
    
    [XmlElement("credentials")]
    public LoginCredentials? Credentials { get; set; }
}

public class LoginHeader
{
    [XmlElement("source")]
    public string? Source { get; set; }
    
    [XmlElement("destination")]
    public string? Destination { get; set; }
    
    [XmlElement("uniqueId")]
    public long UniqueId { get; set; }
    
    [XmlElement("generationTime")]
    public DateTime GenerationTime { get; set; }
    
    [XmlElement("expirationTime")]
    public DateTime ExpirationTime { get; set; }
}

public class LoginCredentials
{
    [XmlElement("token")]
    public string? Token { get; set; }
    
    [XmlElement("sign")]
    public string? Sign { get; set; }
}

// ==================== WSFE ====================
// Simplificación manual para evitar XSD complejos

public class FECAERequest
{
    public FEAuthRequest Auth { get; set; } = new();
    public FECAECabRequest FeCabReq { get; set; } = new();
    public List<FECAEDetRequest> FeDetReq { get; set; } = new();
}

public class FEAuthRequest
{
    public string Token { get; set; } = "";
    public string Sign { get; set; } = "";
    public long Cuit { get; set; }
}

public class FECAECabRequest
{
    public int CantReg { get; set; }
    public int PtoVta { get; set; }
    public int CbteTipo { get; set; }
}

public class FECAEDetRequest
{
    public int Concepto { get; set; } // 1 Productos, 2 Servicios, 3 Productos y Servicios
    public int DocTipo { get; set; } // 80=CUIT, 96=DNI, 99=Consumidor Final
    public long DocNro { get; set; }
    public long CbteDesde { get; set; }
    public long CbteHasta { get; set; }
    public string CbteFch { get; set; } = ""; // YYYYMMDD
    public double ImpTotal { get; set; }
    public double ImpTotConc { get; set; } // No gravado
    public double ImpNeto { get; set; }
    public double ImpOpEx { get; set; } // Exento
    public double ImpTrib { get; set; }
    public double ImpIVA { get; set; }
    public string MonId { get; set; } = "PES";
    public double MonCotiz { get; set; } = 1;
    
    // Opcionales para Servicios
    public string? FchServDesde { get; set; }
    public string? FchServHasta { get; set; }
    public string? FchVtoPago { get; set; }

    public List<AlicIva>? Iva { get; set; }
}

public class AlicIva
{
    public int Id { get; set; } // 3=0%, 4=10.5%, 5=21%, 6=27%
    public double BaseImp { get; set; }
    public double Importe { get; set; }
}

public class FECAEResponse
{
    public FECAECabResponse FeCabResp { get; set; } = new();
    public List<FECAEDetResponse> FeDetResp { get; set; } = new();
    public List<Err>? Errors { get; set; }
}

public class FECAECabResponse
{
    public long Cuit { get; set; }
    public int PtoVta { get; set; }
    public int CbteTipo { get; set; }
    public string FchProceso { get; set; } = "";
    public int CantReg { get; set; }
    public string Resultado { get; set; } = "";
    public string Reproceso { get; set; } = "";
}

public class FECAEDetResponse
{
    public int Concepto { get; set; }
    public int DocTipo { get; set; }
    public long DocNro { get; set; }
    public long CbteDesde { get; set; }
    public long CbteHasta { get; set; }
    public string CbteFch { get; set; } = "";
    public string Resultado { get; set; } = "";
    public string CAE { get; set; } = "";
    public string CAEFchVto { get; set; } = "";
    public List<Obs>? Observaciones { get; set; }
}

public class Obs
{
    public int Code { get; set; }
    public string Msg { get; set; } = "";
}

public class Err
{
    public int Code { get; set; }
    public string Msg { get; set; } = "";
}
