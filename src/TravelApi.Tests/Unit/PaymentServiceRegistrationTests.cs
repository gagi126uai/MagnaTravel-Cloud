using System;
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
/// Red de seguridad para refactor C17 (colapsar TravelReservations.Api).
///
/// PaymentService es uno de los servicios que hoy se registra condicionalmente
/// en TravelApi/Program.cs (HttpProxy si Services:Reservations:BaseUrl esta
/// seteado, in-process si no). Tras el refactor, solo queda la rama in-process.
/// Estos tests validan el comportamiento de la rama in-process: registro de pagos
/// y recalculo de Balance/TotalPaid en la Reserva.
///
/// Uso EF Core InMemory siguiendo el patron de PaymentService (recalculo de balance).
/// La regla de no-overbalance NO esta implementada en el codigo actual: se documenta
/// como bug observado, no se asume.
/// </summary>
public class PaymentServiceRegistrationTests
{
    private readonly DbContextOptions<AppDbContext> _dbOptions;
    private readonly IMapper _mapper;
    private readonly Mock<IOperationalFinanceSettingsService> _settingsServiceMock;

    public PaymentServiceRegistrationTests()
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

    private PaymentService BuildService(AppDbContext context)
    {
        return new PaymentService(
            context,
            new EntityReferenceResolver(context),
            _mapper,
            _settingsServiceMock.Object,
            NullLogger<PaymentService>.Instance);
    }

    private static async Task<Reserva> SeedConfirmedReservaAsync(
        AppDbContext context,
        decimal salePrice,
        decimal netCost = 0m)
    {
        var reserva = new Reserva
        {
            Id = 1,
            NumeroReserva = "F-2026-0001",
            Name = "Reserva test",
            Status = EstadoReserva.Confirmed,
            TotalSale = salePrice,
            TotalCost = netCost,
            Balance = salePrice,
            TotalPaid = 0m
        };
        context.Reservas.Add(reserva);
        // Servicio que sustenta TotalSale (RecalculateReservaBalanceAsync recalcula
        // a partir de los servicios; sin servicio, TotalSale se reescribiria a 0).
        context.Servicios.Add(new ServicioReserva
        {
            Id = 1,
            ReservaId = 1,
            ServiceType = "Hotel",
            ProductType = "Hotel",
            Description = "Servicio sustento",
            ConfirmationNumber = "ABC123",
            Status = "Confirmado",
            DepartureDate = DateTime.UtcNow.AddDays(15),
            SalePrice = salePrice,
            NetCost = netCost,
            Commission = salePrice - netCost,
            CreatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
        return reserva;
    }

    [Fact]
    public async Task CreatePaymentAsync_RegistersPayment_AndRecalculatesBalance()
    {
        await using var context = new AppDbContext(_dbOptions);
        var reserva = await SeedConfirmedReservaAsync(context, salePrice: 1000m);

        var service = BuildService(context);

        var dto = await service.CreatePaymentAsync(
            new CreatePaymentRequest
            {
                ReservaId = reserva.PublicId.ToString(),
                Amount = 300m,
                Method = "Transfer",
                Reference = "TX-1"
            },
            CancellationToken.None);

        Assert.NotNull(dto);
        Assert.Equal(300m, dto.Amount);
        Assert.Equal("Paid", dto.Status);

        var refreshed = await context.Reservas.AsNoTracking().FirstAsync(r => r.Id == 1);
        Assert.Equal(300m, refreshed.TotalPaid);
        Assert.Equal(700m, refreshed.Balance);

        var paymentsCount = await context.Payments.CountAsync();
        Assert.Equal(1, paymentsCount);
    }

    [Fact]
    public async Task CreatePaymentAsync_TwoPaymentsThatSumTotalSale_LeaveBalanceZero()
    {
        await using var context = new AppDbContext(_dbOptions);
        var reserva = await SeedConfirmedReservaAsync(context, salePrice: 1000m);

        var service = BuildService(context);

        await service.CreatePaymentAsync(
            new CreatePaymentRequest
            {
                ReservaId = reserva.PublicId.ToString(),
                Amount = 300m,
                Method = "Transfer"
            },
            CancellationToken.None);

        await service.CreatePaymentAsync(
            new CreatePaymentRequest
            {
                ReservaId = reserva.PublicId.ToString(),
                Amount = 700m,
                Method = "Cash"
            },
            CancellationToken.None);

        var refreshed = await context.Reservas.AsNoTracking().FirstAsync(r => r.Id == 1);
        Assert.Equal(1000m, refreshed.TotalPaid);
        Assert.Equal(0m, refreshed.Balance);
    }

    [Fact]
    public async Task CreatePaymentAsync_OnBudgetReservation_Throws()
    {
        await using var context = new AppDbContext(_dbOptions);
        context.Reservas.Add(new Reserva
        {
            Id = 1,
            NumeroReserva = "F-2026-0099",
            Name = "Reserva budget",
            Status = EstadoReserva.Budget,
            TotalSale = 500m,
            Balance = 500m
        });
        await context.SaveChangesAsync();

        var service = BuildService(context);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreatePaymentAsync(
                new CreatePaymentRequest
                {
                    ReservaId = context.Reservas.First().PublicId.ToString(),
                    Amount = 100m,
                    Method = "Transfer"
                },
                CancellationToken.None));
    }

    [Fact]
    public async Task CreatePaymentAsync_WithZeroAmount_Throws()
    {
        await using var context = new AppDbContext(_dbOptions);
        var reserva = await SeedConfirmedReservaAsync(context, salePrice: 500m);

        var service = BuildService(context);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreatePaymentAsync(
                new CreatePaymentRequest
                {
                    ReservaId = reserva.PublicId.ToString(),
                    Amount = 0m,
                    Method = "Transfer"
                },
                CancellationToken.None));
    }

