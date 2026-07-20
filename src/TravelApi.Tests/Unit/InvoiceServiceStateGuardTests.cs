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
/// Fix 2026-06-17 (auditoria de logica) + ADR-037 (2026-06-21, desacople de facturacion): la FACTURA de
/// venta normal solo se emite desde un estado FACTURABLE. ADR-037 desacoplo la factura del estado: el
/// conjunto facturable es {Confirmed, Traveling, Closed} (la venta firme no-anulada). Los anulados
/// (Cancelled/Lost/PendingOperatorRefund) y la pre-venta (Quotation/Budget/InManagement) siguen rechazados.
/// Las NC/ND SIGUEN permitidas sobre canceladas.
///
/// <para>Este test es el GATE de coherencia capacidades-CreateAsync: CanInvoiceSale.Allowed para un estado
/// debe equivaler a que CreateAsync NO rechace por estado en ese estado.</para>
/// </summary>
public class InvoiceServiceStateGuardTests
{
    private readonly DbContextOptions<AppDbContext> _dbOptions;
    private readonly IMapper _mapper;

    public InvoiceServiceStateGuardTests()
    {
        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _mapper = new MapperConfiguration(c => c.AddProfile<MappingProfile>()).CreateMapper();
    }

    private static readonly Guid ReservaPublicId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static UserManager<ApplicationUser> BuildUserManager()
    {
        var store = new Mock<IUserStore<ApplicationUser>>();
        return new UserManager<ApplicationUser>(
            store.Object, null!, null!,
            Array.Empty<IUserValidator<ApplicationUser>>(),
            Array.Empty<IPasswordValidator<ApplicationUser>>(),
            null!, null!, null!, null!);
    }

