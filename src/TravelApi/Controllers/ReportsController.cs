using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TravelApi.Data;
using TravelApi.Models;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/reports")]
[Authorize]
public class ReportsController : ControllerBase
{
    private readonly AppDbContext _dbContext;

    public ReportsController(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [HttpGet("dashboard")]
    public async Task<ActionResult<DashboardResponse>> GetDashboard(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var startOfMonth = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        // Expedientes por estado
        var filesByStatus = await _dbContext.TravelFiles
            .GroupBy(f => f.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var presupuestos = filesByStatus.FirstOrDefault(x => x.Status == FileStatus.Budget)?.Count ?? 0;
        var reservados = filesByStatus.FirstOrDefault(x => x.Status == FileStatus.Reserved)?.Count ?? 0;
        var operativos = filesByStatus.FirstOrDefault(x => x.Status == FileStatus.Operational)?.Count ?? 0;

        // Cobros del mes (ingresos de clientes)
        var paymentsThisMonth = await _dbContext.Payments
            .Where(p => p.PaidAt >= startOfMonth)
            .SumAsync(p => (decimal?)p.Amount, cancellationToken) ?? 0m;

        // Saldo pendiente total (solo expedientes activos y confirmados)
        var outstandingBalance = await _dbContext.TravelFiles
            .Where(f => f.Status != FileStatus.Closed && f.Status != FileStatus.Cancelled && f.Status != FileStatus.Budget)
            .SumAsync(f => (decimal?)f.Balance, cancellationToken) ?? 0m;

        // Total vendido del mes (Solo Reservado, Operativo, Cerrado)
        var salesThisMonth = await _dbContext.TravelFiles
            .Where(f => f.CreatedAt >= startOfMonth && f.Status != FileStatus.Budget && f.Status != FileStatus.Cancelled)
            .SumAsync(f => (decimal?)f.TotalSale, cancellationToken) ?? 0m;

        // Total costos del mes (Solo Reservado, Operativo, Cerrado)
        var costsThisMonth = await _dbContext.TravelFiles
            .Where(f => f.CreatedAt >= startOfMonth && f.Status != FileStatus.Budget && f.Status != FileStatus.Cancelled)
            .SumAsync(f => (decimal?)f.TotalCost, cancellationToken) ?? 0m;

        // Pagos a proveedores del mes
        var supplierPaymentsThisMonth = await _dbContext.SupplierPayments
            .Where(p => p.PaidAt >= startOfMonth)
            .SumAsync(p => (decimal?)p.Amount, cancellationToken) ?? 0m;

        // Margen Bruto del mes (Venta - Costo)
        var grossMarginThisMonth = salesThisMonth - costsThisMonth;

        // Top 5 expedientes con saldo pendiente
        var pendingFiles = await _dbContext.TravelFiles
            .Where(f => f.Balance > 0 && f.Status != FileStatus.Closed && f.Status != FileStatus.Cancelled)
            .OrderByDescending(f => f.Balance)
            .Take(5)
            .Select(f => new PendingFileDto(f.Id, f.FileNumber, f.Name, f.Balance, f.Status))
            .ToListAsync(cancellationToken);

        // Próximos viajes (siguientes 7 días)
        var next7Days = now.AddDays(7);
        var upcomingTrips = await _dbContext.TravelFiles
            .Where(f => f.StartDate >= now && f.StartDate <= next7Days && f.Status != FileStatus.Cancelled)
            .OrderBy(f => f.StartDate)
            .Take(5)
            .Select(f => new UpcomingTripDto(f.Id, f.FileNumber, f.Name, f.StartDate!.Value, f.Status))
            .ToListAsync(cancellationToken);

        return Ok(new DashboardResponse(
            Presupuestos: presupuestos,
            Reservados: reservados,
            Operativos: operativos,
            CobrosDelMes: paymentsThisMonth,
            SaldoPendiente: outstandingBalance,
            VentasDelMes: salesThisMonth,
            CostosDelMes: costsThisMonth,
            MargenBruto: grossMarginThisMonth,
            PagosProveedores: supplierPaymentsThisMonth,
            ExpedientesPendientes: pendingFiles,
            ProximosViajes: upcomingTrips
        ));
    }

    [HttpGet("summary")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<ReportsSummaryResponse>> GetSummary(CancellationToken cancellationToken)
    {
        var totalCustomers = await _dbContext.Customers.CountAsync(cancellationToken);
        var totalFiles = await _dbContext.TravelFiles.CountAsync(cancellationToken);
        var totalReservations = await _dbContext.Reservations.CountAsync(cancellationToken);
        
        // Ingresos (cobros de clientes)
        var totalRevenue = await _dbContext.Payments.SumAsync(p => (decimal?)p.Amount, cancellationToken) ?? 0m;
        
        // Costos totales
        var totalCosts = await _dbContext.TravelFiles.SumAsync(f => (decimal?)f.TotalCost, cancellationToken) ?? 0m;
        
        // Pagos a proveedores
        var totalSupplierPayments = await _dbContext.SupplierPayments.SumAsync(p => (decimal?)p.Amount, cancellationToken) ?? 0m;
        
        // Saldo pendiente clientes
        var outstandingBalance = await _dbContext.TravelFiles.SumAsync(f => (decimal?)f.Balance, cancellationToken) ?? 0m;
        
        // Ventas totales
        var totalSales = await _dbContext.TravelFiles.SumAsync(f => (decimal?)f.TotalSale, cancellationToken) ?? 0m;
        
        // Margen bruto (Venta - Costo)
        var grossMargin = totalSales - totalCosts;

        return Ok(new ReportsSummaryResponse(
            totalCustomers,
            totalFiles,
            totalReservations,
            totalRevenue,
            outstandingBalance,
            totalCosts,
            totalSupplierPayments,
            totalSales,
            grossMargin));
    }

    /// <summary>
    /// Obtener configuración de la agencia
    /// </summary>
    [HttpGet("settings")]
    public async Task<ActionResult> GetAgencySettings(CancellationToken cancellationToken)
    {
        var settings = await _dbContext.AgencySettings.FirstOrDefaultAsync(cancellationToken);
        return Ok(settings);
    }

    /// <summary>
    /// Actualizar configuración de la agencia
    /// </summary>
    [HttpPut("settings")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult> UpdateAgencySettings([FromBody] AgencySettings updated, CancellationToken cancellationToken)
    {
        var settings = await _dbContext.AgencySettings.FirstOrDefaultAsync(cancellationToken);
        if (settings is null)
        {
            _dbContext.AgencySettings.Add(updated);
        }
        else
        {
            settings.AgencyName = updated.AgencyName;
            settings.TaxId = updated.TaxId;
            settings.Address = updated.Address;
            settings.Phone = updated.Phone;
            settings.Email = updated.Email;
            settings.DefaultCommissionPercent = updated.DefaultCommissionPercent;
            settings.Currency = updated.Currency;
            settings.UpdatedAt = DateTime.UtcNow;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(settings ?? updated);
    }
}

// DTOs
public record DashboardResponse(
    int Presupuestos,
    int Reservados,
    int Operativos,
    decimal CobrosDelMes,
    decimal SaldoPendiente,
    decimal VentasDelMes,
    decimal CostosDelMes,
    decimal MargenBruto,
    decimal PagosProveedores,
    List<PendingFileDto> ExpedientesPendientes,
    List<UpcomingTripDto> ProximosViajes);

public record PendingFileDto(int Id, string FileNumber, string Name, decimal Balance, string Status);
public record UpcomingTripDto(int Id, string FileNumber, string Name, DateTime StartDate, string Status);

public record ReportsSummaryResponse(
    int TotalCustomers,
    int TotalFiles,
    int TotalReservations,
    decimal TotalRevenue,
    decimal OutstandingBalance,
    decimal TotalCosts,
    decimal TotalSupplierPayments,
    decimal TotalSales,
    decimal GrossMargin);

