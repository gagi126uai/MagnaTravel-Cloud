using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/public/countries")]
[AllowAnonymous]
public class PublicCountriesController : ControllerBase
{
    private readonly ICatalogPackageService _catalogPackageService;

    public PublicCountriesController(ICatalogPackageService catalogPackageService)
    {
        _catalogPackageService = catalogPackageService;
    }

    [HttpGet("{countrySlug}")]
    public async Task<ActionResult<PublicCountryEmbedDto>> GetPublicCountry(string countrySlug, CancellationToken cancellationToken)
    {
        var country = await _catalogPackageService.GetPublicCountryBySlugAsync(countrySlug, cancellationToken);
        return country is null ? NotFound() : Ok(country);
    }
}
