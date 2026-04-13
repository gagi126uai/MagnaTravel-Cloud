using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/public/countries")]
[AllowAnonymous]
public class PublicCountriesController : ControllerBase
{
    private readonly ICountryService _countryService;

    public PublicCountriesController(ICountryService countryService)
    {
        _countryService = countryService;
    }

    [HttpGet("{countrySlug}")]
    [OutputCache(PolicyName = "CatalogCache")]
    public async Task<ActionResult<PublicCountryEmbedDto>> GetPublicCountry(string countrySlug, CancellationToken cancellationToken)
    {
        var country = await _countryService.GetPublicCountryBySlugAsync(countrySlug, cancellationToken);
        return country is null ? NotFound() : Ok(country);
    }
}
