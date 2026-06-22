using System;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.Contracts.Reservations;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using TravelApi.Infrastructure.Services.Reservations;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Fix de fondo (2026-06-18): el lead de origen debe pasar a Ganado en TODA entrada de la reserva a un
/// estado firme (venta operativa viva), no solo en la transicion MANUAL Budget -> InManagement.
///
/// <para>Antes, el disparo vivia unicamente en <c>ReservaService.UpdateStatusAsync</c>. Las reservas que
/// llegaban a firme por auto-confirmacion (motor), por el job de lifecycle o por el revert de una Cancelada
/// dejaban su lead sin marcar -> conversion de CRM subreportada. Estos tests cubren cada chokepoint y la
/// idempotencia/no-rotura del helper centralizado <see cref="SourceLeadWonHook"/>.</para>
/// </summary>
public class SourceLeadWonHookTests
{
    private static AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static ReservaAutoStateService NewEngine(AppDbContext context) =>
        new(context, NullLogger<ReservaAutoStateService>.Instance);

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

    // ReservaService cableado CON el motor de estados, para ejercitar el chokepoint de auto-confirmacion
    // (UpdateBalanceAsync -> motor). El mapper minimo solo se usa en GetReservaByIdAsync (post-revert).
    private static ReservaService NewReservaServiceWithEngine(AppDbContext context)
    {
        var settings = new Mock<IOperationalFinanceSettingsService>();
        settings.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OperationalFinanceSettings());

        var mapper = new Mock<IMapper>();
        mapper.Setup(m => m.Map<ReservaDto>(It.IsAny<Reserva>()))
              .Returns((Reserva r) => new ReservaDto
              {
                  PublicId = r.PublicId, NumeroReserva = r.NumeroReserva, Name = r.Name, Status = r.Status
              });

