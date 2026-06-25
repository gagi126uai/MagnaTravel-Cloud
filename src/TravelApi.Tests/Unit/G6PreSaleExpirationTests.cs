using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using TravelApi.Infrastructure.Services.Reservations;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// G6 (caducidad de pre-venta, decision del dueño 2026-06-24): un Presupuesto (Budget) o una Cotizacion
/// (Quotation) que no avanza en X dias caduca SOLO a "Perdido" (Lost). Los dias se configuran POR SEPARADO
/// por tipo; 0 = desactivado. Estos tests cubren <c>AutoExpireStalePreSaleAsync</c> del job nocturno:
/// caduca al pasar los dias, NO caduca si esta desactivado, no toca reservas que avanzaron, y respeta cada
/// tipo por separado.
/// </summary>
public class G6PreSaleExpirationTests
{
    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    /// <summary>
    /// Crea el job con un settings mockeado que devuelve los dias de caducidad pedidos para Budget y Quotation.
    /// </summary>
    private static ReservaLifecycleAutomationService CreateJob(
        AppDbContext context, int budgetDays, int quotationDays)
    {
        var settings = new Mock<IOperationalFinanceSettingsService>();
        settings.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OperationalFinanceSettings
                {
                    BudgetExpirationDays = budgetDays,
                    QuotationExpirationDays = quotationDays
                });
        var engine = new ReservaAutoStateService(context, NullLogger<ReservaAutoStateService>.Instance);
        return new ReservaLifecycleAutomationService(
            context, NullLogger<ReservaLifecycleAutomationService>.Instance, settings.Object, engine);
    }

    /// <summary>
    /// Siembra una reserva en un estado de pre-venta. <paramref name="enteredAt"/> es cuando entro al estado:
    /// se modela con un ReservaStatusChangeLog (ToStatus = estado) para Budget; para Quotation (que nace en ese
    /// estado) se usa CreatedAt si no se pasa log.
    /// </summary>
    private static async Task<Reserva> SeedPreSaleAsync(
        AppDbContext context, int id, string status, DateTime enteredAt, bool writeLog)
    {
        var reserva = new Reserva
        {
            Id = id,
            NumeroReserva = $"R-G6-{id}",
            Name = $"Reserva {id}",
            Status = status,
            CreatedAt = enteredAt
        };
        context.Reservas.Add(reserva);

        if (writeLog)
        {
            context.ReservaStatusChangeLogs.Add(new ReservaStatusChangeLog
            {
                ReservaId = id,
                FromStatus = EstadoReserva.Quotation,
                ToStatus = status,
                Direction = "Forward",
                OccurredAt = enteredAt
            });
        }

        await context.SaveChangesAsync();
        return reserva;
    }

    [Fact]
    public async Task Budget_OlderThanThreshold_ExpiresToLost()
    {
        await using var context = CreateContext();
        // Entro a Budget hace 10 dias; el plazo es 7 -> caduca.
        await SeedPreSaleAsync(context, 1, EstadoReserva.Budget,
            enteredAt: DateTime.UtcNow.AddDays(-10), writeLog: true);

        var job = CreateJob(context, budgetDays: 7, quotationDays: 0);
        var expired = await job.AutoExpireStalePreSaleAsync(CancellationToken.None);

        Assert.Equal(1, expired);
        var refreshed = await context.Reservas.FindAsync(1);
        Assert.Equal(EstadoReserva.Lost, refreshed!.Status);
    }

    [Fact]
    public async Task Budget_WithinThreshold_DoesNotExpire()
    {
        await using var context = CreateContext();
        // Entro a Budget hace 3 dias; el plazo es 7 -> todavia vigente.
        await SeedPreSaleAsync(context, 1, EstadoReserva.Budget,
            enteredAt: DateTime.UtcNow.AddDays(-3), writeLog: true);

        var job = CreateJob(context, budgetDays: 7, quotationDays: 0);
        var expired = await job.AutoExpireStalePreSaleAsync(CancellationToken.None);

        Assert.Equal(0, expired);
        var refreshed = await context.Reservas.FindAsync(1);
        Assert.Equal(EstadoReserva.Budget, refreshed!.Status);
    }

    [Fact]
    public async Task Budget_Disabled_ZeroDays_DoesNotExpire()
    {
        await using var context = CreateContext();
        // Muy vieja (100 dias) pero la caducidad de Budget esta en 0 = desactivada.
        await SeedPreSaleAsync(context, 1, EstadoReserva.Budget,
            enteredAt: DateTime.UtcNow.AddDays(-100), writeLog: true);

        var job = CreateJob(context, budgetDays: 0, quotationDays: 0);
        var expired = await job.AutoExpireStalePreSaleAsync(CancellationToken.None);

        Assert.Equal(0, expired);
        var refreshed = await context.Reservas.FindAsync(1);
        Assert.Equal(EstadoReserva.Budget, refreshed!.Status);
    }

    [Fact]
    public async Task Quotation_OlderThanThreshold_ExpiresToLost_UsesCreatedAtWhenNoLog()
    {
        await using var context = CreateContext();
        // La cotizacion NACE en Quotation, no hay log de entrada -> antigüedad medida desde CreatedAt.
        await SeedPreSaleAsync(context, 1, EstadoReserva.Quotation,
            enteredAt: DateTime.UtcNow.AddDays(-30), writeLog: false);

        var job = CreateJob(context, budgetDays: 0, quotationDays: 20);
        var expired = await job.AutoExpireStalePreSaleAsync(CancellationToken.None);

        Assert.Equal(1, expired);
        var refreshed = await context.Reservas.FindAsync(1);
        Assert.Equal(EstadoReserva.Lost, refreshed!.Status);
    }

    [Fact]
    public async Task EachTypeRespectsItsOwnThreshold()
    {
        await using var context = CreateContext();
        // Budget de 10 dias con plazo 7 -> caduca. Quotation de 10 dias con plazo 20 -> NO caduca.
        await SeedPreSaleAsync(context, 1, EstadoReserva.Budget,
            enteredAt: DateTime.UtcNow.AddDays(-10), writeLog: true);
        await SeedPreSaleAsync(context, 2, EstadoReserva.Quotation,
            enteredAt: DateTime.UtcNow.AddDays(-10), writeLog: false);

        var job = CreateJob(context, budgetDays: 7, quotationDays: 20);
        var expired = await job.AutoExpireStalePreSaleAsync(CancellationToken.None);

        Assert.Equal(1, expired);
        Assert.Equal(EstadoReserva.Lost, (await context.Reservas.FindAsync(1))!.Status);
        Assert.Equal(EstadoReserva.Quotation, (await context.Reservas.FindAsync(2))!.Status);
    }

    [Fact]
    public async Task DoesNotTouchReservationsThatAlreadyAdvanced()
    {
        await using var context = CreateContext();
        // Una reserva que YA avanzo a InManagement (firme) no esta en Budget/Quotation -> el job ni la mira,
        // aunque sea vieja. Esto prueba que el filtro por estado actual protege a las que prosperaron.
        await SeedPreSaleAsync(context, 1, EstadoReserva.InManagement,
            enteredAt: DateTime.UtcNow.AddDays(-100), writeLog: true);

        var job = CreateJob(context, budgetDays: 7, quotationDays: 20);
        var expired = await job.AutoExpireStalePreSaleAsync(CancellationToken.None);

        Assert.Equal(0, expired);
        Assert.Equal(EstadoReserva.InManagement, (await context.Reservas.FindAsync(1))!.Status);
    }

    [Fact]
    public async Task PreSaleWithLivePayment_DoesNotExpire_IsSkipped()
    {
        await using var context = CreateContext();
        // Budget vieja (10 dias, plazo 7) PERO con un cobro vivo cargado (el path legacy AddPaymentAsync
        // permite cargar pagos en pre-venta). Caducarla a Lost (terminal) congelaria esa plata -> se saltea.
        await SeedPreSaleAsync(context, 1, EstadoReserva.Budget,
            enteredAt: DateTime.UtcNow.AddDays(-10), writeLog: true);
        context.Payments.Add(new Payment
        {
            Id = 1,
            ReservaId = 1,
            Amount = 50000m,
            IsDeleted = false
        });
        await context.SaveChangesAsync();

        var job = CreateJob(context, budgetDays: 7, quotationDays: 0);
        var expired = await job.AutoExpireStalePreSaleAsync(CancellationToken.None);

        Assert.Equal(0, expired);
        // Sigue en Budget (no se caduco) y NO quedo rastro de cambio a Lost.
        Assert.Equal(EstadoReserva.Budget, (await context.Reservas.FindAsync(1))!.Status);
        var anyLostLog = await context.ReservaStatusChangeLogs
            .AnyAsync(log => log.ReservaId == 1 && log.ToStatus == EstadoReserva.Lost);
        Assert.False(anyLostLog);
    }

    [Fact]
    public async Task PreSaleWithSoftDeletedPayment_StillExpires()
    {
        await using var context = CreateContext();
        // Un pago BORRADO (IsDeleted) no es plata viva: no debe frenar la caducidad.
        await SeedPreSaleAsync(context, 1, EstadoReserva.Budget,
            enteredAt: DateTime.UtcNow.AddDays(-10), writeLog: true);
        context.Payments.Add(new Payment
        {
            Id = 1,
            ReservaId = 1,
            Amount = 50000m,
            IsDeleted = true
        });
        await context.SaveChangesAsync();

        var job = CreateJob(context, budgetDays: 7, quotationDays: 0);
        var expired = await job.AutoExpireStalePreSaleAsync(CancellationToken.None);

        Assert.Equal(1, expired);
        Assert.Equal(EstadoReserva.Lost, (await context.Reservas.FindAsync(1))!.Status);
    }

    [Fact]
    public async Task Expiration_WritesAuditLogWithSystemActorAndReason()
    {
        await using var context = CreateContext();
        await SeedPreSaleAsync(context, 1, EstadoReserva.Budget,
            enteredAt: DateTime.UtcNow.AddDays(-10), writeLog: true);

        var job = CreateJob(context, budgetDays: 7, quotationDays: 0);
        await job.AutoExpireStalePreSaleAsync(CancellationToken.None);

        // El cambio automatico a Lost debe quedar en el rastro con actor "sistema" y motivo de caducidad.
        var forwardToLost = await context.ReservaStatusChangeLogs
            .Where(log => log.ReservaId == 1 && log.ToStatus == EstadoReserva.Lost)
            .OrderByDescending(log => log.OccurredAt)
            .FirstOrDefaultAsync();

        Assert.NotNull(forwardToLost);
        Assert.Equal(EstadoReserva.Budget, forwardToLost!.FromStatus);
        Assert.Equal("Forward", forwardToLost.Direction);
        Assert.Equal("system:lifecycle", forwardToLost.ByUserId);
        Assert.Contains("Caducó", forwardToLost.Reason);
    }

    [Fact]
    public async Task UsesLatestEntryIntoState_NotCreatedAt()
    {
        await using var context = CreateContext();
        // Reserva creada hace 100 dias PERO que volvio a Budget recien hace 2 dias (ej. revert desde Lost).
        // La antigüedad debe medirse desde la ULTIMA entrada a Budget (2 dias), no desde CreatedAt (100).
        var reserva = new Reserva
        {
            Id = 1,
            NumeroReserva = "R-G6-1",
            Name = "Reserva reabierta",
            Status = EstadoReserva.Budget,
            CreatedAt = DateTime.UtcNow.AddDays(-100)
        };
        context.Reservas.Add(reserva);
        context.ReservaStatusChangeLogs.Add(new ReservaStatusChangeLog
        {
            ReservaId = 1,
            FromStatus = EstadoReserva.Quotation,
            ToStatus = EstadoReserva.Budget,
            Direction = "Forward",
            OccurredAt = DateTime.UtcNow.AddDays(-100)
        });
        context.ReservaStatusChangeLogs.Add(new ReservaStatusChangeLog
        {
            ReservaId = 1,
            FromStatus = EstadoReserva.Lost,
            ToStatus = EstadoReserva.Budget,
            Direction = "Forward",
            OccurredAt = DateTime.UtcNow.AddDays(-2)
        });
        await context.SaveChangesAsync();

        var job = CreateJob(context, budgetDays: 7, quotationDays: 0);
        var expired = await job.AutoExpireStalePreSaleAsync(CancellationToken.None);

        // Entro a Budget hace 2 dias (< 7) -> NO caduca, aunque la reserva sea muy vieja.
        Assert.Equal(0, expired);
        Assert.Equal(EstadoReserva.Budget, (await context.Reservas.FindAsync(1))!.Status);
    }
}
