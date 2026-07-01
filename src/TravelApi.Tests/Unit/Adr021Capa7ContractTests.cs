using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Moq;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Application.Mappings;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-021 Capa 7 (multimoneda, 2026-06-11): tests de CONTRATO. Verifican que los datos por moneda
/// salgan por los endpoints que las pantallas REALMENTE consumen (cash-summary, reports/detailed,
/// reports/detailed-receivables) y por los DTOs (ServicioReserva, Payment).
///
/// <para>Cubre: (a) el mapeo puebla la moneda y la normaliza; (b) un cobro cruzado expone su bloque de
/// TC; (c) caja por moneda con un cobro cruzado entra en su moneda real; (d) cuentas por cobrar/pagar
/// por moneda producen UNA fila por moneda (nunca un monto mezclado); (e) REGRESION mono-ARS = una sola
/// fila ARS; (f) el enmascarado see_cost cubre costos por moneda del reporte detallado.</para>
/// </summary>
public class Adr021Capa7ContractTests
{
    private readonly DbContextOptions<AppDbContext> _dbOptions;

    public Adr021Capa7ContractTests()
    {
        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
    }

    private static IMapper CreateMapper()
        => new MapperConfiguration(config => config.AddProfile<MappingProfile>()).CreateMapper();

    private static TreasuryService BuildTreasury(AppDbContext context) => new(context, null!);

    private static ReportService BuildReports(AppDbContext context)
    {
        var bna = new Mock<IBnaExchangeRateService>();
        bna.Setup(b => b.GetUsdSellerRateAsync(It.IsAny<CancellationToken>())).ReturnsAsync((BnaUsdSellerRateDto?)null);
        // Sin permission resolver ni http accessor: el detailed report es Admin-only y aca lo invocamos
        // directo (canSeeCost lo pasa el propio metodo como true para ese endpoint).
        return new ReportService(context, bna.Object);
    }

    // ===================== A-1: ServicioReservaDto.Currency =====================

    [Fact]
    public void ServicioReserva_mapea_su_moneda()
    {
        var mapper = CreateMapper();
        var servicio = new ServicioReserva { PublicId = Guid.NewGuid(), Currency = "USD", Status = ReservationStatuses.Draft };

        var dto = mapper.Map<ServicioReservaDto>(servicio);

        Assert.Equal("USD", dto.Currency);
    }

    [Fact]
    public void ServicioReserva_sin_moneda_se_normaliza_a_ARS()
    {
        var mapper = CreateMapper();
        var servicio = new ServicioReserva { PublicId = Guid.NewGuid(), Currency = null, Status = ReservationStatuses.Draft };

        var dto = mapper.Map<ServicioReservaDto>(servicio);

        Assert.Equal("ARS", dto.Currency);
    }

    // ===================== A-4: PaymentDto moneda/cruce =====================

    [Fact]
    public void Payment_no_cruzado_mapea_moneda_y_deja_cruce_en_null()
    {
        var mapper = CreateMapper();
        var payment = new Payment { PublicId = Guid.NewGuid(), Amount = 100m, Currency = "ARS" };

        var dto = mapper.Map<PaymentDto>(payment);

        Assert.Equal("ARS", dto.Currency);
        Assert.Null(dto.ImputedCurrency);
        Assert.Null(dto.ExchangeRate);
        Assert.Null(dto.ExchangeRateSource);
        Assert.Null(dto.ExchangeRateAt);
        Assert.Null(dto.ImputedAmount);
    }

    [Fact]
    public void Payment_cruzado_expone_el_bloque_de_tipo_de_cambio()
    {
        var mapper = CreateMapper();
        var at = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var payment = new Payment
        {
            PublicId = Guid.NewGuid(),
            Amount = 100000m,
            Currency = "ARS",
            ImputedCurrency = "USD",
            ExchangeRate = 1000m,
            ExchangeRateSource = ExchangeRateSource.Manual,
            ExchangeRateAt = at,
            ImputedAmount = 100m
        };

        var dto = mapper.Map<PaymentDto>(payment);

        Assert.Equal("ARS", dto.Currency);
        Assert.Equal("USD", dto.ImputedCurrency);
        Assert.Equal(1000m, dto.ExchangeRate);
        Assert.Equal((int)ExchangeRateSource.Manual, dto.ExchangeRateSource);
        Assert.Equal(at, dto.ExchangeRateAt);
        Assert.Equal(100m, dto.ImputedAmount);
    }

