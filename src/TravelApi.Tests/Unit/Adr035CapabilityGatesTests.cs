using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.Contracts.Shared;
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
/// ADR-035 (2026-06-19): la politica de capacidades como PRIMERA COMPUERTA de los services. Cubre:
/// <list type="bullet">
///   <item>Cobro rechazado (409) en estados no cobrables (Budget/Lost/Cancelled).</item>
///   <item>Editar/borrar cobro rechazado (409) en terminales (Closed/Cancelled), pero ANULAR en Closed -&gt; OK.</item>
///   <item>Reabrir Closed -&gt; ToSettle con razon, y luego la factura habilitada en ToSettle.</item>
///   <item>Voucher rechazado en InManagement; permitido en Confirmed (pasa el gate de estado).</item>
/// </list>
/// </summary>
public class Adr035CapabilityGatesTests
{
    private static DbContextOptions<AppDbContext> NewOptions() =>
        new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

    private static IMapper BuildMapper() =>
        new MapperConfiguration(c => c.AddProfile<MappingProfile>()).CreateMapper();

    private static Mock<IOperationalFinanceSettingsService> SettingsMock()
    {
        var settings = new Mock<IOperationalFinanceSettingsService>();
        settings.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OperationalFinanceSettings());
        return settings;
    }

    private static PaymentService NewPaymentService(AppDbContext ctx) =>
        new(ctx, new EntityReferenceResolver(ctx), BuildMapper(), SettingsMock().Object,
            NullLogger<PaymentService>.Instance);

    private static UserManager<ApplicationUser> BuildUserManager()
    {
        var store = new Mock<IUserStore<ApplicationUser>>();
        store.Setup(s => s.FindByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync((ApplicationUser?)null);
        return new UserManager<ApplicationUser>(
            store.Object, null!, null!,
            Array.Empty<IUserValidator<ApplicationUser>>(),
            Array.Empty<IPasswordValidator<ApplicationUser>>(),
            null!, null!, null!, null!);
    }

    private static ReservaService NewReservaService(AppDbContext ctx)
    {
        var mapper = new Mock<IMapper>();
        mapper.Setup(m => m.Map<ReservaDto>(It.IsAny<Reserva>()))
              .Returns((Reserva r) => new ReservaDto
              {
                  PublicId = r.PublicId, NumeroReserva = r.NumeroReserva, Name = r.Name, Status = r.Status
              });
        return new ReservaService(ctx, mapper.Object, SettingsMock().Object,
            BuildUserManager(), NullLogger<ReservaService>.Instance);
    }

    /// <summary>Reserva con un servicio Confirmado que sustenta la deuda, para que el recalculo no la borre.</summary>
    private static async Task<Reserva> SeedReservaWithDebtAsync(AppDbContext ctx, string status, decimal salePrice = 1000m)
    {
        var reserva = new Reserva
        {
            NumeroReserva = "R-035",
            Name = "Reserva ADR-035",
            Status = status,
            TotalSale = salePrice,
            ConfirmedSale = salePrice,
            Balance = salePrice,
            TotalPaid = 0m,
        };
        ctx.Reservas.Add(reserva);
        ctx.Servicios.Add(new ServicioReserva
        {
            ReservaId = reserva.Id,
            ServiceType = "Hotel",
            ProductType = "Hotel",
            Description = "Sustento deuda",
            ConfirmationNumber = "ABC123",
            Status = "Confirmado",
            DepartureDate = DateTime.UtcNow.AddDays(10),
            SalePrice = salePrice,
            NetCost = 0m,
            Commission = salePrice,
            CreatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();
        return reserva;
    }

    // =====================================================================================================
    // Cobro: 409 en estados no cobrables (Budget / Lost / Cancelled).
    // =====================================================================================================

    [Theory]
    [InlineData(EstadoReserva.Budget)]
    [InlineData(EstadoReserva.Lost)]
    [InlineData(EstadoReserva.Cancelled)]
    public async Task CreatePayment_OnNonCollectableState_Throws(string status)
    {
        await using var ctx = new AppDbContext(NewOptions());
        var reserva = await SeedReservaWithDebtAsync(ctx, status);
        var service = NewPaymentService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreatePaymentAsync(
                new CreatePaymentRequest
                {
                    ReservaId = reserva.PublicId.ToString(),
                    Amount = 100m,
                    Method = "Transfer",
                },
                CancellationToken.None));

        Assert.Equal(0, await ctx.Payments.CountAsync());
    }

    [Fact]
    public async Task CreatePayment_OnConfirmedWithDebt_Succeeds()
    {
        await using var ctx = new AppDbContext(NewOptions());
        var reserva = await SeedReservaWithDebtAsync(ctx, EstadoReserva.Confirmed);
        var service = NewPaymentService(ctx);

        var dto = await service.CreatePaymentAsync(
            new CreatePaymentRequest
            {
                ReservaId = reserva.PublicId.ToString(),
                Amount = 300m,
                Method = "Transfer",
            },
            CancellationToken.None);

        Assert.Equal(300m, dto.Amount);
        Assert.Equal(1, await ctx.Payments.CountAsync());
    }

    // =====================================================================================================
    // Editar/borrar cobro: 409 en terminales (Closed/Cancelled). ANULAR en Closed: OK.
    // =====================================================================================================

    private static async Task<Payment> SeedPaymentAsync(AppDbContext ctx, Reserva reserva, decimal amount = 200m)
    {
        var payment = new Payment
        {
            ReservaId = reserva.Id,
            Amount = amount,
            Currency = "ARS",
            Method = "Transfer",
            Status = "Paid",
            EntryType = PaymentEntryTypes.Payment,
            AffectsCash = true,
            PaidAt = DateTime.UtcNow,
        };
        ctx.Payments.Add(payment);
        await ctx.SaveChangesAsync();
        return payment;
    }

    [Theory]
    [InlineData(EstadoReserva.Closed)]
    [InlineData(EstadoReserva.Cancelled)]
    public async Task DeletePayment_OnTerminalState_Throws(string status)
    {
        await using var ctx = new AppDbContext(NewOptions());
        var reserva = await SeedReservaWithDebtAsync(ctx, status);
        var payment = await SeedPaymentAsync(ctx, reserva);
        var service = NewPaymentService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DeletePaymentAsync(payment.PublicId.ToString(), CancellationToken.None));

        // El pago sigue vivo (no se borro).
        var stillThere = await ctx.Payments.IgnoreQueryFilters().FirstAsync(p => p.Id == payment.Id);
        Assert.False(stillThere.IsDeleted);
    }

    [Theory]
    [InlineData(EstadoReserva.Closed)]
    [InlineData(EstadoReserva.Cancelled)]
    public async Task UpdatePayment_OnTerminalState_Throws(string status)
    {
        await using var ctx = new AppDbContext(NewOptions());
        var reserva = await SeedReservaWithDebtAsync(ctx, status);
        var payment = await SeedPaymentAsync(ctx, reserva);
        var service = NewPaymentService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UpdatePaymentAsync(
                payment.PublicId.ToString(),
                new UpdatePaymentRequest { Amount = 500m, Method = "Cash" },
                CancellationToken.None));
    }

    [Fact]
    public async Task AnnulPayment_OnClosedState_Succeeds()
    {
        // ANNUL es la salida valida en terminal: NO pasa por la compuerta de estado de ADR-035.
        await using var ctx = new AppDbContext(NewOptions());
        var reserva = await SeedReservaWithDebtAsync(ctx, EstadoReserva.Closed);
        var payment = await SeedPaymentAsync(ctx, reserva);
        var service = NewPaymentService(ctx);

        await service.AnnulPaymentAsync(payment.PublicId.ToString(), "cobro mal cargado", CancellationToken.None);

        var annulled = await ctx.Payments.IgnoreQueryFilters().FirstAsync(p => p.Id == payment.Id);
        Assert.True(annulled.IsDeleted);
    }

    // =====================================================================================================
    // Reabrir Closed -> ToSettle (Decision 4-bis) con razon; luego la factura queda habilitada en ToSettle.
    // =====================================================================================================

    [Fact]
    public async Task RevertClosedToToSettle_NoLongerAllowed_ADR036()
    {
        // ADR-036 (2026-06-21): se ELIMINO la reapertura "Closed -> ToSettle para facturar tarde". ToSettle
        // murio; corregir una factura de una Finalizada es por NC/ND, sin reabrir el estado. El revert a un
        // estado fuera de la matriz (ToSettle ya no es destino legal de Closed) rebota.
        await using var ctx = new AppDbContext(NewOptions());
        var reserva = await SeedReservaWithDebtAsync(ctx, EstadoReserva.Closed);
        reserva.ClosedAt = DateTime.UtcNow.AddDays(-1);
        await ctx.SaveChangesAsync();

        var service = NewReservaService(ctx);
        await Assert.ThrowsAnyAsync<Exception>(() =>
            service.RevertStatusAsync(
                reserva.PublicId.ToString(),
                new RevertStatusRequest("ToSettle", null, "reabrir para facturar tarde"),
                actorUserId: "admin-1", actorUserName: "Admin", actorIsAdmin: true, CancellationToken.None));

        var refreshed = await ctx.Reservas.FindAsync(reserva.Id);
        Assert.Equal(EstadoReserva.Closed, refreshed!.Status); // sigue Finalizada
    }

    [Fact]
    public void RevertClosed_OnlyTravelingIsLegalTarget_ADR036()
    {
        // ADR-036: Closed revierte SOLO a Traveling (revert de cierre prematuro). ToSettle no es destino.
        Assert.True(TravelApi.Domain.Reservations.ReservaStatusTransitions.Revert
            .TryGetValue(EstadoReserva.Closed, out var targets));
        Assert.Equal(new[] { EstadoReserva.Traveling }, targets);
    }

    [Fact]
    public void Invoice_Capability_EnabledInTraveling_ADR037()
    {
        // ADR-037 (desacople de facturacion): en viaje SI se factura. REVIERTE la restriccion de ADR-036.
        // La factura se desacopla del estado; permitirla en viaje NO reabre edicion ni cobro.
        var caps = TravelApi.Domain.Reservations.ReservaCapabilityPolicy.For(
            new TravelApi.Domain.Reservations.ReservaCapabilityContext(
                EstadoReserva.Traveling, Balance: 0m, false, false, false, false));
        Assert.True(caps.CanInvoiceSale.Allowed);
    }

    // =====================================================================================================
    // Voucher: rechazado en InManagement (gate ADR-035); permitido en Confirmed (pasa el gate de estado).
    // =====================================================================================================

    private static VoucherService NewVoucherService(AppDbContext ctx)
    {
        var fileStorage = new Mock<IFileStoragePort>();
        fileStorage.Setup(m => m.SaveAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StoredFileDescriptor("vouchers/test.pdf", "voucher-test.pdf", "application/pdf", 100L));
        return new VoucherService(ctx, SettingsMock().Object, fileStorage.Object);
    }

    private static async Task<Reserva> SeedReservaWithPassengerAsync(AppDbContext ctx, string status)
    {
        var reserva = new Reserva
        {
            NumeroReserva = "R-VCH",
            Name = "Reserva voucher",
            Status = status,
            Balance = 0m,
        };
        reserva.Passengers.Add(new Passenger { FullName = "Titular Uno", DocumentNumber = "12345678" });
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();
        return reserva;
    }

    [Fact]
    public async Task GenerateVoucher_OnInManagement_RejectedByStateGate()
    {
        await using var ctx = new AppDbContext(NewOptions());
        var reserva = await SeedReservaWithPassengerAsync(ctx, EstadoReserva.InManagement);
        var service = NewVoucherService(ctx);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.GenerateVoucherRecordAsync(
                reserva.PublicId.ToString(),
                new GenerateVoucherRequest { Scope = VoucherScopes.Reservation },
                new OperationActor("admin-1", "Admin", new[] { "Admin" }),
                CancellationToken.None));

        // El motivo es el del gate de estado, no el de pasajeros (la reserva SI tiene pasajeros).
        Assert.Contains("Confirmada", ex.Message);
        Assert.Equal(0, await ctx.Vouchers.CountAsync());
    }

    [Fact]
    public async Task GenerateVoucher_OnConfirmed_PassesStateGate()
    {
        await using var ctx = new AppDbContext(NewOptions());
        var reserva = await SeedReservaWithPassengerAsync(ctx, EstadoReserva.Confirmed);
        var service = NewVoucherService(ctx);

        // Confirmed pasa el gate de estado. Puede fallar mas adelante por generacion de PDF en entorno de
        // test, pero NUNCA por el motivo del gate de estado de voucher.
        var ex = await Record.ExceptionAsync(() =>
            service.GenerateVoucherRecordAsync(
                reserva.PublicId.ToString(),
                new GenerateVoucherRequest { Scope = VoucherScopes.Reservation },
                new OperationActor("admin-1", "Admin", new[] { "Admin" }),
                CancellationToken.None));

        if (ex is InvalidOperationException ioe)
            Assert.DoesNotContain("Confirmada en adelante", ioe.Message);
    }
}
