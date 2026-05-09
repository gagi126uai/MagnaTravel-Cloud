using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TravelApi.Application.Interfaces;
using TravelApi.Authorization;
using TravelApi.Domain.Entities;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/search")]
[Authorize]
public class SearchController : ControllerBase
{
    private readonly ISearchService _searchService;

    public SearchController(ISearchService searchService)
    {
        _searchService = searchService;
    }

    /// <summary>
    /// B1.15 Fase 2a (FIX 3): el endpoint expone reservas/payments/customers — antes
    /// estaba abierto a cualquier autenticado y devolvia ResponsibleUserId/Payment.Reserva
    /// sin filtrar por scope. Ahora exige <c>reservas.view</c> como puerta minima y
    /// el service filtra por owner segun los permisos del user actual.
    /// </summary>
    [HttpGet]
    [RequirePermission(Permissions.ReservasView)]
    public async Task<ActionResult<SearchResultsResponse>> Search([FromQuery] string query, CancellationToken cancellationToken)
    {
        var results = await _searchService.SearchAsync(query, cancellationToken);
        return Ok(results);
    }
}