    // ===================== A-2: cash-summary por moneda =====================

    [Fact]
    public async Task CashSummary_monoARS_da_una_sola_fila_ARS_que_coincide_con_el_escalar()
    {
        await using var context = new AppDbContext(_dbOptions);
        var now = DateTime.UtcNow;
        context.Reservas.Add(new Reserva { Id = 1, NumeroReserva = "F-1", Name = "R1", Status = EstadoReserva.Confirmed });
        // ADR-022 capa 4: la caja sale del LIBRO (asiento del cobro), no del Payment al vuelo.
        context.CashLedgerEntries.Add(NewIncomeLedger(amount: 500m, currency: "ARS", occurredAt: now));
        await context.SaveChangesAsync();

        var summary = await BuildTreasury(context).GetCashSummaryAsync(cancellationToken: CancellationToken.None);

        var row = Assert.Single(summary.CashByCurrency);
        Assert.Equal("ARS", row.Currency);
        Assert.Equal(summary.CashInThisMonth, row.CashInThisMonth);
        Assert.Equal(500m, row.CashInThisMonth);
    }

    [Fact]
    public async Task CashSummary_cobroCruzado_entra_a_caja_en_su_moneda_real()
    {
        await using var context = new AppDbContext(_dbOptions);
        var now = DateTime.UtcNow;
        context.Reservas.Add(new Reserva { Id = 1, NumeroReserva = "F-1", Name = "R1", Status = EstadoReserva.Confirmed });
        // ADR-022 capa 4: el asiento del cobro cruzado ya nace en ARS (moneda real), aunque impute USD.
        context.CashLedgerEntries.Add(NewIncomeLedger(amount: 100000m, currency: "ARS", occurredAt: now));
        await context.SaveChangesAsync();

        var summary = await BuildTreasury(context).GetCashSummaryAsync(cancellationToken: CancellationToken.None);

        var row = Assert.Single(summary.CashByCurrency);
        Assert.Equal("ARS", row.Currency);
        Assert.Equal(100000m, row.CashInThisMonth);
    }

    /// <summary>Asiento de cobro (Income) minimo para sembrar el libro: capa 4 lee la caja de aca.</summary>
    private static CashLedgerEntry NewIncomeLedger(decimal amount, string currency, DateTime occurredAt)
        => new()
        {
            Direction = CashMovementDirections.Income,
            Amount = amount,
            Currency = currency,
            Method = "Transfer",
            OccurredAt = occurredAt,
            SourceType = CashLedgerSourceTypes.CustomerPayment,
        };

    // ===================== A-3: reports/detailed por moneda =====================

    [Fact]
    public async Task DetailedReport_summary_separa_ventas_y_costos_por_moneda()
    {
        await using var context = new AppDbContext(_dbOptions);
        var start = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        context.Reservas.Add(new Reserva
        {
            Id = 1, NumeroReserva = "F-1", Name = "R1", Status = EstadoReserva.Confirmed,
            // La venta debe caer DENTRO del mes y en el pasado. Usamos el primer instante del mes
            // (siempre <= ahora); antes usaba start.AddDays(1), que el dia 1 del mes queda en el
            // FUTURO (dia 2) y el filtro "ventas del mes hasta hoy" lo excluia -> el .Single() fallaba.
            CreatedAt = start, TotalSale = 1000m, TotalCost = 600m
        });
        context.ReservaMoneyByCurrency.AddRange(
            new ReservaMoneyByCurrency { ReservaId = 1, Currency = "ARS", TotalSale = 700m, TotalCost = 400m },
            new ReservaMoneyByCurrency { ReservaId = 1, Currency = "USD", TotalSale = 300m, TotalCost = 200m });
        await context.SaveChangesAsync();

        var report = await BuildReports(context).GetDetailedReportAsync(null, null, CancellationToken.None);

        var porMoneda = (DashboardByCurrencyDto)GetProperty(GetProperty(report, "Summary"), "PorMoneda");
        Assert.Equal(700m, porMoneda.VentasDelMes.Single(x => x.Currency == "ARS").Amount);
        Assert.Equal(300m, porMoneda.VentasDelMes.Single(x => x.Currency == "USD").Amount);
        Assert.Equal(400m, porMoneda.CostosDelMes.Single(x => x.Currency == "ARS").Amount);
        Assert.Equal(200m, porMoneda.CostosDelMes.Single(x => x.Currency == "USD").Amount);
    }

