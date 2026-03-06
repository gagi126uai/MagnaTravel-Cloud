using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/reports")]
[Authorize]
public class ReportsController : ControllerBase
{
    private readonly IReportService _reportService;

    public ReportsController(IReportService reportService)
    {
        _reportService = reportService;
    }

    [HttpGet("dashboard")]
    public async Task<ActionResult<DashboardResponse>> GetDashboard(CancellationToken cancellationToken)
    {
        var response = await _reportService.GetDashboardAsync(cancellationToken);
        return Ok(response);
    }

    [HttpGet("summary")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<ReportsSummaryResponse>> GetSummary(CancellationToken cancellationToken)
    {
        var response = await _reportService.GetSummaryAsync(cancellationToken);
        return Ok(response);
    }

    [HttpGet("detailed")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult> GetDetailedReport(
        [FromQuery] DateTime? from, 
        [FromQuery] DateTime? to,
        CancellationToken cancellationToken)
    {
        var response = await _reportService.GetDetailedReportAsync(from, to, cancellationToken);
        return Ok(response);
    }

    [HttpGet("detailed-receivables")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult> GetDetailedReceivables(CancellationToken cancellationToken)
    {
        var response = await _reportService.GetDetailedReceivablesAsync(cancellationToken);
        return Ok(response);
    }

    [HttpGet("export")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult> ExportReport(
        [FromQuery] DateTime? from, 
        [FromQuery] DateTime? to,
        [FromQuery] bool includeSales = true,
        [FromQuery] bool includeReceivables = true,
        [FromQuery] bool includePayables = true,
        CancellationToken cancellationToken = default)
    {
        var content = await _reportService.ExportReportAsync(from, to, includeSales, includeReceivables, includePayables, cancellationToken);
        return File(content, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"Reporte_{DateTime.Now:yyyyMMdd}.xlsx");
    }

    /// <summary>
    /// Obtener configuración de la agencia
    /// </summary>
    [HttpGet("settings")]
    public async Task<ActionResult> GetAgencySettings(CancellationToken cancellationToken)
    {
        var settings = await _reportService.GetAgencySettingsAsync(cancellationToken);
        return Ok(settings);
    }

    /// <summary>
    /// Actualizar configuración de la agencia
    /// </summary>
    [HttpPut("settings")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult> UpdateAgencySettings([FromBody] AgencySettings updated, CancellationToken cancellationToken)
    {
        var settings = await _reportService.UpdateAgencySettingsAsync(updated, cancellationToken);
        return Ok(settings);
    }
}
