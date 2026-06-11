using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.Interfaces;
using TravelApi.Application.Mappings;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-021 Capa 4 (cobro cruzado, 2026-06-10): tests de integracion del registro/reversa/edicion de un
/// pago CRUZADO (moneda real != moneda del saldo) sobre PaymentService con EF InMemory.
///
/// <para>Cubre §2.7/§2.8 (B3): (a) un cobro cruzado ARS->saldo USD baja la deuda USD por el equivalente
/// imputado, no por el Amount de caja; (b) anular ese pago devuelve el saldo USD (self-healing via
/// recalculo); (c) editar el Amount de un pago cruzado se RECHAZA (anular+recrear); (d) editar
/// Method/Reference de un pago cruzado se permite.</para>
/// </summary>
public class Adr021CrossCurrencyPaymentServiceTests
{
    private readonly DbContextOptions<AppDbContext> _dbOptions;
    private readonly IMapper _mapper;
    private readonly Mock<IOperationalFinanceSettingsService> _settingsServiceMock;

    public Adr021CrossCurrencyPaymentServiceTests()
    {
        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        _mapper = new MapperConfiguration(c => c.AddProfile<MappingProfile>()).CreateMapper();

        _settingsServiceMock = new Mock<IOperationalFinanceSettingsService>();
        _settingsServiceMock
            .Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings());
    }

    private PaymentService BuildService(AppDbContext context) =>
        new(context, new EntityReferenceResolver(context), _mapper, _settingsServiceMock.Object,
            NullLogger<PaymentService>.Instance);

    /// <summary>Reserva con un unico servicio en USD (saldo a cobrar en USD) confirmado.</summary>
    private static async Task<Reserva> SeedUsdReservaAsync(AppDbContext context, decimal usdSale)
    {
        var reserva = new Reserva
        {
            Id = 1,
            NumeroReserva = "F-2026-0500",
            Name = "Reserva USD",
            Status = EstadoReserva.Confirmed
        };
        context.Reservas.Add(reserva);
        context.HotelBookings.Add(new HotelBooking
        {
            ReservaId = 1,
            Status = "Confirmado",
            Currency = Monedas.USD,
            SalePrice = usdSale,
            NetCost = 0m
        });
        await context.SaveChangesAsync();

        // Sincroniza escalar + tabla hija (estado inicial coherente).
        await TravelApi.Infrastructure.Reservations.ReservaMoneyPersister.PersistAsync(context, 1, CancellationToken.None);
        return reserva;
    }

    [Fact]
    public async Task CrossPayment_ArsToUsdSaldo_LowersUsdDebtByImputedAmount_NotByCashAmount()
    {
        await using var context = new AppDbContext(_dbOptions);
        var reserva = await SeedUsdReservaAsync(context, usdSale: 100m);
        var service = BuildService(context);

        // Cliente debe USD 100; paga ARS 100.000 imputado a USD con TC 1000 -> 100 USD imputados.
        var dto = await service.CreatePaymentAsync(new CreatePaymentRequest
        {
            ReservaId = reserva.PublicId.ToString(),
            Amount = 100000m,
            Method = "Transfer",
            Currency = "ARS",
            ImputedCurrency = "USD",
            ExchangeRate = 1000m,
            ExchangeRateSource = (int)ExchangeRateSource.Manual,
            ExchangeRateAt = DateTime.UtcNow
        }, CancellationToken.None);

        Assert.Equal(100000m, dto.Amount); // caja real intacta

        var payment = await context.Payments.AsNoTracking().FirstAsync();
        Assert.Equal("ARS", payment.Currency);
        Assert.Equal("USD", payment.ImputedCurrency);
        Assert.Equal(100m, payment.ImputedAmount);

        // La deuda USD bajo por el equivalente imputado (100), no por el Amount de caja (100000).
        var usdRow = await context.ReservaMoneyByCurrency.AsNoTracking()
            .FirstAsync(r => r.ReservaId == 1 && r.Currency == "USD");
        Assert.Equal(100m, usdRow.TotalPaid);
        Assert.Equal(0m, usdRow.Balance);
    }

    [Fact]
    public async Task CrossPayment_WhenDeleted_RestoresUsdDebt_SelfHealing()
    {
        await using var context = new AppDbContext(_dbOptions);
        var reserva = await SeedUsdReservaAsync(context, usdSale: 100m);
        var service = BuildService(context);

        var dto = await service.CreatePaymentAsync(new CreatePaymentRequest
        {
            ReservaId = reserva.PublicId.ToString(),
            Amount = 100000m,
            Method = "Transfer",
            Currency = "ARS",
            ImputedCurrency = "USD",
            ExchangeRate = 1000m,
            ExchangeRateSource = (int)ExchangeRateSource.Manual,
            ExchangeRateAt = DateTime.UtcNow
        }, CancellationToken.None);

        await service.DeletePaymentAsync(dto.PublicId.ToString(), CancellationToken.None);

        // El saldo USD vuelve al valor previo al pago (sube por el ImputedAmount, no por el Amount).
        var usdRow = await context.ReservaMoneyByCurrency.AsNoTracking()
            .FirstAsync(r => r.ReservaId == 1 && r.Currency == "USD");
        Assert.Equal(0m, usdRow.TotalPaid);
        Assert.Equal(100m, usdRow.Balance);
    }

    [Fact]
    public async Task CrossPayment_EditAmount_IsRejected()
    {
        await using var context = new AppDbContext(_dbOptions);
        var reserva = await SeedUsdReservaAsync(context, usdSale: 100m);
        var service = BuildService(context);

        var dto = await service.CreatePaymentAsync(new CreatePaymentRequest
        {
            ReservaId = reserva.PublicId.ToString(),
            Amount = 100000m,
            Method = "Transfer",
            Currency = "ARS",
            ImputedCurrency = "USD",
            ExchangeRate = 1000m,
            ExchangeRateSource = (int)ExchangeRateSource.Manual,
            ExchangeRateAt = DateTime.UtcNow
        }, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UpdatePaymentAsync(dto.PublicId.ToString(), new UpdatePaymentRequest
            {
                Amount = 90000m, // cambia el monto -> rechazado
                Method = "Transfer"
            }, CancellationToken.None));
    }

    [Fact]
    public async Task CrossPayment_EditMethodOnly_IsAllowed()
    {
        await using var context = new AppDbContext(_dbOptions);
        var reserva = await SeedUsdReservaAsync(context, usdSale: 100m);
        var service = BuildService(context);

        var dto = await service.CreatePaymentAsync(new CreatePaymentRequest
        {
            ReservaId = reserva.PublicId.ToString(),
            Amount = 100000m,
            Method = "Transfer",
            Currency = "ARS",
            ImputedCurrency = "USD",
            ExchangeRate = 1000m,
            ExchangeRateSource = (int)ExchangeRateSource.Manual,
            ExchangeRateAt = DateTime.UtcNow
        }, CancellationToken.None);

        // Mismo Amount, solo cambia Method/Reference -> permitido.
        await service.UpdatePaymentAsync(dto.PublicId.ToString(), new UpdatePaymentRequest
        {
            Amount = 100000m,
            Method = "Cash",
            Reference = "REF-CHANGED"
        }, CancellationToken.None);

        var payment = await context.Payments.AsNoTracking().FirstAsync();
        Assert.Equal("Cash", payment.Method);
        Assert.Equal("REF-CHANGED", payment.Reference);
        Assert.Equal(100000m, payment.Amount); // intacto
    }
}