    private InvoiceService BuildService(AppDbContext context, out List<CreateInvoiceRequest> captured)
    {
        var settings = new Mock<IOperationalFinanceSettingsService>();
        settings.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OperationalFinanceSettings());
        var afip = new Mock<IAfipService>();
        var cap = new List<CreateInvoiceRequest>();
        captured = cap;
        afip.Setup(s => s.CreatePendingInvoice(It.IsAny<int>(), It.IsAny<CreateInvoiceRequest>()))
            .Callback<int, CreateInvoiceRequest>((_, req) => cap.Add(req))
            .ReturnsAsync(new Invoice { Id = 999, ReservaId = 1, TipoComprobante = 6, Resultado = "PENDING" });
        return new InvoiceService(
            context, new EntityReferenceResolver(context), afip.Object,
            new Mock<IInvoicePdfService>().Object, _mapper, new Mock<IBackgroundJobClient>().Object,
            NullLogger<InvoiceService>.Instance, settings.Object, BuildUserManager(),
            permissionResolver: null, httpContextAccessor: null);
    }

    private static async Task SeedReservaAsync(AppDbContext context, string status)
    {
        context.Reservas.Add(new Reserva
        {
            Id = 1,
            PublicId = ReservaPublicId,
            NumeroReserva = "F-GUARD-001",
            Name = "Reserva guard",
            Status = status,
            TotalSale = 1000m,
            Balance = 0m,
            TotalPaid = 1000m
        });
        await context.SaveChangesAsync();
    }

    private static CreateInvoiceRequest BuildRequest(bool isCreditNote) => new()
    {
        ReservaId = ReservaPublicId.ToString(),
        IsCreditNote = isCreditNote,
        IsDebitNote = false,
        Items = new List<InvoiceItemDto>
        {
            new() { Description = "Hotel", Quantity = 1, UnitPrice = 100m, Total = 100m, AlicuotaIvaId = 3 }
        }
    };

    [Theory]
    // Anulados: la venta no existe, NO facturable.
    [InlineData(EstadoReserva.Cancelled)]
    [InlineData(EstadoReserva.Lost)]
    [InlineData(EstadoReserva.PendingOperatorRefund)]
    // Pre-venta: servicios sin resolver / sin confirmar, NO facturable.
    [InlineData(EstadoReserva.Quotation)]
    [InlineData(EstadoReserva.Budget)]
    [InlineData(EstadoReserva.InManagement)]
    public async Task NormalInvoice_OnNonInvoiceableStatus_IsRejected(string status)
    {
        using var context = new AppDbContext(_dbOptions);
        await SeedReservaAsync(context, status);
        var service = BuildService(context, out _);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateAsync(BuildRequest(isCreditNote: false), "u1", "User 1", CancellationToken.None));
        Assert.Contains("No se puede facturar", ex.Message);
    }

    [Theory]
    [InlineData(EstadoReserva.Confirmed)]
    // ADR-037 (2026-06-21, desacople de facturacion): la factura se desacopla del estado. En viaje SI se
    // factura; en Finalizada (Closed) tambien, sin reabrir (se elimino el "reabrir para facturar").
    [InlineData(EstadoReserva.Traveling)]
    [InlineData(EstadoReserva.Closed)]
    public async Task NormalInvoice_OnInvoiceableStatus_PassesStateGuard(string status)
    {
        using var context = new AppDbContext(_dbOptions);
        await SeedReservaAsync(context, status);
        var service = BuildService(context, out var captured);

        await service.CreateAsync(BuildRequest(isCreditNote: false), "u1", "User 1", CancellationToken.None);

        Assert.Single(captured); // llego a emitir (no lo freno el guard de estado)
    }

    // Fix T5 (2026-07-20, contrato pantalla-motor): antes el mensaje de rechazo interpolaba el enum crudo
    // del estado (ej. "estado 'InManagement'"), un nombre tecnico en ingles a la vista del usuario. Este
    // test es el candado que evita que ese enum-leak vuelva a colarse: el mensaje real que ve el usuario
    // NUNCA debe contener el nombre interno del estado, sea cual sea el estado que lo genero.
    [Theory]
    [InlineData(EstadoReserva.InManagement)]
    [InlineData(EstadoReserva.Quotation)]
    [InlineData(EstadoReserva.Budget)]
    [InlineData(EstadoReserva.Cancelled)]
    [InlineData(EstadoReserva.Lost)]
    [InlineData(EstadoReserva.PendingOperatorRefund)]
    public async Task NormalInvoice_OnNonInvoiceableStatus_MessageHasNoInternalStateNames(string status)
    {
        using var context = new AppDbContext(_dbOptions);
        await SeedReservaAsync(context, status);
        var service = BuildService(context, out _);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateAsync(BuildRequest(isCreditNote: false), "u1", "User 1", CancellationToken.None));

        Assert.DoesNotContain("InManagement", ex.Message);
        Assert.DoesNotContain("Quotation", ex.Message);
        Assert.DoesNotContain("Budget", ex.Message);
        Assert.DoesNotContain("Confirmed", ex.Message);
        Assert.DoesNotContain("Cancelled", ex.Message);
        Assert.DoesNotContain("Lost", ex.Message);
        Assert.DoesNotContain("PendingOperatorRefund", ex.Message);
        Assert.DoesNotContain("'", ex.Message); // sin comillas envolviendo un nombre de estado
        Assert.Equal(TravelApi.Domain.Reservations.ReservaCapabilityPolicy.NotInvoiceableStatusReason, ex.Message);
    }

    [Fact]
    public async Task CreditNote_OnCancelledReserva_IsNotBlockedByStateGuard()
    {
        // Una NC sobre una reserva cancelada DEBE poder emitirse (corrige la factura ya emitida).
        // No exigimos que llegue a CreatePendingInvoice (el path NC pide mas datos); solo verificamos
        // que el guard de ESTADO no la frene con su mensaje.
        using var context = new AppDbContext(_dbOptions);
        await SeedReservaAsync(context, EstadoReserva.Cancelled);
        var service = BuildService(context, out _);

        Exception? thrown = null;
        try
        {
            await service.CreateAsync(BuildRequest(isCreditNote: true), "u1", "User 1", CancellationToken.None);
        }
        catch (Exception e) { thrown = e; }

        Assert.True(thrown is null || !thrown.Message.Contains("No se puede facturar una reserva en estado"),
            $"El guard de estado NO debe frenar una NC sobre cancelada. Excepcion: {thrown?.Message}");
    }
}
