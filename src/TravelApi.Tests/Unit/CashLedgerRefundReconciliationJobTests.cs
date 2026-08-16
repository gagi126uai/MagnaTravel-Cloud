using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Reservations;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-022 GAP C (2026-08-16): tests del calculo de divergencia extracto-vs-caja para reembolsos de
/// operador. Dos niveles:
///   - <see cref="CashLedgerRefundReconciliationCalculator"/> (puro, sin DB): la aritmetica de comparar dos
///     diccionarios por moneda.
///   - <see cref="CashLedgerRefundReconciliationJob"/> (InMemory DB): el camino REAL end-to-end, incluido el
///     caso "un asiento de caja fue revertido" (el que motivo este trabajo — GAP C del brief).
/// </summary>
public class CashLedgerRefundReconciliationCalculatorTests
{
    [Fact]
    public void FindDivergences_SameAmountBothSides_ReturnsEmpty()
    {
        var derived = new System.Collections.Generic.Dictionary<string, decimal> { [Monedas.ARS] = 1000m };
        var ledger = new System.Collections.Generic.Dictionary<string, decimal> { [Monedas.ARS] = 1000m };

        var result = CashLedgerRefundReconciliationCalculator.FindDivergences(derived, ledger);

        Assert.Empty(result);
    }

    [Fact]
    public void FindDivergences_DifferentAmount_ReturnsDivergenceWithDelta()
    {
        // El extracto muestra 1000 recibidos pero la caja solo tiene 700 vigentes: divergencia de 300.
        var derived = new System.Collections.Generic.Dictionary<string, decimal> { [Monedas.ARS] = 1000m };
        var ledger = new System.Collections.Generic.Dictionary<string, decimal> { [Monedas.ARS] = 700m };

        var result = CashLedgerRefundReconciliationCalculator.FindDivergences(derived, ledger);

        var divergence = Assert.Single(result);
        Assert.Equal(Monedas.ARS, divergence.Currency);
        Assert.Equal(1000m, divergence.DerivedAmount);
        Assert.Equal(700m, divergence.LedgerAmount);
        Assert.Equal(300m, divergence.Delta);
    }

    [Fact]
    public void FindDivergences_LedgerFullyReversed_DerivedStillShowsAmount_ReturnsDivergence()
    {
        // "Reversado -> no cuenta": si el asiento de caja se revirtio del todo, su aporte al lado "Caja" es 0
        // (el caller ya filtro !IsReversed/!IsReversal antes de armar este diccionario). El extracto, en
        // cambio, sigue mostrando el reembolso como recibido (nadie toco la cancelacion). El calculo debe
        // marcar la divergencia: exactamente el escenario que motiva GAP C.
        var derived = new System.Collections.Generic.Dictionary<string, decimal> { [Monedas.USD] = 500m };
        var ledger = new System.Collections.Generic.Dictionary<string, decimal>(); // nada vigente en USD

        var result = CashLedgerRefundReconciliationCalculator.FindDivergences(derived, ledger);

        var divergence = Assert.Single(result);
        Assert.Equal(Monedas.USD, divergence.Currency);
        Assert.Equal(500m, divergence.DerivedAmount);
        Assert.Equal(0m, divergence.LedgerAmount);
    }

    [Fact]
    public void FindDivergences_WithinRoundingTolerance_ReturnsEmpty()
    {
        // Un centavo de diferencia por redondeo intermedio no es una divergencia real.
        var derived = new System.Collections.Generic.Dictionary<string, decimal> { [Monedas.ARS] = 1000.00m };
        var ledger = new System.Collections.Generic.Dictionary<string, decimal> { [Monedas.ARS] = 1000.01m };

        var result = CashLedgerRefundReconciliationCalculator.FindDivergences(derived, ledger);

        Assert.Empty(result);
    }
}

