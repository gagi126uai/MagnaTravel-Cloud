using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Authorization;
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

    /// <summary>
    /// B1.15 Fase 2a (FIX 4): el dashboard exponia costos / margen / pagos a
    /// proveedores y la lista completa de reservas pendientes a cualquier autenticado.
    /// Ahora exige <c>reportes.view</c> y el service enmascara costos para roles sin
    /// <c>cobranzas.see_cost</c> y filtra <c>ReservasPendientes</c> / <c>ProximosViajes</c>
    /// para roles sin <c>reservas.view_all</c>.
    /// </summary>
    [HttpGet("dashboard")]
    [RequirePermission(Permissions.ReportesView)]
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
    /// "Facturas en dólares" (spec firmada 2026-08-06, Parte B): la solapa donde el contador ve, mes por
    /// mes, cuánto se facturó en moneda extranjera y cuánta plata entró contra esas facturas. Solo
    /// lectura. Detrás del permiso de Reportes: el vendedor común no la ve.
    /// </summary>
    [HttpGet("usd-invoices")]
    [RequirePermission(Permissions.ReportesView)]
    public async Task<ActionResult<UsdInvoicesReportResponse>> GetUsdInvoicesReport(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        CancellationToken cancellationToken)
    {
        var response = await _reportService.GetUsdInvoicesReportAsync(from, to, cancellationToken);
        return Ok(response);
    }

    /// <summary>
    /// El mismo reporte en Excel, con las mismas columnas. Reusa el botón "Exportar Excel" que la
    /// pantalla de Reportes ya tiene arriba.
    /// </summary>
    [HttpGet("usd-invoices/export")]
    [RequirePermission(Permissions.ReportesView)]
    public async Task<ActionResult> ExportUsdInvoicesReport(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        CancellationToken cancellationToken)
    {
        var content = await _reportService.ExportUsdInvoicesReportAsync(from, to, cancellationToken);
        return File(
            content,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"Facturas_en_dolares_{DateTime.Now:yyyyMMdd}.xlsx");
    }

    /// <summary>
    /// Obtener configuración de la agencia
    /// </summary>
    [HttpGet("settings")]
    [Authorize(Roles = "Admin")]
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
    public async Task<ActionResult> UpdateAgencySettings([FromBody] AgencySettingsUpsertRequest updated, CancellationToken cancellationToken)
    {
        var settings = await _reportService.UpdateAgencySettingsAsync(MapAgencySettings(updated), cancellationToken);
        return Ok(settings);
    }

    // ===== BI Analytics Endpoints =====

    [HttpGet("sellers")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult> GetSellerRanking(
        [FromQuery] DateTime? from, [FromQuery] DateTime? to, CancellationToken cancellationToken)
    {
        var result = await _reportService.GetSellerRankingAsync(from, to, cancellationToken);
        return Ok(result);
    }

    [HttpGet("destinations")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult> GetDestinationAnalytics(
        [FromQuery] DateTime? from, [FromQuery] DateTime? to, CancellationToken cancellationToken)
    {
        var result = await _reportService.GetDestinationAnalyticsAsync(from, to, cancellationToken);
        return Ok(result);
    }

    [HttpGet("cashflow")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult> GetCashFlowProjection(
        [FromQuery] int days = 90, CancellationToken cancellationToken = default)
    {
        var result = await _reportService.GetCashFlowProjectionAsync(days, cancellationToken);
        return Ok(result);
    }

    [HttpGet("yoy")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult> GetYearOverYear(CancellationToken cancellationToken)
    {
        var result = await _reportService.GetYearOverYearAsync(cancellationToken);
        return Ok(result);
    }

    // ============================================================================================
    // Obra "PDF de presupuesto" (decisión firmada del dueño, 2026-08-11/12), TANDA 1: logo de la
    // agencia + los 6 bloques de condiciones. Mismo controller/autorización (Admin) que el resto de
    // Configuración de la agencia.
    // ============================================================================================

    /// <summary>Sube o reemplaza el logo de la agencia (PNG/JPG, máximo 2 MB). Usa el mismo almacenamiento que los adjuntos/vouchers (MinIO).</summary>
    [HttpPost("settings/logo")]
    [Authorize(Roles = "Admin")]
    [RequestSizeLimit(2 * 1024 * 1024)]
    public async Task<ActionResult> UploadAgencyLogo(IFormFile file, CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            return BadRequest(new { message = "Elegí un archivo de imagen." });
        }

        await using var stream = file.OpenReadStream();
        var settings = await _reportService.UpdateAgencyLogoAsync(stream, file.FileName, file.ContentType, cancellationToken);
        return Ok(settings);
    }

    /// <summary>Descarga el logo cargado (para que el front lo muestre en la vista previa de Configuración).</summary>
    [HttpGet("settings/logo")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult> GetAgencyLogo(CancellationToken cancellationToken)
    {
        try
        {
            var (bytes, contentType, fileName) = await _reportService.GetAgencyLogoAsync(cancellationToken);
            return File(bytes, contentType, fileName);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpDelete("settings/logo")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult> DeleteAgencyLogo(CancellationToken cancellationToken)
    {
        await _reportService.RemoveAgencyLogoAsync(cancellationToken);
        return Ok();
    }

    /// <summary>Los 6 bloques de condiciones del presupuesto ("letra chica" del PDF), uno por categoría.</summary>
    [HttpGet("budget-conditions")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult> GetBudgetConditions(CancellationToken cancellationToken)
    {
        var blocks = await _reportService.GetBudgetConditionBlocksAsync(cancellationToken);
        return Ok(blocks);
    }

    /// <summary>Edita el texto de UNA categoría (ej. "Hoteles"). <paramref name="kind"/> es el texto de la categoría, no un número.</summary>
    [HttpPut("budget-conditions/{kind}")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult> UpdateBudgetCondition(string kind, [FromBody] UpdateBudgetConditionBlockRequest request, CancellationToken cancellationToken)
    {
        var updated = await _reportService.UpdateBudgetConditionBlockAsync(kind, request.Text, cancellationToken);
        return Ok(updated);
    }

    /// <summary>
    /// Mini-tanda PDF-2a (2026-08-12): genera con IA un BORRADOR de las condiciones de UNA categoría
    /// ("✨ Ayudame a redactarlo" de la spec de UI). El borrador NUNCA se guarda solo (regla P-21): el
    /// dueño lo revisa en el textarea y, si le sirve, lo confirma con el PUT de arriba.
    /// <paramref name="kind"/> es el texto de la categoría, no un número (mismo criterio que el PUT).
    /// </summary>
    [HttpPost("budget-conditions/{kind}/draft")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<BudgetConditionDraftDto>> GenerateBudgetConditionDraft(
        string kind, [FromBody] GenerateBudgetConditionDraftRequest? request, CancellationToken cancellationToken)
    {
        try
        {
            var draft = await _reportService.GenerateBudgetConditionDraftAsync(kind, request?.CurrentText, cancellationToken);
            return Ok(draft);
        }
        catch (InvalidOperationException ex)
        {
            // La IA no está configurada o no pudo redactar: 409 con un mensaje YA en criollo, apto para
            // mostrar tal cual (el service nunca deja pasar detalle técnico en este mensaje).
            return Conflict(new { message = ex.Message });
        }
    }

    private static AgencySettings MapAgencySettings(AgencySettingsUpsertRequest request)
    {
        return new AgencySettings
        {
            AgencyName = request.AgencyName,
            LegalName = request.LegalName,
            TaxCondition = request.TaxCondition,
            ActivityStartDate = request.ActivityStartDate,
            TaxId = request.TaxId,
            Address = request.Address,
            Phone = request.Phone,
            Email = request.Email,
            DefaultCommissionPercent = request.DefaultCommissionPercent,
            Currency = request.Currency,
            AgencyLicenseNumber = request.AgencyLicenseNumber,
            PdfBandColorHex = request.PdfBandColorHex
        };
    }
}

public record AgencySettingsUpsertRequest(
    string AgencyName,
    string? LegalName,
    string? TaxCondition,
    DateTime? ActivityStartDate,
    string? TaxId,
    string? Address,
    string? Phone,
    string? Email,
    decimal DefaultCommissionPercent,
    string Currency,
    // Obra "PDF de presupuesto" (2026-08-11/12): legajo EVT y color de la banda del PDF. Opcionales al
    // final para no romper callers posicionales existentes (mismo patrón del resto del proyecto).
    string? AgencyLicenseNumber = null,
    string? PdfBandColorHex = null);

public record UpdateBudgetConditionBlockRequest(string? Text);

/// <summary>Body opcional del POST de borrador con IA: si el dueño ya escribió algo, la IA lo usa de base.</summary>
public record GenerateBudgetConditionDraftRequest(string? CurrentText);
