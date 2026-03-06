using System.Globalization;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

public class ReportService : IReportService
{
    private readonly AppDbContext _dbContext;

    public ReportService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<DashboardResponse> GetDashboardAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var startOfMonth = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        var filesByStatus = await _dbContext.TravelFiles
            .GroupBy(f => f.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var presupuestos = filesByStatus.FirstOrDefault(x => x.Status == FileStatus.Budget)?.Count ?? 0;
        var reservados = filesByStatus.FirstOrDefault(x => x.Status == FileStatus.Reserved)?.Count ?? 0;
        var operativos = filesByStatus.FirstOrDefault(x => x.Status == FileStatus.Operational)?.Count ?? 0;
        var cerrados = filesByStatus.FirstOrDefault(x => x.Status == FileStatus.Closed)?.Count ?? 0;
        var cancelados = filesByStatus.FirstOrDefault(x => x.Status == FileStatus.Cancelled)?.Count ?? 0;

        var paymentsThisMonth = await _dbContext.Payments
            .Where(p => p.PaidAt >= startOfMonth && !p.IsDeleted)
            .SumAsync(p => (decimal?)p.Amount, cancellationToken) ?? 0m;

        var outstandingBalance = await _dbContext.TravelFiles
            .Where(f => f.Status != FileStatus.Closed && f.Status != FileStatus.Cancelled && f.Status != FileStatus.Budget)
            .SumAsync(f => (decimal?)f.Balance, cancellationToken) ?? 0m;

        var salesThisMonth = await _dbContext.TravelFiles
            .Where(f => f.CreatedAt >= startOfMonth && f.Status != FileStatus.Budget && f.Status != FileStatus.Cancelled)
            .SumAsync(f => (decimal?)f.TotalSale, cancellationToken) ?? 0m;

        var costsThisMonth = await _dbContext.TravelFiles
            .Where(f => f.CreatedAt >= startOfMonth && f.Status != FileStatus.Budget && f.Status != FileStatus.Cancelled)
            .SumAsync(f => (decimal?)f.TotalCost, cancellationToken) ?? 0m;

        var supplierPaymentsThisMonth = await _dbContext.SupplierPayments
            .Where(p => p.PaidAt >= startOfMonth)
            .SumAsync(p => (decimal?)p.Amount, cancellationToken) ?? 0m;

        var grossMarginThisMonth = salesThisMonth - costsThisMonth;

        var pendingFiles = await _dbContext.TravelFiles
            .Where(f => f.Balance > 0 && f.Status != FileStatus.Closed && f.Status != FileStatus.Cancelled)
            .OrderByDescending(f => f.Balance)
            .Take(5)
            .Select(f => new PendingFileDto(f.Id, f.FileNumber, f.Name, f.Balance, f.Status.ToString()))
            .ToListAsync(cancellationToken);

        var next7Days = now.AddDays(7);
        var upcomingTrips = await _dbContext.TravelFiles
            .Where(f => f.StartDate >= now && f.StartDate <= next7Days && f.Status != FileStatus.Cancelled)
            .OrderBy(f => f.StartDate)
            .Take(5)
            .Select(f => new UpcomingTripDto(f.Id, f.FileNumber, f.Name, f.StartDate!.Value, f.Status.ToString()))
            .ToListAsync(cancellationToken);

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

        var statusDistribution = new StatusDistributionDto(
            presupuestos, 
            reservados, 
            operativos, 
            cerrados, 
            cancelados
        );

        return new DashboardResponse(
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
        );
    }

    public async Task<ReportsSummaryResponse> GetSummaryAsync(CancellationToken cancellationToken)
    {
        var totalCustomers = await _dbContext.Customers.CountAsync(cancellationToken);
        var totalFiles = await _dbContext.TravelFiles.CountAsync(cancellationToken);
        var totalReservations = await _dbContext.Reservations.CountAsync(cancellationToken);
        
        var totalRevenue = await _dbContext.Payments.Where(p => !p.IsDeleted).SumAsync(p => (decimal?)p.Amount, cancellationToken) ?? 0m;
        var totalCosts = await _dbContext.TravelFiles.SumAsync(f => (decimal?)f.TotalCost, cancellationToken) ?? 0m;
        var totalSupplierPayments = await _dbContext.SupplierPayments.SumAsync(p => (decimal?)p.Amount, cancellationToken) ?? 0m;
        var outstandingBalance = await _dbContext.TravelFiles.SumAsync(f => (decimal?)f.Balance, cancellationToken) ?? 0m;
        var totalSales = await _dbContext.TravelFiles.SumAsync(f => (decimal?)f.TotalSale, cancellationToken) ?? 0m;
        var grossMargin = totalSales - totalCosts;

        return new ReportsSummaryResponse(
            totalCustomers,
            totalFiles,
            totalReservations,
            totalRevenue,
            outstandingBalance,
            totalCosts,
            totalSupplierPayments,
            totalSales,
            grossMargin);
    }

