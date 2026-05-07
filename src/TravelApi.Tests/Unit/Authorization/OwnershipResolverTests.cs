using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Authorization;
using TravelApi.Infrastructure.Persistence;
using Xunit;

namespace TravelApi.Tests.Unit.Authorization;

/// <summary>
/// B1.15 Fase 1 — OwnershipResolver.
///
/// Cubre:
///  - Reserva: owner exacto, owner distinto, ResponsibleUserId NULL (legacy), no existe.
///  - Servicio/Payment/Invoice/Voucher: lookup via Reserva padre.
///  - Identificacion por PublicId (Guid) y por legacy id (int).
///  - Inputs invalidos (string vacio, id no parseable).
/// </summary>
public class OwnershipResolverTests
{
    private static AppDbContext BuildContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    private static async Task<Reserva> SeedReservaAsync(AppDbContext ctx, string? responsibleUserId, int id = 1)
    {
        var reserva = new Reserva
        {
            Id = id,
            PublicId = Guid.NewGuid(),
            NumeroReserva = $"F-2026-{id:D4}",
            Name = "Reserva test",
            Status = EstadoReserva.Confirmed,
            ResponsibleUserId = responsibleUserId,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();
        return reserva;
    }

    [Fact]
    public async Task Reserva_owner_match_returns_true()
    {
        await using var ctx = BuildContext();
        var reserva = await SeedReservaAsync(ctx, "user-1");
        var resolver = new OwnershipResolver(ctx);

        var result = await resolver.IsOwnerAsync("user-1", OwnedEntity.Reserva, reserva.PublicId.ToString());

        Assert.True(result);
    }

    [Fact]
    public async Task Reserva_owner_mismatch_returns_false()
    {
        await using var ctx = BuildContext();
        var reserva = await SeedReservaAsync(ctx, "user-1");
        var resolver = new OwnershipResolver(ctx);

        var result = await resolver.IsOwnerAsync("user-2", OwnedEntity.Reserva, reserva.PublicId.ToString());

        Assert.False(result);
    }

    [Fact]
    public async Task Reserva_with_null_responsible_returns_false()
    {
        // Decision Gaston: legacy sin backfill no asume ownership.
        await using var ctx = BuildContext();
        var reserva = await SeedReservaAsync(ctx, responsibleUserId: null);
        var resolver = new OwnershipResolver(ctx);

        var result = await resolver.IsOwnerAsync("user-1", OwnedEntity.Reserva, reserva.PublicId.ToString());

        Assert.False(result);
    }

    [Fact]
    public async Task Reserva_not_found_returns_false()
    {
        await using var ctx = BuildContext();
        var resolver = new OwnershipResolver(ctx);

        var result = await resolver.IsOwnerAsync("user-1", OwnedEntity.Reserva, Guid.NewGuid().ToString());

        Assert.False(result);
    }

    [Fact]
    public async Task Reserva_lookup_by_legacy_id_works()
    {
        await using var ctx = BuildContext();
        var reserva = await SeedReservaAsync(ctx, "user-1", id: 42);
        var resolver = new OwnershipResolver(ctx);

        var result = await resolver.IsOwnerAsync("user-1", OwnedEntity.Reserva, "42");

        Assert.True(result);
    }

    [Fact]
    public async Task Servicio_resolves_via_parent_reserva()
    {
        await using var ctx = BuildContext();
        var reserva = await SeedReservaAsync(ctx, "user-1");

        var servicio = new ServicioReserva
        {
            Id = 100,
            PublicId = Guid.NewGuid(),
            ReservaId = reserva.Id,
        };
        ctx.Servicios.Add(servicio);
        await ctx.SaveChangesAsync();

        var resolver = new OwnershipResolver(ctx);

        Assert.True(await resolver.IsOwnerAsync("user-1", OwnedEntity.Servicio, servicio.PublicId.ToString()));
        Assert.False(await resolver.IsOwnerAsync("user-X", OwnedEntity.Servicio, servicio.PublicId.ToString()));
    }

    [Fact]
    public async Task Payment_resolves_via_parent_reserva()
    {
        await using var ctx = BuildContext();
        var reserva = await SeedReservaAsync(ctx, "user-1");

        var payment = new Payment
        {
            Id = 200,
            PublicId = Guid.NewGuid(),
            ReservaId = reserva.Id,
            Amount = 100m,
        };
        ctx.Payments.Add(payment);
        await ctx.SaveChangesAsync();

        var resolver = new OwnershipResolver(ctx);

        Assert.True(await resolver.IsOwnerAsync("user-1", OwnedEntity.Payment, payment.PublicId.ToString()));
        Assert.False(await resolver.IsOwnerAsync("user-2", OwnedEntity.Payment, payment.PublicId.ToString()));
    }

    [Fact]
    public async Task Invoice_resolves_via_parent_reserva()
    {
        await using var ctx = BuildContext();
        var reserva = await SeedReservaAsync(ctx, "user-1");

        var invoice = new Invoice
        {
            Id = 300,
            PublicId = Guid.NewGuid(),
            ReservaId = reserva.Id,
            ImporteTotal = 1000m,
        };
        ctx.Invoices.Add(invoice);
        await ctx.SaveChangesAsync();

        var resolver = new OwnershipResolver(ctx);

        Assert.True(await resolver.IsOwnerAsync("user-1", OwnedEntity.Invoice, invoice.PublicId.ToString()));
        Assert.False(await resolver.IsOwnerAsync("user-2", OwnedEntity.Invoice, invoice.PublicId.ToString()));
    }

    [Fact]
    public async Task Voucher_resolves_via_parent_reserva()
    {
        await using var ctx = BuildContext();
        var reserva = await SeedReservaAsync(ctx, "user-1");

        var voucher = new Voucher
        {
            Id = 400,
            PublicId = Guid.NewGuid(),
            ReservaId = reserva.Id,
        };
        ctx.Vouchers.Add(voucher);
        await ctx.SaveChangesAsync();

        var resolver = new OwnershipResolver(ctx);

        Assert.True(await resolver.IsOwnerAsync("user-1", OwnedEntity.Voucher, voucher.PublicId.ToString()));
        Assert.False(await resolver.IsOwnerAsync("user-2", OwnedEntity.Voucher, voucher.PublicId.ToString()));
    }

    [Fact]
    public async Task Passenger_resolves_via_parent_reserva()
    {
        await using var ctx = BuildContext();
        var reserva = await SeedReservaAsync(ctx, "user-1");

        var passenger = new Passenger
        {
            Id = 500,
            PublicId = Guid.NewGuid(),
            ReservaId = reserva.Id,
        };
        ctx.Passengers.Add(passenger);
        await ctx.SaveChangesAsync();

        var resolver = new OwnershipResolver(ctx);

        Assert.True(await resolver.IsOwnerAsync("user-1", OwnedEntity.Passenger, passenger.PublicId.ToString()));
        Assert.False(await resolver.IsOwnerAsync("user-2", OwnedEntity.Passenger, passenger.PublicId.ToString()));
    }

    [Fact]
    public async Task Servicio_with_legacy_reserva_without_responsible_returns_false()
    {
        await using var ctx = BuildContext();
        var reserva = await SeedReservaAsync(ctx, responsibleUserId: null);

        var servicio = new ServicioReserva
        {
            Id = 700,
            PublicId = Guid.NewGuid(),
            ReservaId = reserva.Id,
        };
        ctx.Servicios.Add(servicio);
        await ctx.SaveChangesAsync();

        var resolver = new OwnershipResolver(ctx);

        var result = await resolver.IsOwnerAsync("user-1", OwnedEntity.Servicio, servicio.PublicId.ToString());

        Assert.False(result);
    }

    [Fact]
    public async Task Empty_userId_returns_false()
    {
        await using var ctx = BuildContext();
        var reserva = await SeedReservaAsync(ctx, "user-1");
        var resolver = new OwnershipResolver(ctx);

        var result = await resolver.IsOwnerAsync(string.Empty, OwnedEntity.Reserva, reserva.PublicId.ToString());

        Assert.False(result);
    }

    [Fact]
    public async Task Unparseable_id_returns_false()
    {
        await using var ctx = BuildContext();
        var resolver = new OwnershipResolver(ctx);

        var result = await resolver.IsOwnerAsync("user-1", OwnedEntity.Reserva, "no-es-guid-ni-int");

        Assert.False(result);
    }
}
