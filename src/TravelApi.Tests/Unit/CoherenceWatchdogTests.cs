using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// (Tanda 4, 2026-07-04) Tests del vigía de coherencia (<see cref="CoherenceWatchdogJob"/> + los detectores de
/// <see cref="TravelApi.Infrastructure.Reservations.CoherenceChecks"/>). Corren InMemory + Moq (misma regla que el
/// resto de los jobs: sin Postgres, en milisegundos).
///
/// <para>Cubre: W1 repara marca colgada; W3 detecta y corrige la proyección de plata; W2 reporta anulada con
/// servicios vivos; W5 reporta anulada con deuda sin comprobante y NO la modifica; W1 repara + W2 reporta en la
/// misma corrida; datos sanos = 0 hallazgos y 0 notificaciones; idempotencia (dos corridas = mismo estado, sin
/// notificación duplicada).</para>
/// </summary>
public class CoherenceWatchdogTests
{
    // ============================================================
    // Armado
    // ============================================================

    private static AppDbContext NewDbContext() =>
        new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"coherence-watchdog-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    /// <summary>
    /// Construye el job con deps mockeadas. El mock de notificaciones PERSISTE la notificación en el mismo ctx (como
    /// hace el <see cref="INotificationService"/> real), para que el dedup por "no leída con el mismo mensaje"
    /// funcione entre corridas.
    /// </summary>
    private static (
        CoherenceWatchdogJob Job,
        AppDbContext Ctx,
        Mock<INotificationService> NotificationMock,
        Mock<UserManager<ApplicationUser>> UserManagerMock
    ) BuildJob(params string[] adminIds)
    {
        var ctx = NewDbContext();

        var notificationMock = new Mock<INotificationService>();
        // Simula el service real: persiste la Notification en el ctx para que el dedup la encuentre.
        notificationMock
            .Setup(n => n.CreateAndSendAsync(It.IsAny<Notification>(), It.IsAny<CancellationToken>()))
            .Returns<Notification, CancellationToken>(async (notif, token) =>
            {
                ctx.Notifications.Add(notif);
                await ctx.SaveChangesAsync(token);
                return notif;
            });

        var storeMock = new Mock<IUserStore<ApplicationUser>>();
        var userManagerMock = new Mock<UserManager<ApplicationUser>>(
            storeMock.Object,
            null!, null!,
            Array.Empty<IUserValidator<ApplicationUser>>(),
            Array.Empty<IPasswordValidator<ApplicationUser>>(),
            null!, null!, null!, null!);

        var admins = adminIds.Length == 0
            ? new List<ApplicationUser> { new() { Id = "admin-1", UserName = "admin-1" } }
            : adminIds.Select(id => new ApplicationUser { Id = id, UserName = id }).ToList();

        userManagerMock.Setup(u => u.GetUsersInRoleAsync("Admin"))
            .ReturnsAsync((IList<ApplicationUser>)admins);

        var recalculator = new CoherenceMoneyRecalculator(ctx, NullLogger<CoherenceMoneyRecalculator>.Instance);

        var job = new CoherenceWatchdogJob(
            ctx,
            recalculator,
            notificationMock.Object,
            userManagerMock.Object,
            NullLogger<CoherenceWatchdogJob>.Instance);

        return (job, ctx, notificationMock, userManagerMock);
    }

    private static HotelBooking Hotel(int id, int reservaId, string status, decimal salePrice) => new()
    {
        Id = id, ReservaId = reservaId, HotelName = "Hotel test", City = "BRC",
        RoomType = "Doble", MealPlan = "Desayuno", Adults = 1, Rooms = 1,
        CheckIn = DateTime.UtcNow.Date, CheckOut = DateTime.UtcNow.Date.AddDays(2),
        Status = status, SalePrice = salePrice, NetCost = 0m
    };

    private static ReservaMoneyByCurrency ArsRow(
        int id, int reservaId, decimal totalSale, decimal confirmedSale, decimal totalPaid, decimal balance) => new()
    {
        Id = id, ReservaId = reservaId, Currency = Monedas.ARS,
        TotalSale = totalSale, ConfirmedSale = confirmedSale, TotalCost = 0m, TotalPaid = totalPaid, Balance = balance
    };

    // ============================================================
    // W1 — marca colgada en estado terminal → auto-repara.
    // ============================================================

