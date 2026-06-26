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
/// Fase 4 (2026-06-26): PRE-CHEQUEO de emisión de Factura A. El backend deriva la letra (NO el vendedor) con la
/// MISMA matriz fiscal que CreatePendingInvoice (confirmada por dueño + contador en InvoiceTypeResolver) y avisa
/// el único bloqueo duro: cliente que recibiría Factura A pero sin CUIT (ARCA rebota). Regla de oro: ante
/// cualquier duda que NO sea "A sin CUIT", allowed=true.
/// </summary>
public class InvoiceServiceEmissionPreflightTests
{
    // CUIT 20-12345678-6: dígito verificador válido por el algoritmo módulo 11 (mismo que ArcaReceptorResolver).
    private const string ValidCuit = "20123456786";

    private readonly DbContextOptions<AppDbContext> _dbOptions;
    private readonly IMapper _mapper;

    public InvoiceServiceEmissionPreflightTests()
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

    /// <summary>Siembra el emisor (AfipSettings) y una reserva con su cliente pagador. Devuelve la reservaId.</summary>
    private static async Task<int> SeedAsync(
        AppDbContext context,
        string emisorTaxCondition,
        string? customerTaxCondition,
        string? customerTaxId = null,
        string? customerDocumentType = null,
        string? customerDocumentNumber = null)
    {
        context.AfipSettings.Add(new AfipSettings { TaxCondition = emisorTaxCondition });

        var customer = new Customer
        {
            Id = 50,
            FullName = "Cliente Test",
            TaxCondition = customerTaxCondition,
            TaxId = customerTaxId,
            DocumentType = customerDocumentType,
            DocumentNumber = customerDocumentNumber,
        };
        context.Customers.Add(customer);

        context.Reservas.Add(new Reserva
        {
            Id = 1,
            PublicId = Guid.NewGuid(),
            NumeroReserva = "F-PRE",
            Name = "Reserva preflight",
            Status = EstadoReserva.Confirmed,
            PayerId = 50,
        });
        await context.SaveChangesAsync();
        return 1;
    }

    [Fact]
    public async Task RI_ConsumidorFinal_EmiteB_Allowed()
    {
        using var context = new AppDbContext(_dbOptions);
        var reservaId = await SeedAsync(context, "Responsable Inscripto", "Consumidor Final");

        var result = await BuildService(context).GetEmissionPreflightAsync(reservaId, CancellationToken.None);

        Assert.Equal("B", result.WillEmitLetter);
        Assert.True(result.Allowed);
        Assert.Equal("ok", result.Severity);
        Assert.Empty(result.MissingData);
    }

    [Fact]
    public async Task RI_MonotributoConCuit_EmiteA_Allowed_NoFrena()
    {
        using var context = new AppDbContext(_dbOptions);
        // RI -> Monotributo es Factura A (Ley 27.618). Con CUIT cargado NO se frena.
        var reservaId = await SeedAsync(context, "Responsable Inscripto", "Monotributo", customerTaxId: ValidCuit);

        var result = await BuildService(context).GetEmissionPreflightAsync(reservaId, CancellationToken.None);

        Assert.Equal("A", result.WillEmitLetter);
        Assert.True(result.Allowed);
        Assert.Equal("ok", result.Severity);
        Assert.Empty(result.MissingData);
    }

    [Fact]
    public async Task RI_RISinCuit_EmiteA_Bloquea_MissingCuit()
    {
        using var context = new AppDbContext(_dbOptions);
        // RI -> RI es Factura A, pero el cliente no tiene CUIT (ni documento) -> bloqueo duro.
        var reservaId = await SeedAsync(context, "Responsable Inscripto", "Responsable Inscripto");

        var result = await BuildService(context).GetEmissionPreflightAsync(reservaId, CancellationToken.None);

        Assert.Equal("A", result.WillEmitLetter);
        Assert.False(result.Allowed);
        Assert.Equal("block", result.Severity);
        Assert.Contains("CUIT", result.MissingData);
        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
    }

