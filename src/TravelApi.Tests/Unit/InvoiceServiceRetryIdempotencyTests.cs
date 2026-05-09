using System;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Hangfire;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.Interfaces;
using TravelApi.Application.Mappings;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// B1.15 Fase 0' (CODE-02): InvoiceService.RetryAsync debe rechazar reintentar
/// la emision cuando la factura esta en flow de anulacion. Antes:
///  - Pending: el job de anulacion ya esta encolado/corriendo. Reintentar
///    re-arranca el flow de emision sobre una factura que esta a punto de
///    quedar anulada — racing fiscal grave (puede emitirse dos veces).
///  - Succeeded: la NC ya fue aprobada por AFIP. Reintentar emision no tiene
///    sentido — la factura ya fue cerrada.
///  - Failed: el job fallo (timeout AFIP, etc.). Reintentar emision es OK
///    porque la factura sigue viva.
/// </summary>
public class InvoiceServiceRetryIdempotencyTests
{
    private readonly DbContextOptions<AppDbContext> _dbOptions;
    private readonly IMapper _mapper;
    private readonly Mock<IBackgroundJobClient> _jobClientMock;
    private readonly Mock<IAfipService> _afipMock;
    private readonly Mock<IOperationalFinanceSettingsService> _settingsMock;

    public InvoiceServiceRetryIdempotencyTests()
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

    private static async Task<Invoice> SeedInvoiceAsync(AppDbContext ctx, AnnulmentStatus status, string resultado = "PENDING")
    {
        ctx.Reservas.Add(new Reserva
        {
            Id = 1,
            NumeroReserva = "F-RETRY-1",
            Name = "Retry test",
            Status = EstadoReserva.Confirmed
        });
        var inv = new Invoice
        {
            Id = 1,
            ReservaId = 1,
            CAE = "012345",
            Resultado = resultado,
            AnnulmentStatus = status,
            ImporteTotal = 100m,
            ImporteNeto = 82.64m,
            ImporteIva = 17.36m
        };
        ctx.Invoices.Add(inv);
        await ctx.SaveChangesAsync();
        return inv;
    }

    [Fact]
    public async Task RetryAsync_AnnulmentPending_Throws()
    {
        await using var ctx = new AppDbContext(_dbOptions);
        var inv = await SeedInvoiceAsync(ctx, AnnulmentStatus.Pending);
        var service = BuildService(ctx);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.RetryAsync(inv.Id, CancellationToken.None));
        Assert.Contains("anulacion", ex.Message, StringComparison.OrdinalIgnoreCase);
        // No se debe haber encolado un nuevo job ni reseteado el Resultado.
        _jobClientMock.Verify(
            c => c.Create(It.IsAny<Hangfire.Common.Job>(), It.IsAny<Hangfire.States.IState>()),
            Times.Never);
    }

    [Fact]
    public async Task RetryAsync_AnnulmentSucceeded_Throws()
    {
        await using var ctx = new AppDbContext(_dbOptions);
        var inv = await SeedInvoiceAsync(ctx, AnnulmentStatus.Succeeded);
        var service = BuildService(ctx);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.RetryAsync(inv.Id, CancellationToken.None));
        Assert.Contains("anulada", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RetryAsync_AnnulmentFailed_AllowsRetry()
    {
        // Failed = la NC fallo en AFIP. La factura sigue viva — reintento OK.
        await using var ctx = new AppDbContext(_dbOptions);
        var inv = await SeedInvoiceAsync(ctx, AnnulmentStatus.Failed);
        var service = BuildService(ctx);

        var result = await service.RetryAsync(inv.Id, CancellationToken.None);
        Assert.True(result);
    }

    [Fact]
    public async Task RetryAsync_AnnulmentNone_AllowsRetry()
    {
        await using var ctx = new AppDbContext(_dbOptions);
        var inv = await SeedInvoiceAsync(ctx, AnnulmentStatus.None);
        var service = BuildService(ctx);

        var result = await service.RetryAsync(inv.Id, CancellationToken.None);
        Assert.True(result);
    }

    [Fact]
    public async Task RetryAsync_ResultadoApproved_Throws_PreservesExistingMessage()
    {
        // Pre-existente (no Fase 0'): factura ya aprobada (Resultado="A") rechaza.
        // Mantener pinned para no regresar el comportamiento al refactorizar.
        await using var ctx = new AppDbContext(_dbOptions);
        var inv = await SeedInvoiceAsync(ctx, AnnulmentStatus.None, resultado: "A");
        var service = BuildService(ctx);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.RetryAsync(inv.Id, CancellationToken.None));
        Assert.Contains("aprobada", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
