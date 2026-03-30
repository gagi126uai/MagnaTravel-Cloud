using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/destinations")]
[Authorize]
public class DestinationsController : ControllerBase
{
    private readonly IDestinationService _destinationService;
    private readonly EntityReferenceResolver _entityReferenceResolver;

    public DestinationsController(
        IDestinationService destinationService,
        EntityReferenceResolver entityReferenceResolver)
    {
        _destinationService = destinationService;
        _entityReferenceResolver = entityReferenceResolver;
    }

    [HttpGet("{publicIdOrLegacyId}")]
    public async Task<ActionResult<DestinationDetailDto>> GetDestination(
        string publicIdOrLegacyId,
        CancellationToken cancellationToken)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Destination>(publicIdOrLegacyId, cancellationToken);
            var destination = await _destinationService.GetDestinationByIdAsync(id, cancellationToken);
            return destination is null ? NotFound() : Ok(destination);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpGet("preview/by-slug/{slug}")]
    public async Task<ActionResult<PublicPackageDetailDto>> GetPreviewBySlug(
        string slug,
        CancellationToken cancellationToken)
    {
        var preview = await _destinationService.GetPreviewPackageBySlugAsync(slug, cancellationToken);
        return preview is null ? NotFound() : Ok(preview);
    }

    [HttpPost]
    public async Task<ActionResult<DestinationDetailDto>> Create(
        [FromBody] DestinationUpsertRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var created = await _destinationService.CreateAsync(request, cancellationToken);
            return CreatedAtAction(nameof(GetDestination), new { publicIdOrLegacyId = created.PublicId }, created);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPut("{publicIdOrLegacyId}")]
    public async Task<ActionResult<DestinationDetailDto>> Update(
        string publicIdOrLegacyId,
        [FromBody] DestinationUpsertRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Destination>(publicIdOrLegacyId, cancellationToken);
            var updated = await _destinationService.UpdateAsync(id, request, cancellationToken);
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
    public async Task<ActionResult<DestinationDetailDto>> Publish(
        string publicIdOrLegacyId,
        CancellationToken cancellationToken)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Destination>(publicIdOrLegacyId, cancellationToken);
            return Ok(await _destinationService.PublishAsync(id, cancellationToken));
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
    public async Task<ActionResult<DestinationDetailDto>> Unpublish(
        string publicIdOrLegacyId,
        CancellationToken cancellationToken)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Destination>(publicIdOrLegacyId, cancellationToken);
            return Ok(await _destinationService.UnpublishAsync(id, cancellationToken));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpPost("{publicIdOrLegacyId}/hero-image")]
    [EnableRateLimiting("uploads")]
    public async Task<ActionResult<DestinationDetailDto>> UploadHeroImage(
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
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Destination>(publicIdOrLegacyId, cancellationToken);
            await using var stream = file.OpenReadStream();
            var updated = await _destinationService.UploadHeroImageAsync(
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
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Destination>(publicIdOrLegacyId, cancellationToken);
            var image = await _destinationService.GetHeroImageAsync(id, cancellationToken);
            return image is null ? NotFound() : File(image.Value.Bytes, image.Value.ContentType);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }
}
