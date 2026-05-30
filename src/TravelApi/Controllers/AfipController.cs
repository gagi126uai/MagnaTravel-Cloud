using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using TravelApi.Application.Interfaces;
using TravelApi.Application.DTOs;
using TravelApi.Authorization;
using TravelApi.Domain.Entities;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/afip")]
[Authorize]
[EnableRateLimiting("afip")]
public class AfipController : ControllerBase
{
    private readonly IAfipService _afipService;
    private readonly IOperationalFinanceSettingsService _operationalFinanceSettingsService;

    public AfipController(
        IAfipService afipService,
        IOperationalFinanceSettingsService operationalFinanceSettingsService)
    {
        _afipService = afipService;
        _operationalFinanceSettingsService = operationalFinanceSettingsService;
    }

    [HttpGet("status")]
    [RequirePermission(Permissions.CobranzasInvoice)]
    public async Task<IActionResult> GetStatus()
    {
        var status = await _afipService.GetStatus();
        return Ok(new { status });
    }

    // El response (AfipSettingsResponse) NO expone secretos: cert binario y password
    // nunca salen, los tokens se convierten a booleanos HasXxx. Gateamos con
    // CobranzasInvoice porque el modal de emision de factura necesita TaxCondition
    // para decidir si discrimina IVA (Monotributo/Exento -> factura C sin IVA).
    [HttpGet("settings")]
    [RequirePermission(Permissions.CobranzasInvoice)]
    public async Task<ActionResult<AfipSettingsResponse>> GetSettings(CancellationToken cancellationToken)
    {
        var settings = await _afipService.GetSettingsAsync();
        if (settings == null) return NotFound();

        var financeSettings = await _operationalFinanceSettingsService.GetEntityAsync(cancellationToken);
        return Ok(MapResponse(settings, financeSettings.EnableMultiCurrencyInvoicing, financeSettings.EnableSoldToSettleStates));
    }

    public class AfipSettingsRequest
    {
        public long Cuit { get; set; }
        public int PuntoDeVenta { get; set; }
        public bool IsProduction { get; set; }
        public string TaxCondition { get; set; } = "Responsable Inscripto";
        
        public IFormFile? Certificate { get; set; }
        public string? Password { get; set; }
        
        public IFormFile? ProdCertificate { get; set; }
        public string? ProdPassword { get; set; }
    }

    [HttpPost("settings")]
    [RequirePermission(Permissions.ConfiguracionAfip)]
    public async Task<ActionResult<AfipSettingsResponse>> UpdateSettings([FromForm] AfipSettingsRequest request, CancellationToken cancellationToken)
    {
        const long maxCertSizeBytes = 100 * 1024; // 100 KB

        byte[]? certData = null;
        string? certFileName = null;
        if (request.Certificate != null)
        {
            if (request.Certificate.Length > maxCertSizeBytes)
                return BadRequest(new { message = "El certificado excede el tamaño máximo permitido (100 KB)." });
            using var memoryStream = new MemoryStream();
            await request.Certificate.CopyToAsync(memoryStream);
            certData = memoryStream.ToArray();
            certFileName = request.Certificate.FileName;
        }

        byte[]? prodCertData = null;
        string? prodCertFileName = null;
        if (request.ProdCertificate != null)
        {
            if (request.ProdCertificate.Length > maxCertSizeBytes)
                return BadRequest(new { message = "El certificado de producción excede el tamaño máximo permitido (100 KB)." });
            using var memoryStream = new MemoryStream();
            await request.ProdCertificate.CopyToAsync(memoryStream);
            prodCertData = memoryStream.ToArray();
            prodCertFileName = request.ProdCertificate.FileName;
        }

        try
        {
            var settings = await _afipService.UpdateSettingsAsync(
                request.Cuit, 
                request.PuntoDeVenta, 
                request.IsProduction, 
                request.TaxCondition, 
                certData, 
                certFileName, 
                request.Password,
                prodCertData,
                prodCertFileName,
                request.ProdPassword
            );

            // Los flags operativos no se tocan desde este endpoint (son solo lectura), pero los
            // proyectamos en la respuesta para que el shape sea identico al del GET /afip/settings.
            var financeSettings = await _operationalFinanceSettingsService.GetEntityAsync(cancellationToken);
            return Ok(MapResponse(settings, financeSettings.EnableMultiCurrencyInvoicing, financeSettings.EnableSoldToSettleStates));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "No se pudo validar la configuracion AFIP.");
        }
    }

    private static AfipSettingsResponse MapResponse(AfipSettings settings, bool enableMultiCurrencyInvoicing, bool enableSoldToSettleStates)
    {
        return new AfipSettingsResponse
        {
            EnableMultiCurrencyInvoicing = enableMultiCurrencyInvoicing,
            EnableSoldToSettleStates = enableSoldToSettleStates,

            Cuit = settings.Cuit,
            PuntoDeVenta = settings.PuntoDeVenta,
            IsProduction = settings.IsProduction,
            TaxCondition = settings.TaxCondition,
            
            HasCertificate = settings.CertificateData != null && settings.CertificateData.Length > 0,
            CertificateFileName = settings.CertificatePath,
            HasAuthToken = !string.IsNullOrWhiteSpace(settings.Token),
            HasPadronToken = !string.IsNullOrWhiteSpace(settings.PadronToken),
            
            HasProdCertificate = settings.ProdCertificateData != null && settings.ProdCertificateData.Length > 0,
            ProdCertificateFileName = settings.ProdCertificatePath,
            HasProdAuthToken = !string.IsNullOrWhiteSpace(settings.ProdToken),
            HasProdPadronToken = !string.IsNullOrWhiteSpace(settings.ProdPadronToken)
        };
    }
}
