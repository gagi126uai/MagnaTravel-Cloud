using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.RateLimiting;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/public/packages")]
[AllowAnonymous]
public class PublicPackagesController : ControllerBase
{
    private readonly IDestinationService _destinationService;

    public PublicPackagesController(IDestinationService destinationService)
    {
        _destinationService = destinationService;
    }

    [HttpGet("{slug}")]
    [OutputCache(PolicyName = "CatalogCache")]
    public async Task<ActionResult<PublicPackageDetailDto>> GetPublicPackage(string slug, CancellationToken cancellationToken)
    {
        var package = await _destinationService.GetPublicPackageBySlugAsync(slug, cancellationToken);
        return package is null ? NotFound() : Ok(package);
    }

    [HttpGet("{slug}/hero-image")]
    [OutputCache(PolicyName = "CatalogCache")]
    public async Task<IActionResult> GetPublicHeroImage(string slug, CancellationToken cancellationToken)
    {
        var image = await _destinationService.GetPublicHeroImageBySlugAsync(slug, cancellationToken);
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
            await _destinationService.CreatePublicLeadAsync(
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
