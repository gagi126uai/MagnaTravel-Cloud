using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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

    /// <summary>UserId del usuario "feliz" que sembramos por default: activo y con LOS DOS permisos
    /// (<c>tesoreria.supplier_payments</c> + <c>cobranzas.see_cost</c>) — mismo comportamiento que las
    /// corridas historicas de este suite, donde el destinatario veia todo. Los tests que necesitan un
    /// escenario distinto (otro usuario, sin permiso de costos, inactivo...) pisan el setup del mock
    /// DESPUES de llamar a <see cref="BuildJob"/> (el ultimo <c>Setup</c> registrado en Moq gana).</summary>
    private const string DefaultUserId = "admin-1";

    private static (
        CashLedgerRefundReconciliationJob Job,
        AppDbContext Ctx,
        Mock<INotificationService> NotificationMock,
        Mock<IUserPermissionResolver> PermissionResolverMock
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

        // Revision seguridad 2026-08-19 (B1+B2): la audiencia ya NO se resuelve reproduciendo el handler de
        // autorizacion a mano (RolePermissions + bypass Admin) — se resuelve usuario-por-usuario via
        // IUserPermissionResolver.GetPermissionsAsync, la MISMA fuente de verdad que usa el resto del
        // sistema (aplica IsActive y el permiso real). El job candidatea TODO ApplicationUser activo desde
        // AppDbContext.Users (sembrar la fila real, no alcanza con mockear el resolver).
        if (!ctx.Users.Any(u => u.Id == DefaultUserId))
        {
            ctx.Users.Add(new ApplicationUser { Id = DefaultUserId, UserName = DefaultUserId, IsActive = true });
            ctx.SaveChanges();
        }

        var permissionResolverMock = new Mock<IUserPermissionResolver>();
        // Default fail-closed (mismo criterio que UserPermissionResolver real): cualquier userId sin setup
        // propio no tiene ningun permiso.
        permissionResolverMock
            .Setup(r => r.GetPermissionsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlySet<string>)new HashSet<string>());
        // El usuario "feliz" default: registrado DESPUES del wildcard de arriba, asi Moq lo prioriza para
        // ese userId puntual (ultimo Setup que matchea gana).
        permissionResolverMock
            .Setup(r => r.GetPermissionsAsync(DefaultUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlySet<string>)new HashSet<string>
            {
                Permissions.TesoreriaSupplierPayments,
                Permissions.CobranzasSeeCost,
            });

        var job = new CashLedgerRefundReconciliationJob(
            ctx, notificationMock.Object, permissionResolverMock.Object,
            NullLogger<CashLedgerRefundReconciliationJob>.Instance);

        return (job, ctx, notificationMock, permissionResolverMock);
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
        // Decision 2026-08-19: texto EXACTO (T-6) + Priority "Normal" (chau banner naranja/etiqueta
        // "Urgente" para un descalce de UNA reserva puntual).
        notificationMock.Verify(
            s => s.CreateAndSendAsync(
                It.Is<Notification>(n =>
                    n.Priority == "Normal"
                    && n.Message == "La devolución del operador de la reserva F-2026-0003 no coincide con " +
                       "la caja: hay una diferencia de ARS 600,00. Revisala cuando puedas."),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RunAsync_TwoCurrenciesDiverge_MessageListsBothSeparately_NeverSummed()
    {
        // P-3: dos monedas distintas de la MISMA reserva divergen a la vez -> el mensaje lista las DOS,
        // cada una con su propio numero, nunca un total sumado (no tendria sentido sumar ARS + USD).
        await using var ctx = NewDbContext();

        ctx.Reservas.Add(new Reserva { Id = 20, NumeroReserva = "F-2026-0020" });
        var bc = new BookingCancellation
        {
            Id = 20, PublicId = Guid.NewGuid(), ReservaId = 20, CustomerId = 1, SupplierId = 1,
            Status = BookingCancellationStatus.AwaitingOperatorRefund, Reason = "Test dos monedas",
            DraftedAt = DateTime.UtcNow.AddDays(-5), DraftedByUserId = "vendedor-1",
            FiscalSnapshot = new FiscalSnapshot(),
        };
        ctx.BookingCancellations.Add(bc);

        // Una linea ARS y una linea USD sobre la MISMA cancelacion, cada una con su reembolso propio
        // (un reembolso tiene UNA sola moneda) y su caja divergente.
        ctx.BookingCancellationLines.Add(new BookingCancellationLine
        {
            Id = 20, PublicId = Guid.NewGuid(), BookingCancellationId = bc.Id, SupplierId = 1,
            ServiceTable = CancellableServiceTable.Generic, ServiceId = 1,
            Scope = BookingCancellationLineScope.Full, Currency = Monedas.ARS, ReceivedRefundAmount = 1000m,
        });
        ctx.BookingCancellationLines.Add(new BookingCancellationLine
        {
            Id = 21, PublicId = Guid.NewGuid(), BookingCancellationId = bc.Id, SupplierId = 1,
            ServiceTable = CancellableServiceTable.Generic, ServiceId = 2,
            Scope = BookingCancellationLineScope.Full, Currency = Monedas.USD, ReceivedRefundAmount = 500m,
        });

        var refundArs = new OperatorRefundReceived
        {
            Id = 20, PublicId = Guid.NewGuid(), SupplierId = 1, ReceivedAt = DateTime.UtcNow.AddDays(-5),
            ReceivedAmount = 1000m, AllocatedAmount = 1000m, Currency = Monedas.ARS,
            ReceivedByUserId = "admin-1", ReceivedByUserName = "Admin Uno",
        };
        var refundUsd = new OperatorRefundReceived
        {
            Id = 21, PublicId = Guid.NewGuid(), SupplierId = 1, ReceivedAt = DateTime.UtcNow.AddDays(-5),
            ReceivedAmount = 500m, AllocatedAmount = 500m, Currency = Monedas.USD,
            ReceivedByUserId = "admin-1", ReceivedByUserName = "Admin Uno",
        };
        ctx.OperatorRefundReceived.AddRange(refundArs, refundUsd);

        ctx.OperatorRefundAllocations.Add(new OperatorRefundAllocation
        {
            Id = 20, PublicId = Guid.NewGuid(), OperatorRefundReceivedId = refundArs.Id,
            BookingCancellationId = bc.Id, GrossAmount = 1000m, NetAmount = 1000m,
            IsVoided = false, CreatedByUserId = "admin-1",
        });
        ctx.OperatorRefundAllocations.Add(new OperatorRefundAllocation
        {
            Id = 21, PublicId = Guid.NewGuid(), OperatorRefundReceivedId = refundUsd.Id,
            BookingCancellationId = bc.Id, GrossAmount = 500m, NetAmount = 500m,
            IsVoided = false, CreatedByUserId = "admin-1",
        });

        // Caja: ARS quedo en 700 (divergencia 300) y USD en 0 (divergencia 500 — asiento nunca se asento).
        ctx.CashLedgerEntries.Add(new CashLedgerEntry
        {
            Id = 20, PublicId = Guid.NewGuid(), Direction = CashMovementDirections.Income, Amount = 700m,
            Currency = Monedas.ARS, Method = "Transfer", OccurredAt = DateTime.UtcNow.AddDays(-5),
            SourceType = CashLedgerSourceTypes.OperatorRefund, OperatorRefundReceivedId = refundArs.Id,
        });

        await ctx.SaveChangesAsync();

        var (job, _, notificationMock, _) = BuildJob(ctx);

        var result = await job.RunAsync(CancellationToken.None);

        Assert.Equal(1, result.DivergencesFound); // 1 cancelacion, aunque diverja en 2 monedas.
        notificationMock.Verify(
            s => s.CreateAndSendAsync(
                It.Is<Notification>(n =>
                    n.Message == "La devolución del operador de la reserva F-2026-0020 no coincide con la " +
                       "caja: hay una diferencia de ARS 300,00 y USD 500,00. Revisala cuando puedas."),
                It.IsAny<CancellationToken>()),
            Times.Once);
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
    public async Task RunAsync_LiveAlertAlreadyDismissedByUser_StillDoesNotRecreate()
    {
        // Decision 2026-08-19: "vivo" para el dedup de este job es SOLO ResolvedAt == null. Antes tambien
        // exigia !IsRead && !IsDismissed, asi que un aviso que el usuario ya vio/descarto se volvia a crear
        // al dia siguiente si la divergencia seguia sin corregirse — el "grita todos los dias" que motivo
        // esta obra. El estado real ahora vive en la ficha del operador, no en si alguien cerro la campanita.
        await using var ctx = NewDbContext();
        await SeedCancellationWithRefundAsync(
            ctx, reservaId: 6, numeroReserva: "F-2026-0006",
            currency: Monedas.ARS, receivedRefundAmount: 1000m, ledgerAmount: 400m);

        ctx.Notifications.Add(new Notification
        {
            UserId = "admin-1",
            Type = "Warning",
            Priority = "Normal",
            RelatedEntityId = 6,
            RelatedEntityType = "CashLedgerRefundReconciliation",
            ResolutionKey = "CashLedgerRefundReconciliation:6",
            Message = "aviso previo, ya visto y descartado por el usuario",
            IsRead = true,
            IsDismissed = true,
            ResolvedAt = null, // la causa sigue viva: la divergencia NUNCA se corrigio.
        });
        await ctx.SaveChangesAsync();

        var (job, _, notificationMock, _) = BuildJob(ctx);

        var result = await job.RunAsync(CancellationToken.None);

        Assert.Equal(1, result.DivergencesFound);
        notificationMock.Verify(
            s => s.CreateAndSendAsync(It.IsAny<Notification>(), It.IsAny<CancellationToken>()),
            Times.Never); // ya lo vio y lo descarto: no se le vuelve a mandar mientras siga sin resolverse.
    }

    [Fact]
    public async Task RunAsync_AudienceIsSupplierPaymentsPermission_NotJustHardcodedAdmin()
    {
        // Decision 2026-08-19 (fix B2 revision seguridad): la audiencia se resuelve usuario-por-usuario via
        // IUserPermissionResolver, NO reproduciendo RolePermissions/bypass Admin a mano. Este test prueba el
        // camino NO-Admin: un usuario "colaborador-1" con el permiso (segun el resolver) recibe el aviso,
        // aun con el usuario default de BuildJob ("admin-1") SIN el permiso esta vez.
        await using var ctx = NewDbContext();
        await SeedCancellationWithRefundAsync(
            ctx, reservaId: 7, numeroReserva: "F-2026-0007",
            currency: Monedas.ARS, receivedRefundAmount: 1000m, ledgerAmount: 400m);

        var (job, _, notificationMock, permissionResolverMock) = BuildJob(ctx);
        // Pisamos el default "feliz" de admin-1: en este test especifico NO tiene el permiso, para probar
        // que la audiencia depende del permiso real y no de un hardcodeo a un rol/usuario fijo.
        permissionResolverMock
            .Setup(r => r.GetPermissionsAsync(DefaultUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlySet<string>)new HashSet<string>());

        ctx.Users.Add(new ApplicationUser { Id = "colaborador-1", UserName = "colaborador-1", IsActive = true });
        await ctx.SaveChangesAsync();
        permissionResolverMock
            .Setup(r => r.GetPermissionsAsync("colaborador-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlySet<string>)new HashSet<string> { Permissions.TesoreriaSupplierPayments, Permissions.CobranzasSeeCost });

        var result = await job.RunAsync(CancellationToken.None);

        Assert.Equal(1, result.DivergencesFound);
        notificationMock.Verify(
            s => s.CreateAndSendAsync(
                It.Is<Notification>(n => n.UserId == "colaborador-1"), It.IsAny<CancellationToken>()),
            Times.Once);
        // admin-1 (sin el permiso esta vez) NO debe recibir nada.
        notificationMock.Verify(
            s => s.CreateAndSendAsync(
                It.Is<Notification>(n => n.UserId == DefaultUserId), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RunAsync_UserHasSupplierPaymentsButNotSeeCost_ReceivesVariantWithoutAmounts()
    {
        // Fix B1 (revision seguridad 2026-08-19, F-14): el modulo "Tesoreria" seedeado NO incluye
        // cobranzas.see_cost por default (Permissions.cs) — un rol asi armado puede leer el descalce como
        // HECHO, pero el monto de la diferencia es plata de costo y se le tiene que enmascarar, igual que
        // ya se hace con NetAmount/DerivedAmount/LedgerAmount en el DTO de la solapa Reembolsos.
        await using var ctx = NewDbContext();
        await SeedCancellationWithRefundAsync(
            ctx, reservaId: 9, numeroReserva: "F-2026-0009",
            currency: Monedas.ARS, receivedRefundAmount: 1000m, ledgerAmount: 400m);

        var (job, _, notificationMock, permissionResolverMock) = BuildJob(ctx);
        // Concentramos el test en un unico destinatario: admin-1 (default) no participa esta vez.
        permissionResolverMock
            .Setup(r => r.GetPermissionsAsync(DefaultUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlySet<string>)new HashSet<string>());

        ctx.Users.Add(new ApplicationUser { Id = "tesorero-sin-costos", UserName = "tesorero-sin-costos", IsActive = true });
        await ctx.SaveChangesAsync();
        // Tiene tesoreria.supplier_payments PERO NO cobranzas.see_cost.
        permissionResolverMock
            .Setup(r => r.GetPermissionsAsync("tesorero-sin-costos", It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlySet<string>)new HashSet<string> { Permissions.TesoreriaSupplierPayments });

        var result = await job.RunAsync(CancellationToken.None);

        Assert.Equal(1, result.DivergencesFound);
        notificationMock.Verify(
            s => s.CreateAndSendAsync(
                It.Is<Notification>(n =>
                    n.UserId == "tesorero-sin-costos"
                    && n.Message == "La devolución del operador de la reserva F-2026-0009 no coincide con la caja. Revisala cuando puedas."
                    && !n.Message.Contains("ARS") // sin monto: F-14, este usuario no ve costos.
                    && !n.Message.Contains("diferencia de")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RunAsync_InactiveUserWithQualifyingPermission_DoesNotReceiveNotification()
    {
        // Fix B2 (revision seguridad 2026-08-19): un usuario dado de baja (IsActive=false) NUNCA debe
        // recibir el aviso, aunque el permiso (segun el resolver mockeado) diga que SI calificaria. El job
        // tiene que filtrar por IsActive ANTES de siquiera resolver permisos — si ese filtro se sacara, este
        // test lo pesca.
        await using var ctx = NewDbContext();
        await SeedCancellationWithRefundAsync(
            ctx, reservaId: 8, numeroReserva: "F-2026-0008",
            currency: Monedas.ARS, receivedRefundAmount: 1000m, ledgerAmount: 400m);

        var (job, _, notificationMock, permissionResolverMock) = BuildJob(ctx);
        permissionResolverMock
            .Setup(r => r.GetPermissionsAsync(DefaultUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlySet<string>)new HashSet<string>());

        ctx.Users.Add(new ApplicationUser { Id = "tesorero-inactivo", UserName = "tesorero-inactivo", IsActive = false });
        await ctx.SaveChangesAsync();
        permissionResolverMock
            .Setup(r => r.GetPermissionsAsync("tesorero-inactivo", It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlySet<string>)new HashSet<string> { Permissions.TesoreriaSupplierPayments, Permissions.CobranzasSeeCost });

        var result = await job.RunAsync(CancellationToken.None);

        Assert.Equal(1, result.DivergencesFound); // la divergencia se detecta igual...
        notificationMock.Verify(
            s => s.CreateAndSendAsync(It.IsAny<Notification>(), It.IsAny<CancellationToken>()),
            Times.Never); // ...pero nadie la recibe: el unico candidato esta inactivo.
    }

    [Fact]
    public async Task RunAsync_ProductionLedgerShape_ViaManualCashMovement_MatchesDerived_NoFalsePositive()
    {
        // Fix de correctitud 2026-08-19 (ver XML-doc de CashLedgerRefundLedgerAmountLoader): el camino REAL
        // de escritura (OperatorRefundService) asienta el ingreso via ManualCashMovement — el CashLedgerEntry
        // resultante tiene ManualCashMovementId poblado y OperatorRefundReceivedId en null (el CHECK SQL
        // "exactamente un origen" lo exige). Antes del fix, el job buscaba SOLO por
        // CashLedgerEntry.OperatorRefundReceivedId directo y jamas encontraba nada por este camino: TODO
        // reembolso real quedaba marcado como divergente aunque la caja coincidiera perfecto. Este test
        // reproduce la forma REAL de los datos (no el atajo que usa SeedCancellationWithRefundAsync) para
        // que una regresion de ese bug rompa el suite.
        await using var ctx = NewDbContext();

        ctx.Reservas.Add(new Reserva { Id = 30, NumeroReserva = "F-2026-0030" });
        var bc = new BookingCancellation
        {
            Id = 30, PublicId = Guid.NewGuid(), ReservaId = 30, CustomerId = 1, SupplierId = 1,
            Status = BookingCancellationStatus.AwaitingOperatorRefund, Reason = "Test forma real de escritura",
            DraftedAt = DateTime.UtcNow.AddDays(-5), DraftedByUserId = "vendedor-1",
            FiscalSnapshot = new FiscalSnapshot(),
        };
        ctx.BookingCancellations.Add(bc);

        ctx.BookingCancellationLines.Add(new BookingCancellationLine
        {
            Id = 30, PublicId = Guid.NewGuid(), BookingCancellationId = bc.Id, SupplierId = 1,
            ServiceTable = CancellableServiceTable.Generic, ServiceId = 1,
            Scope = BookingCancellationLineScope.Full, Currency = Monedas.ARS, ReceivedRefundAmount = 1000m,
        });

        var refund = new OperatorRefundReceived
        {
            Id = 30, PublicId = Guid.NewGuid(), SupplierId = 1, ReceivedAt = DateTime.UtcNow.AddDays(-5),
            ReceivedAmount = 1000m, AllocatedAmount = 1000m, Currency = Monedas.ARS,
            ReceivedByUserId = "admin-1", ReceivedByUserName = "Admin Uno",
        };
        ctx.OperatorRefundReceived.Add(refund);

        ctx.OperatorRefundAllocations.Add(new OperatorRefundAllocation
        {
            Id = 30, PublicId = Guid.NewGuid(), OperatorRefundReceivedId = refund.Id,
            BookingCancellationId = bc.Id, GrossAmount = 1000m, NetAmount = 1000m,
            IsVoided = false, CreatedByUserId = "admin-1",
        });

        // Forma REAL (ManualCashMovementBuilder.BuildIncomeForRefund + CashLedgerEntryFactory.ForManualMovement):
        // el manual lleva el FK al refund, y el asiento de caja lleva el FK al manual — NUNCA al refund directo.
        var manualMovement = new ManualCashMovement
        {
            Id = 30, PublicId = Guid.NewGuid(), Direction = CashMovementDirections.Income, Amount = 1000m,
            Currency = Monedas.ARS, OccurredAt = DateTime.UtcNow.AddDays(-5), Method = "Transfer",
            Category = "OperatorRefund", Description = "Devolucion del operador Test",
            CreatedBy = "admin-1", OperatorRefundReceivedId = refund.Id,
        };
        ctx.ManualCashMovements.Add(manualMovement);

        ctx.CashLedgerEntries.Add(new CashLedgerEntry
        {
            Id = 30, PublicId = Guid.NewGuid(), Direction = CashMovementDirections.Income, Amount = 1000m,
            Currency = Monedas.ARS, Method = "Transfer", OccurredAt = DateTime.UtcNow.AddDays(-5),
            SourceType = CashLedgerSourceTypes.OperatorRefund, ManualCashMovementId = manualMovement.Id,
            // A proposito SIN OperatorRefundReceivedId: asi se escribe de verdad en produccion.
        });

        await ctx.SaveChangesAsync();

        var (job, _, notificationMock, _) = BuildJob(ctx);

        var result = await job.RunAsync(CancellationToken.None);

        Assert.Equal(0, result.DivergencesFound);
        notificationMock.Verify(
            s => s.CreateAndSendAsync(It.IsAny<Notification>(), It.IsAny<CancellationToken>()), Times.Never);
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
