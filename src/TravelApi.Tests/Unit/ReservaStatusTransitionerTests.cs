using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Reservations;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Reservations;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// (2026-07-04) PUNTO ÚNICO de transición de estado de reserva
/// (<see cref="ReservaStatusTransitioner"/>) + su tabla declarativa de limpieza
/// (<see cref="ReservaStateCleanupRules"/>). Cierra el hueco de la auditoría: la marca "confirmada con cambios"
/// (ADR-027) y el motivo de revisión (LastRegression*) ahora se limpian de forma consistente en TODA transición,
/// según el estado destino.
///
/// <para>Verifica las reglas críticas: los terminales (Cancelled/PendingOperatorRefund/Closed/Lost) y los reverts a
/// pre-venta (Budget/Quotation) descartan la marca + el detalle; entrar a Confirmed limpia SOLO el motivo de
/// revisión y NUNCA la marca; el log solo se escribe en cambios reales; y el rastro conserva los campos de cada
/// transición (dirección, autorizante del revert, instante del lote).</para>
/// </summary>
public class ReservaStatusTransitionerTests
{
    private static AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"transitioner-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    /// <summary>
    /// Siembra una reserva en <paramref name="fromStatus"/> con TODAS las marcas de revisión encendidas: la marca
    /// "confirmada con cambios" (flag + fecha + 1 fila de detalle) y el motivo de revisión (LastRegression*).
    /// </summary>
    private static async Task<Reserva> SeedFullyMarkedAsync(AppDbContext ctx, string fromStatus)
    {
        var reserva = new Reserva
        {
            NumeroReserva = "F-TRANS",
            Name = "Reserva transicion",
            Status = fromStatus,
            HasUnacknowledgedChanges = true,
            ChangesPendingSince = DateTime.UtcNow.AddDays(-2),
            LastRegressionReason = "El operador reprogramó un servicio",
            LastRegressionAt = DateTime.UtcNow.AddDays(-2),
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        ctx.ReservaPendingChanges.Add(new ReservaPendingChange
        {
            ReservaId = reserva.Id,
            ServiceType = "Hotel",
            ServiceDescription = "Hotel 4 estrellas",
            Field = "SalePrice",
            OldValue = 100_000m,
            NewValue = 120_000m,
            Currency = "ARS",
            ChangedAt = DateTime.UtcNow.AddDays(-2),
        });
        await ctx.SaveChangesAsync();
        return reserva;
    }

    private static Task<int> CountPendingChangesAsync(AppDbContext ctx, int reservaId) =>
        ctx.ReservaPendingChanges.AsNoTracking().CountAsync(c => c.ReservaId == reservaId);

    // ============================ Terminales: descartan la marca + el detalle ============================

    [Theory]
    [InlineData(EstadoReserva.Cancelled)]
    [InlineData(EstadoReserva.PendingOperatorRefund)]
    [InlineData(EstadoReserva.Closed)]
    [InlineData(EstadoReserva.Lost)]
    public async Task ToTerminalState_DiscardsMark_AndDeletesDetail_AndKeepsRegression_AndLogs(string toStatus)
    {
        await using var ctx = NewContext();
        var reserva = await SeedFullyMarkedAsync(ctx, EstadoReserva.Confirmed);

        await ReservaStatusTransitioner.ApplyAsync(
            ctx, reserva, toStatus, "Forward",
            actorUserId: "u1", actorUserName: "User One", reason: "motivo", ct: CancellationToken.None);
        await ctx.SaveChangesAsync();

        var after = await ctx.Reservas.AsNoTracking().FirstAsync(r => r.Id == reserva.Id);
        Assert.Equal(toStatus, after.Status);
        // Marca "confirmada con cambios" descartada + detalle borrado.
        Assert.False(after.HasUnacknowledgedChanges);
        Assert.Null(after.ChangesPendingSince);
        Assert.Equal(0, await CountPendingChangesAsync(ctx, reserva.Id));
        // El motivo de revisión NO se toca en terminales (queda como historial informativo).
        Assert.Equal("El operador reprogramó un servicio", after.LastRegressionReason);
        Assert.NotNull(after.LastRegressionAt);
        // Rastro auditable del cambio real.
        var log = await ctx.ReservaStatusChangeLogs.AsNoTracking().SingleAsync(l => l.ReservaId == reserva.Id);
        Assert.Equal(EstadoReserva.Confirmed, log.FromStatus);
        Assert.Equal(toStatus, log.ToStatus);
        Assert.Equal("Forward", log.Direction);
        Assert.Equal("u1", log.ByUserId);
    }

    // ============================ Confirmed: preserva la marca, limpia solo el motivo ============================

    [Fact]
    public async Task ToConfirmed_NeverClearsMark_ButClearsRegression()
    {
        // CRÍTICO (ADR-027): la marca "confirmada con cambios" VIVE en Confirmed; entrar a Confirmed no la baja.
        await using var ctx = NewContext();
        var reserva = await SeedFullyMarkedAsync(ctx, EstadoReserva.InManagement);

        await ReservaStatusTransitioner.ApplyAsync(
            ctx, reserva, EstadoReserva.Confirmed, "Forward",
            actorUserId: "system", actorUserName: "Motor", reason: "confirmacion automatica", ct: CancellationToken.None);
        await ctx.SaveChangesAsync();

        var after = await ctx.Reservas.AsNoTracking().FirstAsync(r => r.Id == reserva.Id);
        Assert.Equal(EstadoReserva.Confirmed, after.Status);
        // La marca SIGUE puesta (solo una persona la baja con el OK).
        Assert.True(after.HasUnacknowledgedChanges);
        Assert.NotNull(after.ChangesPendingSince);
        // El detalle NO se borra.
        Assert.Equal(1, await CountPendingChangesAsync(ctx, reserva.Id));
        // El motivo de revisión SÍ se limpia (no arrastra una franja vieja al confirmar).
        Assert.Null(after.LastRegressionReason);
        Assert.Null(after.LastRegressionAt);
    }

    // ============================ Revert a pre-venta: descarta TODO, incluido el motivo ============================

    [Fact]
    public async Task ToBudget_DiscardsMark_AndDetail_AndRegression_WithRevertLog()
    {
        await using var ctx = NewContext();
        var reserva = await SeedFullyMarkedAsync(ctx, EstadoReserva.InManagement);

        await ReservaStatusTransitioner.ApplyAsync(
            ctx, reserva, EstadoReserva.Budget, "Revert",
            actorUserId: "vendedor-1", actorUserName: "Vendedor Uno", reason: "el cliente retomó el presupuesto",
            ct: CancellationToken.None,
            authorizedBySuperiorUserId: "sup-9", authorizedBySuperiorUserName: "Supervisora Nueve");
        await ctx.SaveChangesAsync();

        var after = await ctx.Reservas.AsNoTracking().FirstAsync(r => r.Id == reserva.Id);
        Assert.Equal(EstadoReserva.Budget, after.Status);
        Assert.False(after.HasUnacknowledgedChanges);
        Assert.Null(after.ChangesPendingSince);
        Assert.Equal(0, await CountPendingChangesAsync(ctx, reserva.Id));
        // A pre-venta también se limpia el motivo de revisión.
        Assert.Null(after.LastRegressionReason);
        Assert.Null(after.LastRegressionAt);
        // El log de revert conserva el autorizante.
        var log = await ctx.ReservaStatusChangeLogs.AsNoTracking().SingleAsync(l => l.ReservaId == reserva.Id);
        Assert.Equal("Revert", log.Direction);
        Assert.Equal("sup-9", log.AuthorizedBySuperiorUserId);
        Assert.Equal("Supervisora Nueve", log.AuthorizedBySuperiorUserName);
    }

    // ============================ Idempotencia: mismo estado no loguea, pero sanea ============================

    [Fact]
    public async Task SameStatus_DoesNotLog_ButStillSanitizesStaleMark()
    {
        // Reserva YA Cancelled con una marca colgada (dato legacy). Re-aplicar Cancelled no genera log (no es un
        // cambio real) pero igual limpia la marca colgada: la limpieza es idempotente.
        await using var ctx = NewContext();
        var reserva = await SeedFullyMarkedAsync(ctx, EstadoReserva.Cancelled);

        await ReservaStatusTransitioner.ApplyAsync(
            ctx, reserva, EstadoReserva.Cancelled, "Forward",
            actorUserId: "u1", actorUserName: "User One", reason: null, ct: CancellationToken.None);
        await ctx.SaveChangesAsync();

        // No se escribió log (mismo estado).
        Assert.Equal(0, await ctx.ReservaStatusChangeLogs.AsNoTracking().CountAsync(l => l.ReservaId == reserva.Id));
        // Pero la marca colgada se saneó igual.
        var after = await ctx.Reservas.AsNoTracking().FirstAsync(r => r.Id == reserva.Id);
        Assert.False(after.HasUnacknowledgedChanges);
        Assert.Equal(0, await CountPendingChangesAsync(ctx, reserva.Id));
    }

    // ============================ occurredAt del lote se respeta ============================

    [Fact]
    public async Task OccurredAt_WhenProvided_IsStampedOnLog()
    {
        await using var ctx = NewContext();
        var reserva = await SeedFullyMarkedAsync(ctx, EstadoReserva.Confirmed);
        var batchInstant = new DateTime(2026, 7, 4, 3, 0, 0, DateTimeKind.Utc);

        await ReservaStatusTransitioner.ApplyAsync(
            ctx, reserva, EstadoReserva.Closed, "Forward",
            actorUserId: "system", actorUserName: "Job", reason: "cierre por fin de viaje",
            ct: CancellationToken.None, occurredAt: batchInstant);
        await ctx.SaveChangesAsync();

        var log = await ctx.ReservaStatusChangeLogs.AsNoTracking().SingleAsync(l => l.ReservaId == reserva.Id);
        Assert.Equal(batchInstant, log.OccurredAt);
    }

    // ============================ stampChangeLog=false suprime el log, sigue el resto ============================

    [Fact]
    public async Task StampChangeLogFalse_SkipsLog_ButStillTransitionsAndSanitizes()
    {
        await using var ctx = NewContext();
        var reserva = await SeedFullyMarkedAsync(ctx, EstadoReserva.Confirmed);

        await ReservaStatusTransitioner.ApplyAsync(
            ctx, reserva, EstadoReserva.Cancelled, "Forward",
            actorUserId: null, actorUserName: null, reason: null, ct: CancellationToken.None,
            stampChangeLog: false);
        await ctx.SaveChangesAsync();

        Assert.Equal(0, await ctx.ReservaStatusChangeLogs.AsNoTracking().CountAsync(l => l.ReservaId == reserva.Id));
        var after = await ctx.Reservas.AsNoTracking().FirstAsync(r => r.Id == reserva.Id);
        Assert.Equal(EstadoReserva.Cancelled, after.Status);
        Assert.False(after.HasUnacknowledgedChanges);
    }

    // ============================ Reglas puras (sin DB) ============================

    [Theory]
    [InlineData(EstadoReserva.Cancelled, true, true, false)]
    [InlineData(EstadoReserva.PendingOperatorRefund, true, true, false)]
    [InlineData(EstadoReserva.Closed, true, true, false)]
    [InlineData(EstadoReserva.Lost, true, true, false)]
    [InlineData(EstadoReserva.Budget, true, true, true)]
    [InlineData(EstadoReserva.Quotation, true, true, true)]
    [InlineData(EstadoReserva.Confirmed, false, false, true)]
    [InlineData(EstadoReserva.InManagement, false, false, false)]
    [InlineData(EstadoReserva.Traveling, false, false, false)]
    [InlineData("Archived", false, false, false)]
    public void CleanupRules_MapEachStateCorrectly(
        string toStatus, bool clearMark, bool clearRows, bool clearRegression)
    {
        var cleanup = ReservaStateCleanupRules.For(toStatus);
        Assert.Equal(clearMark, cleanup.ClearUnacknowledgedChanges);
        Assert.Equal(clearRows, cleanup.ClearPendingChangeRows);
        Assert.Equal(clearRegression, cleanup.ClearLastRegression);
    }
}