    /// <summary>
    /// Comportamiento ACTUAL (no necesariamente deseado): el codigo no valida que
    /// Amount no exceda Balance. Este test documenta y bloquea regresion silenciosa
    /// en cualquier sentido — si el equipo decide agregar la validacion, tiene
    /// que actualizar este test deliberadamente.
    /// </summary>
    [Fact]
    public async Task CreatePaymentAsync_OverBalanceAmount_IsCurrentlyAccepted_AndProducesNegativeBalance()
    {
        await using var context = new AppDbContext(_dbOptions);
        var reserva = await SeedConfirmedReservaAsync(context, salePrice: 1000m);

        var service = BuildService(context);

        var dto = await service.CreatePaymentAsync(
            new CreatePaymentRequest
            {
                ReservaId = reserva.PublicId.ToString(),
                Amount = 1500m,
                Method = "Transfer"
            },
            CancellationToken.None);

        Assert.Equal(1500m, dto.Amount);

        var refreshed = await context.Reservas.AsNoTracking().FirstAsync(r => r.Id == 1);
        Assert.Equal(1500m, refreshed.TotalPaid);
        // Balance negativo = sobrepago. Sin validacion de overbalance, el saldo
        // queda en -500.
        Assert.Equal(-500m, refreshed.Balance);
    }

    [Fact]
    public async Task DeletePaymentAsync_SoftDeletesPayment_AndExcludesItFromBalance()
    {
        await using var context = new AppDbContext(_dbOptions);
        var reserva = await SeedConfirmedReservaAsync(context, salePrice: 1000m);

        var service = BuildService(context);

        var p1 = await service.CreatePaymentAsync(
            new CreatePaymentRequest
            {
                ReservaId = reserva.PublicId.ToString(),
                Amount = 400m,
                Method = "Transfer"
            },
            CancellationToken.None);

        var p2 = await service.CreatePaymentAsync(
            new CreatePaymentRequest
            {
                ReservaId = reserva.PublicId.ToString(),
                Amount = 200m,
                Method = "Cash"
            },
            CancellationToken.None);

        var beforeDelete = await context.Reservas.AsNoTracking().FirstAsync(r => r.Id == 1);
        Assert.Equal(600m, beforeDelete.TotalPaid);
        Assert.Equal(400m, beforeDelete.Balance);

        await service.DeletePaymentAsync(p2.PublicId.ToString(), CancellationToken.None);

        // El pago sigue existiendo en la tabla pero esta IsDeleted=true
        // (soft delete), y NO debe contar en TotalPaid/Balance.
        var deletedPayment = await context.Payments
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstAsync(p => p.PublicId == p2.PublicId);
        Assert.True(deletedPayment.IsDeleted);
        Assert.NotNull(deletedPayment.DeletedAt);

        var afterDelete = await context.Reservas.AsNoTracking().FirstAsync(r => r.Id == 1);
        Assert.Equal(400m, afterDelete.TotalPaid);
        Assert.Equal(600m, afterDelete.Balance);
    }
}
