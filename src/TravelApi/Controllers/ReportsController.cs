using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TravelApi.Data;
using TravelApi.Models;
using System.Globalization;

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
        var cerrados = filesByStatus.FirstOrDefault(x => x.Status == FileStatus.Closed)?.Count ?? 0;
        var cancelados = filesByStatus.FirstOrDefault(x => x.Status == FileStatus.Cancelled)?.Count ?? 0;

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

        // --- Historical Data (Last 6 Months) ---
        var sixMonthsAgo = startOfMonth.AddMonths(-5);
        
        var monthlyData = await _dbContext.TravelFiles
            .Where(f => f.CreatedAt >= sixMonthsAgo && f.Status != FileStatus.Budget && f.Status != FileStatus.Cancelled)
            .GroupBy(f => new { f.CreatedAt.Year, f.CreatedAt.Month })
            .Select(g => new
            {
                Year = g.Key.Year,
                Month = g.Key.Month,
                TotalSales = g.Sum(f => f.TotalSale),
                TotalCosts = g.Sum(f => f.TotalCost)
            })
            .ToListAsync(cancellationToken);

        var historicalTrend = new List<MonthlyMetricDto>();
        for (int i = 0; i < 6; i++)
        {
            var targetDate = sixMonthsAgo.AddMonths(i);
            var record = monthlyData.FirstOrDefault(m => m.Year == targetDate.Year && m.Month == targetDate.Month);

            var sales = record?.TotalSales ?? 0m;
            var costs = record?.TotalCosts ?? 0m;
            var profit = sales - costs;
            var monthName = CultureInfo.CurrentCulture.DateTimeFormat.GetAbbreviatedMonthName(targetDate.Month);

            historicalTrend.Add(new MonthlyMetricDto(monthName, sales, costs, profit));
        }

        // --- Status Distribution for Pie Chart ---
        var statusDistribution = new StatusDistributionDto(
            presupuestos, 
            reservados, 
            operativos, 
            cerrados, 
            cancelados
        );

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
            ProximosViajes: upcomingTrips,
            TendenciaHistorica: historicalTrend,
            DistribucionEstados: statusDistribution
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

    [HttpGet("detailed")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult> GetDetailedReport(
        [FromQuery] DateTime? from, 
        [FromQuery] DateTime? to,
        CancellationToken cancellationToken)
    {
        var dateFrom = from?.ToUniversalTime() ?? new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var dateTo = to?.ToUniversalTime() ?? DateTime.UtcNow;

        // 1. Ventas del período (expedientes creados en el rango, estados confirmados)
        var filesInPeriod = await _dbContext.TravelFiles
            .Where(f => f.CreatedAt >= dateFrom && f.CreatedAt <= dateTo 
                && f.Status != FileStatus.Budget && f.Status != FileStatus.Cancelled)
            .Select(f => new { f.TotalSale, f.TotalCost, f.Balance, f.Status })
            .ToListAsync(cancellationToken);

        var totalSales = filesInPeriod.Sum(f => f.TotalSale);
        var totalCosts = filesInPeriod.Sum(f => f.TotalCost);
        var grossMargin = totalSales - totalCosts;
        var marginPercent = totalSales > 0 ? Math.Round((grossMargin / totalSales) * 100, 1) : 0;

        // 2. Cobros de clientes en el período
        var customerPayments = await _dbContext.Payments
            .Where(p => p.PaidAt >= dateFrom && p.PaidAt <= dateTo)
            .SumAsync(p => (decimal?)p.Amount, cancellationToken) ?? 0m;

        // 3. Pagos a proveedores en el período
        var supplierPayments = await _dbContext.SupplierPayments
            .Where(p => p.PaidAt >= dateFrom && p.PaidAt <= dateTo)
            .SumAsync(p => (decimal?)p.Amount, cancellationToken) ?? 0m;

        // 4. Deuda actual por proveedor (sin filtro de fecha, es saldo vivo)
        var supplierDebts = await _dbContext.Suppliers
            .Where(s => s.IsActive && s.CurrentBalance != 0)
            .OrderByDescending(s => s.CurrentBalance)
            .Select(s => new { s.Id, s.Name, s.CurrentBalance })
            .ToListAsync(cancellationToken);

        // 5. Top 10 clientes por venta en el período
        var topCustomers = await _dbContext.TravelFiles
            .Where(f => f.CreatedAt >= dateFrom && f.CreatedAt <= dateTo 
                && f.Status != FileStatus.Budget && f.Status != FileStatus.Cancelled
                && f.PayerId != null)
            .GroupBy(f => new { f.PayerId, f.Payer!.FullName })
            .Select(g => new { 
                Name = g.Key.FullName, 
                TotalSale = g.Sum(f => f.TotalSale),
                FileCount = g.Count(),
                PendingBalance = g.Sum(f => f.Balance)
            })
            .OrderByDescending(x => x.TotalSale)
            .Take(10)
            .ToListAsync(cancellationToken);

        // 6. Ventas por mes (para gráfico dentro del rango)
        var monthlyBreakdown = await _dbContext.TravelFiles
            .Where(f => f.CreatedAt >= dateFrom && f.CreatedAt <= dateTo 
                && f.Status != FileStatus.Budget && f.Status != FileStatus.Cancelled)
            .GroupBy(f => new { f.CreatedAt.Year, f.CreatedAt.Month })
            .Select(g => new {
                Year = g.Key.Year,
                Month = g.Key.Month,
                Sales = g.Sum(f => f.TotalSale),
                Costs = g.Sum(f => f.TotalCost),
                FileCount = g.Count()
            })
            .OrderBy(x => x.Year).ThenBy(x => x.Month)
            .ToListAsync(cancellationToken);

        var monthlyData = monthlyBreakdown.Select(m => new {
            Month = CultureInfo.CurrentCulture.DateTimeFormat.GetAbbreviatedMonthName(m.Month) + " " + m.Year,
            m.Sales,
            m.Costs,
            Margin = m.Sales - m.Costs,
            m.FileCount
        });

        return Ok(new {
            Period = new { From = dateFrom, To = dateTo },
            Summary = new { TotalSales = totalSales, TotalCosts = totalCosts, GrossMargin = grossMargin, MarginPercent = marginPercent, CustomerPayments = customerPayments, SupplierPayments = supplierPayments, FilesCount = filesInPeriod.Count },
            SupplierDebts = supplierDebts,
            TopCustomers = topCustomers,
            MonthlyBreakdown = monthlyData
        });
    }

    [HttpGet("detailed-receivables")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult> GetDetailedReceivables(CancellationToken cancellationToken)
    {
        // Clientes con saldo positivo (nos deben)
        var debtors = await _dbContext.Customers
            .Where(c => c.CurrentBalance > 0 && c.IsActive)
            .OrderByDescending(c => c.CurrentBalance)
            .Select(c => new 
            {
                c.Id,
                c.FullName,
                c.DocumentNumber,
                c.CurrentBalance,
                LastMovementDate = _dbContext.TravelFiles
                    .Where(f => f.PayerId == c.Id)
                    .OrderByDescending(f => f.CreatedAt)
                    .Select(f => f.CreatedAt)
                    .FirstOrDefault()
            })
            .ToListAsync(cancellationToken);

        return Ok(debtors);
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
        var dateFrom = from?.ToUniversalTime() ?? new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var dateTo = to?.ToUniversalTime() ?? DateTime.UtcNow;

        using var workbook = new ClosedXML.Excel.XLWorkbook();

        // 1. Hoja de Ventas
        if (includeSales)
        {
            var salesSheet = workbook.Worksheets.Add("Ventas");
            salesSheet.Cell(1, 1).Value = "Expediente";
            salesSheet.Cell(1, 2).Value = "Cliente";
            salesSheet.Cell(1, 3).Value = "Fecha";
            salesSheet.Cell(1, 4).Value = "Estado";
            salesSheet.Cell(1, 5).Value = "Venta";
            salesSheet.Cell(1, 6).Value = "Costo";
            salesSheet.Cell(1, 7).Value = "Margen";

            var files = await _dbContext.TravelFiles
                .Include(f => f.Payer)
                .Where(f => f.CreatedAt >= dateFrom && f.CreatedAt <= dateTo 
                    && f.Status != FileStatus.Budget && f.Status != FileStatus.Cancelled)
                .OrderByDescending(f => f.CreatedAt)
                .ToListAsync(cancellationToken);

            int row = 2;
            foreach (var file in files)
            {
                salesSheet.Cell(row, 1).Value = file.FileNumber;
                salesSheet.Cell(row, 2).Value = file.Payer?.FullName ?? "Cliente Ocasional";
                salesSheet.Cell(row, 3).Value = file.CreatedAt;
                salesSheet.Cell(row, 4).Value = file.Status.ToString();
                salesSheet.Cell(row, 5).Value = file.TotalSale;
                salesSheet.Cell(row, 6).Value = file.TotalCost;
                salesSheet.Cell(row, 7).Value = file.TotalSale - file.TotalCost;
                row++;
            }
            
            // Formato moneda
            salesSheet.Range(2, 5, row - 1, 7).Style.NumberFormat.Format = "$ #,##0.00";
            salesSheet.Columns().AdjustToContents();
        }

        // 2. Hoja de Deudas (Cuentas por Cobrar)
        if (includeReceivables)
        {
            var debtSheet = workbook.Worksheets.Add("Cuentas por Cobrar");
            debtSheet.Cell(1, 1).Value = "Cliente";
            debtSheet.Cell(1, 2).Value = "Documento";
            debtSheet.Cell(1, 3).Value = "Saldo Deudor";
            
            var debtors = await _dbContext.Customers
                .Where(c => c.CurrentBalance > 0)
                .OrderByDescending(c => c.CurrentBalance)
                .ToListAsync(cancellationToken);

            int row = 2;
            foreach (var debtor in debtors)
            {
                debtSheet.Cell(row, 1).Value = debtor.FullName;
                debtSheet.Cell(row, 2).Value = debtor.DocumentNumber;
                debtSheet.Cell(row, 3).Value = debtor.CurrentBalance;
                row++;
            }

            debtSheet.Range(2, 3, row - 1, 3).Style.NumberFormat.Format = "$ #,##0.00";
            debtSheet.Columns().AdjustToContents();
        }

        // 3. Hoja de Pagos (Cuentas por Pagar)
        if (includePayables)
        {
            var payableSheet = workbook.Worksheets.Add("Cuentas por Pagar");
            payableSheet.Cell(1, 1).Value = "Proveedor";
            payableSheet.Cell(1, 2).Value = "Saldo a Favor";

            var creditors = await _dbContext.Suppliers
                .Where(s => s.CurrentBalance > 0)
                .OrderByDescending(s => s.CurrentBalance)
                .ToListAsync(cancellationToken);

            int row = 2;
            foreach (var creditor in creditors)
            {
                payableSheet.Cell(row, 1).Value = creditor.Name;
                payableSheet.Cell(row, 2).Value = creditor.CurrentBalance;
                row++;
            }

            payableSheet.Range(2, 2, row - 1, 2).Style.NumberFormat.Format = "$ #,##0.00";
            payableSheet.Columns().AdjustToContents();
        }

        if (!workbook.Worksheets.Any())
        {
            var sheet = workbook.Worksheets.Add("Info");
            sheet.Cell(1, 1).Value = "No se seleccionaron reportes para exportar.";
        }

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        var content = stream.ToArray();

        return File(content, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"Reporte_{DateTime.Now:yyyyMMdd}.xlsx");
    }
            .Where(c => c.CurrentBalance > 0 && c.IsActive)
            .OrderByDescending(c => c.CurrentBalance)
            .ToListAsync(cancellationToken);

        row = 2;
        foreach (var debtor in debtors)
        {
            debtSheet.Cell(row, 1).Value = debtor.FullName;
            debtSheet.Cell(row, 2).Value = debtor.DocumentNumber;
            debtSheet.Cell(row, 3).Value = debtor.CurrentBalance;
            row++;
        }
        debtSheet.Range(2, 3, row - 1, 3).Style.NumberFormat.Format = "$ #,##0.00";
        debtSheet.Columns().AdjustToContents();

        // 3. Hoja de Proveedores (Cuentas por Pagar)
        var supplierSheet = workbook.Worksheets.Add("Cuentas por Pagar");
        supplierSheet.Cell(1, 1).Value = "Proveedor";
        supplierSheet.Cell(1, 2).Value = "Saldo a Favor";

        var suppliers = await _dbContext.Suppliers
            .Where(s => s.CurrentBalance != 0 && s.IsActive)
            .OrderByDescending(s => s.CurrentBalance)
            .ToListAsync(cancellationToken);

        row = 2;
        foreach (var sup in suppliers)
        {
            supplierSheet.Cell(row, 1).Value = sup.Name;
            supplierSheet.Cell(row, 2).Value = sup.CurrentBalance;
            row++;
        }
        supplierSheet.Range(2, 2, row - 1, 2).Style.NumberFormat.Format = "$ #,##0.00";
        supplierSheet.Columns().AdjustToContents();

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        var content = stream.ToArray();

        return File(content, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"Reporte_MagnaTravel_{DateTime.Now:yyyyMMdd}.xlsx");
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
            settings.LegalName = updated.LegalName;
            settings.TaxCondition = updated.TaxCondition;
            settings.ActivityStartDate = updated.ActivityStartDate.HasValue 
                ? DateTime.SpecifyKind(updated.ActivityStartDate.Value, DateTimeKind.Utc) 
                : null;
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
    List<UpcomingTripDto> ProximosViajes,
    List<MonthlyMetricDto> TendenciaHistorica,
    StatusDistributionDto DistribucionEstados);

public record PendingFileDto(int Id, string FileNumber, string Name, decimal Balance, string Status);
public record UpcomingTripDto(int Id, string FileNumber, string Name, DateTime StartDate, string Status);
public record MonthlyMetricDto(string Month, decimal Sales, decimal Costs, decimal Profit);
public record StatusDistributionDto(int Budgets, int Reserved, int Operational, int Closed, int Cancelled);

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


