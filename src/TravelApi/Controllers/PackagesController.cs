using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/packages")]
[Authorize]
public class PackagesController : ControllerBase
{
    private readonly ICatalogPackageService _catalogPackageService;
    private readonly EntityReferenceResolver _entityReferenceResolver;

    public PackagesController(
        ICatalogPackageService catalogPackageService,
        EntityReferenceResolver entityReferenceResolver)
    {
        _catalogPackageService = catalogPackageService;
        _entityReferenceResolver = entityReferenceResolver;
    }

    [HttpGet]
    public async Task<ActionResult<PagedResponse<CatalogPackageListItemDto>>> GetPackages(
        [FromQuery] PackageListQuery query,
        CancellationToken cancellationToken)
    {
        return Ok(await _catalogPackageService.GetPackagesAsync(query, cancellationToken));
    }

    [HttpGet("{publicIdOrLegacyId}")]
    public async Task<ActionResult<CatalogPackageDetailDto>> GetPackage(
        string publicIdOrLegacyId,
        CancellationToken cancellationToken)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<CatalogPackage>(publicIdOrLegacyId, cancellationToken);
            var package = await _catalogPackageService.GetPackageByIdAsync(id, cancellationToken);
            return package is null ? NotFound() : Ok(package);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpPost]
    public async Task<ActionResult<CatalogPackageDetailDto>> Create(
        [FromBody] PackageUpsertRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var created = await _catalogPackageService.CreateAsync(request, cancellationToken);
            return CreatedAtAction(nameof(GetPackage), new { publicIdOrLegacyId = created.PublicId }, created);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPut("{publicIdOrLegacyId}")]
    public async Task<ActionResult<CatalogPackageDetailDto>> Update(
        string publicIdOrLegacyId,
        [FromBody] PackageUpsertRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<CatalogPackage>(publicIdOrLegacyId, cancellationToken);
            var updated = await _catalogPackageService.UpdateAsync(id, request, cancellationToken);
            return updated is null ? NotFound() : Ok(updated);
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

    [HttpPatch("{publicIdOrLegacyId}/publish")]
    public async Task<ActionResult<CatalogPackageDetailDto>> Publish(
        string publicIdOrLegacyId,
        CancellationToken cancellationToken)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<CatalogPackage>(publicIdOrLegacyId, cancellationToken);
            return Ok(await _catalogPackageService.PublishAsync(id, cancellationToken));
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

    [HttpPatch("{publicIdOrLegacyId}/unpublish")]
    public async Task<ActionResult<CatalogPackageDetailDto>> Unpublish(
        string publicIdOrLegacyId,
        CancellationToken cancellationToken)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<CatalogPackage>(publicIdOrLegacyId, cancellationToken);
            return Ok(await _catalogPackageService.UnpublishAsync(id, cancellationToken));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpPost("{publicIdOrLegacyId}/hero-image")]
    [EnableRateLimiting("uploads")]
    public async Task<ActionResult<CatalogPackageDetailDto>> UploadHeroImage(
        string publicIdOrLegacyId,
        IFormFile file,
        CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            return BadRequest(new { message = "Debes enviar una imagen." });
        }

        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<CatalogPackage>(publicIdOrLegacyId, cancellationToken);
            await using var stream = file.OpenReadStream();
            var updated = await _catalogPackageService.UploadHeroImageAsync(
                id,
                stream,
                file.FileName,
                file.ContentType,
                cancellationToken);

            return Ok(updated);
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

    [HttpGet("{publicIdOrLegacyId}/hero-image")]
    public async Task<IActionResult> GetHeroImage(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<CatalogPackage>(publicIdOrLegacyId, cancellationToken);
            var image = await _catalogPackageService.GetHeroImageAsync(id, cancellationToken);
            return image is null ? NotFound() : File(image.Value.Bytes, image.Value.ContentType);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }
}