    [Fact]
    public async Task RI_RIConCuit_EmiteA_Allowed()
    {
        using var context = new AppDbContext(_dbOptions);
        var reservaId = await SeedAsync(context, "Responsable Inscripto", "Responsable Inscripto", customerTaxId: ValidCuit);

        var result = await BuildService(context).GetEmissionPreflightAsync(reservaId, CancellationToken.None);

        Assert.Equal("A", result.WillEmitLetter);
        Assert.True(result.Allowed);
        Assert.Equal("ok", result.Severity);
    }

    [Fact]
    public async Task RI_CondicionBasura_Warn_Allowed_LetraB()
    {
        using var context = new AppDbContext(_dbOptions);
        // Condición de IVA no reconocida -> normaliza Unknown -> degrada a Factura B -> warn (no bloquea).
        var reservaId = await SeedAsync(context, "Responsable Inscripto", "Cualquier Cosa Rara");

        var result = await BuildService(context).GetEmissionPreflightAsync(reservaId, CancellationToken.None);

        Assert.Equal("B", result.WillEmitLetter);
        Assert.True(result.Allowed);
        Assert.Equal("warn", result.Severity);
        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
    }

    [Theory]
    [InlineData("Monotributo")]
    [InlineData("Exento")]
    public async Task EmisorMonoOExento_EmiteC_Allowed(string emisor)
    {
        using var context = new AppDbContext(_dbOptions);
        // Emisor Monotributo/Exento emite SIEMPRE C, sin importar el receptor (ni siquiera frena por CUIT).
        var reservaId = await SeedAsync(context, emisor, "Responsable Inscripto");

        var result = await BuildService(context).GetEmissionPreflightAsync(reservaId, CancellationToken.None);

        Assert.Equal("C", result.WillEmitLetter);
        Assert.True(result.Allowed);
        Assert.Equal("ok", result.Severity);
        Assert.Empty(result.MissingData);
    }

    [Fact]
    public async Task ReservaInexistente_Throws()
    {
        using var context = new AppDbContext(_dbOptions);
        await SeedAsync(context, "Responsable Inscripto", "Consumidor Final");

        // Otra reservaId que no existe -> InvalidOperationException (mismo contrato que suggested-items).
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BuildService(context).GetEmissionPreflightAsync(999, CancellationToken.None));
    }

    [Fact]
    public async Task ReservaSinCliente_Warn_Allowed()
    {
        using var context = new AppDbContext(_dbOptions);
        // AFIP configurado pero la reserva NO tiene cliente asignado (PayerId null). La emisión real rechazaría
        // "sin cliente asignado"; el preflight avisa (warn) sin frenar.
        context.AfipSettings.Add(new AfipSettings { TaxCondition = "Responsable Inscripto" });
        context.Reservas.Add(new Reserva
        {
            Id = 1,
            PublicId = Guid.NewGuid(),
            NumeroReserva = "F-SINCLI",
            Name = "Sin cliente",
            Status = EstadoReserva.Confirmed,
            PayerId = null,
        });
        await context.SaveChangesAsync();

        var result = await BuildService(context).GetEmissionPreflightAsync(1, CancellationToken.None);

        Assert.True(result.Allowed);
        Assert.Equal("warn", result.Severity);
        Assert.Contains("cliente", result.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AfipSinConfigurar_Warn_Allowed()
    {
        using var context = new AppDbContext(_dbOptions);
        // Sin fila de AfipSettings: la facturación electrónica no está configurada. La reserva SÍ tiene cliente.
        // La emisión real fallaría "AFIP no configurado"; el preflight avisa (warn) sin frenar.
        var customer = new Customer { Id = 60, FullName = "Cliente", TaxCondition = "Consumidor Final" };
        context.Customers.Add(customer);
        context.Reservas.Add(new Reserva
        {
            Id = 1,
            PublicId = Guid.NewGuid(),
            NumeroReserva = "F-NOAFIP",
            Name = "Sin AFIP",
            Status = EstadoReserva.Confirmed,
            PayerId = 60,
        });
        await context.SaveChangesAsync();

        var result = await BuildService(context).GetEmissionPreflightAsync(1, CancellationToken.None);

        Assert.True(result.Allowed);
        Assert.Equal("warn", result.Severity);
        Assert.Contains("configurada", result.Reason!, StringComparison.OrdinalIgnoreCase);
    }
}
