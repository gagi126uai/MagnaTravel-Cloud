using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using TravelApi.Application.Interfaces;
using TravelApi.Application.DTOs;
using TravelApi.Domain.Entities;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/afip")]
[Authorize(Roles = "Admin")]
[EnableRateLimiting("afip")]
public class AfipController : ControllerBase
{
    private readonly IAfipService _afipService;

    public AfipController(IAfipService afipService)
    {
        _afipService = afipService;
    }

    [HttpGet("status")]
    public async Task<IActionResult> GetStatus()
    {
        var status = await _afipService.GetStatus();
        return Ok(new { status });
    }

    [HttpGet("settings")]
    public async Task<ActionResult<AfipSettingsResponse>> GetSettings()
    {
        var settings = await _afipService.GetSettingsAsync();
        if (settings == null) return NotFound();

        return Ok(MapResponse(settings));
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
    public async Task<ActionResult<AfipSettingsResponse>> UpdateSettings([FromForm] AfipSettingsRequest request)
    {
        byte[]? certData = null;
        string? certFileName = null;
        if (request.Certificate != null)
        {
            using var memoryStream = new MemoryStream();
            await request.Certificate.CopyToAsync(memoryStream);
            certData = memoryStream.ToArray();
            certFileName = request.Certificate.FileName;
        }

        byte[]? prodCertData = null;
        string? prodCertFileName = null;
        if (request.ProdCertificate != null)
        {
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

            return Ok(MapResponse(settings));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: $"No se pudo validar la configuracion AFIP: {ex.Message}");
        }
    }

    private static AfipSettingsResponse MapResponse(AfipSettings settings)
    {
        return new AfipSettingsResponse
        {
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
