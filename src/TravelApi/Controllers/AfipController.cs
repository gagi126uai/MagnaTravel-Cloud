using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/afip")]
[Authorize]
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
    public async Task<ActionResult<AfipSettings>> GetSettings()
    {
        var settings = await _afipService.GetSettingsAsync();
        if (settings == null) return NotFound();
        
        // Hide sensitive data
        settings.CertificatePassword = null; 
        settings.Token = null;
        settings.Sign = null;

        return settings;
    }

    public class AfipSettingsRequest
    {
        public long Cuit { get; set; }
        public int PuntoDeVenta { get; set; }
        public bool IsProduction { get; set; }
        public string TaxCondition { get; set; } = "Responsable Inscripto";
        public IFormFile? Certificate { get; set; }
        public string? Password { get; set; }
    }

    [HttpPost("settings")]
    public async Task<ActionResult<AfipSettings>> UpdateSettings([FromForm] AfipSettingsRequest request)
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

        try
        {
            var settings = await _afipService.UpdateSettingsAsync(
                request.Cuit, 
                request.PuntoDeVenta, 
                request.IsProduction, 
                request.TaxCondition, 
                certData, 
                certFileName, 
                request.Password
            );

            // Hide sensitive data before returning
            settings.CertificatePassword = null;
            settings.Token = null;
            settings.Sign = null;

            return Ok(settings);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            return BadRequest($"Error validando certificado: {ex.Message}");
        }
    }
}
