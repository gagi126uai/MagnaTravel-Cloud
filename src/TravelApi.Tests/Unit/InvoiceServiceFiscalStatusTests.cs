using System;
using System.Linq;
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
/// H2 (2026-06-24): estado fiscal CLARO de las facturas de una reserva (endpoint de POLL). La emision es
/// asincrona; este metodo SOLO LEE el resultado ya persistido por el job que pide el CAE y lo traduce a
/// InProcess/Issued/Rejected. Cubre los tres estados, el motivo de rechazo y que el motivo NO se filtre
/// cuando la factura sigue en proceso.
/// </summary>
public class InvoiceServiceFiscalStatusTests
{
    private readonly DbContextOptions<AppDbContext> _dbOptions;
    private readonly IMapper _mapper;

    public InvoiceServiceFiscalStatusTests()
    {
        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _mapper = new MapperConfiguration(c => c.AddProfile<MappingProfile>()).CreateMapper();
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

    private InvoiceService BuildService(AppDbContext context)
    {
        var settings = new Mock<IOperationalFinanceSettingsService>();
        settings.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OperationalFinanceSettings());
        return new InvoiceService(
            context, new EntityReferenceResolver(context), new Mock<IAfipService>().Object,
            new Mock<IInvoicePdfService>().Object, _mapper, new Mock<IBackgroundJobClient>().Object,
            NullLogger<InvoiceService>.Instance, settings.Object, BuildUserManager(),
            permissionResolver: null, httpContextAccessor: null);
    }

    private static async Task SeedReservaAsync(AppDbContext context)
    {
        context.Reservas.Add(new Reserva
        {
            Id = 1,
            PublicId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            NumeroReserva = "F-001",
            Name = "Reserva test",
            Status = EstadoReserva.Confirmed
        });
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task PendingInvoice_MapsToInProcess_AndHidesObservaciones()
    {
        using var context = new AppDbContext(_dbOptions);
        await SeedReservaAsync(context);
        context.Invoices.Add(new Invoice
        {
            Id = 10,
            ReservaId = 1,
            Resultado = "PENDING",
            // Observaciones transitorias mientras espera a ARCA: NO es un rechazo definitivo y no debe
            // exponerse como motivo de rechazo.
            Observaciones = "AFIP respondio con un error de red. Reintenta en unos segundos.",
            TipoComprobante = 6,
            CreatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        var service = BuildService(context);
        var result = (await service.GetFiscalStatusByReservaIdAsync(1, CancellationToken.None)).ToList();

        var dto = Assert.Single(result);
        Assert.Equal("InProcess", dto.Status);
        Assert.Null(dto.RejectionReason); // no se filtra el texto transitorio
    }

    [Fact]
    public async Task ApprovedInvoice_MapsToIssued_WithCaeAndNumber()
    {
        using var context = new AppDbContext(_dbOptions);
        await SeedReservaAsync(context);
        context.Invoices.Add(new Invoice
        {
            Id = 11,
            ReservaId = 1,
            Resultado = "A",
            TipoComprobante = 1, // Factura A
            PuntoDeVenta = 4,
            NumeroComprobante = 12345,
            CAE = "74123456789012",
            VencimientoCAE = new DateTime(2026, 7, 10, 0, 0, 0, DateTimeKind.Utc),
            ImporteTotal = 1500m,
            MonId = "PES",
            CreatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        var service = BuildService(context);
        var result = (await service.GetFiscalStatusByReservaIdAsync(1, CancellationToken.None)).ToList();

        var dto = Assert.Single(result);
        Assert.Equal("Issued", dto.Status);
        Assert.Equal("A", dto.InvoiceType);
        Assert.Equal(4, dto.PuntoDeVenta);
        Assert.Equal(12345, dto.NumeroComprobante);
        Assert.Equal("74123456789012", dto.CAE);
        Assert.NotNull(dto.VencimientoCAE);
        Assert.Equal(1500m, dto.ImporteTotal);
        Assert.Equal("PES", dto.MonId);
        Assert.Null(dto.RejectionReason);
    }

    [Fact]
    public async Task RejectedInvoice_MapsToRejected_AndExposesReason()
    {
        using var context = new AppDbContext(_dbOptions);
        await SeedReservaAsync(context);
        context.Invoices.Add(new Invoice
        {
            Id = 12,
            ReservaId = 1,
            Resultado = "R",
            // Motivo ya traducido por el backend (TranslateAfipError). El front lo necesita para "Corregir
            // y reintentar".
            Observaciones = "CUIT del receptor invalido: el documento del cliente no existe en AFIP.",
            TipoComprobante = 6,
            CreatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        var service = BuildService(context);
        var result = (await service.GetFiscalStatusByReservaIdAsync(1, CancellationToken.None)).ToList();

        var dto = Assert.Single(result);
        Assert.Equal("Rejected", dto.Status);
        Assert.Equal("CUIT del receptor invalido: el documento del cliente no existe en AFIP.", dto.RejectionReason);
    }

    [Fact]
    public async Task ReturnsMostRecentFirst()
    {
        using var context = new AppDbContext(_dbOptions);
        await SeedReservaAsync(context);
        context.Invoices.Add(new Invoice
        {
            Id = 20, ReservaId = 1, Resultado = "A", TipoComprobante = 6,
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        });
        context.Invoices.Add(new Invoice
        {
            Id = 21, ReservaId = 1, Resultado = "PENDING", TipoComprobante = 6,
            CreatedAt = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)
        });
        await context.SaveChangesAsync();

        var service = BuildService(context);
        var result = (await service.GetFiscalStatusByReservaIdAsync(1, CancellationToken.None)).ToList();

        Assert.Equal(2, result.Count);
        Assert.Equal("InProcess", result[0].Status); // la mas nueva primero
        Assert.Equal("Issued", result[1].Status);
    }
}
