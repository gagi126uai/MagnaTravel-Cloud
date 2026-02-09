using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TravelApi.Data;
using TravelApi.Models;
using TravelApi.Services;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/afip")]
[Authorize]
public class AfipController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IAfipService _afipService;

    public AfipController(AppDbContext context, IAfipService afipService)
    {
        _context = context;
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
        var settings = await _context.AfipSettings.FirstOrDefaultAsync();
        if (settings == null) return NotFound();
        
        // Hide sensitive data
        settings.CertificatePassword = null; 
        settings.Token = null;
        settings.Sign = null;

        return settings;
    }

    [HttpPost("settings")]
    public async Task<ActionResult<AfipSettings>> UpdateSettings([FromForm] long cuit, [FromForm] int puntoDeVenta, [FromForm] bool isProduction, [FromForm] IFormFile? certificate, [FromForm] string? password)
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

        if (certificate != null)
        {
            using (var memoryStream = new MemoryStream())
            {
                await certificate.CopyToAsync(memoryStream);
                var certData = memoryStream.ToArray();
                
                // Validate before saving
                var certPassword = !string.IsNullOrEmpty(password) ? password : settings.CertificatePassword;
                if (!await _afipService.ValidateCertificate(certData, certPassword))
                {
                    return BadRequest("El certificado es inválido o la contraseña es incorrecta. Verifique que sea un archivo .pfx válido.");
                }

                settings.CertificateData = certData;
                settings.CertificatePath = certificate.FileName; // Just for display
            }
        }

        if (!string.IsNullOrEmpty(password))
        {
            settings.CertificatePassword = password;
        }

        await _context.SaveChangesAsync();
        return Ok(settings);
    }
}
