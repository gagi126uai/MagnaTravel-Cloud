using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/settings/operational-finance")]
[Authorize]
public class OperationalFinanceSettingsController : ControllerBase
{
    private readonly IOperationalFinanceSettingsService _service;

    public OperationalFinanceSettingsController(IOperationalFinanceSettingsService service)
    {
        _service = service;
    }

    [HttpGet]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<OperationalFinanceSettingsDto>> Get(CancellationToken cancellationToken)
    {
        return Ok(await _service.GetAsync(cancellationToken));
    }

    [HttpPut]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<OperationalFinanceSettingsDto>> Update(
        [FromBody] OperationalFinanceSettingsDto request,
        CancellationToken cancellationToken)
    {
        return Ok(await _service.UpdateAsync(request, cancellationToken));
    }
}
