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

        // Cobros del mes
        var paymentsThisMonth = await _dbContext.Payments
            .Where(p => p.PaidAt >= startOfMonth)
            .SumAsync(p => (decimal?)p.Amount, cancellationToken) ?? 0m;

        // Saldo pendiente total (solo expedientes activos)
        var outstandingBalance = await _dbContext.TravelFiles
            .Where(f => f.Status != FileStatus.Closed && f.Status != FileStatus.Cancelled)
            .SumAsync(f => (decimal?)f.Balance, cancellationToken) ?? 0m;

        // Total vendido del mes
        var salesThisMonth = await _dbContext.TravelFiles
            .Where(f => f.CreatedAt >= startOfMonth)
            .SumAsync(f => (decimal?)f.TotalSale, cancellationToken) ?? 0m;

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
        var totalRevenue = await _dbContext.Payments.SumAsync(p => (decimal?)p.Amount, cancellationToken) ?? 0m;
        var outstandingBalance = await _dbContext.TravelFiles.SumAsync(f => (decimal?)f.Balance, cancellationToken) ?? 0m;

        return Ok(new ReportsSummaryResponse(
            totalCustomers,
            totalFiles,
            totalReservations,
            totalRevenue,
            outstandingBalance));
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
    List<PendingFileDto> ExpedientesPendientes,
    List<UpcomingTripDto> ProximosViajes);

public record PendingFileDto(int Id, string FileNumber, string Name, decimal Balance, string Status);
public record UpcomingTripDto(int Id, string FileNumber, string Name, DateTime StartDate, string Status);

public record ReportsSummaryResponse(
    int TotalCustomers,
    int TotalFiles,
    int TotalReservations,
    decimal TotalRevenue,
    decimal OutstandingBalance);