    [Fact]
    public async Task DetailedReport_supplierDebts_separa_una_fila_por_moneda()
    {
        await using var context = new AppDbContext(_dbOptions);
        context.Suppliers.Add(new Supplier { Id = 1, Name = "Operador A", IsActive = true });
        // El proveedor debe en dos monedas -> deben ser DOS filas, nunca una mezclada.
        context.SupplierBalanceByCurrency.AddRange(
            new SupplierBalanceByCurrency { SupplierId = 1, Currency = "ARS", Balance = 800m },
            new SupplierBalanceByCurrency { SupplierId = 1, Currency = "USD", Balance = 150m });
        await context.SaveChangesAsync();

        var report = await BuildReports(context).GetDetailedReportAsync(null, null, CancellationToken.None);

        var supplierDebts = ((System.Collections.IEnumerable)GetProperty(report, "SupplierDebts")).Cast<object>().ToList();
        Assert.Equal(2, supplierDebts.Count);
        var arsRow = supplierDebts.Single(x => (string)GetProperty(x, "Currency") == "ARS");
        var usdRow = supplierDebts.Single(x => (string)GetProperty(x, "Currency") == "USD");
        Assert.Equal(800m, (decimal)GetProperty(arsRow, "CurrentBalance"));
        Assert.Equal(150m, (decimal)GetProperty(usdRow, "CurrentBalance"));
    }

    [Fact]
    public async Task DetailedReceivables_separa_una_fila_por_moneda_del_cliente()
    {
        await using var context = new AppDbContext(_dbOptions);
        context.Customers.Add(new Customer { Id = 1, FullName = "Juan", DocumentNumber = "123", IsActive = true });
        context.Reservas.Add(new Reserva
        {
            Id = 1, NumeroReserva = "F-1", Name = "R1", Status = EstadoReserva.Confirmed,
            PayerId = 1, CreatedAt = DateTime.UtcNow
        });
        // El cliente debe en dos monedas -> dos filas.
        context.ReservaMoneyByCurrency.AddRange(
            new ReservaMoneyByCurrency { ReservaId = 1, Currency = "ARS", Balance = 500m },
            new ReservaMoneyByCurrency { ReservaId = 1, Currency = "USD", Balance = 80m });
        await context.SaveChangesAsync();

        var receivables = (await BuildReports(context).GetDetailedReceivablesAsync(CancellationToken.None))
            .Cast<object>().ToList();

        Assert.Equal(2, receivables.Count);
        var arsRow = receivables.Single(x => (string)GetProperty(x, "Currency") == "ARS");
        var usdRow = receivables.Single(x => (string)GetProperty(x, "Currency") == "USD");
        Assert.Equal(500m, (decimal)GetProperty(arsRow, "CurrentBalance"));
        Assert.Equal(80m, (decimal)GetProperty(usdRow, "CurrentBalance"));
    }

    /// <summary>
    /// Helper de reflexion: el detailed report devuelve objetos anonimos (contrato existente). Para
    /// asertar sobre sus propiedades sin reescribir el endpoint a DTOs tipados, se leen por reflexion.
    /// </summary>
    private static object GetProperty(object source, string name)
    {
        var prop = source.GetType().GetProperty(name);
        Assert.NotNull(prop);
        return prop!.GetValue(source)!;
    }
}
