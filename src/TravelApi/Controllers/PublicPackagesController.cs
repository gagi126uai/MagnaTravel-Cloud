using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/public/packages")]
[AllowAnonymous]
public class PublicPackagesController : ControllerBase
{
    private readonly ICatalogPackageService _catalogPackageService;

    public PublicPackagesController(ICatalogPackageService catalogPackageService)
    {
        _catalogPackageService = catalogPackageService;
    }

    [HttpGet("{slug}")]
    public async Task<ActionResult<PublicPackageDetailDto>> GetPublicPackage(string slug, CancellationToken cancellationToken)
    {
        var package = await _catalogPackageService.GetPublicPackageBySlugAsync(slug, cancellationToken);
        return package is null ? NotFound() : Ok(package);
    }

    [HttpGet("{slug}/hero-image")]
    public async Task<IActionResult> GetPublicHeroImage(string slug, CancellationToken cancellationToken)
    {
        var image = await _catalogPackageService.GetPublicHeroImageBySlugAsync(slug, cancellationToken);
        return image is null ? NotFound() : File(image.Value.Bytes, image.Value.ContentType);
    }

    [HttpPost("{slug}/leads")]
    [EnableRateLimiting("public-leads")]
    public async Task<IActionResult> CreateLead(
        string slug,
        [FromBody] PublicPackageLeadRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            await _catalogPackageService.CreatePublicLeadAsync(
                slug,
                request,
                Request.Headers.Referer.ToString(),
                cancellationToken);

            return Ok(new { message = "Consulta enviada." });
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }
}
