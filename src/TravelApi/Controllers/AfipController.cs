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
    public async Task<ActionResult<string>> GetStatus()
    {
        return await _afipService.GetStatus();
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
            // Save file
            var certsDir = Path.Combine(Directory.GetCurrentDirectory(), "Certificates");
            if (!Directory.Exists(certsDir)) Directory.CreateDirectory(certsDir);

            var fileName = $"{cuit}_{DateTime.Now.Ticks}.pfx";
            var filePath = Path.Combine(certsDir, fileName);

            using (var stream = new FileStream(filePath, FileMode.Create))
            {
                await certificate.CopyToAsync(stream);
            }

            settings.CertificatePath = filePath;
            // Validar que el pfx abre con el password?
        }

        if (!string.IsNullOrEmpty(password))
        {
            settings.CertificatePassword = password;
        }

        await _context.SaveChangesAsync();
        return Ok(settings);
    }
}
