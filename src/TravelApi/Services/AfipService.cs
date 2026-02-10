#pragma warning disable CS8601, CS8602, CS8604, CS8618
using System.Net.Http;
using System.Globalization;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Serialization;
using Microsoft.EntityFrameworkCore;
using TravelApi.Data;
using TravelApi.Models;
using TravelApi.Models.Afip;

namespace TravelApi.Services;

public interface IAfipService
{
    // Configuration
    Task<bool> ValidateCertificate(byte[] certData, string password);
    Task<string> GetStatus();

    // Core
    Task<Invoice> CreateInvoice(int travelFileId, Invoice invoiceData);
}

public class AfipService : IAfipService
{
    private readonly AppDbContext _context;
    private readonly ILogger<AfipService> _logger;
    private readonly HttpClient _httpClient;

    // URLs (TODO: move to config)
    private const string WsaaUrlDev = "https://wsaahomo.afip.gov.ar/ws/services/LoginCms";
    private const string WsaaUrlProd = "https://wsaa.afip.gov.ar/ws/services/LoginCms";
    private const string WsfeUrlDev = "https://wswhomo.afip.gov.ar/wsfev1/service.asmx";
    private const string WsfeUrlProd = "https://servicios1.afip.gov.ar/wsfev1/service.asmx";

    public AfipService(AppDbContext context, ILogger<AfipService> logger, HttpClient httpClient)
    {
        _context = context;
        _logger = logger;
        _httpClient = httpClient;
    }

    public Task<bool> ValidateCertificate(byte[] certData, string password)
    {
        try
        {
            if (certData == null || certData.Length == 0) return Task.FromResult(false);
            // Validate we can open it
            var cert = new X509Certificate2(certData, password, X509KeyStorageFlags.Exportable);
            return Task.FromResult(cert.HasPrivateKey);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating certificate");
            return Task.FromResult(false);
        }
    }

    public async Task<string> GetStatus()
    {
        try
        {
            var settings = await _context.AfipSettings.FirstOrDefaultAsync();
            if (settings == null) return "No Configurado";
            if (settings.CertificateData == null || settings.CertificateData.Length == 0) return "Certificado Faltante";

            // Token Validity Check (Handle potential Timezone mismatch from previous saves)
            // Existing data might be stored as Argentina Time (e.g. 18:00) but without offset.
            // System is UTC (e.g. 21:00).
            // Robust check: If stored time is "close" to now, check if adding 4 hours makes it valid.
            // Or better: ensure we store UTC going forward.
            
            bool isValid = false;
            
            if (!string.IsNullOrEmpty(settings.Token))
            {
                 if (settings.TokenExpiration > DateTime.UtcNow) 
                 {
                     isValid = true;
                 }
                 // Legacy fix: Check if it's Argentina Time treated as UTC
                 else if (settings.TokenExpiration.HasValue && settings.TokenExpiration.Value.AddHours(3) > DateTime.UtcNow)
                 {
                     isValid = true;
                 }
            }

            if (isValid)
            {
                // Verify WSFE access quickly (Ping)
                // return "Online (Token Válido)";
                return await CheckWsfeStatus(settings);
            }
            
            // Try to login if expired
            await EnsureAuth(settings);
            return await CheckWsfeStatus(settings);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AFIP Status Error");
            return $"Error: {ex.Message}";
        }
    }

