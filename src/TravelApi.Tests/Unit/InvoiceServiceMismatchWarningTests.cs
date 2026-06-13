using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Hangfire;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Application.Mappings;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Hallazgo auditoria ERP #9 (2026-06-13): al crear una factura, el service compara la suma de los
/// items facturados contra la venta confirmada de la reserva en esa moneda y devuelve un AVISO no
/// bloqueante (InvoiceDto.Warning) si no cuadra. Estos tests prueban end-to-end (InMemory + mocks)
/// que el aviso aparece cuando descuadra, NO aparece cuando cuadra, y que NO bloquea la emision
/// (la factura igual se crea y el job se encola).
/// </summary>
public class InvoiceServiceMismatchWarningTests
{
    private readonly DbContextOptions<AppDbContext> _dbOptions;
    private readonly IMapper _mapper;
    private readonly Mock<IBackgroundJobClient> _jobClientMock;
    private readonly Mock<IAfipService> _afipMock;
    private readonly Mock<IOperationalFinanceSettingsService> _settingsMock;

    // PublicId fijo de la reserva sembrada: el service resuelve la reserva por su PublicId (Guid),
    // asi que el request debe traer este string, no el id interno "1".
    private static readonly Guid ReservaPublicId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public InvoiceServiceMismatchWarningTests()
    {
        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _mapper = new MapperConfiguration(c => c.AddProfile<MappingProfile>()).CreateMapper();
        _jobClientMock = new Mock<IBackgroundJobClient>();
        _afipMock = new Mock<IAfipService>();
        _settingsMock = new Mock<IOperationalFinanceSettingsService>();
        _settingsMock
            .Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings());
    }

    private static UserManager<ApplicationUser> BuildUserManager()
    {
        var store = new Mock<IUserStore<ApplicationUser>>();
        return new UserManager<ApplicationUser>(
            store.Object, null!, null!,
            Array.Empty<IUserValidator<ApplicationUser>>(),
            Array.Empty<IPasswordValidator<ApplicationUser>>(),
            null!, null!, null!, null!);
    }

    private InvoiceService BuildService(AppDbContext ctx)
        => new(ctx,
               new EntityReferenceResolver(ctx),
               _afipMock.Object,
               Mock.Of<IInvoicePdfService>(),
               _mapper,
               _jobClientMock.Object,
               NullLogger<InvoiceService>.Instance,
               _settingsMock.Object,
               BuildUserManager());

    /// <summary>
    /// Siembra una reserva facturable (Confirmed, sin saldo) con su linea de venta confirmada en ARS,
    /// y configura el mock de AFIP para devolver una Invoice PENDING (como hace CreatePendingInvoice).
    /// </summary>
    private async Task SeedAsync(AppDbContext ctx, decimal confirmedSaleArs)
    {
        ctx.Reservas.Add(new Reserva
        {
            Id = 1,
            PublicId = ReservaPublicId,
            NumeroReserva = "F-9-1",
            Name = "Mismatch test",
            Status = EstadoReserva.Confirmed,
            Balance = 0m
        });
        ctx.ReservaMoneyByCurrency.Add(new ReservaMoneyByCurrency
        {
            ReservaId = 1,
            Currency = Monedas.ARS,
            ConfirmedSale = confirmedSaleArs,
            Balance = 0m
        });
        await ctx.SaveChangesAsync();

        _afipMock
            .Setup(a => a.CreatePendingInvoice(It.IsAny<int>(), It.IsAny<CreateInvoiceRequest>()))
            .ReturnsAsync(new Invoice { Id = 100, ReservaId = 1, Resultado = "PENDING", ImporteTotal = 0m });
    }

    private static CreateInvoiceRequest BuildRequest(params decimal[] itemTotals)
    {
        var request = new CreateInvoiceRequest { ReservaId = ReservaPublicId.ToString() };
        foreach (var total in itemTotals)
        {
            request.Items.Add(new InvoiceItemDto
            {
                Description = "Servicio",
                Quantity = 1m,
                UnitPrice = total,
                Total = total,
                AlicuotaIvaId = 3
            });
        }
        return request;
    }

    [Fact]
    public async Task CreateAsync_ItemsMatchConfirmedSale_NoWarning()
    {
        await using var ctx = new AppDbContext(_dbOptions);
        await SeedAsync(ctx, confirmedSaleArs: 1000m);
        var service = BuildService(ctx);

        var dto = await service.CreateAsync(BuildRequest(600m, 400m), "u1", "User 1", CancellationToken.None);

        Assert.Null(dto.Warning);
        // La emision igual ocurrio: el job se encolo.
        _jobClientMock.Verify(
            c => c.Create(It.IsAny<Hangfire.Common.Job>(), It.IsAny<Hangfire.States.IState>()),
            Times.Once);
    }

    [Fact]
    public async Task CreateAsync_ItemsDoNotMatch_ReturnsWarningButStillEmits()
    {
        await using var ctx = new AppDbContext(_dbOptions);
        await SeedAsync(ctx, confirmedSaleArs: 1000m);
        var service = BuildService(ctx);

        // Facturamos 1200 contra 1000 confirmados -> descuadre.
        var dto = await service.CreateAsync(BuildRequest(1200m), "u1", "User 1", CancellationToken.None);

        Assert.NotNull(dto.Warning);
        // NO bloquea: la factura se creo y el job se encolo igual.
        _jobClientMock.Verify(
            c => c.Create(It.IsAny<Hangfire.Common.Job>(), It.IsAny<Hangfire.States.IState>()),
            Times.Once);
    }

    [Fact]
    public async Task CreateAsync_CentToleranceDifference_NoWarning()
    {
        await using var ctx = new AppDbContext(_dbOptions);
        await SeedAsync(ctx, confirmedSaleArs: 1000.00m);
        var service = BuildService(ctx);

        // 1 centavo de diferencia (redondeo) no debe disparar aviso.
        var dto = await service.CreateAsync(BuildRequest(1000.01m), "u1", "User 1", CancellationToken.None);

        Assert.Null(dto.Warning);
    }
}
