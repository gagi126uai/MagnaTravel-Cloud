using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/countries")]
[Authorize]
public class CountriesController : ControllerBase
{
    private readonly ICountryService _countryService;
    private readonly IDestinationService _destinationService;
    private readonly EntityReferenceResolver _entityReferenceResolver;

    public CountriesController(
        ICountryService countryService,
        IDestinationService destinationService,
        EntityReferenceResolver entityReferenceResolver)
    {
        _countryService = countryService;
        _destinationService = destinationService;
        _entityReferenceResolver = entityReferenceResolver;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<CountryListItemDto>>> GetCountries(
        [FromQuery] string? search,
        CancellationToken cancellationToken)
    {
        return Ok(await _countryService.GetCountriesAsync(search, cancellationToken));
    }

    [HttpGet("{publicIdOrLegacyId}")]
    public async Task<ActionResult<CountryDetailDto>> GetCountry(
        string publicIdOrLegacyId,
        CancellationToken cancellationToken)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Country>(publicIdOrLegacyId, cancellationToken);
            var country = await _countryService.GetCountryByIdAsync(id, cancellationToken);
            return country is null ? NotFound() : Ok(country);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpGet("preview/by-slug/{countrySlug}")]
    public async Task<ActionResult<PublicCountryEmbedDto>> GetPreviewBySlug(
        string countrySlug,
        CancellationToken cancellationToken)
    {
        var preview = await _countryService.GetPreviewCountryBySlugAsync(countrySlug, cancellationToken);
        return preview is null ? NotFound() : Ok(preview);
    }

    [HttpPost]
    public async Task<ActionResult<CountryDetailDto>> Create(
        [FromBody] CountryUpsertRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var created = await _countryService.CreateAsync(request, cancellationToken);
            return CreatedAtAction(nameof(GetCountry), new { publicIdOrLegacyId = created.PublicId }, created);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPut("{publicIdOrLegacyId}")]
    public async Task<ActionResult<CountryDetailDto>> Update(
        string publicIdOrLegacyId,
        [FromBody] CountryUpsertRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Country>(publicIdOrLegacyId, cancellationToken);
            var updated = await _countryService.UpdateAsync(id, request, cancellationToken);
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

    [HttpGet("{publicIdOrLegacyId}/destinations")]
    public async Task<ActionResult<IReadOnlyList<DestinationListItemDto>>> GetDestinations(
        string publicIdOrLegacyId,
        CancellationToken cancellationToken)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Country>(publicIdOrLegacyId, cancellationToken);
            return Ok(await _destinationService.GetDestinationsByCountryIdAsync(id, cancellationToken));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }
}