    public async Task<object> GetDetailedReportAsync(DateTime? from, DateTime? to, CancellationToken cancellationToken)
    {
        var dateFrom = from?.ToUniversalTime() ?? new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var dateTo = to?.ToUniversalTime() ?? DateTime.UtcNow;

        var filesInPeriod = await _dbContext.TravelFiles
            .Where(f => f.CreatedAt >= dateFrom && f.CreatedAt <= dateTo 
                && f.Status != FileStatus.Budget && f.Status != FileStatus.Cancelled)
            .Select(f => new { f.TotalSale, f.TotalCost, f.Balance, f.Status })
            .ToListAsync(cancellationToken);

        var totalSales = filesInPeriod.Sum(f => f.TotalSale);
        var totalCosts = filesInPeriod.Sum(f => f.TotalCost);
        var grossMargin = totalSales - totalCosts;
        var marginPercent = totalSales > 0 ? Math.Round((grossMargin / totalSales) * 100, 1) : 0;

        var customerPayments = await _dbContext.Payments
            .Where(p => p.PaidAt >= dateFrom && p.PaidAt <= dateTo && !p.IsDeleted)
            .SumAsync(p => (decimal?)p.Amount, cancellationToken) ?? 0m;

        var supplierPayments = await _dbContext.SupplierPayments
            .Where(p => p.PaidAt >= dateFrom && p.PaidAt <= dateTo)
            .SumAsync(p => (decimal?)p.Amount, cancellationToken) ?? 0m;

        var supplierDebts = await _dbContext.Suppliers
            .Where(s => s.IsActive && s.CurrentBalance != 0)
            .OrderByDescending(s => s.CurrentBalance)
            .Select(s => new { s.Id, s.Name, s.CurrentBalance })
            .ToListAsync(cancellationToken);

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

        return new {
            Period = new { From = dateFrom, To = dateTo },
            Summary = new { TotalSales = totalSales, TotalCosts = totalCosts, GrossMargin = grossMargin, MarginPercent = marginPercent, CustomerPayments = customerPayments, SupplierPayments = supplierPayments, FilesCount = filesInPeriod.Count },
            SupplierDebts = supplierDebts,
            TopCustomers = topCustomers,
            MonthlyBreakdown = monthlyData
        };
    }

    public async Task<IEnumerable<object>> GetDetailedReceivablesAsync(CancellationToken cancellationToken)
    {
        return await _dbContext.Customers
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
    }

    public async Task<byte[]> ExportReportAsync(DateTime? from, DateTime? to, bool includeSales, bool includeReceivables, bool includePayables, CancellationToken cancellationToken)
    {
        var dateFrom = from?.ToUniversalTime() ?? new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var dateTo = to?.ToUniversalTime() ?? DateTime.UtcNow;

        using var workbook = new XLWorkbook();

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
            
            salesSheet.Range(2, 5, row - 1, 7).Style.NumberFormat.Format = "$ #,##0.00";
            salesSheet.Columns().AdjustToContents();
        }

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
        return stream.ToArray();
    }

    public async Task<AgencySettings?> GetAgencySettingsAsync(CancellationToken cancellationToken)
    {
        return await _dbContext.AgencySettings.FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<AgencySettings> UpdateAgencySettingsAsync(AgencySettings updated, CancellationToken cancellationToken)
    {
        var settings = await _dbContext.AgencySettings.FirstOrDefaultAsync(cancellationToken);
        if (settings == null)
        {
            _dbContext.AgencySettings.Add(updated);
            settings = updated;
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
        return settings;
    }
}