    private async Task<string> CheckWsfeStatus(AfipSettings settings)
    {
        try
        {
             // Call FECompUltimoAutorizado to check if we can reach the business service
             var url = settings.IsProduction ? WsfeUrlProd : WsfeUrlDev;
             var action = "http://ar.gov.afip.dif.FEV1/FECompUltimoAutorizado";
             
             // Determine Voucher Type based on Agency Tax Condition
             // Responsable Inscripto -> Check Type 001 (Factura A)
             // Monotributo -> Check Type 011 (Factura C)
             // Exento -> Check Type 011 (Factura C) (Usually)
             int cbteTipo = 1; // Default Factura A
             if (settings.TaxCondition == "Monotributo" || settings.TaxCondition == "Exento")
             {
                 cbteTipo = 11; // Factura C
             }

             var soapEnv = $@"<?xml version=""1.0"" encoding=""utf-8""?>
<soap:Envelope xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xmlns:xsd=""http://www.w3.org/2001/XMLSchema"" xmlns:soap=""http://schemas.xmlsoap.org/soap/envelope/"">
  <soap:Body>
    <FECompUltimoAutorizado xmlns=""http://ar.gov.afip.dif.FEV1/"">
      <Auth>
        <Token>{settings.Token}</Token>
        <Sign>{settings.Sign}</Sign>
        <Cuit>{settings.Cuit}</Cuit>
      </Auth>
      <PtoVta>{settings.PuntoDeVenta}</PtoVta>
      <CbteTipo>{cbteTipo}</CbteTipo> 
    </FECompUltimoAutorizado>
  </soap:Body>
</soap:Envelope>";

            var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Add("SOAPAction", action);
            request.Content = new StringContent(soapEnv, Encoding.UTF8, "text/xml");
            
            var response = await _httpClient.SendAsync(request);
            var responseXml = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode && responseXml.Contains("FECompUltimoAutorizadoResult"))
            {
                // Parse CBTE
                 var doc = XDocument.Parse(responseXml);
                 var result = doc.Descendants(XName.Get("FECompUltimoAutorizadoResult", "http://ar.gov.afip.dif.FEV1/")).FirstOrDefault();
                 var cbteNro = result?.Element(XName.Get("CbteNro", "http://ar.gov.afip.dif.FEV1/"))?.Value;
                 
                 return $"Online (WSFE OK - Ult. Factura A: {cbteNro})";
            }
            
            return "Online (Auth OK, WSFE Error)";
        }
        catch
        {
            return "Online (Auth OK, WSFE Unreachable)";
        }
    }
    
    private async Task EnsureAuth(AfipSettings settings)
    {
        // ... (Certificate checks) ...
        if (settings.CertificateData == null) throw new Exception("Certificado no configurado");

        try 
        {
            // 1. Load Certificate
            var cert = new X509Certificate2(settings.CertificateData, settings.CertificatePassword, X509KeyStorageFlags.Exportable);

            // 2. Create Login Ticket
            // UniqueId must be 32-bit unsigned int
            var uniqueId = (uint)(DateTime.UtcNow.Ticks % uint.MaxValue);
            
            // AFIP expects Argentina Time (UTC-3)
            var argentinaTime = DateTime.UtcNow.AddHours(-3);
            
            var xml = new XElement("loginTicketRequest",
                new XAttribute("version", "1.0"),
                new XElement("header",
                    new XElement("uniqueId", uniqueId),
                    new XElement("generationTime", argentinaTime.AddMinutes(-10).ToString("yyyy-MM-ddTHH:mm:ss")),
                    new XElement("expirationTime", argentinaTime.AddMinutes(+10).ToString("yyyy-MM-ddTHH:mm:ss"))
                ),
                new XElement("service", "wsfe")
            );

            // 3. Sign Ticket (CMS)
            var cms = new SignedCms(new ContentInfo(Encoding.UTF8.GetBytes(xml.ToString())));
            var signer = new CmsSigner(cert);
            signer.IncludeOption = X509IncludeOption.EndCertOnly;
            cms.ComputeSignature(signer);
            var signatureBase64 = Convert.ToBase64String(cms.Encode());

            // 4. Call WSAA (SOAP)
            var soapEnv = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
    <soapenv:Envelope xmlns:soapenv=""http://schemas.xmlsoap.org/soap/envelope/"" xmlns:ser=""http://wsaa.view.sua.dnet.fpm.afip.gov.ar/report/LoginCms"">
    <soapenv:Header/>
    <soapenv:Body>
        <ser:loginCms>
            <in0>{signatureBase64}</in0>
        </ser:loginCms>
    </soapenv:Body>
    </soapenv:Envelope>";

            var url = settings.IsProduction ? WsaaUrlProd : WsaaUrlDev;
            
            var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Add("SOAPAction", "\"\""); // Empty action for WSAA
            request.Content = new StringContent(soapEnv, Encoding.UTF8, "text/xml");

            var response = await _httpClient.SendAsync(request);
            var responseXml = await response.Content.ReadAsStringAsync();

            // 5. Parse Response
            // AFIP returns 500 for Faults, so we must parse XML first before checking status code
            XDocument doc;
            try 
            {
                doc = XDocument.Parse(responseXml);
            }
            catch
            {
                // If not XML and error status, throw original error
                if (!response.IsSuccessStatusCode)
                    throw new Exception($"WSAA Error {response.StatusCode}: {responseXml}");
                throw;
            }

            // Check for Faults explicitly
            var fault = doc.Descendants(XName.Get("Fault", "http://schemas.xmlsoap.org/soap/envelope/")).FirstOrDefault();
            if (fault != null)
            {
                var faultString = fault.Element("faultstring")?.Value;
                var faultCode = fault.Element("faultcode")?.Value;
                
                // Handle "Already Authenticated" - If we have a token, assume it's valid
                if (faultCode != null && faultCode.Contains("alreadyAuthenticated"))
                {
                     if (!string.IsNullOrEmpty(settings.Token)) 
                     {
                        _logger.LogWarning("AFIP reported alreadyAuthenticated. Using existing local token.");
                        return;
                     }
                     else
                     {
                         // No local token but AFIP says we have one. We are locked out.
                         throw new Exception($"AFIP Error: Ya existe un token válido pero no lo tenemos guardado. Esperá 10 minutos para reintentar. ({faultCode})");
                     }
                }
                
                throw new Exception($"WSAA Fault: {faultCode} - {faultString}");
            }

            if (!response.IsSuccessStatusCode)
                throw new Exception($"WSAA Error {response.StatusCode}: {responseXml}");

            var loginCmsReturn = doc.Descendants().FirstOrDefault(x => x.Name.LocalName == "loginCmsReturn")?.Value;
            
            if (string.IsNullOrEmpty(loginCmsReturn))
                throw new Exception("WSAA Empty Response");

            var ticket = XDocument.Parse(loginCmsReturn);
            var token = ticket.Descendants("token").First().Value;
            var sign = ticket.Descendants("sign").First().Value;
            
            // Parse Expiration correctly as Argentina Time (-03:00) then convert to UTC
            var expirationStr = ticket.Descendants("expirationTime").First().Value; // yyyy-MM-ddTHH:mm:ss
            var expirationLocal = DateTime.Parse(expirationStr); // Unspecified
            // Assume it is Argentina Time (-3). To get UTC, we add 3 hours.
            // We specify it as UTC directly to avoid ambiguity.
            var expirationUtc = DateTime.SpecifyKind(expirationLocal.AddHours(3), DateTimeKind.Utc);
            // Wait, if Parse returns Unspecified, and we know it's -3. 
            // -3 means "add 3 hours to get UTC".
            // So 17:00 ART + 3 = 20:00 UTC.
            
            // 6. Save to DB
            settings.Token = token;
            settings.Sign = sign;
            settings.TokenExpiration = expirationUtc;
            
            try 
            {
                await _context.SaveChangesAsync();
            }
            catch (Exception dbEx)
            {
                var innerMessage = dbEx.InnerException?.Message ?? "No inner exception";
                _logger.LogError(dbEx, $"Error saving AFIP Token to DB: {dbEx.Message} | Inner: {innerMessage}");
                throw new Exception($"Error guardando token en BD: {innerMessage}");
            }
        }
        catch (Exception ex)
        {
            // If we caught an exception but checked for alreadyAuthenticated inside, rethrow unless we handled it.
            // But here we just rethrow any unforeseen errors.
            throw; 
        }
    }

    public async Task<Invoice> CreateInvoice(int travelFileId, Invoice invoiceData)
    {
        var settings = await _context.AfipSettings.FirstOrDefaultAsync();
        if (settings == null) throw new Exception("AFIP no configurado");

        await EnsureAuth(settings);

        // 1. Get TravelFile & Customer
        var travelFile = await _context.TravelFiles
            .Include(f => f.Payer)
            .FirstOrDefaultAsync(f => f.Id == travelFileId);

        if (travelFile == null) throw new Exception("Expediente no encontrado");
        var customer = travelFile.Payer;
        if (customer == null) throw new Exception("El expediente no tiene cliente asignado");

        // 2. Determine Invoice Type (A, B, C)
        int cbteTipo = 6; // Default B
        
        if (settings.TaxCondition == "Responsable Inscripto")
        {
            if (customer.TaxCondition == "Responsable Inscripto")
            {
                cbteTipo = 1; // A
            }
            else
            {
                cbteTipo = 6; // B
            }
        }
        else // Monotributo or Exento
        {
            cbteTipo = 11; // C
        }

        // 3. Get Next Voucher Number
        int cbteNro = await GetNextVoucherNumber(settings, cbteTipo);

        // 4. Prepare Data
        var concept = 2; // Servicios
        var docTipo = 99; // Sin identificar / Consumidor Final
        long docNro = 0;

        if (!string.IsNullOrEmpty(customer.TaxId))
        {
            var cleanCuit = customer.TaxId.Replace("-", "").Replace(".", "").Trim();
            if (long.TryParse(cleanCuit, out long cuitVal))
            {
                docTipo = 80; // CUIT
                docNro = cuitVal;
            }
        }
        else if (!string.IsNullOrEmpty(customer.DocumentNumber))
        {
             // Assume DNI if TaxId is empty but DocumentNumber exists
             if (long.TryParse(customer.DocumentNumber, out long dniVal))
             {
                 docTipo = 96; // DNI
                 docNro = dniVal;
             }
        }

        // Dates
        var today = DateTime.Now.ToString("yyyyMMdd"); 
        var fchServDesde = today;
        var fchServHasta = today;
        var fchVtoPago = DateTime.Now.AddDays(10).ToString("yyyyMMdd");

        // Amounts
        decimal total = invoiceData.ImporteTotal;
        decimal net = total;
        decimal iva = 0;
        int ivaId = 3; // 0%

        if (cbteTipo == 1 || cbteTipo == 6)
        {
            net = Math.Round(total / 1.21m, 2);
            iva = Math.Round(total - net, 2);
            ivaId = 5; // 21%
        }
        else 
        {
            net = total;
            iva = 0;
            ivaId = 3;
        }

        // 5. Build FECAESolicitar
        var url = settings.IsProduction ? WsfeUrlProd : WsfeUrlDev;
        var action = "http://ar.gov.afip.dif.FEV1/FECAESolicitar";
        
        string ivaBlock = "";
        if (cbteTipo == 1 || cbteTipo == 6)
        {
            ivaBlock = $@"
            <Iva>
                <AlicIva>
                    <Id>{ivaId}</Id>
                    <BaseImp>{net.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}</BaseImp>
                    <Importe>{iva.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}</Importe>
                </AlicIva>
            </Iva>";
        }

        var soapEnv = $@"<?xml version=""1.0"" encoding=""utf-8""?>
<soap:Envelope xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xmlns:xsd=""http://www.w3.org/2001/XMLSchema"" xmlns:soap=""http://schemas.xmlsoap.org/soap/envelope/"">
  <soap:Body>
    <FECAESolicitar xmlns=""http://ar.gov.afip.dif.FEV1/"">
      <Auth>
        <Token>{settings.Token}</Token>
        <Sign>{settings.Sign}</Sign>
        <Cuit>{settings.Cuit}</Cuit>
      </Auth>
      <FeCAEReq>
        <FeCabReq>
          <CantReg>1</CantReg>
          <PtoVta>{settings.PuntoDeVenta}</PtoVta>
          <CbteTipo>{cbteTipo}</CbteTipo>
        </FeCabReq>
        <FeDetReq>
          <FECAEDetRequest>
            <Concepto>{concept}</Concepto>
            <DocTipo>{docTipo}</DocTipo>
            <DocNro>{docNro}</DocNro>
            <CbteDesde>{cbteNro}</CbteDesde>
            <CbteHasta>{cbteNro}</CbteHasta>
            <CbteFch>{today}</CbteFch>
            <ImpTotal>{total.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}</ImpTotal>
            <ImpTotConc>0</ImpTotConc>
            <ImpNeto>{net.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}</ImpNeto>
            <ImpOpEx>0</ImpOpEx>
            <ImpTrib>0</ImpTrib>
            <ImpIVA>{iva.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}</ImpIVA>
            <FchServDesde>{fchServDesde}</FchServDesde>
            <FchServHasta>{fchServHasta}</FchServHasta>
            <FchVtoPago>{fchVtoPago}</FchVtoPago>
            <MonId>PES</MonId>
            <MonCotiz>1</MonCotiz>
            <CondicionIVAReceptorId>{GetTaxConditionId(travelFile.Payer)}</CondicionIVAReceptorId>
            {ivaBlock}
          </FECAEDetRequest>
        </FeDetReq>
      </FeCAEReq>
    </FECAESolicitar>
  </soap:Body>
</soap:Envelope>";

        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("SOAPAction", action);
        request.Content = new StringContent(soapEnv, Encoding.UTF8, "text/xml");

        var response = await _httpClient.SendAsync(request);
        var responseXml = await response.Content.ReadAsStringAsync();
        
        // 6. Parse Response
        var doc = XDocument.Parse(responseXml);
        
        // Check Errors in FECAESolicitarResult
        var resultNode = doc.Descendants(XName.Get("FECAESolicitarResult", "http://ar.gov.afip.dif.FEV1/")).FirstOrDefault();
        if (resultNode == null) 
             throw new Exception($"Error SOAP (Sin Resultado): {responseXml}");

        var cabResult = resultNode.Element(XName.Get("FeCabResp", "http://ar.gov.afip.dif.FEV1/"))?.Element(XName.Get("Resultado", "http://ar.gov.afip.dif.FEV1/"))?.Value;
        
        if (cabResult == "R")
        {
             _logger.LogError($"AFIP Response (Rechazado): {responseXml}");

             // Extract Errors
             var errors = resultNode.Descendants(XName.Get("Errors", "http://ar.gov.afip.dif.FEV1/")).Descendants(XName.Get("Err", "http://ar.gov.afip.dif.FEV1/"));
             var sb = new StringBuilder();
             foreach(var err in errors)
             {
                 sb.AppendLine($"{err.Element(XName.Get("Code", "http://ar.gov.afip.dif.FEV1/"))?.Value}: {err.Element(XName.Get("Msg", "http://ar.gov.afip.dif.FEV1/"))?.Value}");
             }
             
             // Extract Observations (sometimes useful info is here too)
             var obs = resultNode.Descendants(XName.Get("Observaciones", "http://ar.gov.afip.dif.FEV1/")).Descendants(XName.Get("Obs", "http://ar.gov.afip.dif.FEV1/"));
             if (obs.Any())
             {
                 sb.AppendLine("Observaciones:");
                 foreach(var o in obs)
                 {
                      sb.AppendLine($"{o.Element(XName.Get("Code", "http://ar.gov.afip.dif.FEV1/"))?.Value}: {o.Element(XName.Get("Msg", "http://ar.gov.afip.dif.FEV1/"))?.Value}");
                 }
             }

             throw new Exception($"AFIP Rechazó el comprobante: {sb.ToString()}");
        }

        // Success (A or P)
        var detResp = resultNode.Descendants(XName.Get("FECAEDetResponse", "http://ar.gov.afip.dif.FEV1/")).First();
        var cae = detResp.Element(XName.Get("CAE", "http://ar.gov.afip.dif.FEV1/"))?.Value;
        var caeVto = detResp.Element(XName.Get("CAEFchVto", "http://ar.gov.afip.dif.FEV1/"))?.Value;

        if (string.IsNullOrEmpty(cae) || string.IsNullOrEmpty(caeVto))
        {
            throw new Exception("AFIP autorizó pero no devolvió CAE/Vencimiento");
        }

        // 7. Save Entity
        var newInvoice = new Invoice
        {
            TravelFileId = travelFileId,
            TipoComprobante = cbteTipo,
            PuntoDeVenta = settings.PuntoDeVenta,
            NumeroComprobante = cbteNro,
            CAE = cae,
            VencimientoCAE = DateTime.SpecifyKind(DateTime.ParseExact(caeVto!, "yyyyMMdd", null), DateTimeKind.Utc),
            Resultado = cabResult,
            ImporteTotal = total,
            ImporteNeto = net,
            ImporteIva = iva,
            CreatedAt = DateTime.UtcNow
        };

        _context.Invoices.Add(newInvoice);
        await _context.SaveChangesAsync();

        return newInvoice;
    }

    private async Task<int> GetNextVoucherNumber(AfipSettings settings, int cbteTipo)
    {
        var url = settings.IsProduction ? WsfeUrlProd : WsfeUrlDev;
        var action = "http://ar.gov.afip.dif.FEV1/FECompUltimoAutorizado";

        var soapEnv = $@"<?xml version=""1.0"" encoding=""utf-8""?>
<soap:Envelope xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xmlns:xsd=""http://www.w3.org/2001/XMLSchema"" xmlns:soap=""http://schemas.xmlsoap.org/soap/envelope/"">
  <soap:Body>
    <FECompUltimoAutorizado xmlns=""http://ar.gov.afip.dif.FEV1/"">
      <Auth>
        <Token>{settings.Token}</Token>
        <Sign>{settings.Sign}</Sign>
        <Cuit>{settings.Cuit}</Cuit>
      </Auth>
      <PtoVta>{settings.PuntoDeVenta}</PtoVta>
      <CbteTipo>{cbteTipo}</CbteTipo>
    </FECompUltimoAutorizado>
  </soap:Body>
</soap:Envelope>";

        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("SOAPAction", action);
        request.Content = new StringContent(soapEnv, Encoding.UTF8, "text/xml");

        var response = await _httpClient.SendAsync(request);
        var responseXml = await response.Content.ReadAsStringAsync();
        
        var doc = XDocument.Parse(responseXml);
        var resultNode = doc.Descendants(XName.Get("FECompUltimoAutorizadoResult", "http://ar.gov.afip.dif.FEV1/")).FirstOrDefault();
        
        if (resultNode == null) throw new Exception("Error al obtener último comprobante: " + responseXml);
        
        var cbteNroStr = resultNode.Element(XName.Get("CbteNro", "http://ar.gov.afip.dif.FEV1/"))?.Value;
        
        if (int.TryParse(cbteNroStr, out int cbteNro))
        {
            return cbteNro + 1;
        }
        
        return 1; 
    }

    private int GetTaxConditionId(Customer payer)
    {
        // 1 = IVA Responsable Inscripto
        // 4 = IVA Sujeto Exento
        // 5 = Consumidor Final
        // 6 = Responsable Monotributo
        // 8 = Proveedor del Exterior
        // 9 = Cliente del Exterior
        // 10 = IVA Liberado - Ley Nº 19.640
        // 11 = IVA Responsable Inscripto - Agente de Percepción
        // 13 = Monotributista Social
        // 15 = IVA No Alcanzado

        if (payer.TaxConditionId.HasValue) return payer.TaxConditionId.Value;

        // Fallback by String
        var condition = payer.TaxCondition?.ToLower() ?? "";
        
        if (condition.Contains("inscripto")) return 1;
        if (condition.Contains("exento")) return 4;
        if (condition.Contains("monotributo")) return 6;
        
        return 5; // Default: Consumidor Final
    }
}
