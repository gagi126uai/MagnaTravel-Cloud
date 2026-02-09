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

    public async Task<bool> ValidateCertificate(byte[] certData, string password)
    {
        try
        {
            if (certData == null || certData.Length == 0) return false;
            // Validate we can open it
            var cert = new X509Certificate2(certData, password, X509KeyStorageFlags.Exportable);
            return cert.HasPrivateKey;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating certificate");
            return false;
        }
    }

    public async Task<string> GetStatus()
    {
        try
        {
            var settings = await _context.AfipSettings.FirstOrDefaultAsync();
            if (settings == null) return "No Configurado";
            if (settings.CertificateData == null || settings.CertificateData.Length == 0) return "Certificado Faltante";

            // Try to check token validity
            if (settings.TokenExpiration > DateTime.Now)
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
        if (!string.IsNullOrEmpty(settings.Token) && settings.TokenExpiration > DateTime.Now.AddMinutes(10))
        {
            return; // Valid token
        }

        if (settings.CertificateData == null) throw new Exception("Certificado no configurado");

        // 1. Load Certificate
        var cert = new X509Certificate2(settings.CertificateData, settings.CertificatePassword, X509KeyStorageFlags.Exportable);

        // 2. Create Login Ticket
        var xml = new XElement("loginTicketRequest",
            new XAttribute("version", "1.0"),
            new XElement("header",
                new XElement("uniqueId", DateTime.Now.Ticks),
                new XElement("generationTime", DateTime.Now.AddMinutes(-10).ToString("yyyy-MM-ddTHH:mm:ss")),
                new XElement("expirationTime", DateTime.Now.AddMinutes(+10).ToString("yyyy-MM-ddTHH:mm:ss"))
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
        // Extract loginTicketResponse from SOAP Body
        // It returns <loginCmsReturn> escaped XML </loginCmsReturn>
        var loginCmsReturn = doc.Descendants().FirstOrDefault(x => x.Name.LocalName == "loginCmsReturn")?.Value;
        
        if (string.IsNullOrEmpty(loginCmsReturn))
             throw new Exception("WSAA Empty Response");

        var ticket = XDocument.Parse(loginCmsReturn);
        var token = ticket.Descendants("token").First().Value;
        var sign = ticket.Descendants("sign").First().Value;
        var expiration = DateTime.Parse(ticket.Descendants("expirationTime").First().Value);

        // 6. Save to DB
        settings.Token = token;
        settings.Sign = sign;
        settings.TokenExpiration = expiration;
        await _context.SaveChangesAsync();
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