public class CashLedgerRefundReconciliationJobTests
{
    private static AppDbContext NewDbContext() =>
        new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"adr022-gapc-job-tests-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static (
        CashLedgerRefundReconciliationJob Job,
        AppDbContext Ctx,
        Mock<INotificationService> NotificationMock,
        Mock<UserManager<ApplicationUser>> UserManagerMock
    ) BuildJob(AppDbContext ctx)
    {
        var notificationMock = new Mock<INotificationService>();
        notificationMock
            .Setup(s => s.CreateAndSendAsync(It.IsAny<Notification>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Notification n, CancellationToken _) => n);
        // Espejo minimo del NotificationService real (NotificationService.cs:109): resuelve los avisos VIVOS
        // con esa clave contra el MISMO ctx del test, para que "RunAsync_DivergenceNoLongerPresent_..." pueda
        // verificar el auto-apagado end-to-end (si solo devolvieramos un numero fijo, no probariamos nada).
        notificationMock
            .Setup(s => s.ResolveByKeyAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns<string?, CancellationToken>(async (key, token) =>
            {
                if (string.IsNullOrWhiteSpace(key)) return 0;
                var live = await ctx.Notifications
                    .Where(n => n.ResolutionKey == key && n.ResolvedAt == null && !n.IsRead && !n.IsDismissed)
                    .ToListAsync(token);
                if (live.Count == 0) return 0;
                var now = DateTime.UtcNow;
                foreach (var n in live) n.ResolvedAt = now;
                await ctx.SaveChangesAsync(token);
                return live.Count;
            });

        var storeMock = new Mock<IUserStore<ApplicationUser>>();
        var userManagerMock = new Mock<UserManager<ApplicationUser>>(
            storeMock.Object, null!, null!,
            Array.Empty<IUserValidator<ApplicationUser>>(),
            Array.Empty<IPasswordValidator<ApplicationUser>>(),
            null!, null!, null!, null!);
        userManagerMock
            .Setup(m => m.GetUsersInRoleAsync("Admin"))
            .ReturnsAsync(new System.Collections.Generic.List<ApplicationUser>
            {
                new() { Id = "admin-1", UserName = "admin-1" }
            });

        var job = new CashLedgerRefundReconciliationJob(
            ctx, notificationMock.Object, userManagerMock.Object,
            NullLogger<CashLedgerRefundReconciliationJob>.Instance);

        return (job, ctx, notificationMock, userManagerMock);
    }

    /// <summary>
    /// Seedea una cancelacion con su linea (moneda + ReceivedRefundAmount ya imputado), su reembolso, su
    /// asignacion viva a esa MISMA cancelacion, y — si <paramref name="ledgerAmount"/> tiene valor — un
    /// CashLedgerEntry del reembolso (vigente o revertido segun <paramref name="reversed"/>).
    /// </summary>
    private static async Task SeedCancellationWithRefundAsync(
        AppDbContext ctx,
        int reservaId,
        string numeroReserva,
        string currency,
        decimal receivedRefundAmount,
        decimal? ledgerAmount,
        bool reversed = false)
    {
        var reserva = new Reserva { Id = reservaId, NumeroReserva = numeroReserva };
        ctx.Reservas.Add(reserva);

        var bc = new BookingCancellation
        {
            Id = reservaId,
            PublicId = Guid.NewGuid(),
            ReservaId = reservaId,
            CustomerId = 1,
            SupplierId = 1,
            Status = BookingCancellationStatus.AwaitingOperatorRefund,
            Reason = "Test GAP C",
            DraftedAt = DateTime.UtcNow.AddDays(-5),
            DraftedByUserId = "vendedor-1",
            FiscalSnapshot = new FiscalSnapshot(),
        };
        ctx.BookingCancellations.Add(bc);

        var line = new BookingCancellationLine
        {
            Id = reservaId,
            PublicId = Guid.NewGuid(),
            BookingCancellationId = bc.Id,
            SupplierId = 1,
            ServiceTable = CancellableServiceTable.Generic,
            ServiceId = 1,
            Scope = BookingCancellationLineScope.Full,
            Currency = currency,
            ReceivedRefundAmount = receivedRefundAmount,
        };
        ctx.BookingCancellationLines.Add(line);

        var refund = new OperatorRefundReceived
        {
            Id = reservaId,
            PublicId = Guid.NewGuid(),
            SupplierId = 1,
            ReceivedAt = DateTime.UtcNow.AddDays(-5),
            ReceivedAmount = receivedRefundAmount,
            AllocatedAmount = receivedRefundAmount,
            Currency = currency,
            ReceivedByUserId = "admin-1",
            ReceivedByUserName = "Admin Uno",
        };
        ctx.OperatorRefundReceived.Add(refund);

        var allocation = new OperatorRefundAllocation
        {
            Id = reservaId,
            PublicId = Guid.NewGuid(),
            OperatorRefundReceivedId = refund.Id,
            BookingCancellationId = bc.Id,
            GrossAmount = receivedRefundAmount,
            NetAmount = receivedRefundAmount,
            IsVoided = false,
            CreatedByUserId = "admin-1",
        };
        ctx.OperatorRefundAllocations.Add(allocation);

        if (ledgerAmount.HasValue)
        {
            ctx.CashLedgerEntries.Add(new CashLedgerEntry
            {
                Id = reservaId,
                PublicId = Guid.NewGuid(),
                Direction = CashMovementDirections.Income,
                Amount = ledgerAmount.Value,
                Currency = currency,
                Method = "Transfer",
                OccurredAt = DateTime.UtcNow.AddDays(-5),
                SourceType = CashLedgerSourceTypes.OperatorRefund,
                OperatorRefundReceivedId = refund.Id,
                IsReversed = reversed,
                IsReversal = false,
            });
        }

        await ctx.SaveChangesAsync();
    }

    [Fact]
    public async Task RunAsync_DerivedMatchesLedger_DoesNotNotify()
    {
        await using var ctx = NewDbContext();
        // Extracto = 1000 recibidos, caja = 1000 vigentes: coinciden.
        await SeedCancellationWithRefundAsync(
            ctx, reservaId: 1, numeroReserva: "F-2026-0001",
            currency: Monedas.ARS, receivedRefundAmount: 1000m, ledgerAmount: 1000m);

        var (job, _, notificationMock, _) = BuildJob(ctx);

        var result = await job.RunAsync(CancellationToken.None);

        Assert.Equal(0, result.DivergencesFound);
        notificationMock.Verify(
            s => s.CreateAndSendAsync(It.IsAny<Notification>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_LedgerEntryReversed_DoesNotCountTowardsLiveTotal_NotifiesAdmins()
    {
        // GAP C en carne viva: alguien revirtio el asiento del reembolso desde Tesoreria (IsReversed=true),
        // pero la cancelacion NUNCA se entero: su linea sigue mostrando 1000 recibidos. El asiento revertido
        // NO CUENTA para el total de caja (queda en 0) -> divergencia -> aviso.
        await using var ctx = NewDbContext();
        await SeedCancellationWithRefundAsync(
            ctx, reservaId: 2, numeroReserva: "F-2026-0002",
            currency: Monedas.ARS, receivedRefundAmount: 1000m, ledgerAmount: 1000m, reversed: true);

        var (job, _, notificationMock, _) = BuildJob(ctx);

        var result = await job.RunAsync(CancellationToken.None);

        Assert.Equal(1, result.DivergencesFound);
        notificationMock.Verify(
            s => s.CreateAndSendAsync(
                It.Is<Notification>(n =>
                    n.UserId == "admin-1"
                    && n.Message.Contains("F-2026-0002")
                    && !n.Message.Contains("BookingCancellation") // nada tecnico en el mensaje al usuario
                    && n.ResolutionKey == "CashLedgerRefundReconciliation:2"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RunAsync_DifferentAmount_Notifies()
    {
        // La caja tiene MENOS plata vigente que lo que el extracto dice recibido (dato corrupto/legacy, no
        // por una reversion): tambien debe avisar.
        await using var ctx = NewDbContext();
        await SeedCancellationWithRefundAsync(
            ctx, reservaId: 3, numeroReserva: "F-2026-0003",
            currency: Monedas.ARS, receivedRefundAmount: 1000m, ledgerAmount: 400m);

        var (job, _, notificationMock, _) = BuildJob(ctx);

        var result = await job.RunAsync(CancellationToken.None);

        Assert.Equal(1, result.DivergencesFound);
        notificationMock.Verify(
            s => s.CreateAndSendAsync(It.IsAny<Notification>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_AlreadyHasLiveAlert_DoesNotDuplicateNotification()
    {
        // Corrida anterior ya avisó (aviso vivo con la misma clave de resolucion): la corrida de hoy, con la
        // MISMA divergencia sin resolver, no debe volver a crear otro aviso (anti-spam).
        await using var ctx = NewDbContext();
        await SeedCancellationWithRefundAsync(
            ctx, reservaId: 4, numeroReserva: "F-2026-0004",
            currency: Monedas.ARS, receivedRefundAmount: 1000m, ledgerAmount: 400m);

        ctx.Notifications.Add(new Notification
        {
            UserId = "admin-1",
            Type = "Warning",
            Priority = "Urgent",
            RelatedEntityId = 4,
            RelatedEntityType = "CashLedgerRefundReconciliation",
            ResolutionKey = "CashLedgerRefundReconciliation:4",
            Message = "aviso previo",
        });
        await ctx.SaveChangesAsync();

        var (job, _, notificationMock, _) = BuildJob(ctx);

        var result = await job.RunAsync(CancellationToken.None);

        Assert.Equal(1, result.DivergencesFound); // la divergencia se sigue contando/logueando...
        notificationMock.Verify(
            s => s.CreateAndSendAsync(It.IsAny<Notification>(), It.IsAny<CancellationToken>()),
            Times.Never); // ...pero no se crea un SEGUNDO aviso mientras el primero siga vivo.
    }

    [Fact]
    public async Task RunAsync_DivergenceNoLongerPresent_ResolvesPriorLiveAlert()
    {
        // La divergencia de una corrida anterior ya se corrigio (alguien re-asento la plata en caja): el
        // aviso viejo se apaga solo, no queda colgado para siempre.
        await using var ctx = NewDbContext();
        await SeedCancellationWithRefundAsync(
            ctx, reservaId: 5, numeroReserva: "F-2026-0005",
            currency: Monedas.ARS, receivedRefundAmount: 1000m, ledgerAmount: 1000m); // ya coincide

        ctx.Notifications.Add(new Notification
        {
            UserId = "admin-1",
            Type = "Warning",
            Priority = "Urgent",
            RelatedEntityId = 5,
            RelatedEntityType = "CashLedgerRefundReconciliation",
            ResolutionKey = "CashLedgerRefundReconciliation:5",
            Message = "aviso previo, ya resuelto",
        });
        await ctx.SaveChangesAsync();

        var (job, dbCtx, _, _) = BuildJob(ctx);

        var result = await job.RunAsync(CancellationToken.None);

        Assert.Equal(0, result.DivergencesFound);
        Assert.Equal(1, result.AutoResolved);

        var stillLive = await dbCtx.Notifications
            .AnyAsync(n => n.ResolutionKey == "CashLedgerRefundReconciliation:5" && n.ResolvedAt == null);
        Assert.False(stillLive);
    }

    [Fact]
    public async Task RunAsync_RefundSplitAcrossMultipleCancellations_IsSkipped_NoFalsePositive()
    {
        // Limitacion documentada del job: un reembolso repartido entre DOS cancelaciones distintas queda
        // fuera de esta ronda (no genera un aviso falso por el reparto legitimo).
        await using var ctx = NewDbContext();

        ctx.Reservas.Add(new Reserva { Id = 10, NumeroReserva = "F-2026-0010" });
        ctx.Reservas.Add(new Reserva { Id = 11, NumeroReserva = "F-2026-0011" });

        var bc1 = new BookingCancellation
        {
            Id = 10, PublicId = Guid.NewGuid(), ReservaId = 10, CustomerId = 1, SupplierId = 1,
            Status = BookingCancellationStatus.AwaitingOperatorRefund, Reason = "Test split",
            DraftedAt = DateTime.UtcNow.AddDays(-5), DraftedByUserId = "vendedor-1",
            FiscalSnapshot = new FiscalSnapshot(),
        };
        var bc2 = new BookingCancellation
        {
            Id = 11, PublicId = Guid.NewGuid(), ReservaId = 11, CustomerId = 1, SupplierId = 1,
            Status = BookingCancellationStatus.AwaitingOperatorRefund, Reason = "Test split",
            DraftedAt = DateTime.UtcNow.AddDays(-5), DraftedByUserId = "vendedor-1",
            FiscalSnapshot = new FiscalSnapshot(),
        };
        ctx.BookingCancellations.AddRange(bc1, bc2);

        var refund = new OperatorRefundReceived
        {
            Id = 10, PublicId = Guid.NewGuid(), SupplierId = 1, ReceivedAt = DateTime.UtcNow.AddDays(-5),
            ReceivedAmount = 1000m, AllocatedAmount = 1000m, Currency = Monedas.ARS,
            ReceivedByUserId = "admin-1", ReceivedByUserName = "Admin Uno",
        };
        ctx.OperatorRefundReceived.Add(refund);

        ctx.OperatorRefundAllocations.Add(new OperatorRefundAllocation
        {
            Id = 10, PublicId = Guid.NewGuid(), OperatorRefundReceivedId = refund.Id,
            BookingCancellationId = bc1.Id, GrossAmount = 600m, NetAmount = 600m,
            IsVoided = false, CreatedByUserId = "admin-1",
        });
        ctx.OperatorRefundAllocations.Add(new OperatorRefundAllocation
        {
            Id = 11, PublicId = Guid.NewGuid(), OperatorRefundReceivedId = refund.Id,
            BookingCancellationId = bc2.Id, GrossAmount = 400m, NetAmount = 400m,
            IsVoided = false, CreatedByUserId = "admin-1",
        });

        ctx.BookingCancellationLines.Add(new BookingCancellationLine
        {
            Id = 10, PublicId = Guid.NewGuid(), BookingCancellationId = bc1.Id, SupplierId = 1,
            ServiceTable = CancellableServiceTable.Generic, ServiceId = 1,
            Scope = BookingCancellationLineScope.Full, Currency = Monedas.ARS, ReceivedRefundAmount = 600m,
        });
        ctx.BookingCancellationLines.Add(new BookingCancellationLine
        {
            Id = 11, PublicId = Guid.NewGuid(), BookingCancellationId = bc2.Id, SupplierId = 1,
            ServiceTable = CancellableServiceTable.Generic, ServiceId = 1,
            Scope = BookingCancellationLineScope.Full, Currency = Monedas.ARS, ReceivedRefundAmount = 400m,
        });

        // El asiento del reembolso VIGENTE (1000, unico ingreso fisico) — a proposito NO coincide 1 a 1 con
        // ninguna de las dos lineas por separado, porque el reembolso es UNO solo repartido en dos.
        ctx.CashLedgerEntries.Add(new CashLedgerEntry
        {
            Id = 10, PublicId = Guid.NewGuid(), Direction = CashMovementDirections.Income, Amount = 1000m,
            Currency = Monedas.ARS, Method = "Transfer", OccurredAt = DateTime.UtcNow.AddDays(-5),
            SourceType = CashLedgerSourceTypes.OperatorRefund, OperatorRefundReceivedId = refund.Id,
        });

        await ctx.SaveChangesAsync();

        var (job, _, notificationMock, _) = BuildJob(ctx);

        var result = await job.RunAsync(CancellationToken.None);

        Assert.Equal(0, result.DivergencesFound);
        Assert.Equal(1, result.RefundsSkippedDueToMultiCancellationSplit);
        notificationMock.Verify(
            s => s.CreateAndSendAsync(It.IsAny<Notification>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
