#pragma warning disable CS8601, CS8602, CS8604, CS8618
using Microsoft.Extensions.Logging;
using System.Net.Http;
using System.Globalization;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Serialization;
using Microsoft.EntityFrameworkCore;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Entities.Afip;
using System.Text.Json;
using TravelApi.Application.DTOs;
using Hangfire;


using TravelApi.Application.Interfaces;

namespace TravelApi.Infrastructure.Services;





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

    public async Task<AfipSettings?> GetSettingsAsync()
    {
        return await _context.AfipSettings.FirstOrDefaultAsync();
    }

    public async Task<AfipSettings> UpdateSettingsAsync(long cuit, int puntoDeVenta, bool isProduction, string taxCondition, byte[]? certificateData, string? certificateFileName, string? password)
    {
        var settings = await _context.AfipSettings.FirstOrDefaultAsync();
        if (settings == null)
        {
            settings = new AfipSettings();
            _context.AfipSettings.Add(settings);
        }

        settings.Cuit = cuit;
        settings.PuntoDeVenta = puntoDeVenta;
        settings.IsProduction = isProduction;
        settings.TaxCondition = taxCondition;

        if (certificateData != null)
        {
            var certPassword = !string.IsNullOrEmpty(password) ? password : settings.CertificatePassword;
            if (!await ValidateCertificate(certificateData, certPassword ?? ""))
            {
                throw new ArgumentException("El certificado es inválido o la contraseña es incorrecta. Asegurate de que sea un archivo .pfx válido.");
            }

            settings.CertificateData = certificateData;
            settings.CertificatePath = certificateFileName;
        }

        if (!string.IsNullOrEmpty(password))
        {
            settings.CertificatePassword = password;
        }

        await _context.SaveChangesAsync();
        return settings;
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
                
                if (faultCode != null && faultCode.Contains("cms"))
                {
                     throw new Exception($"Error de Certificado AFIP: El certificado subido no es válido o está corrupto. ({faultString})");
                }
                
                throw new Exception($"Error de Autenticación AFIP (WSAA): {faultString}");
            }

            if (!response.IsSuccessStatusCode)
                throw new Exception($"Error de conexión con AFIP (WSAA): {response.StatusCode}. Intentá de nuevo en unos minutos.");

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
        catch (Exception)
        {
            // If we caught an exception but checked for alreadyAuthenticated inside, rethrow unless we handled it.
            // But here we just rethrow any unforeseen errors.
            throw; 
        }
    }

    public async Task<Invoice> CreatePendingInvoice(int ReservaId, CreateInvoiceRequest request)
    {
        var settings = await _context.AfipSettings.FirstOrDefaultAsync();
        if (settings == null) throw new Exception("AFIP no configurado");

        // 1. Get Reserva & Customer
        var reserva = await _context.Reservas
            .Include(f => f.Payer)
            .FirstOrDefaultAsync(f => f.Id == ReservaId);

        if (reserva == null) throw new Exception("Reserva no encontrada");
        
        var customer = reserva.Payer;
        if (customer == null) throw new Exception("La reserva no tiene cliente asignado"); // Allow Consumer Final?

        // 2. Load Original Invoice (if Annulment/Note)
        Invoice? originalInvoice = null;
        if (request.OriginalInvoiceId.HasValue)
        {
            originalInvoice = await _context.Invoices.FindAsync(request.OriginalInvoiceId.Value);
            if (originalInvoice == null) throw new Exception("Comprobante original no encontrado");
        }

        // 3. Determine Type (Logic kept same)
        int baseType = 6; // B
        if (settings.TaxCondition == "Responsable Inscripto")
        {
            if (customer.TaxCondition != null && customer.TaxCondition.Equals("Responsable Inscripto", StringComparison.OrdinalIgnoreCase)) 
                baseType = 1; // A
            else 
                baseType = 6; // B
        }
        else // Monotributo/Exento
        {
            baseType = 11; // C
        }

        int cbteTipo = baseType;
        if (originalInvoice != null)
        {
             var t = originalInvoice.TipoComprobante;
             if (request.IsCreditNote)
             {
                 if (t == 1 || t == 2) cbteTipo = 3;
                 else if (t == 6 || t == 7) cbteTipo = 8;
                 else if (t == 11 || t == 12) cbteTipo = 13;
                 else if (t == 51 || t == 52) cbteTipo = 53;
             }
             else if (request.IsDebitNote)
             {
                 if (t == 3) cbteTipo = 2;
                 else if (t == 8) cbteTipo = 7;
                 else if (t == 13) cbteTipo = 12;
                 else if (t == 53) cbteTipo = 52;
             }
        }
        else 
        {
            if (request.IsCreditNote)
            {
                if (baseType == 1) cbteTipo = 3;
                else if (baseType == 6) cbteTipo = 8;
                else if (baseType == 11) cbteTipo = 13;
            }
            else if (request.IsDebitNote)
            {
                 if (baseType == 1) cbteTipo = 2;
                 else if (baseType == 6) cbteTipo = 7;
                 else if (baseType == 11) cbteTipo = 12;
            }
        }

        // 4. Calculate Totals
        // (Logic kept same)
        var ivaGroups = request.Items
            .GroupBy(i => i.AlicuotaIvaId)
            .Select(g => new 
            {
                Id = g.Key,
                BaseImp = g.Sum(x => x.Total), 
                Importe = g.Sum(x => x.Total * GetVatMultiplier(g.Key))
            })
            .ToList();
            
        decimal net = request.Items.Sum(i => i.Total);
        decimal iva = ivaGroups.Sum(g => g.Importe);
        decimal tributosTotal = request.Tributes.Sum(t => t.Importe);
        decimal total = net + iva + tributosTotal;

        net = Math.Round(net, 2);
        iva = Math.Round(iva, 2);
        tributosTotal = Math.Round(tributosTotal, 2);
        total = Math.Round(total, 2);

        // 5. Create PENDING Invoice
        var agencySettings = await _context.AgencySettings.FirstOrDefaultAsync();

        var invoice = new Invoice
        {
             ReservaId = ReservaId,
             OriginalInvoiceId = request.OriginalInvoiceId,
             TipoComprobante = cbteTipo,
             PuntoDeVenta = settings.PuntoDeVenta,
             NumeroComprobante = 0, // Placeholder
             CAE = null,
             VencimientoCAE = null,
             Resultado = "PENDING", // <--- NEW STATE
             ImporteTotal = total,
             ImporteNeto = net,
             ImporteIva = iva,
             CreatedAt = DateTime.UtcNow,
             AgencySnapshot = agencySettings != null ? System.Text.Json.JsonSerializer.Serialize(agencySettings) : null,
             CustomerSnapshot = System.Text.Json.JsonSerializer.Serialize(customer, new JsonSerializerOptions { ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles }),
             Items = request.Items.Select(i => new InvoiceItem 
             {
                 Description = i.Description,
                 Quantity = i.Quantity,
                 UnitPrice = i.UnitPrice,
                 Total = i.Total,
                 AlicuotaIvaId = i.AlicuotaIvaId,
                 ImporteIva = i.Total * GetVatMultiplier(i.AlicuotaIvaId)
             }).ToList(),
             Tributes = request.Tributes.Select(t => new InvoiceTribute
             {
                 TributeId = t.TributeId,
                 Description = t.Description,
                 BaseImponible = t.BaseImponible,
                 Alicuota = t.Alicuota,
                 Importe = t.Importe
             }).ToList()
        };

        _context.Invoices.Add(invoice);
        await _context.SaveChangesAsync();
        
        return invoice;
    }

    // Helper functions need to be static or duplicated if used in ProcessInvoiceJob? 
    // No, ProcessInvoiceJob is in same class.
    private decimal GetVatMultiplier(int id) => id switch 
    {
        3 => 0m,     // 0%
        4 => 0.105m, // 10.5%
        5 => 0.21m,  // 21%
        6 => 0.27m,  // 27%
        8 => 0.05m,  // 5%
        9 => 0.025m, // 2.5%
        _ => 0m
    };

    [AutomaticRetry(Attempts = 0)] // Don't auto-retry AFIP calls blindly
    public async Task ProcessInvoiceJob(int invoiceId)
    {
        var invoice = await _context.Invoices
            .Include(i => i.Items)
            .Include(i => i.Tributes)
            .Include(i => i.OriginalInvoice)
            .FirstOrDefaultAsync(i => i.Id == invoiceId);

        if (invoice == null) throw new Exception("Invoice not found");
        if (invoice.Resultado == "A") return; // Already approved

        try 
        {
            var settings = await _context.AfipSettings.FirstOrDefaultAsync();
            if (settings == null) throw new Exception("AFIP Not Configured");

            await EnsureAuth(settings);

            // Re-construct data needed for AFIP from the Invoice entity
            // 1. Next Number
            int cbteNro = await GetNextVoucherNumber(settings, invoice.TipoComprobante);
            
            // 2. Doc Details from Snapshot or Relation? 
            // Better to parse from Snapshot to ensure immutability, but for now use relation or fallback
            // Parsing CustomerSnapshot is safer.
            long docNro = 0;
            int docTipo = 99;
            
            // Try parse customer snapshot
            if (!string.IsNullOrEmpty(invoice.CustomerSnapshot))
            {
                 var cust = System.Text.Json.JsonSerializer.Deserialize<Customer>(invoice.CustomerSnapshot);
                 if (cust != null)
                 {
                     if (!string.IsNullOrEmpty(cust.TaxId))
                     {
                        var clean = cust.TaxId.Replace("-", "").Replace(".", "").Trim();
                        if (long.TryParse(clean, out long val)) { docTipo = 80; docNro = val; }
                     }
                     else if (!string.IsNullOrEmpty(cust.DocumentNumber))
                     {
                        if (long.TryParse(cust.DocumentNumber, out long val)) { docTipo = 96; docNro = val; }
                     }
                 }
            }

            // 3. Re-Calculate IVA Groups (AFIP Needs breakdown)
            bool isFacturaC = invoice.TipoComprobante == 11 || invoice.TipoComprobante == 12 || invoice.TipoComprobante == 13;

            var ivaGroups = invoice.Items
                .GroupBy(i => i.AlicuotaIvaId)
                .Select(g => new 
                {
                    Id = g.Key,
                    BaseImp = g.Sum(x => x.Total), 
                    Importe = g.Sum(x => x.ImporteIva)
                })
                .ToList();

            // 4. Construct XML (Reuse logic)
            var url = settings.IsProduction ? WsfeUrlProd : WsfeUrlDev;
            var action = "http://ar.gov.afip.dif.FEV1/FECAESolicitar";

             // IVA Block
            var sbIva = new StringBuilder();
            if (!isFacturaC && ivaGroups.Any() && (invoice.TipoComprobante == 1 || invoice.TipoComprobante == 6 || invoice.TipoComprobante == 2 || invoice.TipoComprobante == 3 || invoice.TipoComprobante == 7 || invoice.TipoComprobante == 8)) 
            {
                sbIva.Append("<Iva>");
                foreach(var g in ivaGroups)
                {
                    sbIva.Append($@"<AlicIva>
                        <Id>{g.Id}</Id>
                        <BaseImp>{g.BaseImp.ToString("0.00", CultureInfo.InvariantCulture)}</BaseImp>
                        <Importe>{g.Importe.ToString("0.00", CultureInfo.InvariantCulture)}</Importe>
                    </AlicIva>");
                }
                sbIva.Append("</Iva>");
            }

            // Tributes Block
            var sbTrib = new StringBuilder();
            if (invoice.Tributes.Any())
            {
                sbTrib.Append("<Tributos>");
                foreach(var t in invoice.Tributes)
                {
                    sbTrib.Append($@"<Tributo>
                        <Id>{t.TributeId}</Id>
                        <Desc>{t.Description}</Desc>
                        <BaseImp>{t.BaseImponible.ToString("0.00", CultureInfo.InvariantCulture)}</BaseImp>
                        <Alic>{t.Alicuota.ToString("0.00", CultureInfo.InvariantCulture)}</Alic>
                        <Importe>{t.Importe.ToString("0.00", CultureInfo.InvariantCulture)}</Importe>
                    </Tributo>");
                }
                sbTrib.Append("</Tributos>");
            }

            // Associated
            var sbCbtesAsoc = new StringBuilder();
            if (invoice.OriginalInvoiceId.HasValue && invoice.OriginalInvoice != null)
            {
                sbCbtesAsoc.Append($@"<CbtesAsoc>
                    <CbteAsoc>
                        <Tipo>{invoice.OriginalInvoice.TipoComprobante}</Tipo>
                        <PtoVta>{invoice.OriginalInvoice.PuntoDeVenta}</PtoVta>
                        <Nro>{invoice.OriginalInvoice.NumeroComprobante}</Nro>
                        <Cuit>{settings.Cuit}</Cuit>  
                    </CbteAsoc>
                </CbtesAsoc>");
            }
            
            var today = DateTime.Now.ToString("yyyyMMdd");

            // For Factura C, ImpNeto MUST be exactly equal to the subtotal before taxes. 
            // And ImpIVA MUST be 0.
            decimal impNeto = isFacturaC ? invoice.ImporteTotal - invoice.Tributes.Sum(t => t.Importe) : invoice.ImporteNeto;
            decimal impIva = isFacturaC ? 0 : invoice.ImporteIva;

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
                <CbteTipo>{invoice.TipoComprobante}</CbteTipo>
            </FeCabReq>
            <FeDetReq>
                <FECAEDetRequest>
                    <Concepto>2</Concepto>
                    <DocTipo>{docTipo}</DocTipo>
                    <DocNro>{docNro}</DocNro>
                    <CbteDesde>{cbteNro}</CbteDesde>
                    <CbteHasta>{cbteNro}</CbteHasta>
                    <CbteFch>{today}</CbteFch>
                    <ImpTotal>{invoice.ImporteTotal.ToString("0.00", CultureInfo.InvariantCulture)}</ImpTotal>
                    <ImpTotConc>0</ImpTotConc>
                    <ImpNeto>{impNeto.ToString("0.00", CultureInfo.InvariantCulture)}</ImpNeto>
                    <ImpOpEx>0</ImpOpEx>
                    <ImpTrib>{invoice.Tributes.Sum(t=>t.Importe).ToString("0.00", CultureInfo.InvariantCulture)}</ImpTrib>
                    <ImpIVA>{impIva.ToString("0.00", CultureInfo.InvariantCulture)}</ImpIVA>
                    <FchServDesde>{today}</FchServDesde>
                    <FchServHasta>{today}</FchServHasta>
                    <FchVtoPago>{DateTime.Now.AddDays(10).ToString("yyyyMMdd")}</FchVtoPago>
                    <MonId>PES</MonId>
                    <MonCotiz>1</MonCotiz>
                    <CondicionIVAReceptorId>{GetConditionIvaId(null, docTipo)}</CondicionIVAReceptorId>
                    {sbCbtesAsoc}
                    {sbTrib}
                    {sbIva}
                </FECAEDetRequest>
            </FeDetReq>
          </FeCAEReq>
        </FECAESolicitar>
      </soap:Body>
    </soap:Envelope>";

            var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
            httpRequest.Headers.Add("SOAPAction", action);
            httpRequest.Content = new StringContent(soapEnv, Encoding.UTF8, "text/xml");

            var response = await _httpClient.SendAsync(httpRequest);
            var responseXml = await response.Content.ReadAsStringAsync();

             // Parse Response
            var doc = XDocument.Parse(responseXml);
            var resultNode = doc.Descendants(XName.Get("FECAESolicitarResult", "http://ar.gov.afip.dif.FEV1/")).FirstOrDefault();
            
            if (resultNode == null) 
            {
                invoice.Resultado = "R";
                invoice.Observaciones = "AFIP respondió con un error de red o XML inválido.";
                await _context.SaveChangesAsync();
                return;
            }

            var cabResult = resultNode.Element(XName.Get("FeCabResp", "http://ar.gov.afip.dif.FEV1/"))?.Element(XName.Get("Resultado", "http://ar.gov.afip.dif.FEV1/"))?.Value;

            if (cabResult == "R")
            {
                 var sbErr = new StringBuilder();
                 // Capture Errors and Observations
                 var errors = resultNode.Descendants(XName.Get("Err", "http://ar.gov.afip.dif.FEV1/"));
                 foreach(var e in errors) 
                 {
                     var code = e.Element(XName.Get("Code", "http://ar.gov.afip.dif.FEV1/"))?.Value;
                     var msg = e.Element(XName.Get("Msg", "http://ar.gov.afip.dif.FEV1/"))?.Value;
                     sbErr.AppendLine(TranslateAfipError(code, msg));
                 }
                 
                 var obs = resultNode.Descendants(XName.Get("Obs", "http://ar.gov.afip.dif.FEV1/"));
                 foreach(var o in obs) 
                 {
                     var code = o.Element(XName.Get("Code", "http://ar.gov.afip.dif.FEV1/"))?.Value;
                     var msg = o.Element(XName.Get("Msg", "http://ar.gov.afip.dif.FEV1/"))?.Value;
                     sbErr.AppendLine(TranslateAfipError(code, msg));
                 }

                 invoice.Resultado = "R";
                 invoice.Observaciones = sbErr.ToString().Trim();
                 await _context.SaveChangesAsync();
                 return;
            }

            // Success
            var detResp = resultNode.Descendants(XName.Get("FECAEDetResponse", "http://ar.gov.afip.dif.FEV1/")).First();
            var cae = detResp.Element(XName.Get("CAE", "http://ar.gov.afip.dif.FEV1/"))?.Value;
            var caeVto = detResp.Element(XName.Get("CAEFchVto", "http://ar.gov.afip.dif.FEV1/"))?.Value;

            invoice.Resultado = "A";
            invoice.CAE = cae;
            invoice.VencimientoCAE = DateTime.ParseExact(caeVto!, "yyyyMMdd", null).ToUniversalTime();
            invoice.NumeroComprobante = cbteNro; // Assign actual number used
            invoice.Observaciones = null;
            
            await _context.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            invoice.Resultado = "R";
            invoice.Observaciones = $"Error técnico: {ex.Message}";
            await _context.SaveChangesAsync();
            throw; 
        }
    }

    private string TranslateAfipError(string? code, string? rawMsg)
    {
        if (string.IsNullOrEmpty(code)) return rawMsg ?? "Error desconocido";

        return code switch
        {
            "10047" => "Validación de IVA: En facturas tipo C el IVA siempre debe ser cero (0).",
            "10048" => "Desequilibrio numérico: El total no coincide con la suma del neto y tributos. Revisá los importes.",
            "10015" => "Punto de Venta inválido: El punto de venta no está habilitado para factura electrónica en AFIP.",
            "10016" => "Tipo de Comprobante inválido: El tipo de factura no coincide con tu categoría ante AFIP.",
            "501" or "502" => "Certificado expirado o inválido: Es necesario renovar el certificado digital en el panel de configuración.",
            "1000\"" or "1001" => "CUIT emisor no autorizado: Tu CUIT no tiene permisos para emitir este tipo de comprobante. Revisá el 'Punto de Venta' y 'Condición frente al IVA' en la configuración.",
            "10074" => "CUIT del receptor inválido: El CUIT o DNI del cliente no es válido o no existe en los registros de AFIP.",
            _ => rawMsg ?? $"Error AFIP [{code}]"
        };
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


    public async Task<AfipVoucherDetails?> GetVoucherDetails(int cbteTipo, int ptoVta, long cbteNro)
    {
        var settings = await _context.AfipSettings.FirstOrDefaultAsync();
        if (settings == null) return null;

        await EnsureAuth(settings);

        var url = settings.IsProduction ? WsfeUrlProd : WsfeUrlDev;
        var action = "http://ar.gov.afip.dif.FEV1/FECompConsultar";

        var soapEnv = $@"<?xml version=""1.0"" encoding=""utf-8""?>
<soap:Envelope xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xmlns:xsd=""http://www.w3.org/2001/XMLSchema"" xmlns:soap=""http://schemas.xmlsoap.org/soap/envelope/"">
  <soap:Body>
    <FECompConsultar xmlns=""http://ar.gov.afip.dif.FEV1/"">
      <Auth>
        <Token>{settings.Token}</Token>
        <Sign>{settings.Sign}</Sign>
        <Cuit>{settings.Cuit}</Cuit>
      </Auth>
      <FeCompConsReq>
        <CbteTipo>{cbteTipo}</CbteTipo>
        <CbteNro>{cbteNro}</CbteNro>
        <PtoVta>{ptoVta}</PtoVta>
      </FeCompConsReq>
    </FECompConsultar>
  </soap:Body>
</soap:Envelope>";

        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("SOAPAction", action);
        request.Content = new StringContent(soapEnv, Encoding.UTF8, "text/xml");

        var response = await _httpClient.SendAsync(request);
        var responseXml = await response.Content.ReadAsStringAsync();

        var doc = XDocument.Parse(responseXml);
        var result = doc.Descendants(XName.Get("ResultGet", "http://ar.gov.afip.dif.FEV1/")).FirstOrDefault();

        if (result == null) return null;

        var details = new AfipVoucherDetails
        {
            ImporteTotal = ParseDecimal(result.Element(XName.Get("ImpTotal", "http://ar.gov.afip.dif.FEV1/"))?.Value),
            ImporteNeto = ParseDecimal(result.Element(XName.Get("ImpNeto", "http://ar.gov.afip.dif.FEV1/"))?.Value),
            ImporteIva = ParseDecimal(result.Element(XName.Get("ImpIVA", "http://ar.gov.afip.dif.FEV1/"))?.Value),
            ImporteTrib = ParseDecimal(result.Element(XName.Get("ImpTrib", "http://ar.gov.afip.dif.FEV1/"))?.Value)
        };

        var ivaArray = result.Descendants(XName.Get("AlicIva", "http://ar.gov.afip.dif.FEV1/"));
        foreach (var item in ivaArray)
        {
            details.VatDetails.Add(new VatDetail
            {
                Id = int.Parse(item.Element(XName.Get("Id", "http://ar.gov.afip.dif.FEV1/"))?.Value ?? "0"),
                BaseImp = ParseDecimal(item.Element(XName.Get("BaseImp", "http://ar.gov.afip.dif.FEV1/"))?.Value),
                Importe = ParseDecimal(item.Element(XName.Get("Importe", "http://ar.gov.afip.dif.FEV1/"))?.Value)
            });
        }

        var tribArray = result.Descendants(XName.Get("Tributo", "http://ar.gov.afip.dif.FEV1/"));
        foreach (var item in tribArray)
        {
            details.TributeDetails.Add(new TributeDetail
            {
                Id = int.Parse(item.Element(XName.Get("Id", "http://ar.gov.afip.dif.FEV1/"))?.Value ?? "0"),
                Desc = item.Element(XName.Get("Desc", "http://ar.gov.afip.dif.FEV1/"))?.Value ?? "",
                BaseImp = ParseDecimal(item.Element(XName.Get("BaseImp", "http://ar.gov.afip.dif.FEV1/"))?.Value),
                Alic = ParseDecimal(item.Element(XName.Get("Alic", "http://ar.gov.afip.dif.FEV1/"))?.Value),
                Importe = ParseDecimal(item.Element(XName.Get("Importe", "http://ar.gov.afip.dif.FEV1/"))?.Value)
            });
        }

        return details;
    }



    private int GetConditionIvaId(string? taxCondition, int docTipo)
    {
        // 1: IVA Responsable Inscripto
        // 4: IVA Sujeto Exento
        // 5: Consumidor Final
        // 6: Responsable Monotributo
        // 8: Proveedor del Exterior
        // 9: Cliente del Exterior
        // 10: IVA Liberado - Ley Nº 19.640
        // 11: IVA Responsable Inscripto - Agente de Percepción
        // 13: Monotributista Social
        // 15: IVA No Alcanzado

        if (docTipo == 99) return 5; // Final Consumer
        if (docTipo == 80) // CUIT
        {
            if (string.IsNullOrEmpty(taxCondition)) return 5; // Default

            var tc = taxCondition.ToLower();
            if (tc.Contains("inscripto") && !tc.Contains("monotributo")) return 1;
            if (tc.Contains("monotributo")) return 6;
            if (tc.Contains("exento")) return 4;
            if (tc.Contains("consumidor")) return 5;
        }
        
        return 5; // Default to Final Consumer
    }

    private decimal ParseDecimal(string? val)
    {
        if (string.IsNullOrEmpty(val)) return 0;
        if (decimal.TryParse(val, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal d)) return d;
        return 0;
    }
}