        return new ReservaService(context, mapper.Object, settings.Object,
            BuildUserManager(), NullLogger<ReservaService>.Instance,
            permissionResolver: null, httpContextAccessor: null,
            autoStateService: NewEngine(context));
    }

    private static ReservaLifecycleAutomationService NewLifecycleJob(AppDbContext context)
    {
        var settings = new Mock<IOperationalFinanceSettingsService>();
        settings.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OperationalFinanceSettings());

        return new ReservaLifecycleAutomationService(
            context,
            NullLogger<ReservaLifecycleAutomationService>.Instance,
            settings.Object,
            NewEngine(context));
    }

    // ===================== (1) Auto-confirmacion del motor =====================

    [Fact]
    public async Task AutoConfirmation_ReservaWithLeadReachesConfirmed_MarksLeadWon()
    {
        // Una reserva En gestion con todos sus servicios resueltos -> el motor la confirma sola
        // (InManagement -> Confirmed). Como Confirmed es estado firme, el lead de origen debe quedar Ganado.
        await using var ctx = NewContext();
        var lead = new Lead { FullName = "Ana", Phone = "1122334455", Status = LeadStatus.Contacted };
        ctx.Leads.Add(lead);

        var reserva = new Reserva
        {
            Name = "Viaje Ana",
            NumeroReserva = "RES-00001",
            Status = EstadoReserva.InManagement,
            SourceLeadId = lead.Id,
        };
        // Servicio resuelto (Confirmado) -> todos los vivos resueltos -> el motor confirma.
        reserva.HotelBookings.Add(new HotelBooking { Status = "Confirmado", Currency = "ARS", SalePrice = 1000m });
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var engine = NewEngine(ctx);
        var changed = await engine.EvaluateAndApplyAsync(reserva.Id);

        Assert.True(changed); // hubo transicion de estado real
        var refreshedReserva = await ctx.Reservas.FindAsync(reserva.Id);
        Assert.Equal(EstadoReserva.Confirmed, refreshedReserva!.Status);

        var refreshedLead = await ctx.Leads.FindAsync(lead.Id);
        Assert.Equal(LeadStatus.Won, refreshedLead!.Status);
        Assert.NotNull(refreshedLead.ClosedAt);
    }

    [Fact]
    public async Task AutoConfirmation_ViaUpdateBalance_MarksLeadWon()
    {
        // Mismo evento pero por el chokepoint REAL en vivo: UpdateBalanceAsync recalcula el saldo y corre el
        // motor. Prueba que el cableado (no solo el motor aislado) marca el lead.
        await using var ctx = NewContext();
        var lead = new Lead { FullName = "Nora", Phone = "1100110011", Status = LeadStatus.Contacted };
        ctx.Leads.Add(lead);

        var reserva = new Reserva
        {
            Name = "Viaje Nora",
            NumeroReserva = "RES-00010",
            Status = EstadoReserva.InManagement,
            SourceLeadId = lead.Id,
        };
        reserva.HotelBookings.Add(new HotelBooking { Status = "Confirmado", Currency = "ARS", SalePrice = 1000m });
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var service = NewReservaServiceWithEngine(ctx);
        await service.UpdateBalanceAsync(reserva.Id);

        var refreshedReserva = await ctx.Reservas.FindAsync(reserva.Id);
        Assert.Equal(EstadoReserva.Confirmed, refreshedReserva!.Status);

        var refreshedLead = await ctx.Leads.FindAsync(lead.Id);
        Assert.Equal(LeadStatus.Won, refreshedLead!.Status);
    }

    [Fact]
    public async Task Reconciliation_ReservaWithLeadGetsConfirmed_MarksLeadWon()
    {
        // La reconciliacion nocturna corre el mismo motor (suppressNotifications). Una reserva En gestion ya
        // toda resuelta que esquivo el chokepoint queda Confirmed y su lead Ganado.
        await using var ctx = NewContext();
        var lead = new Lead { FullName = "Tito", Phone = "1100220033", Status = LeadStatus.New };
        ctx.Leads.Add(lead);

        var reserva = new Reserva
        {
            Name = "Viaje Tito",
            NumeroReserva = "RES-00020",
            Status = EstadoReserva.InManagement,
            SourceLeadId = lead.Id,
        };
        reserva.HotelBookings.Add(new HotelBooking { Status = "Confirmado", Currency = "ARS", SalePrice = 1000m });
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var job = NewLifecycleJob(ctx);
        await job.ReconcileAutoStatesAsync(CancellationToken.None);

        var refreshedLead = await ctx.Leads.FindAsync(lead.Id);
        Assert.Equal(LeadStatus.Won, refreshedLead!.Status);
    }

    // ===================== (2) Job de lifecycle (Confirmed -> Traveling) =====================

    [Fact]
    public async Task LifecycleJob_ConfirmedToTraveling_PreservesLeadWon_Idempotent()
    {
        // ADR-036 (2026-06-21): el lead-won se dispara al ENTRAR en firme (InManagement/Confirmed), NO al pasar
        // a Traveling (Traveling salio de ActiveCollectionStatuses). Asi que para una reserva que ya llego a
        // Confirmed su lead ya esta Ganado. El job que la promueve a Traveling NO re-toca el lead: idempotente,
        // conserva la fecha del primer Ganado. (Antes este test fijaba que el paso a Traveling "rescataba" el
        // lead-won; ese rescate ya no aplica.)
        await using var ctx = NewContext();
        var lead = new Lead { FullName = "Bruno", Phone = "1100330044", Status = LeadStatus.Won };
        ctx.Leads.Add(lead);

        var reserva = new Reserva
        {
            Name = "Viaje Bruno",
            NumeroReserva = "RES-00030",
            Status = EstadoReserva.Confirmed,
            SourceLeadId = lead.Id,
            StartDate = DateTime.UtcNow.Date.AddDays(-1), // el viaje ya arranco -> promueve a Traveling
            AdultCount = 1,
            Balance = 0m, // ADR-036: candado de pago — el cliente debe estar saldado para viajar
        };
        // ADR-036 (2026-06-22): guard de "reserva vacia" — necesita al menos un servicio para promover.
        reserva.HotelBookings.Add(new HotelBooking { Status = "Confirmado", Currency = "ARS", SalePrice = 1000m });
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var job = NewLifecycleJob(ctx);
        await job.AutoTransitionConfirmedToTravelingAsync(CancellationToken.None);

        var refreshedReserva = await ctx.Reservas.FindAsync(reserva.Id);
        Assert.Equal(EstadoReserva.Traveling, refreshedReserva!.Status);

        // El lead sigue Ganado (no se rompio ni se duplico).
        var refreshedLead = await ctx.Leads.FindAsync(lead.Id);
        Assert.Equal(LeadStatus.Won, refreshedLead!.Status);
    }

    // ===================== ADR-036: gate de pago del cliente en el job (Confirmed -> Traveling) =====================

    [Fact]
    public async Task LifecycleJob_ConfirmedToTraveling_ClientNotFullyPaid_DoesNotPromote_NoThrow()
    {
        // ADR-036 (2026-06-21): candado DURO de pago del cliente. Si la reserva todavia debe (Balance > 0), el
        // job NO la promueve a Traveling, NO lanza excepcion, y se reintenta en la proxima corrida.
        await using var ctx = NewContext();
        var reserva = new Reserva
        {
            Name = "Viaje con deuda",
            NumeroReserva = "RES-00031",
            Status = EstadoReserva.Confirmed,
            StartDate = DateTime.UtcNow.Date.AddDays(-1), // el viaje ya arranco
            AdultCount = 1,
            Balance = 500m, // el cliente todavia debe
        };
        // ADR-036 (2026-06-22): la reserva tiene al menos un servicio para que el guard de "vacia" no la
        // bloquee antes — asi este test aisla el candado de PAGO (la unica razon de no promover debe ser la deuda).
        reserva.HotelBookings.Add(new HotelBooking { Status = "Confirmado", Currency = "ARS", SalePrice = 1000m });
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var job = NewLifecycleJob(ctx);
        // No debe lanzar: un file con saldo no aborta la corrida.
        var promoted = await job.AutoTransitionConfirmedToTravelingAsync(CancellationToken.None);

        Assert.Equal(0, promoted);
        var refreshed = await ctx.Reservas.FindAsync(reserva.Id);
        Assert.Equal(EstadoReserva.Confirmed, refreshed!.Status); // sigue Confirmada, no viajo
    }

    [Fact]
    public async Task LifecycleJob_ConfirmedToTraveling_ClientFullyPaid_Promotes()
    {
        // ADR-036: con el cliente saldado (Balance <= 0) el job SI promueve a Traveling.
        await using var ctx = NewContext();
        var reserva = new Reserva
        {
            Name = "Viaje saldado",
            NumeroReserva = "RES-00032",
            Status = EstadoReserva.Confirmed,
            StartDate = DateTime.UtcNow.Date.AddDays(-1),
            AdultCount = 1,
            Balance = 0m, // saldado
        };
        // ADR-036 (2026-06-22): con al menos un servicio cargado, el guard de "vacia" no bloquea -> promueve.
        reserva.HotelBookings.Add(new HotelBooking { Status = "Confirmado", Currency = "ARS", SalePrice = 1000m });
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var job = NewLifecycleJob(ctx);
        var promoted = await job.AutoTransitionConfirmedToTravelingAsync(CancellationToken.None);

        Assert.Equal(1, promoted);
        var refreshed = await ctx.Reservas.FindAsync(reserva.Id);
        Assert.Equal(EstadoReserva.Traveling, refreshed!.Status);
    }

    // ===================== ADR-036 (2026-06-22): guard de "reserva vacia" en el job (A) =====================

    [Fact]
    public async Task LifecycleJob_ConfirmedToTraveling_NoServices_DoesNotPromote_AndLogsBlock()
    {
        // ADR-036 (A): una reserva Confirmed, con StartDate alcanzada y cliente SALDADO, pero SIN ningun
        // servicio cargado, NO debe pasar a "En viaje". Queda Confirmed y se cuenta como bloqueada.
        await using var ctx = NewContext();
        var reserva = new Reserva
        {
            Name = "Viaje sin servicios",
            NumeroReserva = "RES-00033",
            Status = EstadoReserva.Confirmed,
            StartDate = DateTime.UtcNow.Date.AddDays(-1),
            AdultCount = 1,
            Balance = 0m, // saldado: la UNICA razon de no promover debe ser la falta de servicios
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var job = NewLifecycleJob(ctx);
        var promoted = await job.AutoTransitionConfirmedToTravelingAsync(CancellationToken.None);

        Assert.Equal(0, promoted);
        var refreshed = await ctx.Reservas.FindAsync(reserva.Id);
        Assert.Equal(EstadoReserva.Confirmed, refreshed!.Status); // sigue Confirmada, no viajo
        // No se escribio log de avance (no hubo transicion).
        Assert.Empty(ctx.ReservaStatusChangeLogs.Where(l => l.ReservaId == reserva.Id));
    }

    // ===================== ADR-036 (2026-06-22): saneamiento de "En viaje vacias" (B) =====================

    [Fact]
    public async Task LifecycleJob_AutoCloseEmptyTraveling_EmptyReserva_ClosesWithAudit()
    {
        // ADR-036 (B): una reserva que YA quedo En viaje sin ningun servicio se cierra (Traveling -> Closed),
        // con ClosedAt sellado y log de auditoria del actor "sistema".
        await using var ctx = NewContext();
        var reserva = new Reserva
        {
            Name = "En viaje vacia",
            NumeroReserva = "RES-00034",
            Status = EstadoReserva.Traveling,
            ClosedAt = null,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var job = NewLifecycleJob(ctx);
        var closed = await job.AutoCloseEmptyTravelingAsync(CancellationToken.None);

        Assert.Equal(1, closed);
        var refreshed = await ctx.Reservas.FindAsync(reserva.Id);
        Assert.Equal(EstadoReserva.Closed, refreshed!.Status);
        Assert.NotNull(refreshed.ClosedAt); // ClosedAt sellado

        var log = Assert.Single(ctx.ReservaStatusChangeLogs.Where(l => l.ReservaId == reserva.Id));
        Assert.Equal(EstadoReserva.Traveling, log.FromStatus);
        Assert.Equal(EstadoReserva.Closed, log.ToStatus);
        Assert.Equal("system:lifecycle", log.ByUserId);
    }

    [Fact]
    public async Task LifecycleJob_AutoCloseEmptyTraveling_ReservaWithServices_NotTouched()
    {
        // ADR-036 (B): una reserva En viaje CON al menos un servicio NO se toca por el saneamiento.
        await using var ctx = NewContext();
        var reserva = new Reserva
        {
            Name = "En viaje con servicio",
            NumeroReserva = "RES-00035",
            Status = EstadoReserva.Traveling,
        };
        reserva.HotelBookings.Add(new HotelBooking { Status = "Confirmado", Currency = "ARS", SalePrice = 1000m });
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var job = NewLifecycleJob(ctx);
        var closed = await job.AutoCloseEmptyTravelingAsync(CancellationToken.None);

        Assert.Equal(0, closed);
        var refreshed = await ctx.Reservas.FindAsync(reserva.Id);
        Assert.Equal(EstadoReserva.Traveling, refreshed!.Status); // intacta
    }

    // ===== ADR-036 (review de seguridad 2026-06-22): guard de PLATA en el saneamiento de "En viaje vacias" =====

    [Fact]
    public async Task LifecycleJob_AutoCloseEmptyTraveling_EmptyButHasPayment_DoesNotClose()
    {
        // Guard de plata (1/3): una reserva En viaje SIN servicios pero CON un cobro vivo NO se cierra: el cobro
        // se cuelga de la RESERVA, no de los servicios. Cerrarla esconderia un problema de plata. Queda En viaje
        // para revision manual.
        await using var ctx = NewContext();
        var reserva = new Reserva
        {
            Name = "Vacia con cobro",
            NumeroReserva = "RES-00036",
            Status = EstadoReserva.Traveling,
            Balance = 0m, // el balance esta en cero: la UNICA razon de no cerrar debe ser el cobro vivo
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        ctx.Payments.Add(new Payment { ReservaId = reserva.Id, Amount = 1000m, IsDeleted = false });
        await ctx.SaveChangesAsync();

        var job = NewLifecycleJob(ctx);
        var closed = await job.AutoCloseEmptyTravelingAsync(CancellationToken.None);

        Assert.Equal(0, closed);
        var refreshed = await ctx.Reservas.FindAsync(reserva.Id);
        Assert.Equal(EstadoReserva.Traveling, refreshed!.Status); // no se cerro
        // No se escribio log de avance (no hubo transicion de cierre).
        Assert.Empty(ctx.ReservaStatusChangeLogs.Where(l => l.ReservaId == reserva.Id));
    }

    [Fact]
    public async Task LifecycleJob_AutoCloseEmptyTraveling_EmptyButHasLiveCaeInvoice_DoesNotClose()
    {
        // Guard de plata (2/3): una reserva En viaje SIN servicios pero CON una factura con CAE vivo (no NC, no
        // anulada) NO se cierra. La factura se cuelga de la reserva: cerrarla a ciegas esconderia un comprobante
        // fiscal vivo sin servicio detras.
        await using var ctx = NewContext();
        var reserva = new Reserva
        {
            Name = "Vacia con factura CAE",
            NumeroReserva = "RES-00037",
            Status = EstadoReserva.Traveling,
            Balance = 0m,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        // Factura B (TipoComprobante 6, no NC) con CAE asignado y sin anular -> CAE vivo.
        ctx.Invoices.Add(new Invoice
        {
            ReservaId = reserva.Id,
            TipoComprobante = 6,
            CAE = "75123456789012",
            Resultado = "A",
            AnnulmentStatus = AnnulmentStatus.None,
        });
        await ctx.SaveChangesAsync();

        var job = NewLifecycleJob(ctx);
        var closed = await job.AutoCloseEmptyTravelingAsync(CancellationToken.None);

        Assert.Equal(0, closed);
        var refreshed = await ctx.Reservas.FindAsync(reserva.Id);
        Assert.Equal(EstadoReserva.Traveling, refreshed!.Status); // no se cerro
    }

    [Fact]
    public async Task LifecycleJob_AutoCloseEmptyTraveling_EmptyButBalanceNotZero_DoesNotClose()
    {
        // Guard de plata (3/3): una reserva En viaje SIN servicios pero con Balance != 0 (ej. saldo a FAVOR del
        // cliente por un pago sin venta) NO se cierra. El saldo vivo es plata que hay que resolver antes.
        await using var ctx = NewContext();
        var reserva = new Reserva
        {
            Name = "Vacia con saldo a favor",
            NumeroReserva = "RES-00038",
            Status = EstadoReserva.Traveling,
            Balance = -500m, // saldo a favor (pago sin venta): Balance != 0 con tolerancia de moneda
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var job = NewLifecycleJob(ctx);
        var closed = await job.AutoCloseEmptyTravelingAsync(CancellationToken.None);

        Assert.Equal(0, closed);
        var refreshed = await ctx.Reservas.FindAsync(reserva.Id);
        Assert.Equal(EstadoReserva.Traveling, refreshed!.Status); // no se cerro
    }

    [Fact]
    public async Task LifecycleJob_AutoCloseEmptyTraveling_EmptyAndNoMoney_Closes()
    {
        // Caso feliz (confirmacion explicita del guard de plata): una reserva En viaje SIN servicios y SIN nada
        // de plata (Balance cero, sin cobros, sin facturas) SI se cierra. Es la unica que el saneamiento toca.
        await using var ctx = NewContext();
        var reserva = new Reserva
        {
            Name = "Vacia sin plata",
            NumeroReserva = "RES-00039",
            Status = EstadoReserva.Traveling,
            Balance = 0m,
            ClosedAt = null,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var job = NewLifecycleJob(ctx);
        var closed = await job.AutoCloseEmptyTravelingAsync(CancellationToken.None);

        Assert.Equal(1, closed);
        var refreshed = await ctx.Reservas.FindAsync(reserva.Id);
        Assert.Equal(EstadoReserva.Closed, refreshed!.Status); // cerrada
        Assert.NotNull(refreshed.ClosedAt);
    }

    [Fact]
    public async Task LifecycleJob_AutoCloseEmptyTraveling_MixedBatch_ClosesOnlyTrulyEmptyOnes()
    {
        // Barrido mixto en una sola corrida: de cuatro reservas En viaje vacias, solo se cierran las dos
        // "vacias-de-verdad" (sin plata). Las dos con plata (cobro / saldo a favor) quedan En viaje. El count
        // refleja exactamente las cerradas.
        await using var ctx = NewContext();

        var vacia1 = new Reserva { Name = "Vacia 1", NumeroReserva = "RES-00040", Status = EstadoReserva.Traveling, Balance = 0m };
        var conCobro = new Reserva { Name = "Con cobro", NumeroReserva = "RES-00041", Status = EstadoReserva.Traveling, Balance = 0m };
        var vacia2 = new Reserva { Name = "Vacia 2", NumeroReserva = "RES-00042", Status = EstadoReserva.Traveling, Balance = 0m };
        var conSaldo = new Reserva { Name = "Con saldo a favor", NumeroReserva = "RES-00043", Status = EstadoReserva.Traveling, Balance = -300m };
        ctx.Reservas.AddRange(vacia1, conCobro, vacia2, conSaldo);
        await ctx.SaveChangesAsync();

        ctx.Payments.Add(new Payment { ReservaId = conCobro.Id, Amount = 1000m, IsDeleted = false });
        await ctx.SaveChangesAsync();

        var job = NewLifecycleJob(ctx);
        var closed = await job.AutoCloseEmptyTravelingAsync(CancellationToken.None);

        Assert.Equal(2, closed); // solo las dos vacias-de-verdad

        Assert.Equal(EstadoReserva.Closed, (await ctx.Reservas.FindAsync(vacia1.Id))!.Status);
        Assert.Equal(EstadoReserva.Closed, (await ctx.Reservas.FindAsync(vacia2.Id))!.Status);
        Assert.Equal(EstadoReserva.Traveling, (await ctx.Reservas.FindAsync(conCobro.Id))!.Status);
        Assert.Equal(EstadoReserva.Traveling, (await ctx.Reservas.FindAsync(conSaldo.Id))!.Status);
    }

    // ===================== (2) Revert de una Cancelada a En gestion =====================

    [Fact]
    public async Task RevertCancelledToInManagement_MarksLeadWon()
    {
        // Reabrir una Cancelada (sin huella fiscal/plata) la lleva a En gestion = estado firme. Si nacio de
        // un lead, ese lead debe quedar Ganado.
        await using var ctx = NewContext();
        var lead = new Lead { FullName = "Carla", Phone = "1100440055", Status = LeadStatus.Contacted };
        ctx.Leads.Add(lead);

        var reserva = new Reserva
        {
            Name = "Viaje Carla",
            NumeroReserva = "RES-00040",
            Status = EstadoReserva.Cancelled,
            SourceLeadId = lead.Id,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var service = NewReservaServiceWithEngine(ctx);
        await service.RevertStatusAsync(
            reserva.PublicId.ToString(),
            new RevertStatusRequest(EstadoReserva.InManagement, AuthorizedBySuperiorUserId: null, Reason: "Cliente retoma el viaje"),
            actorUserId: "admin1", actorUserName: "Admin", actorIsAdmin: true, ct: CancellationToken.None);

        var refreshedReserva = await ctx.Reservas.FindAsync(reserva.Id);
        Assert.Equal(EstadoReserva.InManagement, refreshedReserva!.Status);

        var refreshedLead = await ctx.Leads.FindAsync(lead.Id);
        Assert.Equal(LeadStatus.Won, refreshedLead!.Status);
        Assert.NotNull(refreshedLead.ClosedAt);
    }

    // ===================== (3) Idempotencia / seguridad =====================

    [Fact]
    public async Task AutoConfirmation_LeadAlreadyWon_DoesNotMoveClosedAt()
    {
        // Idempotencia: si el lead ya estaba Ganado, la auto-confirmacion NO re-sella ClosedAt (conserva la
        // fecha del primer Ganado). Asi llamar al hook de mas en cada avance no corre la fecha.
        await using var ctx = NewContext();
        var firstWonAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var lead = new Lead { FullName = "Diego", Phone = "1100550066", Status = LeadStatus.Won, ClosedAt = firstWonAt };
        ctx.Leads.Add(lead);

        var reserva = new Reserva
        {
            Name = "Viaje Diego",
            NumeroReserva = "RES-00050",
            Status = EstadoReserva.InManagement,
            SourceLeadId = lead.Id,
        };
        reserva.HotelBookings.Add(new HotelBooking { Status = "Confirmado", Currency = "ARS", SalePrice = 1000m });
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        await NewEngine(ctx).EvaluateAndApplyAsync(reserva.Id);

        var refreshedLead = await ctx.Leads.FindAsync(lead.Id);
        Assert.Equal(LeadStatus.Won, refreshedLead!.Status);
        Assert.Equal(firstWonAt, refreshedLead.ClosedAt); // NO se corrio la fecha
    }

    [Fact]
    public async Task AutoConfirmation_LeadIsLost_DoesNotReopen()
    {
        // Seguridad: un lead Perdido NO se reabre aunque la reserva linkeada se auto-confirme.
        await using var ctx = NewContext();
        var closedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var lead = new Lead { FullName = "Eva", Phone = "1100660077", Status = LeadStatus.Lost, ClosedAt = closedAt };
        ctx.Leads.Add(lead);

        var reserva = new Reserva
        {
            Name = "Viaje Eva",
            NumeroReserva = "RES-00060",
            Status = EstadoReserva.InManagement,
            SourceLeadId = lead.Id,
        };
        reserva.HotelBookings.Add(new HotelBooking { Status = "Confirmado", Currency = "ARS", SalePrice = 1000m });
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        await NewEngine(ctx).EvaluateAndApplyAsync(reserva.Id);

        var refreshedLead = await ctx.Leads.FindAsync(lead.Id);
        Assert.Equal(LeadStatus.Lost, refreshedLead!.Status); // sigue Perdido
        Assert.Equal(closedAt, refreshedLead.ClosedAt);
    }

    // ===================== (4) Reserva sin SourceLeadId =====================

    [Fact]
    public async Task AutoConfirmation_ReservaWithoutSourceLead_DoesNotThrow()
    {
        // Sin lead de origen, el hook es un no-op: la auto-confirmacion procede normal y nada se rompe.
        await using var ctx = NewContext();
        var reserva = new Reserva
        {
            Name = "Reserva suelta",
            NumeroReserva = "RES-00070",
            Status = EstadoReserva.InManagement,
            SourceLeadId = null,
        };
        reserva.HotelBookings.Add(new HotelBooking { Status = "Confirmado", Currency = "ARS", SalePrice = 1000m });
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var changed = await NewEngine(ctx).EvaluateAndApplyAsync(reserva.Id);

        Assert.True(changed);
        var refreshedReserva = await ctx.Reservas.FindAsync(reserva.Id);
        Assert.Equal(EstadoReserva.Confirmed, refreshedReserva!.Status);
    }
}
