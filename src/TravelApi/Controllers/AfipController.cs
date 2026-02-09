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

    public class AfipSettingsRequest
    {
        public long Cuit { get; set; }
        public int PuntoDeVenta { get; set; }
        public bool IsProduction { get; set; }
        public IFormFile? Certificate { get; set; }
        public string? Password { get; set; }
    }

    [HttpPost("settings")]
    public async Task<ActionResult<AfipSettings>> UpdateSettings([FromForm] AfipSettingsRequest request)
    {
        var settings = await _context.AfipSettings.FirstOrDefaultAsync();
        if (settings == null)
        {
            settings = new AfipSettings();
            _context.AfipSettings.Add(settings);
        }

        settings.Cuit = request.Cuit;
        settings.PuntoDeVenta = request.PuntoDeVenta;
        settings.IsProduction = request.IsProduction;

        if (request.Certificate != null)
        {
            using (var memoryStream = new MemoryStream())
            {
                await request.Certificate.CopyToAsync(memoryStream);
                var certData = memoryStream.ToArray();
                
                // Validate before saving
                var certPassword = !string.IsNullOrEmpty(request.Password) ? request.Password : settings.CertificatePassword;
                
                try 
                {
                     if (!await _afipService.ValidateCertificate(certData, certPassword))
                     {
                         return BadRequest("El certificado es inválido o la contraseña es incorrecta. Asegurate de que sea un archivo .pfx válido.");
                     }
                }
                catch (Exception ex)
                {
                     return BadRequest($"Error validando certificado: {ex.Message}");
                }

                settings.CertificateData = certData;
                settings.CertificatePath = request.Certificate.FileName; // Just for display
            }
        }

        if (!string.IsNullOrEmpty(request.Password))
        {
            settings.CertificatePassword = request.Password;
        }

        await _context.SaveChangesAsync();
        return Ok(settings);
    }
}
