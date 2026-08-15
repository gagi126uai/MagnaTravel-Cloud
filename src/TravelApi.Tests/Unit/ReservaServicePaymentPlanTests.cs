using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.Contracts.Reservations;
using TravelApi.Application.Interfaces;
using TravelApi.Application.Mappings;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Obra "PDF ronda 2" (2026-08-14/15): tests de <see cref="ReservaService.UpdatePaymentPlanAsync"/> con
/// el <see cref="MappingProfile"/> REAL (no un mapper mockeado) a propósito — el bug que este test hubiera
/// cazado (bloqueante de review, 2026-08-15) era justamente un <c>.Include()</c> faltante en
/// <c>GetReservaByIdAsync(int)</c>: con un mapper mockeado que arma el DTO a mano, el test pasaría igual
/// aunque el Include faltara, porque nunca leería la navegación real de EF. Solo un mapper real que lee
/// <c>Reserva.PaymentPlanInstallments</c> de la entidad devuelta por la query expone ese bug.
/// </summary>
public class ReservaServicePaymentPlanTests
{
    private static AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

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

    private static IMapper BuildRealMapper() =>
        new MapperConfiguration(c => c.AddProfile<MappingProfile>()).CreateMapper();

    private static ReservaService NewReservaService(AppDbContext context)
    {
        var settings = new Mock<IOperationalFinanceSettingsService>();
        settings.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OperationalFinanceSettings());