    [Fact]
    public async Task W1_TerminalReservaWithLiveMark_IsCleaned_NoNotification()
    {
        var (job, ctx, notificationMock, _) = BuildJob();

        // Reserva anulada, sin servicios ni plata (sana salvo por la marca colgada).
        ctx.Reservas.Add(new Reserva
        {
            Id = 1, NumeroReserva = "F-W1", Name = "Anulada con marca colgada",
            Status = EstadoReserva.Cancelled, AdultCount = 1,
            HasUnacknowledgedChanges = true, ChangesPendingSince = DateTime.UtcNow.AddDays(-3),
            TotalSale = 0m, ConfirmedSale = 0m, TotalPaid = 0m, Balance = 0m
        });
        await ctx.SaveChangesAsync();

        var result = await job.RunAsync(CancellationToken.None);

        Assert.Equal(1, result.AutoRepairedMarks);
        Assert.False(result.NotificationSent);

        var reserva = await ctx.Reservas.AsNoTracking().FirstAsync(r => r.Id == 1);
        Assert.False(reserva.HasUnacknowledgedChanges);
        Assert.Null(reserva.ChangesPendingSince);

        // Nada para revisar → ninguna notificación.
        notificationMock.Verify(n => n.CreateAndSendAsync(It.IsAny<Notification>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ============================================================
    // W3 — proyección de plata desactualizada → auto-corrige con el escritor canónico.
    // ============================================================

    [Fact]
    public async Task W3_StaleMoneyProjection_IsRecalculated_NoNotification()
    {
        var (job, ctx, notificationMock, _) = BuildJob();

        // Reserva viva con un hotel confirmado de 1000, pero los escalares quedaron en 0 (proyección vieja: algún
        // write-path escribió la plata sin pasar por el persister canónico).
        ctx.Reservas.Add(new Reserva
        {
            Id = 2, NumeroReserva = "F-W3", Name = "Plata desactualizada",
            Status = EstadoReserva.Confirmed, AdultCount = 1,
            TotalSale = 0m, ConfirmedSale = 0m, TotalPaid = 0m, Balance = 0m
        });
        ctx.HotelBookings.Add(Hotel(id: 20, reservaId: 2, status: WorkflowStatuses.Confirmado, salePrice: 1000m));
        await ctx.SaveChangesAsync();

        var result = await job.RunAsync(CancellationToken.None);

        Assert.Equal(1, result.AutoRepairedMoney);
        Assert.False(result.NotificationSent);

        var reserva = await ctx.Reservas.AsNoTracking().FirstAsync(r => r.Id == 2);
        Assert.Equal(1000m, reserva.TotalSale);
        Assert.Equal(1000m, reserva.ConfirmedSale);
        Assert.Equal(1000m, reserva.Balance);

        // Y quedó la fila hija por moneda que faltaba.
        var arsRow = await ctx.ReservaMoneyByCurrency.AsNoTracking().FirstAsync(m => m.ReservaId == 2);
        Assert.Equal(1000m, arsRow.ConfirmedSale);

        notificationMock.Verify(n => n.CreateAndSendAsync(It.IsAny<Notification>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ============================================================
    // W2 — anulada con servicios vivos → reporta (NO cancela los servicios) + notifica.
    // ============================================================

    [Fact]
    public async Task W2_AnnulledWithLiveService_IsReported_ServiceUntouched_Notifies()
    {
        var (job, ctx, notificationMock, _) = BuildJob();

        // Anulada con un hotel CONFIRMADO (vivo) y un cobro que deja el saldo en 0 (para aislar W2 de W5). La plata
        // se seedea consistente para que W3 no dispare.
        ctx.Reservas.Add(new Reserva
        {
            Id = 3, NumeroReserva = "F-W2", Name = "Anulada con servicio vivo",
            Status = EstadoReserva.Cancelled, AdultCount = 1,
            TotalSale = 1000m, ConfirmedSale = 1000m, TotalPaid = 1000m, Balance = 0m
        });
        ctx.HotelBookings.Add(Hotel(id: 30, reservaId: 3, status: WorkflowStatuses.Confirmado, salePrice: 1000m));
        ctx.Payments.Add(new Payment { Id = 300, ReservaId = 3, Amount = 1000m, Currency = Monedas.ARS, Status = "Paid" });
        ctx.ReservaMoneyByCurrency.Add(ArsRow(id: 3000, reservaId: 3,
            totalSale: 1000m, confirmedSale: 1000m, totalPaid: 1000m, balance: 0m));
        await ctx.SaveChangesAsync();

        var result = await job.RunAsync(CancellationToken.None);

        Assert.Equal(1, result.AnnulledWithLiveServices);
        Assert.Equal(0, result.AnnulledWithUnjustifiedDebt); // saldo 0 → no cae en W5
        Assert.True(result.NotificationSent);

        // El servicio NO fue tocado (la reparación la decide una persona): sigue confirmado.
        var hotel = await ctx.HotelBookings.AsNoTracking().FirstAsync(h => h.Id == 30);
        Assert.Equal(WorkflowStatuses.Confirmado, hotel.Status);

        notificationMock.Verify(n => n.CreateAndSendAsync(
            It.Is<Notification>(notif =>
                notif.RelatedEntityType == "CoherenceWatchdogReport"
                && notif.Priority == "Urgent"),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ============================================================
    // W5 — anulada con deuda sin Nota de Débito → reporta y NO modifica la reserva.
    // ============================================================

    [Fact]
    public async Task W5_AnnulledWithUnjustifiedDebt_IsReported_ReservaUntouched()
    {
        var (job, ctx, notificationMock, _) = BuildJob();

        // Anulada con saldo POSITIVO real: hotel cancelado (no aporta venta) + un puente que dejó el "pagado" en
        // negativo (cobro +500, puente -1000 → pagado -500 → saldo = 0 - (-500) = +500). Sin Nota de Débito → la
        // regla de dominio la marca Inconsistente. La plata se seedea consistente para que W3 no la cambie.
        ctx.Reservas.Add(new Reserva
        {
            Id = 4, NumeroReserva = "F-W5", Name = "Anulada con deuda sin comprobante",
            Status = EstadoReserva.Cancelled, AdultCount = 1,
            TotalSale = 0m, ConfirmedSale = 0m, TotalPaid = -500m, Balance = 500m
        });
        ctx.HotelBookings.Add(Hotel(id: 40, reservaId: 4, status: WorkflowStatuses.Cancelado, salePrice: 1000m));
        ctx.Payments.Add(new Payment { Id = 400, ReservaId = 4, Amount = 500m, Currency = Monedas.ARS, Status = "Paid" });
        ctx.Payments.Add(new Payment { Id = 401, ReservaId = 4, Amount = -1000m, Currency = Monedas.ARS, Status = "Paid" });
        ctx.ReservaMoneyByCurrency.Add(ArsRow(id: 4000, reservaId: 4,
            totalSale: 0m, confirmedSale: 0m, totalPaid: -500m, balance: 500m));
        await ctx.SaveChangesAsync();

        var result = await job.RunAsync(CancellationToken.None);

        Assert.Equal(1, result.AnnulledWithUnjustifiedDebt);
        Assert.Equal(0, result.AnnulledWithLiveServices); // sin servicios vivos → no cae en W2
        Assert.True(result.NotificationSent);

        // W5 NO modifica la reserva: sigue anulada con su mismo saldo.
        var reserva = await ctx.Reservas.AsNoTracking().FirstAsync(r => r.Id == 4);
        Assert.Equal(EstadoReserva.Cancelled, reserva.Status);
        Assert.Equal(500m, reserva.Balance);
    }

    // ============================================================
    // W1 repara Y W2 reporta en la MISMA corrida.
    // ============================================================

    [Fact]
    public async Task W1RepairsAndW2Reports_InSameRun()
    {
        var (job, ctx, notificationMock, _) = BuildJob();

        // Anulada con: (a) marca colgada (W1 la limpia) y (b) hotel vivo con cobro que deja saldo 0 (W2 la reporta).
        ctx.Reservas.Add(new Reserva
        {
            Id = 5, NumeroReserva = "F-W1W2", Name = "Anulada con marca y servicio vivo",
            Status = EstadoReserva.Cancelled, AdultCount = 1,
            HasUnacknowledgedChanges = true, ChangesPendingSince = DateTime.UtcNow.AddDays(-1),
            TotalSale = 1000m, ConfirmedSale = 1000m, TotalPaid = 1000m, Balance = 0m
        });
        ctx.HotelBookings.Add(Hotel(id: 50, reservaId: 5, status: WorkflowStatuses.Confirmado, salePrice: 1000m));
        ctx.Payments.Add(new Payment { Id = 500, ReservaId = 5, Amount = 1000m, Currency = Monedas.ARS, Status = "Paid" });
        ctx.ReservaMoneyByCurrency.Add(ArsRow(id: 5000, reservaId: 5,
            totalSale: 1000m, confirmedSale: 1000m, totalPaid: 1000m, balance: 0m));
        await ctx.SaveChangesAsync();

        var result = await job.RunAsync(CancellationToken.None);

        Assert.Equal(1, result.AutoRepairedMarks);
        Assert.Equal(1, result.AnnulledWithLiveServices);
        Assert.True(result.NotificationSent);

        var reserva = await ctx.Reservas.AsNoTracking().FirstAsync(r => r.Id == 5);
        Assert.False(reserva.HasUnacknowledgedChanges); // W1 la limpió
        Assert.Null(reserva.ChangesPendingSince);

        var hotel = await ctx.HotelBookings.AsNoTracking().FirstAsync(h => h.Id == 50);
        Assert.Equal(WorkflowStatuses.Confirmado, hotel.Status); // W2 solo reporta, no toca el servicio
    }

    // ============================================================
    // Datos sanos → 0 hallazgos, 0 notificaciones.
    // ============================================================

    [Fact]
    public async Task HealthyData_NoFindings_NoNotification()
    {
        var (job, ctx, notificationMock, _) = BuildJob();

        // Confirmada consistente (hotel 1000 + cobro 1000, escalares y fila hija al día).
        ctx.Reservas.Add(new Reserva
        {
            Id = 6, NumeroReserva = "F-SANA-VIVA", Name = "Confirmada sana",
            Status = EstadoReserva.Confirmed, AdultCount = 1,
            TotalSale = 1000m, ConfirmedSale = 1000m, TotalPaid = 1000m, Balance = 0m
        });
        ctx.HotelBookings.Add(Hotel(id: 60, reservaId: 6, status: WorkflowStatuses.Confirmado, salePrice: 1000m));
        ctx.Payments.Add(new Payment { Id = 600, ReservaId = 6, Amount = 1000m, Currency = Monedas.ARS, Status = "Paid" });
        ctx.ReservaMoneyByCurrency.Add(ArsRow(id: 6000, reservaId: 6,
            totalSale: 1000m, confirmedSale: 1000m, totalPaid: 1000m, balance: 0m));

        // Anulada sana: servicio cancelado, en cero, sin marca ni deuda.
        ctx.Reservas.Add(new Reserva
        {
            Id = 7, NumeroReserva = "F-SANA-ANULADA", Name = "Anulada sana",
            Status = EstadoReserva.Cancelled, AdultCount = 1,
            TotalSale = 0m, ConfirmedSale = 0m, TotalPaid = 0m, Balance = 0m
        });
        ctx.HotelBookings.Add(Hotel(id: 70, reservaId: 7, status: WorkflowStatuses.Cancelado, salePrice: 500m));
        await ctx.SaveChangesAsync();

        var result = await job.RunAsync(CancellationToken.None);

        Assert.Equal(0, result.AutoRepairedMarks);
        Assert.Equal(0, result.AutoRepairedMoney);
        Assert.Equal(0, result.AnnulledWithLiveServices);
        Assert.Equal(0, result.AnnulledWithUnjustifiedDebt);
        Assert.False(result.NotificationSent);

        notificationMock.Verify(n => n.CreateAndSendAsync(It.IsAny<Notification>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ============================================================
    // Idempotencia: dos corridas seguidas = mismo estado final, sin notificación duplicada.
    // ============================================================

    [Fact]
    public async Task TwoRuns_SameFinalState_NoDuplicateNotification()
    {
        var (job, ctx, notificationMock, _) = BuildJob("admin-1");

        // Un caso reportable estable (W5: anulada con deuda sin comprobante) que persiste corrida tras corrida.
        ctx.Reservas.Add(new Reserva
        {
            Id = 8, NumeroReserva = "F-IDEM", Name = "Anulada con deuda (idempotencia)",
            Status = EstadoReserva.Cancelled, AdultCount = 1,
            TotalSale = 0m, ConfirmedSale = 0m, TotalPaid = -500m, Balance = 500m
        });
        ctx.HotelBookings.Add(Hotel(id: 80, reservaId: 8, status: WorkflowStatuses.Cancelado, salePrice: 1000m));
        ctx.Payments.Add(new Payment { Id = 800, ReservaId = 8, Amount = 500m, Currency = Monedas.ARS, Status = "Paid" });
        ctx.Payments.Add(new Payment { Id = 801, ReservaId = 8, Amount = -1000m, Currency = Monedas.ARS, Status = "Paid" });
        ctx.ReservaMoneyByCurrency.Add(ArsRow(id: 8000, reservaId: 8,
            totalSale: 0m, confirmedSale: 0m, totalPaid: -500m, balance: 500m));
        await ctx.SaveChangesAsync();

        var firstRun = await job.RunAsync(CancellationToken.None);
        Assert.Equal(1, firstRun.AnnulledWithUnjustifiedDebt);
        Assert.True(firstRun.NotificationSent);

        var secondRun = await job.RunAsync(CancellationToken.None);
        // Sigue detectando el mismo caso (no lo arregla solo, es reportable)...
        Assert.Equal(1, secondRun.AnnulledWithUnjustifiedDebt);
        // ...pero NO crea una segunda notificación: ya hay una no leída con el mismo mensaje.
        Assert.False(secondRun.NotificationSent);

        // En total, una sola notificación creada entre las dos corridas.
        notificationMock.Verify(n => n.CreateAndSendAsync(It.IsAny<Notification>(), It.IsAny<CancellationToken>()),
            Times.Once);

        var notifications = await ctx.Notifications.AsNoTracking().CountAsync();
        Assert.Equal(1, notifications);

        // El estado de la reserva quedó igual (el caso reportable no se auto-modifica).
        var reserva = await ctx.Reservas.AsNoTracking().FirstAsync(r => r.Id == 8);
        Assert.Equal(EstadoReserva.Cancelled, reserva.Status);
        Assert.Equal(500m, reserva.Balance);
    }

    // ============================================================
    // Happy path del contexto: anulada con deuda PERO con Nota de Débito de multa viva → NO cae en W5.
    // ============================================================

    [Fact]
    public async Task AnnulledWithLiveDebitNote_AndPositiveBalance_IsNotReportedAsW5()
    {
        var (job, ctx, notificationMock, _) = BuildJob();

        // Mismo saldo positivo real que el caso W5 (cobro +500, puente -1000 → saldo +500), pero acá SÍ hay una
        // multa respaldada por una Nota de Débito viva → el contexto es "MultaPorCobrar", NO "Inconsistente".
        ctx.Reservas.Add(new Reserva
        {
            Id = 9, NumeroReserva = "F-CONMULTA", Name = "Anulada con multa respaldada",
            Status = EstadoReserva.Cancelled, AdultCount = 1,
            TotalSale = 0m, ConfirmedSale = 0m, TotalPaid = -500m, Balance = 500m
        });
        ctx.HotelBookings.Add(Hotel(id: 90, reservaId: 9, status: WorkflowStatuses.Cancelado, salePrice: 1000m));
        ctx.Payments.Add(new Payment { Id = 900, ReservaId = 9, Amount = 500m, Currency = Monedas.ARS, Status = "Paid" });
        ctx.Payments.Add(new Payment { Id = 901, ReservaId = 9, Amount = -1000m, Currency = Monedas.ARS, Status = "Paid" });
        ctx.ReservaMoneyByCurrency.Add(ArsRow(id: 9000, reservaId: 9,
            totalSale: 0m, confirmedSale: 0m, totalPaid: -500m, balance: 500m));

        // Multa viva: penalidad confirmada con monto positivo (rama de emisión diferida del predicado compartido).
        ctx.BookingCancellations.Add(new BookingCancellation
        {
            Id = 9, PublicId = Guid.NewGuid(), ReservaId = 9,
            CustomerId = 1, SupplierId = 1,
            Status = BookingCancellationStatus.ManualReviewPending,
            PenaltyStatus = PenaltyStatus.Confirmed,
            PenaltyAmountAtEvent = 500m,
            Reason = "test", DraftedAt = DateTime.UtcNow, DraftedByUserId = "tester",
            FiscalSnapshot = new FiscalSnapshot(),
        });
        await ctx.SaveChangesAsync();

        var result = await job.RunAsync(CancellationToken.None);

        // La multa respaldada NO es un dato roto → no cae en W5, y sin servicios vivos tampoco en W2.
        Assert.Equal(0, result.AnnulledWithUnjustifiedDebt);
        Assert.Equal(0, result.AnnulledWithLiveServices);
        Assert.False(result.NotificationSent);

        notificationMock.Verify(n => n.CreateAndSendAsync(It.IsAny<Notification>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task AnnulledWithPenaltyUnderReview_AndPositiveBalance_IsNotReportedAsW5()
    {
        // Fix "multa fantasma": una anulada con saldo positivo cuya multa se CONFIRMÓ pero su Nota de Débito quedó
        // FALLIDA (o en resolución manual) NO es un dato roto para W5: la vigila la bandeja de back-office. W5 solo
        // reporta el caso sin NINGÚN rastro de multa (Inconsistente).
        var (job, ctx, notificationMock, _) = BuildJob();

        ctx.Reservas.Add(new Reserva
        {
            Id = 12, NumeroReserva = "F-ENREVISION", Name = "Anulada con multa en revisión",
            Status = EstadoReserva.Cancelled, AdultCount = 1,
            TotalSale = 0m, ConfirmedSale = 0m, TotalPaid = -500m, Balance = 500m
        });
        ctx.HotelBookings.Add(Hotel(id: 120, reservaId: 12, status: WorkflowStatuses.Cancelado, salePrice: 1000m));
        ctx.Payments.Add(new Payment { Id = 1200, ReservaId = 12, Amount = 500m, Currency = Monedas.ARS, Status = "Paid" });
        ctx.Payments.Add(new Payment { Id = 1201, ReservaId = 12, Amount = -1000m, Currency = Monedas.ARS, Status = "Paid" });
        ctx.ReservaMoneyByCurrency.Add(ArsRow(id: 12000, reservaId: 12,
            totalSale: 0m, confirmedSale: 0m, totalPaid: -500m, balance: 500m));

        ctx.BookingCancellations.Add(new BookingCancellation
        {
            Id = 12, PublicId = Guid.NewGuid(), ReservaId = 12,
            CustomerId = 1, SupplierId = 1,
            Status = BookingCancellationStatus.ManualReviewPending,
            PenaltyStatus = PenaltyStatus.Confirmed,
            PenaltyAmountAtEvent = 500m,
            DebitNoteStatus = DebitNoteStatus.Failed, // ND fallida = "en revisión", no dato roto.
            Reason = "test", DraftedAt = DateTime.UtcNow, DraftedByUserId = "tester",
            FiscalSnapshot = new FiscalSnapshot(),
        });
        await ctx.SaveChangesAsync();

        var result = await job.RunAsync(CancellationToken.None);

        Assert.Equal(0, result.AnnulledWithUnjustifiedDebt); // NO reportada por W5.
        notificationMock.Verify(n => n.CreateAndSendAsync(It.IsAny<Notification>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ============================================================
    // No-fuga: el mensaje de la notificación NO expone internos técnicos.
    // ============================================================

    [Fact]
    public async Task NotificationMessage_DoesNotLeakTechnicalInternals()
    {
        var (job, ctx, _, _) = BuildJob();

        // Caso reportable (W2: anulada con servicio vivo, saldo 0). El id 91 no debe aparecer en el mensaje.
        ctx.Reservas.Add(new Reserva
        {
            Id = 91, NumeroReserva = "F-NOFUGA", Name = "Anulada con servicio vivo",
            Status = EstadoReserva.Cancelled, AdultCount = 1,
            TotalSale = 1000m, ConfirmedSale = 1000m, TotalPaid = 1000m, Balance = 0m
        });
        ctx.HotelBookings.Add(Hotel(id: 910, reservaId: 91, status: WorkflowStatuses.Confirmado, salePrice: 1000m));
        ctx.Payments.Add(new Payment { Id = 9100, ReservaId = 91, Amount = 1000m, Currency = Monedas.ARS, Status = "Paid" });
        ctx.ReservaMoneyByCurrency.Add(ArsRow(id: 91000, reservaId: 91,
            totalSale: 1000m, confirmedSale: 1000m, totalPaid: 1000m, balance: 0m));
        await ctx.SaveChangesAsync();

        var result = await job.RunAsync(CancellationToken.None);
        Assert.True(result.NotificationSent);

        var notification = await ctx.Notifications.AsNoTracking().FirstAsync();
        var message = notification.Message;

        // Nada técnico: ni el id de la reserva, ni códigos de check, ni el estado crudo en inglés.
        Assert.DoesNotContain("91", message);
        Assert.DoesNotContain("W1", message);
        Assert.DoesNotContain("W2", message);
        Assert.DoesNotContain("W5", message);
        Assert.DoesNotContain("Cancelled", message);

        // Sí el texto de negocio esperado.
        Assert.Contains("servicios que quedaron sin cancelar", message);
    }

    // ============================================================
    // Números de reserva en el mensaje: helpers de seed.
    // ============================================================

    /// <summary>
    /// Seedea una reserva anulada con un servicio VIVO y saldo 0 (cae en W2, no en W5). La plata queda consistente
    /// para que W3 no dispare. Cada tabla tiene su propio espacio de claves en InMemory, por eso se derivan ids únicos
    /// del id de reserva.
    /// </summary>
    private static void SeedAnnulledWithLiveService(AppDbContext ctx, int reservaId, string numeroReserva)
    {
        ctx.Reservas.Add(new Reserva
        {
            Id = reservaId, NumeroReserva = numeroReserva, Name = "Anulada con servicio vivo",
            Status = EstadoReserva.Cancelled, AdultCount = 1,
            TotalSale = 1000m, ConfirmedSale = 1000m, TotalPaid = 1000m, Balance = 0m
        });
        ctx.HotelBookings.Add(Hotel(id: reservaId * 10, reservaId: reservaId,
            status: WorkflowStatuses.Confirmado, salePrice: 1000m));
        ctx.Payments.Add(new Payment
        {
            Id = reservaId * 10, ReservaId = reservaId, Amount = 1000m, Currency = Monedas.ARS, Status = "Paid"
        });
        ctx.ReservaMoneyByCurrency.Add(ArsRow(id: reservaId * 100, reservaId: reservaId,
            totalSale: 1000m, confirmedSale: 1000m, totalPaid: 1000m, balance: 0m));
    }

    /// <summary>
    /// Seedea una reserva anulada con saldo POSITIVO sin Nota de Débito que lo respalde (cae en W5, no en W2). Mismo
    /// patrón que el test W5 individual: hotel cancelado + puente negativo que deja el saldo en +500.
    /// </summary>
    private static void SeedAnnulledWithUnjustifiedDebt(AppDbContext ctx, int reservaId, string numeroReserva)
    {
        ctx.Reservas.Add(new Reserva
        {
            Id = reservaId, NumeroReserva = numeroReserva, Name = "Anulada con deuda sin comprobante",
            Status = EstadoReserva.Cancelled, AdultCount = 1,
            TotalSale = 0m, ConfirmedSale = 0m, TotalPaid = -500m, Balance = 500m
        });
        ctx.HotelBookings.Add(Hotel(id: reservaId * 10, reservaId: reservaId,
            status: WorkflowStatuses.Cancelado, salePrice: 1000m));
        ctx.Payments.Add(new Payment
        {
            Id = reservaId * 10, ReservaId = reservaId, Amount = 500m, Currency = Monedas.ARS, Status = "Paid"
        });
        ctx.Payments.Add(new Payment
        {
            Id = reservaId * 10 + 1, ReservaId = reservaId, Amount = -1000m, Currency = Monedas.ARS, Status = "Paid"
        });
        ctx.ReservaMoneyByCurrency.Add(ArsRow(id: reservaId * 100, reservaId: reservaId,
            totalSale: 0m, confirmedSale: 0m, totalPaid: -500m, balance: 500m));
    }

    private static async Task<string> RunAndGetMessage(CoherenceWatchdogJob job, AppDbContext ctx)
    {
        var result = await job.RunAsync(CancellationToken.None);
        Assert.True(result.NotificationSent);
        var notification = await ctx.Notifications.AsNoTracking().FirstAsync();
        return notification.Message;
    }

    // ============================================================
    // (a) El mensaje incluye los números de reserva de W2 y W5.
    // ============================================================

    [Fact]
    public async Task Message_IncludesReservaNumbers_OfW2AndW5()
    {
        var (job, ctx, _, _) = BuildJob();

        SeedAnnulledWithLiveService(ctx, reservaId: 101, numeroReserva: "F-2026-1001");
        SeedAnnulledWithUnjustifiedDebt(ctx, reservaId: 110, numeroReserva: "F-2026-1010");
        await ctx.SaveChangesAsync();

        var message = await RunAndGetMessage(job, ctx);

        Assert.Contains("F-2026-1001", message);
        Assert.Contains("F-2026-1010", message);
    }

    // ============================================================
    // (b) El mensaje NO expone el Id interno de la reserva.
    // ============================================================

    [Fact]
    public async Task Message_DoesNotContainInternalId()
    {
        var (job, ctx, _, _) = BuildJob();

        // Id interno "987654" elegido para que no colisione con el número de reserva mostrable ("F-2026-1500").
        SeedAnnulledWithUnjustifiedDebt(ctx, reservaId: 987654, numeroReserva: "F-2026-1500");
        await ctx.SaveChangesAsync();

        var message = await RunAndGetMessage(job, ctx);

        Assert.DoesNotContain("987654", message); // el id interno nunca se muestra
        Assert.Contains("F-2026-1500", message);   // el número de negocio sí
    }

    // ============================================================
    // (c) Tope de 10 números por categoría + "... y N más".
    // ============================================================

    [Fact]
    public async Task Message_CapsReferencesAtTen_AndSummarizesRest()
    {
        var (job, ctx, _, _) = BuildJob();

        // 11 anuladas con deuda sin comprobante (misma categoría W5). Ids ascendentes 2001..2011 → se listan las
        // primeras 10 y la 11ª se resume como "y 1 más".
        for (var i = 1; i <= 11; i++)
        {
            var reservaId = 2000 + i;
            SeedAnnulledWithUnjustifiedDebt(ctx, reservaId: reservaId, numeroReserva: $"F-2026-{reservaId}");
        }
        await ctx.SaveChangesAsync();

        var message = await RunAndGetMessage(job, ctx);

        Assert.Contains("... y 1 más", message);
        Assert.Contains("F-2026-2001", message);          // la primera sí se lista
        Assert.DoesNotContain("F-2026-2011", message);    // la 11ª queda fuera del listado (resumida)
    }

    // ============================================================
    // (d) Dos categorías juntas con el formato exacto esperado.
    // ============================================================

    [Fact]
    public async Task Message_TwoCategories_ExactFormat()
    {
        var (job, ctx, _, _) = BuildJob();

        // 2 con servicios sin cancelar (W2) + 3 con deuda sin comprobante (W5). Ids ascendentes para orden estable.
        SeedAnnulledWithLiveService(ctx, reservaId: 1001, numeroReserva: "F-2026-1001");
        SeedAnnulledWithLiveService(ctx, reservaId: 1002, numeroReserva: "F-2026-1002");
        SeedAnnulledWithUnjustifiedDebt(ctx, reservaId: 1010, numeroReserva: "F-2026-1010");
        SeedAnnulledWithUnjustifiedDebt(ctx, reservaId: 1012, numeroReserva: "F-2026-1012");
        SeedAnnulledWithUnjustifiedDebt(ctx, reservaId: 1020, numeroReserva: "F-2026-1020");
        await ctx.SaveChangesAsync();

        var message = await RunAndGetMessage(job, ctx);

        Assert.Equal(
            "Revisá estas reservas anuladas: " +
            "en 3 figura una deuda que no cierra (F-2026-1010, F-2026-1012 y F-2026-1020) y " +
            "2 tienen servicios que quedaron sin cancelar (F-2026-1001 y F-2026-1002).",
            message);
    }

    // ============================================================
    // (e) Singular intacto con una sola reserva.
    // ============================================================

    [Fact]
    public async Task Message_SingleReserva_KeepsSingularConcordance()
    {
        var (job, ctx, _, _) = BuildJob();

        SeedAnnulledWithUnjustifiedDebt(ctx, reservaId: 3001, numeroReserva: "F-2026-3001");
        await ctx.SaveChangesAsync();

        var message = await RunAndGetMessage(job, ctx);

        Assert.Equal(
            "Revisá esta reserva anulada: figura una deuda que no cierra (F-2026-3001).",
            message);
    }

    // ============================================================
    // (f) N3 (review 2026-07-08): formatos EXACTOS que faltaban.
    //     Singular-servicio + plural de UNA sola categoría (deuda-sola y servicio-sola).
    // ============================================================

    [Fact]
    public async Task Message_SingleReserva_ServiceOnly_ExactFormat()
    {
        var (job, ctx, _, _) = BuildJob();

        // Una sola reserva, y el hallazgo es de SERVICIOS (no deuda) -> singular de servicio.
        SeedAnnulledWithLiveService(ctx, reservaId: 4001, numeroReserva: "F-2026-4001");
        await ctx.SaveChangesAsync();

        var message = await RunAndGetMessage(job, ctx);

        Assert.Equal(
            "Revisá esta reserva anulada: tiene servicios que quedaron sin cancelar (F-2026-4001).",
            message);
    }

    [Fact]
    public async Task Message_DebtOnly_Plural_ExactFormat()
    {
        var (job, ctx, _, _) = BuildJob();

        // Varias reservas, TODAS de la misma categoría (deuda) -> plural de una sola categoría, sin " y ".
        SeedAnnulledWithUnjustifiedDebt(ctx, reservaId: 1010, numeroReserva: "F-2026-1010");
        SeedAnnulledWithUnjustifiedDebt(ctx, reservaId: 1012, numeroReserva: "F-2026-1012");
        await ctx.SaveChangesAsync();

        var message = await RunAndGetMessage(job, ctx);

        Assert.Equal(
            "Revisá estas reservas anuladas: en 2 figura una deuda que no cierra (F-2026-1010 y F-2026-1012).",
            message);
    }

    [Fact]
    public async Task Message_ServiceOnly_Plural_ExactFormat()
    {
        var (job, ctx, _, _) = BuildJob();

        // Varias reservas, TODAS de la misma categoría (servicios) -> plural de una sola categoría, sin " y ".
        SeedAnnulledWithLiveService(ctx, reservaId: 1001, numeroReserva: "F-2026-1001");
        SeedAnnulledWithLiveService(ctx, reservaId: 1002, numeroReserva: "F-2026-1002");
        await ctx.SaveChangesAsync();

        var message = await RunAndGetMessage(job, ctx);

        Assert.Equal(
            "Revisá estas reservas anuladas: 2 tienen servicios que quedaron sin cancelar (F-2026-1001 y F-2026-1002).",
            message);
    }
}
