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
                return "Online (Token Válido)";
            }
            
            // Try to login if expired
            await EnsureAuth(settings);
            return "Online (Conectado)";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AFIP Status Error");
            return $"Error: {ex.Message}";
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

            if (!response.IsSuccessStatusCode)
                throw new Exception($"WSAA Error {response.StatusCode}: {responseXml}");

            // 5. Parse Response
            var doc = XDocument.Parse(responseXml);
            // Check for Faults explicitly
            var fault = doc.Descendants(XName.Get("Fault", "http://schemas.xmlsoap.org/soap/envelope/")).FirstOrDefault();
            if (fault != null)
            {
                var faultString = fault.Element("faultstring")?.Value;
                var faultCode = fault.Element("faultcode")?.Value;
                
                // Handle "Already Authenticated" - If we have a token, assume it's valid
                if (faultCode != null && faultCode.Contains("alreadyAuthenticated") && !string.IsNullOrEmpty(settings.Token))
                {
                    _logger.LogWarning("AFIP reported alreadyAuthenticated. Assuming current local token is valid despite expiration check.");
                    return; 
                }
                
                throw new Exception($"WSAA Fault: {faultCode} - {faultString}");
            }

            var loginCmsReturn = doc.Descendants().FirstOrDefault(x => x.Name.LocalName == "loginCmsReturn")?.Value;
            
            if (string.IsNullOrEmpty(loginCmsReturn))
                throw new Exception("WSAA Empty Response");

            var ticket = XDocument.Parse(loginCmsReturn);
            var token = ticket.Descendants("token").First().Value;
            var sign = ticket.Descendants("sign").First().Value;
            
            // Parse Expiration correctly as Argentina Time (-03:00) then convert to UTC
            var expirationStr = ticket.Descendants("expirationTime").First().Value; // yyyy-MM-ddTHH:mm:ss
            var expirationLocal = DateTime.Parse(expirationStr); // Unspecified
            // Assume it is Argentina Time (-3)
            var expirationUtc = DateTime.SpecifyKind(expirationLocal, DateTimeKind.Unspecified).AddHours(3);
            // Wait, if Parse returns Unspecified, and we know it's -3. 
            // -3 means "add 3 hours to get UTC".
            // So 17:00 ART + 3 = 20:00 UTC.
            
            // 6. Save to DB
            settings.Token = token;
            settings.Sign = sign;
            settings.TokenExpiration = expirationUtc;
            await _context.SaveChangesAsync();
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

        if (invoiceData.ImporteTotal > 0)
        {
             // return Mock invoice...
        }
        
        throw new NotImplementedException("Facturación real pendiente para Fase 2");
    }
}