        return new ReservaService(
            context, BuildRealMapper(), settings.Object, BuildUserManager(), NullLogger<ReservaService>.Instance);
    }

    private static Reserva Reserva(int id, string status = EstadoReserva.Budget, string numero = "2026-1") => new()
    {
        Id = id,
        NumeroReserva = numero,
        Name = $"Reserva {id}",
        Status = status,
    };

    // ================================================================================
    // Round-trip: lo que se guarda es lo que vuelve en el DTO (cazaría el Include faltante).
    // ================================================================================

    [Fact]
    public async Task UpdatePaymentPlanAsync_SavesRows_AndReturnsThemOrderedByPosition_InTheDto()
    {
        await using var ctx = NewContext();
        ctx.Reservas.Add(Reserva(1));
        await ctx.SaveChangesAsync();

        var service = NewReservaService(ctx);

        var request = new UpdatePaymentPlanRequest(new[]
        {
            new PaymentPlanInstallmentRequest("Al confirmar la reserva", 500m, "USD"),
            new PaymentPlanInstallmentRequest("10 de enero de 2027", 800m, "USD"),
            new PaymentPlanInstallmentRequest("Saldo 30 días antes de la salida", 700m, "USD"),
        });

        var dto = await service.UpdatePaymentPlanAsync("1", request);

        // Si el Include de PaymentPlanInstallments faltara en GetReservaByIdAsync(int), esta lista
        // llegaría SIEMPRE vacía acá aunque las 3 filas ya estén guardadas en la base.
        Assert.Equal(3, dto.PaymentPlanInstallments.Count);
        Assert.Equal(new[] { 1, 2, 3 }, dto.PaymentPlanInstallments.Select(p => p.Position));
        Assert.Equal("Al confirmar la reserva", dto.PaymentPlanInstallments[0].DueText);
        Assert.Equal(500m, dto.PaymentPlanInstallments[0].Amount);
        Assert.Equal("USD", dto.PaymentPlanInstallments[0].Currency);
        Assert.Equal("10 de enero de 2027", dto.PaymentPlanInstallments[1].DueText);
        Assert.Equal("Saldo 30 días antes de la salida", dto.PaymentPlanInstallments[2].DueText);

        // También lo verificamos directo contra la base (el round-trip del DTO no reemplaza chequear
        // que de verdad se persistió, solo agrega la pata que el bloqueante de review necesitaba).
        var persistedRows = await ctx.BudgetPaymentPlanInstallments
            .Where(p => p.ReservaId == 1)
            .OrderBy(p => p.Position)
            .ToListAsync();
        Assert.Equal(3, persistedRows.Count);
    }

    [Fact]
    public async Task UpdatePaymentPlanAsync_NewListReplacesOldOne_NeverAccumulates()
    {
        await using var ctx = NewContext();
        ctx.Reservas.Add(Reserva(1));
        await ctx.SaveChangesAsync();

        var service = NewReservaService(ctx);

        await service.UpdatePaymentPlanAsync("1", new UpdatePaymentPlanRequest(new[]
        {
            new PaymentPlanInstallmentRequest("Al confirmar la reserva", 500m, "USD"),
            new PaymentPlanInstallmentRequest("Saldo antes de viajar", 1500m, "USD"),
        }));

        // Segunda edición: el vendedor rehace el plan completo con una sola fila -- la vieja NO debe
        // quedar pegada (ni las 2 filas viejas conviviendo con la nueva).
        var dto = await service.UpdatePaymentPlanAsync("1", new UpdatePaymentPlanRequest(new[]
        {
            new PaymentPlanInstallmentRequest("Pago único al confirmar", 2000m, "USD"),
        }));

        Assert.Single(dto.PaymentPlanInstallments);
        Assert.Equal("Pago único al confirmar", dto.PaymentPlanInstallments[0].DueText);
        Assert.Equal(1, dto.PaymentPlanInstallments[0].Position);

        var persistedRows = await ctx.BudgetPaymentPlanInstallments.Where(p => p.ReservaId == 1).ToListAsync();
        Assert.Single(persistedRows);
    }

    [Fact]
    public async Task UpdatePaymentPlanAsync_EmptyList_ClearsThePlan()
    {
        await using var ctx = NewContext();
        ctx.Reservas.Add(Reserva(1));
        await ctx.SaveChangesAsync();

        var service = NewReservaService(ctx);

        await service.UpdatePaymentPlanAsync("1", new UpdatePaymentPlanRequest(new[]
        {
            new PaymentPlanInstallmentRequest("Al confirmar la reserva", 500m, "USD"),
        }));

        var dto = await service.UpdatePaymentPlanAsync("1", new UpdatePaymentPlanRequest(null));

        Assert.Empty(dto.PaymentPlanInstallments);
        Assert.False(await ctx.BudgetPaymentPlanInstallments.AnyAsync(p => p.ReservaId == 1));
    }

    // ================================================================================
    // Tope de 24 filas (hallazgo de seguridad, review 2026-08-15).
    // ================================================================================

    [Fact]
    public async Task UpdatePaymentPlanAsync_MoreThan24Rows_ThrowsBusinessMessage()
    {
        await using var ctx = NewContext();
        ctx.Reservas.Add(Reserva(1));
        await ctx.SaveChangesAsync();

        var service = NewReservaService(ctx);

        var tooManyRows = Enumerable.Range(1, 25)
            .Select(i => new PaymentPlanInstallmentRequest($"Cuota {i}", 100m, "USD"))
            .ToList();

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => service.UpdatePaymentPlanAsync("1", new UpdatePaymentPlanRequest(tooManyRows)));

        Assert.Contains("24 filas", ex.Message);
    }

    [Fact]
    public async Task UpdatePaymentPlanAsync_Exactly24Rows_IsAllowed()
    {
        await using var ctx = NewContext();
        ctx.Reservas.Add(Reserva(1));
        await ctx.SaveChangesAsync();

        var service = NewReservaService(ctx);

        var exactly24Rows = Enumerable.Range(1, 24)
            .Select(i => new PaymentPlanInstallmentRequest($"Cuota {i}", 100m, "USD"))
            .ToList();

        var dto = await service.UpdatePaymentPlanAsync("1", new UpdatePaymentPlanRequest(exactly24Rows));

        Assert.Equal(24, dto.PaymentPlanInstallments.Count);
    }

    [Fact]
    public async Task UpdatePaymentPlanAsync_ReservaNotFound_ThrowsKeyNotFound()
    {
        await using var ctx = NewContext();
        var service = NewReservaService(ctx);

        await Assert.ThrowsAsync<System.Collections.Generic.KeyNotFoundException>(
            () => service.UpdatePaymentPlanAsync("999", new UpdatePaymentPlanRequest(null)));
    }
}
