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
using TravelApi.Domain.Helpers;
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
    private readonly ISensitiveDataProtector _sensitiveDataProtector;
    // B1.15 (2026-05-11): IAuditService opcional para audit trail diferenciado del
    // cascade NC -> Receipt Voided. Opcional para no romper tests existentes y
    // ctors legacy (mismo patron que PaymentService.VoidReceiptAsync).
    private readonly IAuditService? _auditService;

    // URLs (TODO: move to config)
    private const string WsaaUrlDev = "https://wsaahomo.afip.gov.ar/ws/services/LoginCms";
    private const string WsaaUrlProd = "https://wsaa.afip.gov.ar/ws/services/LoginCms";
    private const string WsfeUrlDev = "https://wswhomo.afip.gov.ar/wsfev1/service.asmx";
    private const string WsfeUrlProd = "https://servicios1.afip.gov.ar/wsfev1/service.asmx";
    private const string WsPadronUrlDev = "https://awshomo.afip.gov.ar/sr-padron/webservices/personaServiceA5";
    private const string WsPadronUrlProd = "https://aws.afip.gov.ar/sr-padron/webservices/personaServiceA5";

    public AfipService(
        AppDbContext context,
        ILogger<AfipService> logger,
        HttpClient httpClient,
        ISensitiveDataProtector sensitiveDataProtector,
        IAuditService? auditService = null)
    {
        _context = context;
        _logger = logger;
        _httpClient = httpClient;
        _sensitiveDataProtector = sensitiveDataProtector;
        _auditService = auditService;
    }

    private byte[]? GetCertificateData(AfipSettings settings) => 
        settings.IsProduction 
            ? _sensitiveDataProtector.UnprotectBytes(settings.ProdCertificateData)
            : _sensitiveDataProtector.UnprotectBytes(settings.CertificateData);
    
    private string? GetCertificatePassword(AfipSettings settings) => 
        settings.IsProduction 
            ? _sensitiveDataProtector.UnprotectString(settings.ProdCertificatePassword)
            : _sensitiveDataProtector.UnprotectString(settings.CertificatePassword);
    
    private string? GetAuthToken(AfipSettings settings) => 
        settings.IsProduction 
            ? _sensitiveDataProtector.UnprotectString(settings.ProdToken)
            : _sensitiveDataProtector.UnprotectString(settings.Token);
    
    private string? GetAuthSign(AfipSettings settings) => 
        settings.IsProduction 
            ? _sensitiveDataProtector.UnprotectString(settings.ProdSign)
            : _sensitiveDataProtector.UnprotectString(settings.Sign);
    
    private string? GetPadronToken(AfipSettings settings) => 
        settings.IsProduction 
            ? _sensitiveDataProtector.UnprotectString(settings.ProdPadronToken)
            : _sensitiveDataProtector.UnprotectString(settings.PadronToken);
    
    private string? GetPadronSign(AfipSettings settings) => 
        settings.IsProduction 
            ? _sensitiveDataProtector.UnprotectString(settings.ProdPadronSign)
            : _sensitiveDataProtector.UnprotectString(settings.PadronSign);

    private AfipSettings MapDecryptedSettings(AfipSettings settings)
    {
        return new AfipSettings
        {
            Id = settings.Id,
            Cuit = settings.Cuit,
            PuntoDeVenta = settings.PuntoDeVenta,
            IsProduction = settings.IsProduction,
            CertificatePath = settings.CertificatePath,
            CertificateData = _sensitiveDataProtector.UnprotectBytes(settings.CertificateData),
            CertificatePassword = _sensitiveDataProtector.UnprotectString(settings.CertificatePassword),
            Token = _sensitiveDataProtector.UnprotectString(settings.Token),
            Sign = _sensitiveDataProtector.UnprotectString(settings.Sign),
            TokenExpiration = settings.TokenExpiration,
            PadronToken = _sensitiveDataProtector.UnprotectString(settings.PadronToken),
            PadronSign = _sensitiveDataProtector.UnprotectString(settings.PadronSign),
            PadronTokenExpiration = settings.PadronTokenExpiration,
            
            ProdCertificatePath = settings.ProdCertificatePath,
            ProdCertificateData = _sensitiveDataProtector.UnprotectBytes(settings.ProdCertificateData),
            ProdCertificatePassword = _sensitiveDataProtector.UnprotectString(settings.ProdCertificatePassword),
            ProdToken = _sensitiveDataProtector.UnprotectString(settings.ProdToken),
            ProdSign = _sensitiveDataProtector.UnprotectString(settings.ProdSign),
            ProdTokenExpiration = settings.ProdTokenExpiration,
            ProdPadronToken = _sensitiveDataProtector.UnprotectString(settings.ProdPadronToken),
            ProdPadronSign = _sensitiveDataProtector.UnprotectString(settings.ProdPadronSign),
            ProdPadronTokenExpiration = settings.ProdPadronTokenExpiration,

            TaxCondition = settings.TaxCondition
        };
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

            byte[]? certificateData;
            try
            {
                certificateData = GetCertificateData(settings);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "AFIP status unavailable because Security__EncryptionKey is not configured.");
                return "Falta clave Security__EncryptionKey";
            }

            if (certificateData == null || certificateData.Length == 0) return "Certificado Faltante";

            // Token Validity Check (Handle potential Timezone mismatch from previous saves)
            // Existing data might be stored as Argentina Time (e.g. 18:00) but without offset.
            // System is UTC (e.g. 21:00).
            // Robust check: If stored time is "close" to now, check if adding 4 hours makes it valid.
            // Or better: ensure we store UTC going forward.
            
            bool isValid = false;
            
            string? authToken;
            try
            {
                authToken = GetAuthToken(settings);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "AFIP token could not be read because Security__EncryptionKey is not configured.");
                return "Falta clave Security__EncryptionKey";
            }

            if (!string.IsNullOrEmpty(authToken))
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
            return "Error de conexion o autenticacion";
        }
    }

    public async Task<AfipSettings?> GetSettingsAsync()
    {
        var settings = await _context.AfipSettings.AsNoTracking().FirstOrDefaultAsync();
        if (settings is null)
        {
            return null;
        }

        try
        {
            return MapDecryptedSettings(settings);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "AFIP settings were loaded without decrypting sensitive fields because Security__EncryptionKey is missing.");
            return settings;
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            _logger.LogError(ex, 
                "AFIP encryption key mismatch detected. Auto-repairing: clearing corrupted encrypted fields from the database. " +
                "The admin must re-upload the AFIP certificate and password from the Settings page.");

            // Auto-repair: persist the cleanup to the database so it doesn't fail on every request
            try
            {
                var tracked = await _context.AfipSettings.FirstOrDefaultAsync();
                if (tracked != null)
                {
                    tracked.Token = null;
                    tracked.Sign = null;
                    tracked.TokenExpiration = null;
                    tracked.CertificateData = null;
                    tracked.CertificatePassword = null;
                    tracked.PadronToken = null;
                    tracked.PadronSign = null;
                    tracked.PadronTokenExpiration = null;

                    tracked.ProdToken = null;
                    tracked.ProdSign = null;
                    tracked.ProdTokenExpiration = null;
                    tracked.ProdCertificateData = null;
                    tracked.ProdCertificatePassword = null;
                    tracked.ProdPadronToken = null;
                    tracked.ProdPadronSign = null;
                    tracked.ProdPadronTokenExpiration = null;

                    await _context.SaveChangesAsync();
                    _logger.LogWarning("AFIP auto-repair complete. Corrupted encrypted fields have been cleared from the database.");
                }
            }
            catch (Exception repairEx)
            {
                _logger.LogError(repairEx, "AFIP auto-repair failed to persist cleanup to database.");
            }

            // Return clean settings so the UI shows "no certificate" instead of crashing
            settings.Token = null;
            settings.Sign = null;
            settings.CertificateData = null;
            settings.CertificatePassword = null;
            settings.PadronToken = null;
            settings.PadronSign = null;
            settings.ProdToken = null;
            settings.ProdSign = null;
            settings.ProdCertificateData = null;
            settings.ProdCertificatePassword = null;
            settings.ProdPadronToken = null;
            settings.ProdPadronSign = null;

            return settings;
        }
    }

    public async Task<AfipSettings> UpdateSettingsAsync(long cuit, int puntoDeVenta, bool isProduction, string taxCondition, 
        byte[]? certificateData, string? certificateFileName, string? password,
        byte[]? prodCertificateData, string? prodCertificateFileName, string? prodPassword)
    {
        var settings = await _context.AfipSettings.FirstOrDefaultAsync();
        if (settings == null)
        {
            settings = new AfipSettings();
            _context.AfipSettings.Add(settings);
        }

        // Si cambia el entorno, no necesitamos borrar todo porque ahora cada entorno tiene sus propios campos de token.
        // Pero si el usuario cambia el CUIT, sí deberíamos invalidar ambos.
        if (settings.Cuit != cuit)
        {
            settings.Token = null; settings.Sign = null; settings.TokenExpiration = null;
            settings.ProdToken = null; settings.ProdSign = null; settings.ProdTokenExpiration = null;
            settings.PadronToken = null; settings.PadronSign = null; settings.PadronTokenExpiration = null;
            settings.ProdPadronToken = null; settings.ProdPadronSign = null; settings.ProdPadronTokenExpiration = null;
        }

        settings.Cuit = cuit;
        settings.PuntoDeVenta = puntoDeVenta;
        settings.IsProduction = isProduction;
        settings.TaxCondition = taxCondition;

        // Process DEV Certificate
        if (certificateData != null)
        {
            var certPassword = !string.IsNullOrEmpty(password) ? password : _sensitiveDataProtector.UnprotectString(settings.CertificatePassword);
            if (!await ValidateCertificate(certificateData, certPassword ?? ""))
            {
                throw new ArgumentException("El certificado de Homologación es inválido o la contraseña es incorrecta.");
            }

            settings.CertificateData = _sensitiveDataProtector.ProtectBytes(certificateData);
            settings.CertificatePath = certificateFileName;
        }

        if (!string.IsNullOrEmpty(password))
        {
            settings.CertificatePassword = _sensitiveDataProtector.ProtectString(password);
        }

        // Process PROD Certificate
        if (prodCertificateData != null)
        {
            var certPassword = !string.IsNullOrEmpty(prodPassword) ? prodPassword : _sensitiveDataProtector.UnprotectString(settings.ProdCertificatePassword);
            if (!await ValidateCertificate(prodCertificateData, certPassword ?? ""))
            {
                throw new ArgumentException("El certificado de Producción es inválido o la contraseña es incorrecta.");
            }

            settings.ProdCertificateData = _sensitiveDataProtector.ProtectBytes(prodCertificateData);
            settings.ProdCertificatePath = prodCertificateFileName;
        }

        if (!string.IsNullOrEmpty(prodPassword))
        {
            settings.ProdCertificatePassword = _sensitiveDataProtector.ProtectString(prodPassword);
        }

        await _context.SaveChangesAsync();
        return MapDecryptedSettings(settings);
    }

    private async Task<string> CheckWsfeStatus(AfipSettings settings)
    {
        try
        {
             // Call FECompUltimoAutorizado to check if we can reach the business service
             var url = settings.IsProduction ? WsfeUrlProd : WsfeUrlDev;
             var action = "http://ar.gov.afip.dif.FEV1/FECompUltimoAutorizado";
             
             // Tipo de comprobante para consultar el ULTIMO numero autorizado, segun la condicion
             // fiscal del EMISOR (la agencia):
             //   - Monotributo / Exento -> Factura C (11)
             //   - cualquier otro (Responsable Inscripto, default) -> Factura A (1)
             //
             // Normalizamos con TaxConditionNormalizer (igual que InvoiceTypeResolver en el path de
             // emision) para que la SECUENCIA y la EMISION usen exactamente la misma logica: asi una
             // variante de texto ("MONOTRIBUTISTA", "IVA_EXENTO", con tildes) no consulta la serie
             // equivocada. Para los valores actuales del dropdown ("Monotributo"/"Exento") el
             // resultado es identico al literal previo.
             var emisorCondition = TaxConditionNormalizer.Normalize(settings.TaxCondition);
             int cbteTipo = 1; // Default Factura A
             if (emisorCondition == TaxConditionCanonical.Monotributista ||
                 emisorCondition == TaxConditionCanonical.Exento)
             {
                 cbteTipo = 11; // Factura C
             }

             var soapEnv = $@"<?xml version=""1.0"" encoding=""utf-8""?>
<soap:Envelope xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xmlns:xsd=""http://www.w3.org/2001/XMLSchema"" xmlns:soap=""http://schemas.xmlsoap.org/soap/envelope/"">
  <soap:Body>
    <FECompUltimoAutorizado xmlns=""http://ar.gov.afip.dif.FEV1/"">
      <Auth>
        <Token>{GetAuthToken(settings)}</Token>
        <Sign>{GetAuthSign(settings)}</Sign>
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
        var certificateData = GetCertificateData(settings);
        var certificatePassword = GetCertificatePassword(settings);
        if (certificateData == null) throw new Exception("Certificado no configurado");

        try 
        {
            // 1. Load Certificate
            var cert = new X509Certificate2(certificateData, certificatePassword, X509KeyStorageFlags.Exportable);

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
                     if (!string.IsNullOrEmpty(GetAuthToken(settings))) 
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
            // 6. Save to DB
            if (settings.IsProduction)
            {
                settings.ProdToken = _sensitiveDataProtector.ProtectString(token);
                settings.ProdSign = _sensitiveDataProtector.ProtectString(sign);
                settings.ProdTokenExpiration = expirationUtc;
            }
            else
            {
                settings.Token = _sensitiveDataProtector.ProtectString(token);
                settings.Sign = _sensitiveDataProtector.ProtectString(sign);
                settings.TokenExpiration = expirationUtc;
            }
            
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
        if (!string.IsNullOrWhiteSpace(request.OriginalInvoiceId))
        {
            var originalInvoiceId = await _context.Invoices
                .AsNoTracking()
                .ResolveInternalIdAsync(request.OriginalInvoiceId);

            if (!originalInvoiceId.HasValue)
                throw new Exception("Comprobante original no encontrado");

            originalInvoice = await _context.Invoices.FindAsync(originalInvoiceId.Value);
            if (originalInvoice == null) throw new Exception("Comprobante original no encontrado");
        }

        // 3. Determine Type (matriz emisor x receptor).
        //
        // ADR (fix fiscal RI->Monotributo, 2026-06-13): la decision A/B/C ahora vive en
        // InvoiceTypeResolver (Domain, testeable como unidad pura) y NO se compara mas por
        // string literal. Cambios respecto del codigo previo:
        //   - emisor RI a receptor Monotributo -> AHORA Factura A (1). Antes daba B (6). Era un
        //     BUG fiscal: ARCA (RG 5003/2021, Ley 27.618) exige A para esa combinacion.
        //   - las dos condiciones (agencia y cliente) pasan por TaxConditionNormalizer, asi
        //     variantes de texto ("Monotributista", "MONOTRIBUTO", con tildes) no degradan la
        //     letra en silencio.
        // Los casos ya correctos se preservan EXACTO: RI->RI=A, RI->Consumidor Final=B,
        // Monotributo=C, Exento=C.
        int baseType = InvoiceTypeResolver.ResolveSaleInvoiceType(
            emisorTaxCondition: settings.TaxCondition,
            receptorTaxCondition: customer.TaxCondition);

        // Leyenda obligatoria Ley 27.618 cuando RI factura a Monotributista (Factura A). Va SOLO
        // en ese caso. Se persiste en Invoice.FiscalLegend para que el PDF la imprima
        // (InvoicePdfService.ComposeFiscalLegend). NO va en el envelope WSFEv1: la leyenda es un
        // requisito del comprobante IMPRESO, no un dato que ARCA reciba (no existe ese nodo en el XSD).
        string? fiscalLegend = null;
        if (InvoiceTypeResolver.RequiresMonotributistaLegend(settings.TaxCondition, customer.TaxCondition))
        {
            fiscalLegend = InvoiceTypeResolver.LeyendaFacturaAMonotributista;
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
                 // ADR-013 §3.9 (M1, 2026-06-01) — FIX de bug fiscal bloqueante.
                 //
                 // ANTES este bloque solo contemplaba como origen los tipos de NOTA DE
                 // CREDITO (3/8/13/53). Una ND asociada a una FACTURA C=11 (el caso del
                 // MVP de ADR-013: penalidad de cancelacion -> ND C asociada a la factura
                 // original C) NO matcheaba ninguna rama, y cbteTipo quedaba en baseType.
                 // Para Monotributo baseType=11 (factura C), asi que la ND salia con
                 // CbteTipo=11 (FACTURA C) en vez de 12 (ND C): comprobante equivocado ->
                 // rechazo de ARCA o, peor, una "factura" emitida en lugar de una ND.
                 //
                 // El helper deriva la LETRA de la ND del tipo del comprobante ASOCIADO
                 // (factura O nota de credito), NO de la condicion fiscal del emisor. Esto
                 // ademas es lo correcto fiscalmente (RG 4540: misma letra que el
                 // comprobante asociado), y evita desincronizaciones cuando el emisor paso
                 // de Mono a RI pero la factura asociada sigue siendo C.
                 //
                 // Si el helper no reconoce el tipo asociado (devuelve null), dejamos
                 // cbteTipo en baseType (comportamiento previo para tipos raros) en vez de
                 // inventar uno.
                 var debitNoteTipo = InvoiceComprobanteHelpers.GetDebitNoteTypeForAssociated(t);
                 if (debitNoteTipo.HasValue) cbteTipo = debitNoteTipo.Value;
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
        //
        // FC1.3.F2.2 (fix fiscal B1, 2026-05-27): dos caminos.
        //
        //   - request.TotalsOverride == null  -> FACTURACION NORMAL (FC1.2) + NC total.
        //     El pipeline calcula los totales como siempre (rama 'else' de abajo). Ese
        //     bloque quedo IDENTICO en comportamiento al codigo previo al fix.
        //
        //   - request.TotalsOverride != null  -> NC PARCIAL (caller con cuadre exacto).
        //     El caller (InvoiceService.EmitPartialCreditNoteAsync) ya prorrateo el IVA con
        //     PartialCreditNoteIvaCalculator y nos pasa los mismos numeros que valida antes
        //     de POSTear. Usamos ESOS numeros tal cual, sin recalcular, para que el envelope
        //     cuadre EXACTO a 2 decimales (ImpIVA == Σ AlicIva.Importe). Ver clase
        //     InvoiceTotalsOverride para el invariante.
        decimal net;
        decimal iva;
        decimal tributosTotal;
        decimal total;
        List<InvoiceItem> invoiceItems;

        if (request.TotalsOverride != null)
        {
            var ov = request.TotalsOverride;

            // Tomamos los totales del override TAL CUAL: ya vienen redondeados a 2 decimales
            // y cuadrados por el caller. NO recalculamos ni redondeamos de nuevo.
            net = ov.ImpNeto;
            iva = ov.ImpIVA;
            tributosTotal = ov.ImpTrib;
            total = ov.ImpTotal;

            // Construimos los InvoiceItem repartiendo el IVA redondeado de cada grupo de
            // alicuota entre sus items. Esto es la pieza delicada del fix: el job RELEE la
            // Invoice de BD y vuelve a agrupar invoice.Items por alicuota sumando
            // InvoiceItem.ImporteIva. Para que esa suma por grupo de EXACTAMENTE el Importe
            // redondeado del override (y la suma total de EXACTAMENTE ImpIVA), persistimos
            // los ImporteIva de los items ya distribuidos y cuadrados aca.
            invoiceItems = BuildInvoiceItemsFromOverride(request.Items, ov);
        }
        else
        {
            // ===== RAMA FC1.2 / NC total (comportamiento original, sin cambios) =====
            var ivaGroups = request.Items
                .GroupBy(i => i.AlicuotaIvaId)
                .Select(g => new
                {
                    Id = g.Key,
                    BaseImp = g.Sum(x => x.Total),
                    Importe = g.Sum(x => x.Total * GetVatMultiplier(g.Key))
                })
                .ToList();

            net = request.Items.Sum(i => i.Total);
            iva = ivaGroups.Sum(g => g.Importe);
            tributosTotal = request.Tributes.Sum(t => t.Importe);
            total = net + iva + tributosTotal;

            net = Math.Round(net, 2);
            iva = Math.Round(iva, 2);
            tributosTotal = Math.Round(tributosTotal, 2);
            total = Math.Round(total, 2);

            invoiceItems = request.Items.Select(i => new InvoiceItem
            {
                Description = i.Description,
                Quantity = i.Quantity,
                UnitPrice = i.UnitPrice,
                Total = i.Total,
                AlicuotaIvaId = i.AlicuotaIvaId,
                ImporteIva = i.Total * GetVatMultiplier(i.AlicuotaIvaId)
            }).ToList();
        }

        // 5. Create PENDING Invoice
        var agencySettings = await _context.AgencySettings.FirstOrDefaultAsync();

        var invoice = new Invoice
        {
             ReservaId = ReservaId,
             OriginalInvoiceId = originalInvoice?.Id,
             TipoComprobante = cbteTipo,
             PuntoDeVenta = settings.PuntoDeVenta,
             NumeroComprobante = 0, // Placeholder
             CAE = null,
             VencimientoCAE = null,
             Resultado = "PENDING", // <--- NEW STATE
             // Leyenda Ley 27.618 (solo RI->Monotributista, Factura A). NULL en cualquier otro caso.
             // Se imprime en el PDF (InvoicePdfService.ComposeFiscalLegend); NO se manda a ARCA.
             FiscalLegend = fiscalLegend,
             ImporteTotal = total,
             ImporteNeto = net,
             ImporteIva = iva,
             // FC1.3.F2.5 (multimoneda, 2026-05-28): persistimos la moneda/cotizacion del
             // request en la Invoice. ProcessInvoiceJob las relee de esta misma fila para
             // armar el XML SOAP. Los callers FC1.2 no setean estas props -> defaults
             // ("PES", 1) -> comportamiento identico a antes de F2.5.
             MonId = request.MonId,
             MonCotiz = request.MonCotiz,
             // ADR-012 MVP (facturar en dolares, 2026-05-29): trazabilidad del TC. Los
             // callers de pesos no setean estos campos -> quedan NULL (factura en pesos).
             // Para moneda extranjera, InvoiceService.ValidateMultiCurrencyInvoicingAsync
             // ya garantizo que vengan completos antes de llegar aca.
             ExchangeRateSource = request.ExchangeRateSource,
             ExchangeRateFetchedAt = request.ExchangeRateFetchedAt,
             ExchangeRateJustification = request.ExchangeRateJustification,
             CreatedAt = DateTime.UtcNow,
             WasForced = request.ForceIssue,
             ForceReason = request.ForceReason,
             ForcedByUserId = request.ForcedByUserId,
             ForcedByUserName = request.ForcedByUserName,
             ForcedAt = request.ForceIssue ? DateTime.UtcNow : null,
             // ADR-024 item 3 (auditoria de emision, 2026-06-12): quien emite. InvoiceService.CreateAsync
             // los sello server-side con el usuario actual. IssuedAt queda NULL hasta que llegue el CAE.
             IssuedByUserId = request.IssuedByUserId,
             IssuedByUserName = request.IssuedByUserName,
             OutstandingBalanceAtIssuance = reserva.Balance,
             AgencySnapshot = agencySettings != null ? System.Text.Json.JsonSerializer.Serialize(agencySettings) : null,
             CustomerSnapshot = System.Text.Json.JsonSerializer.Serialize(customer, new JsonSerializerOptions { ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles }),
             Items = invoiceItems,
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

    /// <summary>
    /// FC1.3.F2.2 (fix fiscal B1, 2026-05-27 + fix de semantica BRUTO 2026-05-28): construye
    /// los <see cref="InvoiceItem"/> de una NC parcial cuando el caller paso un
    /// <see cref="InvoiceTotalsOverride"/> con el cuadre ya redondeado.
    ///
    /// <para><b>Por que es estatico y puro</b>: no toca BD ni ARCA. Recibe las lineas + el
    /// override y devuelve los items con su <c>ImporteIva</c> ya distribuido. Asi se puede
    /// testear LOCAL sin Docker el caso que rompia B1 (≥2 lineas de la misma alicuota cuyos
    /// IVA por linea redondeados suman distinto que el round del agregado).</para>
    ///
    /// <para><b>SEMANTICA DE <c>line.Total</c> (decision Gaston 2026-05-28)</b>: el <c>Total</c>
    /// de cada <see cref="InvoiceItemDto"/> de la NC parcial es BRUTO (incluye IVA por dentro),
    /// igual que en la factura origen. El IVA por item se EXTRAE del bruto: NO se hace gross-up.
    /// Ver <c>PartialCreditNoteIvaCalculator</c> para el rationale completo.</para>
    ///
    /// <para><b>La mecanica (el punto delicado del fix)</b>: el override trae el IVA YA
    /// REDONDEADO POR GRUPO de alicuota (un <see cref="AlicIvaOverride"/> por alicuota). Pero
    /// puede haber N lineas por alicuota, y el job (<c>ProcessInvoiceJob</c>) RELEE la Invoice
    /// de BD y reagrupa <c>invoice.Items</c> sumando <c>InvoiceItem.ImporteIva</c>. Para que
    /// esa suma por grupo de EXACTAMENTE el Importe redondeado del override, repartimos ese
    /// Importe entre los items del grupo asi:
    /// <list type="number">
    ///   <item>A cada item del grupo le EXTRAEMOS su IVA del bruto: <c>itemBaseImp =
    ///   round(item.Total / (1+tasa), 2)</c>, <c>itemIva = item.Total - itemBaseImp</c>
    ///   (residuo exacto a nivel item).</item>
    ///   <item>Calculamos el residuo del GRUPO = <c>override.Importe del grupo - Σ de esos
    ///   itemIva ya calculados</c>.</item>
    ///   <item>Ese residuo de grupo (1-2 centavos como mucho) se lo sumamos al ULTIMO item
    ///   del grupo para que el AGREGADO por alicuota cierre exacto contra el override.</item>
    /// </list>
    /// Resultado: <c>Σ InvoiceItem.ImporteIva del grupo == override.Importe del grupo</c>,
    /// exacto y persistido. Y como la suma de todos los grupos del override es <c>ImpIVA</c>
    /// (el caller ya lo cuadro), el envelope cierra exacto al releer de BD.</para>
    ///
    /// <para><b>Sobre el residuo en el ultimo item</b>: NO buscamos repartir "justo" el
    /// centavo entre items (eso es criterio fiscal de detalle que el contador no pidio). Solo
    /// nos importa que el AGREGADO por alicuota cuadre, que es lo unico que el ARCA valida en
    /// el <c>AlicIva</c>. Cargar el residuo al ultimo item es deterministico y simple.</para>
    ///
    /// <para><b>Guardas defensivas (MEJORA 2)</b>: este metodo LANZA
    /// <see cref="InvalidOperationException"/> si detecta un override desalineado con las lineas
    /// (bug de programacion aguas arriba): (a) si el residuo cargado al ultimo item supera unos
    /// centavos o dejaria su IVA negativo, o (b) si una alicuota presente en las lineas no esta
    /// en el override. En ambos casos preferimos fallar ruidoso a persistir un comprobante con
    /// IVA raro/faltante que ARCA despues rebota. El caller (<c>EmitPartialCreditNoteAsync</c>)
    /// valida el cuadre ANTES, asi que en el flujo normal estas guardas nunca disparan.</para>
    /// </summary>
    /// <param name="lines">Las lineas de la NC tal cual vienen en el request (no se mutan).</param>
    /// <param name="totalsOverride">El override con el desglose por alicuota ya redondeado.</param>
    /// <returns>Los <see cref="InvoiceItem"/> a persistir, con <c>ImporteIva</c> cuadrado por grupo.</returns>
    /// <exception cref="InvalidOperationException">Override desalineado con las lineas (ver Guardas defensivas).</exception>
    internal static List<InvoiceItem> BuildInvoiceItemsFromOverride(
        IReadOnlyList<InvoiceItemDto> lines,
        InvoiceTotalsOverride totalsOverride)
    {
        // Indexamos el override por codigo de alicuota para buscar rapido el Importe del grupo.
        var importePorAlicuota = totalsOverride.AlicIvas
            .ToDictionary(group => group.Id, group => group.Importe);

        var items = new List<InvoiceItem>(lines.Count);

        // Recorremos las lineas agrupadas por alicuota MANTENIENDO el orden de aparicion.
        // GroupBy de LINQ preserva el orden del primer elemento de cada grupo, lo que hace
        // el reparto deterministico (el "ultimo item" es siempre el mismo dado el mismo input).
        foreach (var lineGroup in lines.GroupBy(line => line.AlicuotaIvaId))
        {
            int alicuotaIvaId = lineGroup.Key;
            var groupLines = lineGroup.ToList();

            // GUARDA (b) (MEJORA 2): si las lineas traen una alicuota que el override NO incluye,
            // antes repartiamos 0 de IVA en SILENCIO para ese grupo. Eso es un descuadre fiscal
            // serio (el comprobante saldria con menos IVA del que corresponde a esas lineas) que
            // hoy pasaba callado. Es un bug de programacion aguas arriba: el calculator genera un
            // grupo por cada alicuota presente, asi que si falta es porque el override y las lineas
            // se desincronizaron. Fallamos RUIDOSO en vez de emitir un comprobante con IVA faltante.
            if (!importePorAlicuota.TryGetValue(alicuotaIvaId, out var importeDelGrupo))
            {
                throw new InvalidOperationException(
                    $"BuildInvoiceItemsFromOverride: la alicuota {alicuotaIvaId} esta presente en " +
                    $"las lineas pero NO en el override (AlicIvas). Override desincronizado con las " +
                    $"lineas: se emitiria una NC con IVA faltante para ese grupo. Abortado.");
            }

            // Paso 1: EXTRAEMOS el IVA de cada item por separado. Paso 2: el residuo del
            // grupo (Importe override - Σ IVA por item) se lo cargamos al ULTIMO item.
            decimal multiplier = GetVatMultiplierStatic(alicuotaIvaId);
            decimal acumuladoRedondeado = 0m;
            for (int indice = 0; indice < groupLines.Count; indice++)
            {
                var line = groupLines[indice];
                bool esUltimoDelGrupo = indice == groupLines.Count - 1;

                // IVA "propio" del item: extraido del bruto del item. La base se redondea a 2
                // decimales y el IVA queda como residuo, garantizando lineBaseImp + lineIva
                // == line.Total a centavo exacto (lo que el cliente vio en la factura).
                decimal itemBaseImp = Math.Round(line.Total / (1m + multiplier), 2);
                decimal itemIvaExtraido = line.Total - itemBaseImp;

                decimal importeIvaItem;
                if (esUltimoDelGrupo)
                {
                    // El ultimo item absorbe lo que falte para que el grupo sume EXACTO el
                    // Importe redondeado del override.
                    importeIvaItem = importeDelGrupo - acumuladoRedondeado;

                    // GUARDA (a) (MEJORA 2): el residuo (lo que carga el ultimo item por encima de
                    // su IVA extraido propio) deberia ser de 1-2 centavos como mucho. Si es mas
                    // grande, o si deja el IVA del ultimo item NEGATIVO, el override esta
                    // desalineado con las lineas (bug aguas arriba: el Importe del grupo no se
                    // calculo sobre estas mismas lineas). Mejor fallar ruidoso con diagnostico que
                    // persistir un InvoiceItem con IVA raro/negativo que ARCA despues rebota.
                    decimal residuo = importeIvaItem - itemIvaExtraido;
                    if (importeIvaItem < 0m || Math.Abs(residuo) > 0.05m)
                    {
                        throw new InvalidOperationException(
                            $"BuildInvoiceItemsFromOverride: el reparto de IVA del grupo alicuota " +
                            $"{alicuotaIvaId} quedo inconsistente. Importe override del grupo=" +
                            $"{importeDelGrupo}, Σ IVA extraido por item={acumuladoRedondeado + itemIvaExtraido}, " +
                            $"residuo cargado al ultimo item={residuo}, IVA resultante del ultimo item=" +
                            $"{importeIvaItem}. El override esta desalineado con las lineas. Abortado.");
                    }
                }
                else
                {
                    importeIvaItem = itemIvaExtraido;
                    acumuladoRedondeado += importeIvaItem;
                }

                items.Add(new InvoiceItem
                {
                    Description = line.Description,
                    Quantity = line.Quantity,
                    UnitPrice = line.UnitPrice,
                    Total = line.Total,
                    AlicuotaIvaId = line.AlicuotaIvaId,
                    ImporteIva = importeIvaItem
                });
            }
        }

        return items;
    }

    /// <summary>
    /// FC1.3.F2.2: version estatica de <see cref="GetVatMultiplier"/>, para usar desde el
    /// helper puro <see cref="BuildInvoiceItemsFromOverride"/> (que no tiene instancia).
    /// MISMA tabla canonica que la version de instancia y que
    /// <c>PartialCreditNoteIvaCalculator.GetVatMultiplier</c>.
    /// </summary>
    private static decimal GetVatMultiplierStatic(int id) => id switch
    {
        3 => 0m,     // 0%
        4 => 0.105m, // 10.5%
        5 => 0.21m,  // 21%
        6 => 0.27m,  // 27%
        8 => 0.05m,  // 5%
        9 => 0.025m, // 2.5%
        _ => 0m
    };

    /// <summary>
    /// FC1.3.F2.5 (multimoneda) — fuente UNICA del fragmento SOAP &lt;MonId&gt;/&lt;MonCotiz&gt; que
    /// viaja en el envelope FECAESolicitar de <see cref="ProcessInvoiceJob"/>.
    ///
    /// <para><b>Por que existe este metodo</b> (fix M-2, revision 2026-05-28): antes la
    /// interpolacion estaba inline en el SOAP y el test unit la replicaba en su propio helper —
    /// un test tautologico que assertaba contra su propia copia, no contra el codigo real. Al
    /// extraer el armado aca, el test de formato llama a ESTE metodo: si alguien cambia el formato,
    /// el test rojo lo detecta (en vez de un comprobante rebotado por ARCA en produccion).</para>
    ///
    /// <para><b>Byte-identidad para PES</b> (de-riesgo homologacion): este metodo corre para TODOS
    /// los comprobantes (es el path comun FC1.2, NO esta gateado por el flag de F2.5). Para no
    /// arriesgar una regresion en la facturacion existente —que ya esta homologada con ARCA—,
    /// cuando la moneda es "PES" emitimos el literal <c>&lt;MonCotiz&gt;1&lt;/MonCotiz&gt;</c>
    /// EXACTAMENTE como el hardcoded historico. El formato de 6 decimales (1234.560000) se usa
    /// SOLO para moneda extranjera, que se homologa por separado antes de prender el flag.</para>
    ///
    /// <para><b>Por que 6 decimales + InvariantCulture en extranjera</b>: ARCA exige el PUNTO como
    /// separador decimal. Sin InvariantCulture, un server con locale es-AR escribiria una coma y
    /// ARCA rebotaria el comprobante. Los 6 decimales dan precision suficiente para cualquier TC.</para>
    /// </summary>
    /// <param name="monId">Codigo de moneda ARCA ("PES", "DOL", ...).</param>
    /// <param name="monCotiz">Cotizacion contra el peso (1 para pesos, TC del comprobante para extranjera).</param>
    /// <returns>El fragmento XML <c>&lt;MonId&gt;...&lt;/MonId&gt;&lt;MonCotiz&gt;...&lt;/MonCotiz&gt;</c>.</returns>
    internal static string BuildMonedaSoapFragment(string monId, decimal monCotiz)
    {
        // Pesos: byte-identico al hardcoded historico "<MonCotiz>1</MonCotiz>". Cero riesgo de
        // regresion para la facturacion FC1.2 ya homologada. Usamos InvariantCulture igual por
        // las dudas que monCotiz no sea exactamente 1 (defensivo), manteniendo el formato sin
        // decimales para que coincida con lo que ARCA ya acepta hace tiempo.
        if (string.Equals(monId, "PES", StringComparison.OrdinalIgnoreCase))
        {
            return $"<MonId>{monId}</MonId>" +
                   $"<MonCotiz>{monCotiz.ToString("0.######", CultureInfo.InvariantCulture)}</MonCotiz>";
        }

        // Moneda extranjera (F2.5): 6 decimales fijos para precision del TC.
        return $"<MonId>{monId}</MonId>" +
               $"<MonCotiz>{monCotiz.ToString("0.000000", CultureInfo.InvariantCulture)}</MonCotiz>";
    }

    /// <summary>
    /// FC1.3.F2.5 (multimoneda) — arma el fragmento SOAP &lt;CanMisMonExt&gt; del envelope
    /// FECAESolicitar. CanMisMonExt = "Cancela en Misma Moneda Extranjera".
    ///
    /// <para><b>Que significa el campo</b> (RG AFIP/ARCA 5616/2024): para comprobantes emitidos
    /// en moneda extranjera, indica si el comprobante se COBRA en esa misma moneda extranjera
    /// ("S") o en otra moneda — pesos ("N"). Ejemplo cotidiano: facturas un paquete en dolares;
    /// si el cliente despues te paga en dolares es "S", si te paga en pesos es "N".</para>
    ///
    /// <para><b>Regla de emision del nodo</b>: el nodo SOLO se emite para moneda extranjera.
    /// Si el comprobante es en pesos (MonId="PES") NO se emite — devolvemos string vacio para
    /// que el envelope quede BYTE-IDENTICO al comportamiento historico de la facturacion en
    /// pesos (cero riesgo de regresion sobre lo ya homologado con ARCA).</para>
    ///
    /// <para><b>Valor fijo "N" en el MVP</b>: esta agencia factura en USD pero COBRA en pesos,
    /// asi que el unico caso real hoy es "N". El caso "S" (cobro en la misma moneda extranjera)
    /// queda como deuda futura.
    /// TODO (futuro): cuando exista en el modelo el dato "moneda de cobro" del comprobante, este
    /// helper debe recibir ese dato por parametro (ej. una moneda de cobro o un bool) y devolver
    /// "S" cuando la moneda de cobro coincida con la moneda extranjera del comprobante, "N" si no.
    /// NO implementar "S" hasta tener ese dato: hoy seria adivinar un valor fiscal.</para>
    /// </summary>
    /// <param name="monId">Codigo de moneda ARCA del comprobante ("PES", "DOL", ...).</param>
    /// <returns>
    /// String vacio si el comprobante es en pesos (no se emite el nodo); de lo contrario
    /// <c>&lt;CanMisMonExt&gt;N&lt;/CanMisMonExt&gt;</c> (MVP: cobro en pesos).
    /// </returns>
    internal static string BuildCanMisMonExtFragment(string monId)
    {
        // Pesos: no aplica el campo, no se emite el nodo. Byte-identico al envelope historico.
        if (string.Equals(monId, "PES", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        // Moneda extranjera: MVP fijo "N" (factura en USD, cobra en pesos). Ver TODO arriba
        // para el caso "S" (cobro en la misma moneda extranjera) cuando exista el dato de cobro.
        return "<CanMisMonExt>N</CanMisMonExt>";
    }

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

            // FC1.3.F2.5 (fix m-2, 2026-05-28): validacion defensiva del boundary. MonId es una
            // prop publica de CreateInvoiceRequest sin validacion; un caller equivocado podria
            // dejar el ISO ("USD") en vez del codigo ARCA ("DOL"), y el SOAP lo mandaria literal.
            // Fallar ACA con un mensaje claro es mejor que un rechazo opaco de ARCA. La validacion
            // usa la fuente unica de codigos (ArcaCurrencyMapper) para no duplicar el catalogo.
            if (!ArcaCurrencyMapper.IsValidArcaCurrencyCode(invoice.MonId))
            {
                _logger.LogError(
                    "ProcessInvoiceJob abortado para Invoice {InvoiceId}: MonId '{MonId}' no es un codigo ARCA valido " +
                    "(esperado PES o DOL). Posible ISO sin mapear. No se POSTea a ARCA.",
                    invoiceId, invoice.MonId);
                throw new InvalidOperationException(
                    $"Invoice {invoiceId} tiene MonId '{invoice.MonId}', que no es un codigo de moneda ARCA valido " +
                    "(esperado PES o DOL). El comprobante no se envia a ARCA. Revisar el caller que poblo MonId.");
            }

            await EnsureAuth(settings);

            // ============================================================================
            // IDEMPOTENCIA ANTI-DOBLE-CAE (2026-06-25) — mismo mecanismo que la NC parcial
            // (ProcessPartialCreditNoteJob en InvoiceService: ArcaIdempotencyKeys + stale key
            // recovery). Cierra el agujero por el que un re-despacho del job (reinicio del
            // worker, redelivery at-least-once de Hangfire) podia pedir un NUEVO numero y
            // emitir un SEGUNDO CAE real para la misma operacion cuando el primer POST
            // autorizo en ARCA pero la respuesta/SaveChanges se perdio.
            //
            // Cubre los 3 tipos que pasan por aca: factura de venta, NC total y ND.
            //
            // Flujo (capas del plan FC1.3.F2.2 adaptadas a este job):
            //   (a) Snapshot del numerador ARCA ANTES de insertar la key.
            //   (b) idemKey deterministica por Invoice (mismo invoiceId => misma key).
            //   (c) INSERT de la key (UNIQUE). Si entra, seguimos a emitir.
            //   (d) Si choca (re-despacho): consultar ARCA. Si el comprobante YA se emitio,
            //       adoptamos su CAE/numero en ESTA Invoice (sin re-POSTear). Si no viajo,
            //       borramos la key huerfana y reintentamos limpio.
            // ============================================================================

            // (a) Snapshot del numerador ARCA PRIMERO. Tiene que vivir en la misma ejecucion
            //     del job que el POST: si Hangfire reintenta, este snapshot se recaptura y la
            //     capa de recovery usa el de la corrida ANTERIOR (persistido en la key huerfana).
            int lastSeenNumeroBeforePost = await GetLastAuthorizedNumeroAsync(
                puntoVenta: settings.PuntoDeVenta,
                cbteTipo: invoice.TipoComprobante,
                ct: CancellationToken.None);

            // (b) idemKey por Invoice: el invoiceId + tipo + punto de venta identifican de forma
            //     unica la operacion. El mismo invoiceId re-despachado produce la MISMA key.
            string invoiceIdemKey = BuildInvoiceIdempotencyKey(
                invoiceId: invoice.Id,
                tipoComprobante: invoice.TipoComprobante,
                puntoDeVenta: settings.PuntoDeVenta);

            // Umbrales de idempotencia: se leen de OperationalFinanceSettings (la misma fuente
            // que usa la NC parcial). Si la fila no existe (entornos de test InMemory) caemos a
            // los defaults de la entidad — comportamiento seguro e identico al diseño base.
            var idempotencySettings = await _context.OperationalFinanceSettings
                .OrderBy(s => s.Id)
                .FirstOrDefaultAsync()
                ?? new OperationalFinanceSettings();

            // (c) INSERT de la key ANTES de tocar ARCA.
            bool keyInserted = await TryInsertInvoiceIdempotencyKeyAsync(
                invoiceIdemKey,
                lastSeenNumeroBeforePost,
                CancellationToken.None);

            if (!keyInserted)
            {
                // (d) La key ya existia (re-despacho del job). Arbitrar contra ARCA: derivar el
                //     CAE ya emitido, o limpiar la huerfana para re-emitir limpio.
                bool resolvedWithoutNewPost = await HandleStaleInvoiceIdempotencyKeyAsync(
                    idemKey: invoiceIdemKey,
                    invoice: invoice,
                    settings: settings,
                    staleThresholdMinutes: idempotencySettings.IdempotencyKeyStaleThresholdMinutes,
                    roundingTolerance: idempotencySettings.PartialCreditNoteRoundingTolerance,
                    ct: CancellationToken.None);

                if (resolvedWithoutNewPost)
                {
                    // Recuperamos el CAE ya emitido (o no es nuestro turno): NO re-POSTear.
                    return;
                }

                // La key huerfana se borro -> reintentar el INSERT limpio. Si vuelve a chocar,
                // otra corrida gano la carrera: no re-emitimos (el otro intento o un re-despacho
                // posterior lo resuelven).
                bool reinserted = await TryInsertInvoiceIdempotencyKeyAsync(
                    invoiceIdemKey,
                    lastSeenNumeroBeforePost,
                    CancellationToken.None);

                if (!reinserted)
                {
                    _logger.LogWarning(
                        "ProcessInvoiceJob: IdempotencyKey activa tras limpiar la huerfana para " +
                        "Invoice {InvoiceId}. Otro intento gano la carrera. No se re-emite.",
                        invoiceId);
                    return;
                }
            }

            // Re-construct data needed for AFIP from the Invoice entity
            // 1. Next Number
            //    Usamos snapshot + 1 (= ultimo autorizado + 1) en vez de un segundo
            //    GetNextVoucherNumber: es el MISMO valor pero evita una consulta extra a ARCA y
            //    queda consistente con el snapshot que persiste la idempotency key (lo que hace
            //    que el recovery pueda comparar correctamente el numerador).
            int cbteNro = lastSeenNumeroBeforePost + 1;
            
            // 2. Doc Details from Snapshot or Relation? 
            // Better to parse from Snapshot to ensure immutability, but for now use relation or fallback
            // Parsing CustomerSnapshot is safer.
            // ADR-024 (2026-06-12): los datos fiscales del receptor (DocTipo, DocNro, CondicionIVAReceptorId)
            // salen del snapshot inmutable del cliente y se resuelven con ArcaReceptorResolver (fuente de
            // verdad unica, blindada por tests). Antes esta logica estaba inline aca con dos bugs reales:
            // un pasaporte extranjero se emitia como DNI argentino (DocTipo=96) y la condicion IVA del
            // receptor nunca leia Customer.TaxConditionId (siempre salia Consumidor Final). Si el snapshot
            // no trae datos, el resolver cae al fallback consumidor final sin identificar (99/0) -> mismo
            // comportamiento que el default historico.
            long docNro = 0;
            int docTipo = ArcaReceptorResolver.DocTipoConsumidorFinalSinIdentificar; // 99
            int condicionIvaReceptorId = ArcaReceptorResolver.CondicionIvaConsumidorFinal; // 5

            if (!string.IsNullOrEmpty(invoice.CustomerSnapshot))
            {
                 var cust = System.Text.Json.JsonSerializer.Deserialize<Customer>(invoice.CustomerSnapshot);
                 if (cust != null)
                 {
                     // ADR-024 §3.4: DocTipo/DocNro con precedencia CUIT > DocumentType > numero suelto > sin dato.
                     var receptorDoc = ArcaReceptorResolver.ResolveDocument(cust.TaxId, cust.DocumentType, cust.DocumentNumber);
                     docTipo = receptorDoc.DocTipo;
                     docNro = receptorDoc.DocNro;

                     // ADR-024 §4.2: CondicionIVAReceptorId desde TaxConditionId del snapshot (snapshots viejos
                     // sin ese campo lo tienen null tras deserializar -> el resolver parsea el texto o deriva
                     // por DocTipo). Se evalua contra el docTipo YA resuelto para la derivacion conservadora.
                     condicionIvaReceptorId = ArcaReceptorResolver.ResolveCondicionIva(
                         cust.TaxConditionId, cust.TaxCondition, docTipo);
                 }
            }

            // 3. Re-Calculate IVA Groups (AFIP Needs breakdown)
            bool isFacturaC = invoice.TipoComprobante == 11 || invoice.TipoComprobante == 12 || invoice.TipoComprobante == 13;

            // FC1.3.F2.2 (fix fiscal B1): el job RELEE la Invoice de BD y reagrupa los items
            // por alicuota sumando InvoiceItem.ImporteIva (NO recalcula Total*tasa). Por eso el
            // cuadre exacto de la NC parcial tiene que estar YA PERSISTIDO en los items:
            // CreatePendingInvoice + BuildInvoiceItemsFromOverride distribuyen el IVA de cada
            // grupo de modo que Σ ImporteIva por grupo == el Importe redondeado del override.
            // INVARIANTE (no romper): <ImpIVA> == Σ <AlicIva><Importe> == invoice.ImporteIva.
            // Como aca NO se vuelve a redondear (solo ToString("0.00") al serializar) y los
            // ImporteIva persistidos ya son de 2 decimales, la suma es exacta. Para la
            // facturacion FC1.2 / NC total los items vienen de la rama sin override, identico
            // a como era antes del fix.
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
            
            // LEYENDA FISCAL (Ley 27.618 / RG 5003) -> NO va en el envelope WSFEv1.
            //
            // El experto ARCA verifico contra el XSD del WSFEv1 (y pyafipws) que NO existe un nodo
            // <Observaciones>/<Obs> de texto libre en FECAEDetRequest. El unico campo de datos
            // libres es <Opcionales>, cuyos Id salen de una tabla enumerada (FEParamGetTiposOpcional),
            // NO texto arbitrario con Id=1. Ademas, la leyenda RG 5003 es un requisito del comprobante
            // IMPRESO (PDF), no un dato que ARCA reciba o valide. Por eso:
            //   - NO se construye ni se emite el nodo <Observaciones> aca (era invalido: un emisor RI
            //     habria hecho REBOTAR el comprobante en ARCA).
            //   - La leyenda se sigue persistiendo en Invoice.FiscalLegend (CreatePendingInvoice) y se
            //     RENDERIZA en el PDF (ver InvoicePdfService.ComposeFiscalLegend), que es donde la
            //     norma la exige.
            // Para emisor Monotributo FiscalLegend siempre fue NULL, asi que el envelope queda
            // BYTE-IDENTICO al historico (no se quita ningun nodo que se estuviera emitiendo en Mono).

            var today = DateTime.Now.ToString("yyyyMMdd");

            // For Factura C, ImpNeto MUST be exactly equal to the subtotal before taxes.
            // And ImpIVA MUST be 0.
            decimal impNeto = isFacturaC ? invoice.ImporteTotal - invoice.Tributes.Sum(t => t.Importe) : invoice.ImporteNeto;
            decimal impIva = isFacturaC ? 0 : invoice.ImporteIva;

            // MULTIALICUOTA (verificado 2026-06-13): el desglose por renglon YA esta soportado.
            // Cada InvoiceItem lleva su propio AlicuotaIvaId; el bloque <Iva> (sbIva, mas arriba)
            // agrupa los items por alicuota y emite un <AlicIva> por cada grupo. O sea 21% + 10.5%
            // conviven en una misma Factura A sin nada hardcodeado, y las tasas viven en
            // GetVatMultiplier (tabla), no en linea. No hubo que tocar nada para esto.
            //
            // GAP CONOCIDO - EXENTO real + base sobre el MARGEN (PENDIENTE FIRMA CONTADOR):
            // <ImpOpEx> esta fijo en 0. Una operacion EXENTA (sin IVA, distinta de la alicuota 0%)
            // deberia ir a ImpOpEx + ImpTotConc, NO como un <AlicIva> de 0%. Hoy un item con
            // AlicuotaIvaId=3 se trata como alicuota 0% (AlicIva Id=3, Importe=0), que NO es
            // identico fiscalmente a 'exento'. Tampoco se descuentan los componentes de tercero del
            // operador para facturar IVA sobre el MARGEN (art. 61 DR Ley 23.349 / Ley 18.829 IVA
            // agencias). Ambas cosas necesitan criterio fino del matriculado caso por caso: hoy la
            // Factura A se arma por linea manual. NO construir sin firma del contador.

            // FC1.3.F2.5 (multimoneda) — nodo CanMisMonExt ("Cancela en Misma Moneda Extranjera",
            // RG ARCA 5616/2024). Lo arma BuildCanMisMonExtFragment(invoice.MonId): para pesos
            // devuelve "" (no se emite el nodo, envelope byte-identico al historico); para moneda
            // extranjera devuelve "<CanMisMonExt>N</CanMisMonExt>" (MVP: factura USD, cobra pesos).
            // ATENCION - CONFIRMAR EN HOMOLOGACION: el ORDEN del nodo en el envelope (DESPUES de
            // MonId/MonCotiz y ANTES de CondicionIVAReceptorId) hay que verificarlo contra el XSD del
            // WSFEv1 v4. Un nodo fuera de orden hace REBOTAR el comprobante. No lo afirmamos de
            // memoria; validar con un CAE aprobado en el ambiente de homologacion ARCA.
             var soapEnv = $@"<?xml version=""1.0"" encoding=""utf-8""?>
    <soap:Envelope xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xmlns:xsd=""http://www.w3.org/2001/XMLSchema"" xmlns:soap=""http://schemas.xmlsoap.org/soap/envelope/"">
      <soap:Body>
        <FECAESolicitar xmlns=""http://ar.gov.afip.dif.FEV1/"">
          <Auth>
            <Token>{GetAuthToken(settings)}</Token>
            <Sign>{GetAuthSign(settings)}</Sign>
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
                    <!-- DEUDA F2.x (B2): Concepto/fechas (CbteFch, FchServDesde/Hasta,
                         FchVtoPago) hardcoded. El arquitecto lo difirio como deuda +
                         preguntas al contador. NO se cambia el comportamiento en este fix. -->
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
                    <!-- FC1.3.F2.5 (multimoneda, 2026-05-28): MonId/MonCotiz salen de la Invoice
                         (poblada en CreatePendingInvoice). El armado lo centraliza
                         BuildMonedaSoapFragment para que el test unit blinde el MISMO codigo que
                         corre en produccion (ver AfipServiceMonedaSoapFormatTests). Para pesos el
                         fragmento es BYTE-IDENTICO al hardcoded historico; el formato de 6 decimales
                         se usa SOLO para moneda extranjera (ver el metodo). -->
                    {BuildMonedaSoapFragment(invoice.MonId, invoice.MonCotiz)}
                    <!-- FC1.3.F2.5: CanMisMonExt (solo moneda extranjera). Ver nota arriba del envelope. -->
                    {BuildCanMisMonExtFragment(invoice.MonId)}
                    <!-- ADR-024 §4: CondicionIVAReceptorId resuelto desde el snapshot del cliente
                         (ArcaReceptorResolver), NO mas fijado en Consumidor Final. Obligatorio RG 5616. -->
                    <CondicionIVAReceptorId>{condicionIvaReceptorId}</CondicionIVAReceptorId>
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
                // B1.15 (2026-05-10): error transitorio (red / XML invalido / AFIP
                // intermitente) NO es rechazo definitivo. Marcamos PENDING (no "R")
                // para que la UI muestre "En proceso / Reintentar" en vez de
                // "Rechazado". El Vendedor decide cuando reintentar manualmente
                // (boton Reintentar en InvoicingTab).
                //
                // IDEMPOTENCIA: NO resolvemos la key. Si el POST llego a ARCA igual (el
                // numerador pudo avanzar) y solo se perdio la respuesta, dejar la key SIN
                // resolver permite que un re-despacho la detecte como huerfana y recupere el
                // CAE consultando a ARCA, en vez de re-emitir.
                invoice.Resultado = "PENDING";
                invoice.Observaciones = "AFIP respondió con un error de red o XML inválido. Reintentá en unos segundos.";
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
                 // IDEMPOTENCIA: rechazo DEFINITIVO de ARCA -> NO se emitio comprobante, no hay
                 // CAE que recuperar. Resolvemos la key (cierra el intento). Un re-despacho la
                 // vera resuelta y no re-POSTeara; el reintento real lo decide el usuario via
                 // RetryAsync, que limpia el estado.
                 await ResolveInvoiceIdempotencyKeyAsync(invoiceIdemKey, CancellationToken.None);
                 await _context.SaveChangesAsync();
                 return;
            }

            // Success path. ARCA respondio Resultado="A" (aprobado) o "O"/"P" (aprobado CON
            // observaciones / parcial). En AMBOS casos el comprobante queda emitido CON CAE: la
            // diferencia es que con observaciones ARCA agrega codigos en <Obs> que conviene
            // preservar (no son un rechazo). Antes el codigo solo distinguia "R" de "no R" y
            // asumia que el CAE siempre venia: un CAE nulo en este punto tiraba excepcion -> la
            // factura quedaba PENDING en silencio aunque ARCA ya le hubiera dado un CAE.
            var detResp = resultNode.Descendants(XName.Get("FECAEDetResponse", "http://ar.gov.afip.dif.FEV1/")).FirstOrDefault();
            var cae = detResp?.Element(XName.Get("CAE", "http://ar.gov.afip.dif.FEV1/"))?.Value;
            var caeVto = detResp?.Element(XName.Get("CAEFchVto", "http://ar.gov.afip.dif.FEV1/"))?.Value;

            // Manejo robusto del "Observado" (O): capturamos las observaciones del detalle para
            // dejarlas en la factura aunque haya CAE (son informativas, no un rechazo).
            string? successObservations = null;
            if (detResp != null)
            {
                var detObs = detResp.Descendants(XName.Get("Obs", "http://ar.gov.afip.dif.FEV1/"));
                var sbObs = new StringBuilder();
                foreach (var o in detObs)
                {
                    var code = o.Element(XName.Get("Code", "http://ar.gov.afip.dif.FEV1/"))?.Value;
                    var msg = o.Element(XName.Get("Msg", "http://ar.gov.afip.dif.FEV1/"))?.Value;
                    sbObs.AppendLine(TranslateAfipError(code, msg));
                }
                var obsText = sbObs.ToString().Trim();
                if (obsText.Length > 0)
                {
                    successObservations = obsText;
                }
            }

            // CASO ANOMALO: ARCA no rechazo (no "R") pero NO devolvio CAE. No podemos afirmar
            // que el comprobante quedo emitido ni inventar un CAE. En vez de tirar excepcion (que
            // dejaba un PENDING silencioso con un posible CAE real perdido), marcamos PENDING con
            // las observaciones capturadas y NO resolvemos la key: un re-despacho consultara ARCA
            // y, si el comprobante SI se emitio, recuperara el CAE real (sin doble emision).
            if (string.IsNullOrWhiteSpace(cae) || string.IsNullOrWhiteSpace(caeVto))
            {
                _logger.LogWarning(
                    "ProcessInvoiceJob: ARCA respondio sin rechazo (Resultado={CabResult}) pero sin CAE/Vto " +
                    "para Invoice {InvoiceId}. Se deja PENDING para recuperar via idempotencia en el proximo despacho.",
                    cabResult, invoiceId);

                invoice.Resultado = "PENDING";
                invoice.Observaciones = successObservations is null
                    ? "AFIP no devolvió CAE en la respuesta. Reintentá en unos segundos."
                    : "AFIP no devolvió CAE en la respuesta. Observaciones: " + successObservations;
                await _context.SaveChangesAsync();
                return;
            }

            invoice.Resultado = "A";
            invoice.CAE = cae;
            invoice.VencimientoCAE = DateTime.ParseExact(caeVto, "yyyyMMdd", null).ToUniversalTime();
            invoice.NumeroComprobante = cbteNro; // Assign actual number used
            // Observado (O): preservamos las observaciones; aprobado limpio (A): null como antes.
            invoice.Observaciones = successObservations;
            // ADR-024 item 3 (auditoria de emision, 2026-06-12): IssuedAt = momento en que ARCA aprobo el
            // CAE (Resultado="A"). Es la evidencia fiscal de cuando se emitio realmente el comprobante. UTC
            // para coherencia con el resto de timestamps (la columna es timestamptz).
            invoice.IssuedAt = DateTime.UtcNow;

            // IDEMPOTENCIA: emision exitosa -> resolvemos la key. La factura ya tiene CAE; un
            // re-despacho ahora corta por el guard Resultado=="A" al inicio del job.
            await ResolveInvoiceIdempotencyKeyAsync(invoiceIdemKey, CancellationToken.None);

            await _context.SaveChangesAsync();

            if (IsCreditNote(invoice.TipoComprobante))
            {
                await ApplyCreditNoteEconomicReversalAsync(invoice.Id);
            }
        }
        catch (Exception ex)
        {
            // B1.15 (2026-05-10): excepcion tecnica (timeout, auth, parsing, DNS,
            // etc.) NO es rechazo definitivo de AFIP. Marcamos PENDING para que
            // la UI muestre "En proceso / Reintentar". El rechazo definitivo
            // ("R") queda reservado para cuando AFIP procesa la solicitud y
            // responde explicitamente con cabResult == "R" (ver caso de mas
            // arriba). El throw se mantiene para que Hangfire registre el job
            // como failed en el dashboard (visibilidad operativa).
            invoice.Resultado = "PENDING";
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

    private static bool IsCreditNote(int tipoComprobante)
    {
        return tipoComprobante == 3 || tipoComprobante == 8 || tipoComprobante == 13 || tipoComprobante == 53;
    }

    /// <summary>
    /// B1.15 (2026-05-11) — internal para tests del cascade.
    /// Crea la reversion economica de un Payment cuando AFIP aprobo la NC y, si
    /// existe Receipt Issued atado al Payment, lo marca Voided con audit trail
    /// completo (VoidedByUser* / VoidReason / accion `ReceiptVoidedByCascade`).
    ///
    /// <para><b>FC1.3 F2.3 (2026-05-28, cierra RH-005 + G-F2-D)</b>: el comportamiento
    /// cambia segun NC total vs NC parcial.
    /// <list type="bullet">
    ///   <item><b>NC TOTAL</b> (FC1.2 / fallback Fase 2 / kind null/total): comportamiento
    ///   actual sin cambios. Cascade-void del receipt cuando hay match exacto por monto.</item>
    ///   <item><b>NC PARCIAL</b> (FC1.3 Fase 2, <c>BookingCancellation.CreditNoteKind ==
    ///   PartialOnOriginal</c>): NO cascade-void de receipts. Crear el Payment reversal con
    ///   <c>OriginalPaymentId = null</c> y emitir un audit <c>PartialCreditNoteEconomicReversalNoCascade</c>
    ///   con la lista de receipt IDs vivos para que el admin pueda anular manualmente.</item>
    /// </list>
    /// </para>
    ///
    /// <para><b>Discriminador NC total vs parcial</b>: leer el <c>BookingCancellation</c>
    /// asociado a la NC via <c>bc.CreditNoteInvoiceId == invoice.Id</c>. Si existe BC con
    /// <c>CreditNoteKind == PartialOnOriginal</c> -> parcial. Si no existe BC (NCs
    /// pre-FC1.3 historicas), fallback a comparacion por monto contra la factura original.
    /// </para>
    /// </summary>
    internal async Task ApplyCreditNoteEconomicReversalAsync(int invoiceId)
    {
        var existingReversal = await _context.Payments
            .FirstOrDefaultAsync(p => p.RelatedInvoiceId == invoiceId && p.EntryType == PaymentEntryTypes.CreditNoteReversal);

        if (existingReversal != null)
            return;

        // B1.15 (2026-05-11): Include(OriginalInvoice) para propagar audit trail.
        // El user que anulo la factura original (persistido en
        // Invoice.AnnulledByUserId/Name antes de encolar el job) es el responsable
        // logico del cascade Receipt Voided. Esto evita cambiar la firma del job
        // Hangfire (cambio de firma rompe jobs encolados ante deploy y obliga a
        // mantener default values) y ademas refleja correctamente quien autorizo
        // la cadena de eventos.
        var invoice = await _context.Invoices
            .Include(i => i.Reserva)
            .Include(i => i.OriginalInvoice)
            .FirstOrDefaultAsync(i => i.Id == invoiceId);

        if (invoice?.ReservaId == null || invoice.Reserva == null)
            return;

        // FC1.3 F2.3: detectar NC parcial vs total ANTES de tocar Payments.
        // Decision primaria (RH2-003): leer BookingCancellation asociado al CreditNoteInvoiceId.
        // Si existe BC con CreditNoteKind == PartialOnOriginal -> parcial.
        // Fallback historico: comparacion por monto contra OriginalInvoice (NCs pre-FC1.3
        // que no tienen BC asociado tienen ImporteTotal == OriginalInvoice.ImporteTotal).
        var bc = await _context.BookingCancellations
            .FirstOrDefaultAsync(b => b.CreditNoteInvoiceId == invoice.Id);

        bool isPartialNc;
        if (bc != null)
        {
            // Path canonico FC1.3+: el kind persistido es la fuente de verdad.
            isPartialNc = bc.CreditNoteKind == CreditNoteKind.PartialOnOriginal;
        }
        else
        {
            // Fallback historico: NCs pre-FC1.3 sin BC asociado. La comparacion por monto
            // sigue siendo correcta porque las NCs emitidas via FC1.2 son siempre totales
            // y matchean el ImporteTotal original. Si la NC parcial existe en BD pero el
            // BC no esta seteado (deberia ser imposible en Fase 2), tambien caemos aca y
            // detectamos parcial por monto.
            isPartialNc = invoice.OriginalInvoice != null
                          && invoice.ImporteTotal < invoice.OriginalInvoice.ImporteTotal;
        }

        if (isPartialNc)
        {
            await ApplyPartialCreditNoteReversalAsync(invoice, bc);
        }
        else
        {
            await ApplyTotalCreditNoteReversalAsync(invoice);
        }

        await RecalculateReservaBalanceAsync(invoice.ReservaId.Value);
    }

    /// <summary>
    /// FC1.3 F2.3 (2026-05-28, cierra RH-005 + G-F2-D): aplica la reversion economica de
    /// una NC PARCIAL. A diferencia de la NC total, NO cascade-voida receipts: deja todos
    /// los receipts del payment original vivos y emite un audit con la lista de receipt IDs
    /// para que el admin decida manualmente cual anular (UI Fase 3, fuera de scope).
    ///
    /// <para><b>Diseño del Payment reversal</b>: <c>OriginalPaymentId = null</c> intencional
    /// — una NC parcial no apunta a un Payment unico (puede haber multiples, ej. G-F2-D:
    /// factura $1.000 pagada en 3 cuotas $300+$300+$400, NC parcial $250 no tiene un
    /// Payment "exacto" al cual atarse). El monto del reversal es el ImporteTotal de la NC
    /// parcial en negativo.</para>
    /// </summary>
    private async Task ApplyPartialCreditNoteReversalAsync(Invoice invoice, BookingCancellation? bc)
    {
        // 1) Crear el Payment reversal por el ImporteTotal de la NC parcial.
        //    OriginalPaymentId = null por diseño (no hay payment unico para apuntar).
        var reversal = new Payment
        {
            ReservaId = invoice.ReservaId,
            Amount = -invoice.ImporteTotal,
            PaidAt = DateTime.UtcNow,
            Method = "CreditNote",
            Reference = $"NC parcial AFIP {invoice.PuntoDeVenta:D5}-{invoice.NumeroComprobante:D8}",
            Notes = $"Reversion economica por nota de credito PARCIAL AFIP #{invoice.Id}. " +
                    $"Receipts NO cascade-voided (politica F2.3). Revision manual via UI Fase 3.",
            Status = "Paid",
            EntryType = PaymentEntryTypes.CreditNoteReversal,
            AffectsCash = false,
            RelatedInvoiceId = invoice.Id,
            OriginalPaymentId = null, // NC parcial: por diseño, sin payment exacto.
        };
        _context.Payments.Add(reversal);

        // 2) Query de receipts vivos (RH2-006): los Issued cuyos Payments tienen
        //    RelatedInvoiceId == invoice.OriginalInvoiceId. Estos son los receipts que
        //    el admin podra anular manualmente en la UI Fase 3.
        //
        //    Traemos los datos completos del recibo (no solo el Id) porque la bandeja de
        //    reconciliacion (Fase 3) necesita un snapshot: PaymentReceiptId + PaymentId +
        //    Amount + estado al abrir. El PaymentId es clave porque el endpoint de anular
        //    recibo resuelve por Payment, no por receipt (ADR-010 N1).
        var liveReceipts = new List<PaymentReceipt>();
        if (invoice.OriginalInvoiceId.HasValue)
        {
            liveReceipts = await _context.PaymentReceipts
                .Where(r => r.Payment!.RelatedInvoiceId == invoice.OriginalInvoiceId
                            && r.Status == PaymentReceiptStatuses.Issued)
                .ToListAsync();
        }
        var liveReceiptIds = liveReceipts.Select(r => r.Id).ToList();

        // 2.bis) FC1.3 Fase 3 (ADR-010, B1): crear el caso de reconciliacion DENTRO DEL
        //        MISMO SaveChanges que el Payment reversal. Esto es deliberado y critico:
        //        si lo creáramos despues del SaveChanges, se abriria una ventana donde el
        //        reversal ya esta aplicado + los recibos siguen vivos + NO hay caso en la
        //        bandeja = "plata invisible". Al agregar el caso al mismo ChangeTracker,
        //        reversal + caso commitean juntos en una sola transaccion implicita de EF.
        //
        //        Solo creamos el caso si hay recibos vivos: sin recibos no hay nada que
        //        acomodar (ej. factura sin recibos emitidos).
        //
        //        R3 (ADR-010): este metodo SOLO corre en el path CAE aprobado (Success).
        //        Por eso nunca nace un caso huerfano de una NC que ARCA rechazo.
        if (liveReceipts.Count > 0)
        {
            // El user de apertura sale del que disparo la cancelacion (AnnulledByUserId).
            // Si fue un proceso automatico, queda "system" (ADR-010 N3): cualquier
            // encargado podra cerrar el caso sin pedir bypass de 4-ojos, porque no hay
            // una "persona que abrio" a la cual exigirsela.
            var openedByUserId = invoice.OriginalInvoice?.AnnulledByUserId ?? "system";
            var openedByUserName = invoice.OriginalInvoice?.AnnulledByUserName ?? "Sistema";

            // Moneda del caso: la del FiscalLiquidation del BC si existe (fuente fiscal
            // del monto acreditado), si no ARS por defecto (hoy todo se factura en pesos;
            // multimoneda llega en F2.5). NO usamos invoice.MonId porque ese es el codigo
            // ARCA ('PES'), no el ISO ('ARS') que maneja el resto del modulo.
            var currency = bc?.FiscalLiquidation?.Currency ?? "ARS";

            var reconciliation = new PartialCreditNoteReconciliation
            {
                CreditNoteInvoiceId = invoice.Id,
                OriginalInvoiceId = invoice.OriginalInvoiceId!.Value,
                ReservaId = invoice.ReservaId,
                FiscalAmountCredited = invoice.ImporteTotal,
                Currency = currency,
                Status = PartialCreditNoteReconciliationStatus.Pending,
                OpenedAt = DateTime.UtcNow,
                OpenedByUserId = openedByUserId,
                OpenedByUserName = openedByUserName,
                Receipts = liveReceipts
                    .Select(r => new PartialCreditNoteReconciliationReceipt
                    {
                        PaymentReceiptId = r.Id,
                        PaymentId = r.PaymentId,
                        Amount = r.Amount,
                        StatusAtOpen = r.Status,
                    })
                    .ToList(),
            };
            _context.PartialCreditNoteReconciliations.Add(reconciliation);
        }

        // reversal + caso (si aplica) commitean juntos aca (B1).
        await _context.SaveChangesAsync();

        // 3) Audit nuevo (PartialCreditNoteEconomicReversalNoCascade) con detalle JSON.
        //    Sirve para auditoria + futuras queries: "cuantas NCs parciales emitidas dejaron
        //    receipts vivos sin anular?".
        var auditUserId = invoice.OriginalInvoice?.AnnulledByUserId ?? "system";
        var auditUserName = invoice.OriginalInvoice?.AnnulledByUserName ?? "Sistema";

        if (_auditService is not null)
        {
            await _auditService.LogBusinessEventAsync(
                action: "PartialCreditNoteEconomicReversalNoCascade",
                entityName: "Invoice",
                entityId: invoice.Id.ToString(),
                details: JsonSerializer.Serialize(new
                {
                    invoiceId = invoice.Id,
                    bcPublicId = bc?.PublicId.ToString(),
                    reversalAmount = -invoice.ImporteTotal,
                    liveReceiptIds = liveReceiptIds,
                    liveReceiptCount = liveReceiptIds.Count,
                    note = "NC parcial: receipts NO cascade-voided. Admin debe revisar manualmente.",
                }),
                userId: auditUserId,
                userName: auditUserName,
                ct: CancellationToken.None);
        }
        else
        {
            _logger.LogInformation(
                "PartialCreditNoteEconomicReversalNoCascade. InvoiceId={InvoiceId} BcPublicId={BcPublicId} " +
                "ReversalAmount={Amount} LiveReceiptCount={Count} LiveReceiptIds={Ids}",
                invoice.Id, bc?.PublicId, -invoice.ImporteTotal, liveReceiptIds.Count,
                string.Join(",", liveReceiptIds));
        }

        // FC1.3.F2.6 (counter, G-F2-D): contamos cada vez que la rama PARCIAL preserva
        // receipts (no cascade-void). El tag liveReceiptCount permite alertar si crece el
        // backlog de receipts que el admin todavia no anulo manualmente (UI Fase 3).
        _logger.LogInformation(
            "metric:Fc13.PartialCreditNote.NoCascadeReceiptsPreserved | creditNoteInvoiceId={InvoiceId} liveReceiptCount={Count}",
            invoice.Id, liveReceiptIds.Count);
    }

    /// <summary>
    /// FC1.2 (comportamiento historico, byte-identico al previo a F2.3): aplica la
    /// reversion economica de una NC TOTAL — cascade-voida el Receipt asociado si existe.
    /// </summary>
    private async Task ApplyTotalCreditNoteReversalAsync(Invoice invoice)
    {
        var matchedPayment = await _context.Payments
            .Include(p => p.Receipt)
            .Where(p =>
                p.ReservaId == invoice.ReservaId &&
                p.EntryType == PaymentEntryTypes.Payment &&
                !p.IsDeleted &&
                p.Status != "Cancelled" &&
                p.Amount == invoice.ImporteTotal)
            .OrderByDescending(p => p.PaidAt)
            .FirstOrDefaultAsync();

        var reversal = new Payment
        {
            ReservaId = invoice.ReservaId,
            Amount = -invoice.ImporteTotal,
            PaidAt = DateTime.UtcNow,
            Method = "CreditNote",
            Reference = $"NC AFIP {invoice.PuntoDeVenta:D5}-{invoice.NumeroComprobante:D8}",
            Notes = $"Reversion economica por nota de credito AFIP #{invoice.Id}.",
            Status = "Paid",
            EntryType = PaymentEntryTypes.CreditNoteReversal,
            AffectsCash = false,
            RelatedInvoiceId = invoice.Id,
            OriginalPaymentId = matchedPayment?.Id
        };

        _context.Payments.Add(reversal);

        // B1.15 (2026-05-11): cascade NC -> Receipt Voided con audit trail completo.
        // Idempotente (Status == Voided no se re-toca). El user se toma de la
        // invoice original que disparo la anulacion; fallback "system"/"Sistema"
        // si la NC se origino por un camino que no setea AnnulledByUserId
        // (defensivo, hoy todos los paths que llegan aca lo pueblan).
        PaymentReceipt? voidedReceipt = null;
        string voidUserId = "system";
        string voidUserName = "Sistema";
        string voidReason = string.Empty;

        if (matchedPayment?.Receipt != null && matchedPayment.Receipt.Status != PaymentReceiptStatuses.Voided)
        {
            voidUserId = string.IsNullOrWhiteSpace(invoice.OriginalInvoice?.AnnulledByUserId)
                ? "system"
                : invoice.OriginalInvoice!.AnnulledByUserId!;
            voidUserName = string.IsNullOrWhiteSpace(invoice.OriginalInvoice?.AnnulledByUserName)
                ? "Sistema"
                : invoice.OriginalInvoice!.AnnulledByUserName!;
            voidReason = $"Cascade automatico por NC AFIP {invoice.PuntoDeVenta:D5}-{invoice.NumeroComprobante:D8} (Invoice #{invoice.Id})";

            matchedPayment.Receipt.Status = PaymentReceiptStatuses.Voided;
            matchedPayment.Receipt.VoidedAt = DateTime.UtcNow;
            matchedPayment.Receipt.VoidedByUserId = voidUserId;
            matchedPayment.Receipt.VoidedByUserName = voidUserName;
            matchedPayment.Receipt.VoidReason = voidReason;

            voidedReceipt = matchedPayment.Receipt;
        }

        await _context.SaveChangesAsync();

        // Audit trail diferenciado del void manual (`ReceiptVoided` desde
        // PaymentService.VoidReceiptAsync). `ReceiptVoidedByCascade` permite al
        // operador o al auditor discriminar el origen del evento. Best-effort:
        // si _auditService es null (tests legacy / ctor sin DI) fallback a logger.
        if (voidedReceipt != null)
        {
            if (_auditService is not null)
            {
                await _auditService.LogBusinessEventAsync(
                    action: "ReceiptVoidedByCascade",
                    entityName: "PaymentReceipt",
                    entityId: voidedReceipt.Id.ToString(),
                    details: voidReason,
                    userId: voidUserId,
                    userName: voidUserName,
                    ct: CancellationToken.None);
            }
            else
            {
                _logger.LogInformation(
                    "PaymentReceipt voided by cascade. ReceiptId={ReceiptId} ReceiptNumber={ReceiptNumber} PaymentId={PaymentId} InvoiceId={InvoiceId} ByUser={UserId} Reason={Reason}",
                    voidedReceipt.Id, voidedReceipt.ReceiptNumber, matchedPayment!.Id, invoice.Id, voidUserId, voidReason);
            }
        }
    }

    // P1.5: el saldo se calcula con la FUENTE UNICA DE VERDAD (ReservaMoneyCalculator),
    // la misma que usa ReservaService.UpdateBalanceAsync. Antes esta copia sumaba PLANO
    // (sin el filtro CountsForReservaBalance), por lo que una reserva con servicios Cancelados
    // mostraba un saldo DISTINTO segun que accion lo recalculara (servicio vs pago vs factura).
    // Unificado -> el saldo es consistente y correcto sin importar que disparo el recalculo.
    private async Task RecalculateReservaBalanceAsync(int reservaId)
    {
        // ADR-021 §4.1/§B5 (este era el TERCER punto de escritura del escalar, disparado por la
        // reversa de NC). Antes actualizaba el escalar pero NO la tabla hija -> tras facturar/anular
        // NC la hija quedaba desactualizada (desincronizacion silenciosa). Ahora delega en el persister
        // consolidado, que escribe escalar surrogate + ReservaMoneyByCurrency en la misma SaveChanges.
        await TravelApi.Infrastructure.Reservations.ReservaMoneyPersister.PersistAsync(_context, reservaId);
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
        <Token>{GetAuthToken(settings)}</Token>
        <Sign>{GetAuthSign(settings)}</Sign>
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
        <Token>{GetAuthToken(settings)}</Token>
        <Sign>{GetAuthSign(settings)}</Sign>
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

        // ---- FC1.3.F2.2 (sub-tarea A.3.1, RH3-001 / RH4-002 round 4, 2026-05-27) ----
        // Extension ADITIVA del parseo: llenamos los campos nullable nuevos de
        // AfipVoucherDetails (Cae, IssuedAt, MonId, MonCotiz, CbteAsoc). Los callers
        // viejos (InvoiceService:723) NO se rompen: solo leen ImporteTotal/VatDetails/
        // TributeDetails/ImporteNeto, que siguen igual. Si el nodo no viene en el
        // response, el campo queda en null (es opcional por diseño).
        ParseVoucherDetailExtras(result, details, _logger);

        return details;
    }

    /// <summary>
    /// FC1.3.F2.2 (sub-tarea A.3.1): mapea los nodos OPCIONALES del response de
    /// <c>FECompConsultar</c> a los campos nullable de <see cref="AfipVoucherDetails"/>.
    /// Aislado en su propio metodo (y <c>static</c>) para mantener <c>GetVoucherDetails</c>
    /// legible y poder testear el parseo del array <c>CbtesAsoc</c> sin pegarle a ARCA ni
    /// instanciar todo el servicio. Es <c>internal</c> porque el assembly de tests tiene
    /// <c>InternalsVisibleTo</c>.
    /// </summary>
    /// <param name="result">El nodo <c>ResultGet</c> del response SOAP.</param>
    /// <param name="details">El DTO que se esta poblando (mutado in-place).</param>
    /// <param name="logger">Logger para el warning defensivo de multiples CbtesAsoc.</param>
    internal static void ParseVoucherDetailExtras(XElement result, AfipVoucherDetails details, ILogger logger)
    {
        var ns = "http://ar.gov.afip.dif.FEV1/";

        // CAE: en el response de FECompConsultar viene como <CodAutorizacion>
        // (no como <CAE>, que es el nombre del nodo en el response de FECAESolicitar).
        var cae = result.Element(XName.Get("CodAutorizacion", ns))?.Value;
        if (!string.IsNullOrWhiteSpace(cae))
        {
            details.Cae = cae;
        }

        // Fecha de emision: <CbteFch> viene en formato yyyyMMdd (ej. "20260527").
        // Si no parsea, dejamos IssuedAt en null en vez de romper toda la consulta.
        var cbteFch = result.Element(XName.Get("CbteFch", ns))?.Value;
        if (!string.IsNullOrWhiteSpace(cbteFch)
            && DateTime.TryParseExact(
                cbteFch,
                "yyyyMMdd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsedDate))
        {
            details.IssuedAt = parsedDate;
        }

        // Moneda + cotizacion del comprobante. MonId es texto ("PES", "DOL"),
        // MonCotiz es decimal. Solo se setean si vienen en el response.
        var monId = result.Element(XName.Get("MonId", ns))?.Value;
        if (!string.IsNullOrWhiteSpace(monId))
        {
            details.MonId = monId;
        }

        var monCotiz = result.Element(XName.Get("MonCotiz", ns))?.Value;
        if (!string.IsNullOrWhiteSpace(monCotiz)
            && decimal.TryParse(monCotiz, NumberStyles.Any, CultureInfo.InvariantCulture, out var cotiz))
        {
            details.MonCotiz = cotiz;
        }

        // ---- Contrato defensivo del array CbtesAsoc [RH4-002 round 4] ----
        //
        // Por construccion, una NC parcial Fase 2 emite exactamente 1 <CbteAsoc>
        // apuntando a la factura origen (ver el path de emision AfipService:830-838).
        // Entonces el response de FECompConsultar para esa NC deberia traer
        // exactamente 1 item bajo <CbtesAsoc>. Pero NO confiamos a ciegas:
        //
        //   - 1 item  -> caso normal: mapeamos su <Nro> a CbteAsoc.
        //   - 0 items -> CbteAsoc queda null. La capa de recovery lo lee como
        //                "mismatch" y reintenta limpio (seguro: no deriva CAE de la nada).
        //   - N>1     -> ARCA cambio comportamiento o el comprobante no es lo que creemos.
        //                Logueamos warning y dejamos CbteAsoc en null. NO elegimos uno
        //                "a ojo": derivar un CAE del comprobante asociado equivocado es
        //                peor (riesgo fiscal real) que un reintento extra del job.
        //
        // Nota: descendemos solo sobre el nodo <CbteAsoc> que cuelga de <CbtesAsoc>,
        // no sobre cualquier <CbteAsoc> del documento, para no contar nodos de otra parte.
        var cbtesAsocContainer = result.Element(XName.Get("CbtesAsoc", ns));
        if (cbtesAsocContainer != null)
        {
            var cbteAsocItems = cbtesAsocContainer
                .Elements(XName.Get("CbteAsoc", ns))
                .ToList();

            if (cbteAsocItems.Count == 1)
            {
                var nroText = cbteAsocItems[0].Element(XName.Get("Nro", ns))?.Value;
                if (int.TryParse(nroText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var nro))
                {
                    details.CbteAsoc = nro;
                }
                // Si <Nro> no parsea, CbteAsoc queda null -> mismatch -> retry limpio.
            }
            else if (cbteAsocItems.Count > 1)
            {
                // Defensa proactiva: ARCA nunca deberia devolver multiples para nuestra NC.
                // No exponemos montos ni CUIT en el log, solo la cantidad inesperada.
                logger.LogWarning(
                    "FECompConsultar devolvio multiples CbtesAsoc inesperado " +
                    "(Count={Count}). Se deja CbteAsoc en null para forzar retry limpio.",
                    cbteAsocItems.Count);
            }
            // Count == 0 -> CbteAsoc queda en null (valor inicial), sin warning.
        }
    }



    // ---- FC1.3.F2.2 (sub-tareas A.7.0 / A.2 / A.3, RH3-001 / RH3-004 round 4) ----
    // Metodos de CONSULTA a ARCA para el anti-duplicados del job de NC parcial.
    // NO emiten nada. Reutilizan el numerador interno (GetNextVoucherNumber) y el
    // detalle (GetVoucherDetails) que ya existen, encapsulando la carga de settings
    // + auth para no exponer internals al caller.

    /// <summary>
    /// FC1.3.F2.2 (sub-tarea A.7.0, RH3-004 round 4): ultimo numero autorizado por
    /// ARCA para el punto de venta + tipo dados (= proximo numerador - 1).
    /// </summary>
    public async Task<int> GetLastAuthorizedNumeroAsync(int puntoVenta, int cbteTipo, CancellationToken ct)
    {
        // Leemos settings TRACKED (no GetSettingsAsync, que es AsNoTracking y ademas no
        // refresca el token) siguiendo el mismo patron que el path de emision
        // (ProcessInvoiceJob:742-749): cargar settings -> EnsureAuth -> GetNextVoucherNumber.
        // EnsureAuth garantiza un token WSAA valido; sin el, GetNextVoucherNumber le pega
        // a ARCA con un token vencido y rebota.
        var settings = await _context.AfipSettings.FirstOrDefaultAsync(ct);
        if (settings == null) throw new InvalidOperationException("AFIP no esta configurado.");

        await EnsureAuth(settings);

        // GetNextVoucherNumber devuelve el PROXIMO numero a emitir; el ultimo autorizado
        // es ese menos uno. Si ARCA todavia no autorizo ninguno (proximo == 1),
        // el ultimo autorizado es 0 (no hay comprobante previo).
        var proximo = await GetNextVoucherNumber(settings, cbteTipo);
        return proximo - 1;
    }

    /// <summary>
    /// FC1.3.F2.2 (sub-tareas A.2 / A.3, RH3-001 round 4): consulta compuesta para el
    /// stale key recovery. Decide <c>Found</c> comparando el numerador actual de ARCA
    /// contra el snapshot que el job tomo ANTES de postear (<paramref name="lastSeenNumeroBeforePost"/>).
    /// Si avanzo, trae el detalle del ultimo comprobante.
    /// </summary>
    public async Task<ArcaCompoundQueryResult> QueryLastAuthorizedWithDetailsAsync(
        int puntoVenta,
        int cbteTipo,
        int? lastSeenNumeroBeforePost,
        CancellationToken ct)
    {
        // Paso 1: ultimo numero autorizado AHORA (reusa el helper publico, que ya hace
        // settings + auth adentro).
        var ultimo = await GetLastAuthorizedNumeroAsync(puntoVenta, cbteTipo, ct);

        // Paso 2: si no teniamos snapshot (key vieja sin LastSeenNumeroBeforePost) o el
        // numerador NO avanzo respecto al snapshot, el POST nunca viajo -> Found=false.
        // El job lo trata como huerfana: borra la key y reintenta limpio.
        if (lastSeenNumeroBeforePost == null || ultimo <= lastSeenNumeroBeforePost.Value)
        {
            return new ArcaCompoundQueryResult(
                Found: false,
                LastNumero: ultimo,
                Cae: null,
                CbteAsoc: null,
                IssuedAt: null,
                ImporteTotal: null,
                MonId: null,
                MonCotiz: null);
        }

        // Paso 3: el numerador avanzo -> hay un comprobante nuevo que inspeccionar.
        // Traemos su detalle (incluye los campos extra que parseamos en A.3.1).
        var detail = await GetVoucherDetails(cbteTipo, puntoVenta, ultimo);

        // Si por algun motivo el detalle no viene (timeout, comprobante no consultable),
        // degradamos a Found=false: mejor reintentar limpio que afirmar que el POST viajo
        // sin tener el comprobante en mano.
        if (detail == null)
        {
            _logger.LogWarning(
                "QueryLastAuthorizedWithDetailsAsync: el numerador avanzo a {Ultimo} pero " +
                "FECompConsultar no devolvio detalle. Se degrada a Found=false.",
                ultimo);

            return new ArcaCompoundQueryResult(
                Found: false,
                LastNumero: ultimo,
                Cae: null,
                CbteAsoc: null,
                IssuedAt: null,
                ImporteTotal: null,
                MonId: null,
                MonCotiz: null);
        }

        return new ArcaCompoundQueryResult(
            Found: true,
            LastNumero: ultimo,
            Cae: detail.Cae,
            CbteAsoc: detail.CbteAsoc,
            IssuedAt: detail.IssuedAt,
            ImporteTotal: detail.ImporteTotal,
            MonId: detail.MonId,
            MonCotiz: detail.MonCotiz);
    }

    // ========================================================================================
    // IDEMPOTENCIA ANTI-DOBLE-CAE de ProcessInvoiceJob (2026-06-25)
    //
    // Mismo mecanismo que la NC parcial (InvoiceService.ProcessPartialCreditNoteJob): tabla
    // ArcaIdempotencyKeys + "stale key recovery". La diferencia es el OBJETO sobre el que se
    // ancla la clave: aca es la Invoice (factura de venta, NC total o ND) identificada por su
    // Id, no una liquidacion de NC parcial. Por eso estos helpers son propios de este job y no
    // reusan las firmas de InvoiceService (que reciben PartialCreditNoteEmissionInput), pero
    // siguen EXACTAMENTE el mismo patron y la misma tabla/infra.
    // ========================================================================================

    /// <summary>
    /// Construye la clave de idempotencia deterministica de una emision de Invoice (factura de
    /// venta, NC total o ND). Dos despachos del job para la MISMA Invoice producen el MISMO hash,
    /// y el indice UNIQUE de <c>ArcaIdempotencyKeys</c> rechaza el segundo INSERT.
    ///
    /// <para>El prefijo <c>inv|</c> evita cualquier colision teorica con las keys de la NC parcial
    /// (que se arman con otra combinacion de campos): ambas familias conviven en la misma tabla.</para>
    /// </summary>
    internal static string BuildInvoiceIdempotencyKey(int invoiceId, int tipoComprobante, int puntoDeVenta)
    {
        // El invoiceId ya es unico por si solo; sumamos tipo + punto de venta para que la key
        // quede autoexplicativa y robusta ante cualquier reuso futuro de Ids entre entornos.
        string raw = string.Create(
            CultureInfo.InvariantCulture,
            $"inv|{invoiceId}|{tipoComprobante}|{puntoDeVenta}");

        byte[] hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw));

        // La columna Key es varchar(64); SHA256 en hex son 64 chars exactos.
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Intenta insertar la clave de idempotencia ANTES del POST a ARCA. Devuelve <c>true</c> si
    /// el INSERT entro, <c>false</c> si choco con el indice UNIQUE (ya existe una key con ese
    /// valor: otra corrida o una huerfana de un crash previo).
    ///
    /// <para>Ante el choque, dejamos el ChangeTracker limpio (Detached) para que el context siga
    /// usable por el recovery — mismo cuidado que <c>InvoiceService.TryInsertIdempotencyKeyAsync</c>.</para>
    /// </summary>
    private async Task<bool> TryInsertInvoiceIdempotencyKeyAsync(
        string idemKey,
        int lastSeenNumeroBeforePost,
        CancellationToken ct)
    {
        var entity = new ArcaIdempotencyKey
        {
            Key = idemKey,
            JobId = null,
            CreatedAt = DateTime.UtcNow,
            ResolvedAt = null,
            LastSeenNumeroBeforePost = lastSeenNumeroBeforePost,
        };

        _context.ArcaIdempotencyKeys.Add(entity);
        try
        {
            await _context.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            // Ya existe una key con ese valor. Limpiamos el tracker para que el context quede
            // usable por el recovery (si re-guardamos con la entidad Added pegada, EF la
            // re-insertaria y volveria a fallar).
            _context.Entry(entity).State = EntityState.Detached;
            return false;
        }
    }

    /// <summary>
    /// Marca una clave de idempotencia como resuelta (intento terminado: exito o rechazo
    /// definitivo). Un re-despacho que vea la key resuelta NO re-POSTea. No hace SaveChanges:
    /// lo deja al SaveChanges del caller para que la resolucion de la key y el estado final de
    /// la Invoice se persistan de forma atomica.
    ///
    /// <para>Si la key no existe (caso defensivo: alguien la borro), no hace nada.</para>
    /// </summary>
    private async Task ResolveInvoiceIdempotencyKeyAsync(string idemKey, CancellationToken ct)
    {
        var key = await _context.ArcaIdempotencyKeys.FirstOrDefaultAsync(k => k.Key == idemKey, ct);
        if (key is not null && key.ResolvedAt is null)
        {
            key.ResolvedAt = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Recovery de una clave de idempotencia que ya existe (re-despacho del job). Decide entre
    /// tres caminos, mismo arbitraje que el de la NC parcial
    /// (<c>InvoiceService.HandleStaleIdempotencyKeyAsync</c>):
    /// <list type="bullet">
    ///   <item><b>Key reciente o ya resuelta</b>: otro intento la procesa AHORA, o ya termino.
    ///   No re-POSTear. Devuelve <c>true</c>.</item>
    ///   <item><b>Key huerfana + ARCA emitio el comprobante</b>: el POST viajo en una corrida
    ///   anterior. Adoptamos el CAE/numero en ESTA Invoice, resolvemos la key y NO re-POSTeamos.
    ///   Devuelve <c>true</c>.</item>
    ///   <item><b>Key huerfana + el POST nunca viajo</b>: borramos la key y devolvemos
    ///   <c>false</c> para que el caller reintente la emision limpia.</item>
    /// </list>
    ///
    /// <para><b>Como matchea el comprobante en ARCA</b>:
    /// <list type="bullet">
    ///   <item>NC total / ND (tienen factura origen): por <c>CbteAsoc == OriginalInvoice.NumeroComprobante</c>
    ///   + monto, igual que la NC parcial. Es el match preciso.</item>
    ///   <item>Factura de venta (sin comprobante asociado): por monto + moneda. Es lo mas preciso
    ///   posible para una factura suelta. Si el monto/moneda no coinciden, se trata como mismatch
    ///   y se reintenta limpio (nunca se adopta un CAE de otro comprobante).</item>
    /// </list>
    /// </para>
    /// </summary>
    private async Task<bool> HandleStaleInvoiceIdempotencyKeyAsync(
        string idemKey,
        Invoice invoice,
        AfipSettings settings,
        int staleThresholdMinutes,
        decimal roundingTolerance,
        CancellationToken ct)
    {
        var existingKey = await _context.ArcaIdempotencyKeys
            .FirstOrDefaultAsync(k => k.Key == idemKey, ct);

        // Carrera: la key existia cuando el INSERT choco pero ya no esta (otro intento la
        // resolvio + housekeeping la borro entre medio). No re-POSTear a ciegas.
        if (existingKey is null)
        {
            _logger.LogWarning(
                "ProcessInvoiceJob: IdempotencyKey desaparecio entre el INSERT fallido y la lectura " +
                "para Invoice {InvoiceId}. Otro intento la resolvio. No se re-emite.",
                invoice.Id);
            return true;
        }

        // Ya resuelta: el intento anterior termino (exito o rechazo terminal). No re-emitir.
        if (existingKey.ResolvedAt is not null)
        {
            _logger.LogWarning(
                "ProcessInvoiceJob: IdempotencyKey ya resuelta (ResolvedAt={ResolvedAt:o}) para " +
                "Invoice {InvoiceId}. No se re-emite.",
                existingKey.ResolvedAt, invoice.Id);
            return true;
        }

        double ageMinutes = (DateTime.UtcNow - existingKey.CreatedAt).TotalMinutes;

        // Key reciente: otro intento esta en vuelo. No es nuestro turno.
        if (ageMinutes <= staleThresholdMinutes)
        {
            _logger.LogWarning(
                "ProcessInvoiceJob: IdempotencyKey activa, otro job procesando " +
                "(age={Age:F1}min, umbral={Umbral}min) para Invoice {InvoiceId}. " +
                "Se resuelve en el proximo despacho.",
                ageMinutes, staleThresholdMinutes, invoice.Id);
            return true;
        }

        // Key huerfana (vieja + sin resolver): consultamos ARCA con el numerador REAL capturado
        // antes del POST de la corrida anterior. Si avanzo, hay un comprobante para inspeccionar.
        var arcaResult = await QueryLastAuthorizedWithDetailsAsync(
            puntoVenta: settings.PuntoDeVenta,
            cbteTipo: invoice.TipoComprobante,
            lastSeenNumeroBeforePost: existingKey.LastSeenNumeroBeforePost,
            ct: ct);

        bool arcaMatchesOurInvoice = ArcaResultMatchesInvoice(arcaResult, invoice, roundingTolerance);

        if (arcaMatchesOurInvoice)
        {
            // El POST viajo en una corrida anterior y ARCA emitio el comprobante que matchea
            // ESTA factura. Adoptamos el CAE ya emitido en vez de re-emitir (anti-doble-CAE).
            invoice.CAE = arcaResult.Cae;
            invoice.Resultado = "A";
            invoice.NumeroComprobante = arcaResult.LastNumero ?? invoice.NumeroComprobante;
            if (arcaResult.IssuedAt.HasValue)
            {
                invoice.IssuedAt = DateTime.SpecifyKind(arcaResult.IssuedAt.Value, DateTimeKind.Utc);
            }
            invoice.Observaciones = null;

            existingKey.ResolvedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(ct);

            _logger.LogWarning(
                "ProcessInvoiceJob idempotency recovery: derivado CAE de comprobante ya emitido " +
                "(no se re-emitio). InvoiceId={InvoiceId} CAE={Cae}",
                invoice.Id, arcaResult.Cae);
            _logger.LogInformation(
                "metric:Afip.ProcessInvoiceJob.RecoveredFromStaleKey | invoiceId={InvoiceId}",
                invoice.Id);

            // La reversion economica de una NC se aplica igual que en el path normal: el
            // comprobante quedo emitido (Resultado="A"), recuperado o no.
            if (IsCreditNote(invoice.TipoComprobante))
            {
                await ApplyCreditNoteEconomicReversalAsync(invoice.Id);
            }

            return true;
        }

        // El POST nunca viajo (Found=false) o el comprobante encontrado NO matchea esta factura.
        // Borramos la key huerfana y dejamos el sistema limpio para re-emitir.
        _context.ArcaIdempotencyKeys.Remove(existingKey);
        await _context.SaveChangesAsync(ct);

        _logger.LogWarning(
            "ProcessInvoiceJob idempotency: stale key removida (huerfana de crash previo o mismatch " +
            "de numerador) para Invoice {InvoiceId}. ArcaFound={Found}. Reintento limpio.",
            invoice.Id, arcaResult.Found);

        return false;
    }

    /// <summary>
    /// Decide si el comprobante que ARCA reporta como ultimo autorizado corresponde a ESTA
    /// Invoice. El criterio depende de si la factura tiene comprobante asociado:
    /// <list type="bullet">
    ///   <item>Con origen (NC total / ND): match fuerte por <c>CbteAsoc</c> apuntando a la
    ///   factura origen + monto dentro de la tolerancia.</item>
    ///   <item>Sin origen (factura de venta): match por monto + moneda (lo mas preciso posible
    ///   para un comprobante suelto). Nunca se adopta un CAE si el monto/moneda no coinciden.</item>
    /// </list>
    /// </summary>
    internal static bool ArcaResultMatchesInvoice(
        ArcaCompoundQueryResult arcaResult,
        Invoice invoice,
        decimal roundingTolerance)
    {
        if (!arcaResult.Found)
        {
            return false;
        }

        bool amountMatches =
            Math.Abs((arcaResult.ImporteTotal ?? -1m) - invoice.ImporteTotal) <= roundingTolerance;

        if (!amountMatches)
        {
            return false;
        }

        bool hasAssociated = invoice.OriginalInvoiceId.HasValue && invoice.OriginalInvoice != null;
        if (hasAssociated)
        {
            // NC total / ND: el comprobante de ARCA tiene que apuntar EXACTAMENTE a nuestra
            // factura origen. Es el match preciso que evita adoptar el CAE de otra NC/ND del
            // mismo monto.
            return arcaResult.CbteAsoc == invoice.OriginalInvoice!.NumeroComprobante;
        }

        // Factura de venta suelta: ademas del monto, exigimos que coincida la moneda. Si ARCA
        // no reporto moneda, no la podemos contradecir: aceptamos solo por monto (degradacion
        // segura, ya filtrada por el monto exacto + numerador avanzado).
        if (string.IsNullOrWhiteSpace(arcaResult.MonId))
        {
            return true;
        }

        return string.Equals(arcaResult.MonId, invoice.MonId, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Detecta si una <see cref="DbUpdateException"/> proviene de una violacion del indice UNIQUE
    /// de Postgres. Mismo patron que <c>InvoiceService.IsUniqueConstraintViolation</c> y
    /// <c>BookingCancellationService.IsUniqueConstraintViolation</c>.
    /// </summary>
    private static bool IsUniqueConstraintViolation(DbUpdateException ex)
    {
        return ex.InnerException is Npgsql.PostgresException pg
            && pg.SqlState == Npgsql.PostgresErrorCodes.UniqueViolation;
    }

    // ADR-024 (2026-06-12): GetConditionIvaId fue ELIMINADO. Tenia dos problemas que la spec ADR-024
    // §4.1 senalo: (1) nunca se le pasaba la condicion del cliente (se llamaba con null literal, asi que
    // TODO receptor salia Consumidor Final); (2) su comentario listaba un codigo "11: RI Agente de
    // Percepcion" que NO existe en la tabla CondicionIVAReceptorId verificada (RG 5616). La resolucion
    // ahora vive en ArcaReceptorResolver.ResolveCondicionIva, que SI lee Customer.TaxConditionId del
    // snapshot y solo emite codigos de la tabla §4.1.

    private decimal ParseDecimal(string? val)
    {
        if (string.IsNullOrEmpty(val)) return 0;
        if (decimal.TryParse(val, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal d)) return d;
        return 0;
    }

    // --- Padron A5 Implementation ---

    public async Task<object?> GetPersonaDetailsAsync(long cuit)
    {
        var settings = await _context.AfipSettings.FirstOrDefaultAsync();
        if (settings == null || GetCertificateData(settings) == null)
            throw new Exception("AFIP no configurado o certificado faltante.");

        // We need a specific token for ws_sr_padron_a5, not wsfe.
        // We will authenticate dynamically for this call.
        var (token, sign) = await GetPadronAuth(settings);

        var url = settings.IsProduction ? WsPadronUrlProd : WsPadronUrlDev;
        var action = "http://a5.soap.ws.server.puc.sr/"; // Note: AFIP SOAP actions can be tricky. Usually for A5 it's not strictly enforced in headers, but we'll set it empty if it fails.
        // The namespace for PersonaServiceA5 is usually http://a5.soap.ws.server.puc.sr/
        
        var soapEnv = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<soapenv:Envelope xmlns:soapenv=""http://schemas.xmlsoap.org/soap/envelope/"" xmlns:a5=""http://a5.soap.ws.server.puc.sr/"">
   <soapenv:Header/>
   <soapenv:Body>
      <a5:getPersona>
         <sign>{sign}</sign>
         <token>{token}</token>
         <cuitRepresentada>{settings.Cuit}</cuitRepresentada>
         <idPersona>{cuit}</idPersona>
      </a5:getPersona>
   </soapenv:Body>
</soapenv:Envelope>";

        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("SOAPAction", "\"\""); // Usually empty or specific action
        request.Content = new StringContent(soapEnv, Encoding.UTF8, "text/xml");

        var response = await _httpClient.SendAsync(request);
        var responseXml = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
             _logger.LogError("Error from ws_sr_padron_a5: {Response}", responseXml);
             return null;
        }

        var doc = XDocument.Parse(responseXml);
        
        // Check for specific padron faults within the success response
        var errorNode = doc.Descendants(XName.Get("errorConstancia", "http://a5.soap.ws.server.puc.sr/")).FirstOrDefault();
        if (errorNode != null)
        {
            var errError = errorNode.Element(XName.Get("error", "http://a5.soap.ws.server.puc.sr/"))?.Value;
            _logger.LogWarning("Padron Error for CUIT {Cuit}: {Error}", cuit, errError);
            return null; // Not found or error
        }

        var personaReturn = doc.Descendants(XName.Get("personaReturn", "http://a5.soap.ws.server.puc.sr/")).FirstOrDefault();
        if (personaReturn == null) return null;

        var datosGenerales = personaReturn.Element(XName.Get("datosGenerales", "http://a5.soap.ws.server.puc.sr/"));
        if (datosGenerales == null) return null;

        var nombre = datosGenerales.Element(XName.Get("nombre", "http://a5.soap.ws.server.puc.sr/"))?.Value;
        var apellido = datosGenerales.Element(XName.Get("apellido", "http://a5.soap.ws.server.puc.sr/"))?.Value;
        var razonSocial = datosGenerales.Element(XName.Get("razonSocial", "http://a5.soap.ws.server.puc.sr/"))?.Value;
        var tipoPersona = datosGenerales.Element(XName.Get("tipoPersona", "http://a5.soap.ws.server.puc.sr/"))?.Value; // FISICA or JURIDICA
        var estadoClave = datosGenerales.Element(XName.Get("estadoClave", "http://a5.soap.ws.server.puc.sr/"))?.Value;

        // Try to determine tax condition from datosMonotributo or datosRegimenGeneral
        string taxCondDesc = "Consumidor Final";
        int taxCondId = 5;

        var monotributoNode = personaReturn.Element(XName.Get("datosMonotributo", "http://a5.soap.ws.server.puc.sr/"));
        if (monotributoNode != null)
        {
            taxCondDesc = "Monotributo";
            taxCondId = 6;
        }
        else
        {
            var regimenGeneral = personaReturn.Element(XName.Get("datosRegimenGeneral", "http://a5.soap.ws.server.puc.sr/"));
            if (regimenGeneral != null)
            {
                var impuestos = regimenGeneral.Element(XName.Get("impuesto", "http://a5.soap.ws.server.puc.sr/")); // Note: it might be an array of <impuesto>
                if (impuestos != null)
                {
                    // If they have any regimen general, usually Responsable Inscripto or Exento
                    // More complex parsing needed for exact IVA status, robust default helps:
                    taxCondDesc = "Responsable Inscripto";
                    taxCondId = 1;

                    // Try to find if IVA Exento (idImpuesto = 32 usually)
                    var allImpuestos = regimenGeneral.Elements(XName.Get("impuesto", "http://a5.soap.ws.server.puc.sr/"));
                    foreach (var imp in allImpuestos)
                    {
                        var desc = imp.Element(XName.Get("descripcionImpuesto", "http://a5.soap.ws.server.puc.sr/"))?.Value?.ToLower() ?? "";
                        if (desc.Contains("exento"))
                        {
                            taxCondDesc = "Exento";
                            taxCondId = 4;
                            break;
                        }
                    }
                }
            }
        }

        return new
        {
            Id = cuit.ToString(),
            Nombre = nombre,
            Apellido = apellido,
            RazonSocial = razonSocial,
            TipoPersona = tipoPersona,
            Estado = estadoClave,
            TaxCondition = taxCondDesc,
            TaxConditionId = taxCondId
        };
    }

    private async Task<(string token, string sign)> GetPadronAuth(AfipSettings settings)
    {
        // 1. Check valid cached token
        if (!string.IsNullOrEmpty(GetPadronToken(settings)) && !string.IsNullOrEmpty(GetPadronSign(settings)))
        {
            if (settings.PadronTokenExpiration.HasValue && settings.PadronTokenExpiration.Value > DateTime.UtcNow.AddMinutes(5))
            {
                return (GetPadronToken(settings)!, GetPadronSign(settings)!);
            }
        }

        var cert = new X509Certificate2(GetCertificateData(settings), GetCertificatePassword(settings), X509KeyStorageFlags.Exportable);
        var uniqueId = (uint)(DateTime.UtcNow.Ticks % uint.MaxValue);
        var argentinaTime = DateTime.UtcNow.AddHours(-3);
        
        var xml = new XElement("loginTicketRequest",
            new XAttribute("version", "1.0"),
            new XElement("header",
                new XElement("uniqueId", uniqueId),
                new XElement("generationTime", argentinaTime.AddMinutes(-10).ToString("yyyy-MM-ddTHH:mm:ss")),
                new XElement("expirationTime", argentinaTime.AddMinutes(+10).ToString("yyyy-MM-ddTHH:mm:ss"))
            ),
            new XElement("service", "ws_sr_padron_a5") // IMPORTANT: Target service is different
        );

        var cms = new SignedCms(new ContentInfo(Encoding.UTF8.GetBytes(xml.ToString())));
        var signer = new CmsSigner(cert);
        signer.IncludeOption = X509IncludeOption.EndCertOnly;
        cms.ComputeSignature(signer);
        var signatureBase64 = Convert.ToBase64String(cms.Encode());

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
        request.Headers.Add("SOAPAction", "\"\""); 
        request.Content = new StringContent(soapEnv, Encoding.UTF8, "text/xml");

        var response = await _httpClient.SendAsync(request);
        var responseXml = await response.Content.ReadAsStringAsync();

        XDocument doc;
        try 
        {
            doc = XDocument.Parse(responseXml);
        }
        catch
        {
            if (!response.IsSuccessStatusCode)
                throw new Exception($"WSAA Error Padron: {response.StatusCode} {responseXml}");
            throw;
        }

        // Check for faults
        var fault = doc.Descendants(XName.Get("Fault", "http://schemas.xmlsoap.org/soap/envelope/")).FirstOrDefault();
        if (fault != null)
        {
            var faultCode = fault.Element("faultcode")?.Value;
            var faultString = fault.Element("faultstring")?.Value;

            if (faultCode != null && faultCode.Contains("alreadyAuthenticated"))
            {
                 throw new Exception("AFIP Error: Ya existe un token generado recientemente para el Padron (posiblemente un intento anterior que no se guardó). Por seguridad de AFIP, debés esperar aproximadamente 12 horas para que el ticket actual expire y el sistema pueda generar y guardar uno nuevo automáticamente.");
            }
            throw new Exception($"Error de Autenticación AFIP Padron: {faultString}");
        }

        if (!response.IsSuccessStatusCode)
            throw new Exception($"Error authenticating with WSAA for ws_sr_padron_a5: {response.StatusCode} {responseXml}");

        var loginCmsReturn = doc.Descendants().FirstOrDefault(x => x.Name.LocalName == "loginCmsReturn")?.Value;
        if (string.IsNullOrEmpty(loginCmsReturn))
            throw new Exception("WSAA Empty Response for ws_sr_padron_a5");

        var ticket = XDocument.Parse(loginCmsReturn);
        var token = ticket.Descendants("token").First().Value;
        var sign = ticket.Descendants("sign").First().Value;
        
        var expirationStr = ticket.Descendants("expirationTime").First().Value;
        var expirationLocal = DateTime.Parse(expirationStr);
        var expirationUtc = DateTime.SpecifyKind(expirationLocal.AddHours(3), DateTimeKind.Utc);

        if (settings.IsProduction)
        {
            settings.ProdPadronToken = _sensitiveDataProtector.ProtectString(token);
            settings.ProdPadronSign = _sensitiveDataProtector.ProtectString(sign);
            settings.ProdPadronTokenExpiration = expirationUtc;
        }
        else
        {
            settings.PadronToken = _sensitiveDataProtector.ProtectString(token);
            settings.PadronSign = _sensitiveDataProtector.ProtectString(sign);
            settings.PadronTokenExpiration = expirationUtc;
        }

        try
        {
            await _context.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error guardando token de Padron en BD");
        }

        return (token, sign);
    }
}
