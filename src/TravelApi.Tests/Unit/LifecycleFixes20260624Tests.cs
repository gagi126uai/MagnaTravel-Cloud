using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Reservations;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using TravelApi.Infrastructure.Services.Reservations;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Lote de correcciones de CICLO DE VIDA (2026-06-24). Cubre:
///  - B2: al FINALIZAR (Traveling -> Closed) los servicios RESUELTOS pasan a "Finalizado" (prestado), pero NO
///    los cancelados (un cancelado se queda cancelado). "Finalizado" NO saca el servicio de la venta. Cubre los
///    DOS caminos a Closed: el cierre MANUAL (ReservaService) y el cierre por el JOB diario (camino dominante).
///  - G4: el mensaje al marcar "El cliente aceptó" sin servicios es claro y sin jerga (no dice "reserva").
/// </summary>
public class LifecycleFixes20260624Tests
{
    private static AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static ReservaLifecycleAutomationService NewLifecycleJob(AppDbContext context)
    {
        var settings = new Mock<IOperationalFinanceSettingsService>();
        settings.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OperationalFinanceSettings());
        var engine = new ReservaAutoStateService(context, NullLogger<ReservaAutoStateService>.Instance);
        return new ReservaLifecycleAutomationService(
            context, NullLogger<ReservaLifecycleAutomationService>.Instance, settings.Object, engine);
    }

    private static ReservaService NewReservaService(AppDbContext context)
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
            BuildUserManager(), NullLogger<ReservaService>.Instance);
    }

    // UserManager minimo (no se usa en estos tests pero el ctor de ReservaService lo exige).
    private static UserManager<ApplicationUser> BuildUserManager()
    {
        var store = new Mock<IUserStore<ApplicationUser>>();
        return new UserManager<ApplicationUser>(
            store.Object, null!, null!,
            Array.Empty<IUserValidator<ApplicationUser>>(),
            Array.Empty<IPasswordValidator<ApplicationUser>>(),
            null!, null!, null!, null!);
    }

    // ===================== B2: Traveling -> Closed marca servicios "Finalizado" =====================

    [Fact]
    public async Task CloseReserva_MarksLiveServicesFinalized_ButKeepsCancelledCancelled()
    {
        await using var context = NewContext();

        // Reserva En viaje y SALDADA (SalePrice 0 -> ConfirmedSale 0 -> Balance 0, asi el cierre no se traba).
        var reserva = new Reserva
        {
            Id = 1, NumeroReserva = "F-2026-CLOSE", Name = "Reserva a finalizar",
            Status = EstadoReserva.Traveling, AdultCount = 1
        };
        context.Reservas.Add(reserva);

        // Hotel vivo (Confirmado) -> debe quedar Finalizado.
        context.HotelBookings.Add(new HotelBooking
        {
            Id = 10, ReservaId = 1, HotelName = "Hotel A", City = "BRC", RoomType = "Doble", MealPlan = "Desayuno",
            CheckIn = DateTime.UtcNow.Date, CheckOut = DateTime.UtcNow.Date.AddDays(2), Adults = 1, Rooms = 1,
            Status = WorkflowStatuses.Confirmado, SalePrice = 0m
        });
        // Transfer CANCELADO -> debe quedar Cancelado (no resucita como Finalizado).
        context.TransferBookings.Add(new TransferBooking
        {
            Id = 20, ReservaId = 1, PickupLocation = "Aeropuerto", DropoffLocation = "Hotel",
            PickupDateTime = DateTime.UtcNow.Date, Passengers = 1,
            Status = WorkflowStatuses.Cancelado, SalePrice = 0m
        });
        // Generico RESUELTO (Confirmado) -> debe quedar Finalizado.
        context.Servicios.Add(new ServicioReserva
        {
            Id = 30, ReservaId = 1, ServiceType = "Excursion", Description = "City tour",
            DepartureDate = DateTime.UtcNow.Date, Status = WorkflowStatuses.Confirmado, SalePrice = 0m
        });
        // Generico NO resuelto (Solicitado): NO se finaliza (no se infla la venta retroactivamente).
        context.Servicios.Add(new ServicioReserva
        {
            Id = 31, ReservaId = 1, ServiceType = "Excursion", Description = "Tour sin confirmar",
            DepartureDate = DateTime.UtcNow.Date, Status = WorkflowStatuses.Solicitado, SalePrice = 0m
        });
        await context.SaveChangesAsync();

        var service = NewReservaService(context);

        var result = await service.UpdateStatusAsync(1, EstadoReserva.Closed);

        Assert.Equal(EstadoReserva.Closed, result.Status);

        var hotel = await context.HotelBookings.SingleAsync();
        Assert.Equal(WorkflowStatuses.Finalizado, hotel.Status);

        var resolvedGeneric = await context.Servicios.SingleAsync(s => s.Id == 30);
        Assert.Equal(WorkflowStatuses.Finalizado, resolvedGeneric.Status);

        // El generico NO resuelto NO se finaliza (no se infla la venta): se queda Solicitado.
        var unresolvedGeneric = await context.Servicios.SingleAsync(s => s.Id == 31);
        Assert.Equal(WorkflowStatuses.Solicitado, unresolvedGeneric.Status);

        // El cancelado NO se toca: sigue cancelado, no se vuelve Finalizado.
        var transfer = await context.TransferBookings.SingleAsync();
        Assert.Equal(WorkflowStatuses.Cancelado, transfer.Status);

        // Un servicio Finalizado sigue contando como activo (resuelto, no cancelado) para la plata.
        Assert.True(ServiceResolutionRules.IsResolved(hotel));
        Assert.False(ServiceResolutionRules.IsCancelled(hotel));
    }

    // ===================== B2 (BLK-1/BLK-2): cierre por el JOB tambien finaliza los servicios =====================

    [Fact]
    public async Task JobClose_TravelingToClosed_MarksResolvedServicesFinalized()
    {
        // El cierre DOMINANTE en produccion lo hace el job diario (Traveling -> Closed por fin de viaje), NO el
        // cierre manual. Este test garantiza que ese camino tambien finalice los servicios (el bug que se colo
        // verde porque el test previo solo cubria el cierre manual).
        await using var context = NewContext();

        var reserva = new Reserva
        {
            Id = 1, NumeroReserva = "F-2026-JOBCLOSE", Name = "Cierre por job",
            Status = EstadoReserva.Traveling,
            // EndDate en el pasado + Balance 0 -> el job la cierra (AutoTransitionTravelingToClosedAsync).
            EndDate = DateTime.UtcNow.Date.AddDays(-1),
            Balance = 0m
        };
        context.Reservas.Add(reserva);

        // Hotel resuelto -> debe quedar Finalizado tras el cierre por job.
        context.HotelBookings.Add(new HotelBooking
        {
            Id = 10, ReservaId = 1, HotelName = "Hotel B", City = "MDZ", RoomType = "Doble", MealPlan = "Desayuno",
            CheckIn = DateTime.UtcNow.Date.AddDays(-3), CheckOut = DateTime.UtcNow.Date.AddDays(-1), Adults = 1, Rooms = 1,
            Status = WorkflowStatuses.Confirmado, SalePrice = 0m
        });
        // Cancelado -> se queda cancelado.
        context.TransferBookings.Add(new TransferBooking
        {
            Id = 20, ReservaId = 1, PickupLocation = "Hotel", DropoffLocation = "Aeropuerto",
            PickupDateTime = DateTime.UtcNow.Date.AddDays(-1), Passengers = 1,
            Status = WorkflowStatuses.Cancelado, SalePrice = 0m
        });
        await context.SaveChangesAsync();

        var job = NewLifecycleJob(context);
        var closed = await job.AutoTransitionTravelingToClosedAsync(CancellationToken.None);

        Assert.Equal(1, closed);
        var refreshed = await context.Reservas.FindAsync(1);
        Assert.Equal(EstadoReserva.Closed, refreshed!.Status);

        // El servicio resuelto quedo Finalizado por el camino del JOB (no solo el manual).
        var hotel = await context.HotelBookings.SingleAsync();
        Assert.Equal(WorkflowStatuses.Finalizado, hotel.Status);

        // El cancelado sigue cancelado.
        var transfer = await context.TransferBookings.SingleAsync();
        Assert.Equal(WorkflowStatuses.Cancelado, transfer.Status);
    }

    // ===================== G4: mensaje claro al aceptar un presupuesto SIN servicios =====================

    [Fact]
    public async Task AcceptBudget_WithoutServices_GivesClearMessage_NoJargon()
    {
        await using var context = NewContext();
        // Presupuesto con pasajero declarado pero SIN ningun servicio cargado.
        context.Reservas.Add(new Reserva
        {
            Id = 1, NumeroReserva = "F-2026-G4", Name = "Presupuesto vacio",
            Status = EstadoReserva.Budget, AdultCount = 1
        });
        await context.SaveChangesAsync();

        var service = NewReservaService(context);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.UpdateStatusAsync(1, EstadoReserva.InManagement));

        // Accionable y sin jerga interna: habla de "servicio" y de "el cliente aceptó", NO de "reserva"/"reservar".
        Assert.Contains("servicio", ex.Message);
        Assert.Contains("aceptó", ex.Message);
        Assert.DoesNotContain("reservar", ex.Message);

        // No transiciono: sigue en Presupuesto.
        Assert.Equal(EstadoReserva.Budget, (await context.Reservas.FindAsync(1))!.Status);
    }

    // ===================== El proceso nocturno YA NO regresa Confirmed -> En gestion =====================

    [Fact]
    public async Task NightlyReconciliation_ConfirmedWithUnresolvedService_DoesNotRegress_OnlyMarksChanges()
    {
        // 2026-06-24 (alineado a Odoo/SAP): la reconciliacion nocturna corre el mismo motor sobre todas las
        // Confirmed/En gestion. Antes regresaba a En gestion las Confirmed con servicios sin resolver; ahora NO
        // mueve el estado: las deja Confirmed pero marcadas "confirmada con cambios". Y como es cura en lote,
        // no notifica (suppressNotifications interno).
        await using var context = NewContext();
        context.Reservas.Add(new Reserva
        {
            Id = 1, NumeroReserva = "F-2026-RECON", Name = "Confirmada con servicio sin resolver",
            Status = EstadoReserva.Confirmed, ResponsibleUserId = "vendedor-1"
        });
        // Un servicio resuelto + uno sin resolver: rompe "todo resuelto".
        context.HotelBookings.Add(new HotelBooking
        {
            Id = 10, ReservaId = 1, HotelName = "Resuelto", Status = WorkflowStatuses.Confirmado, ConfirmedAt = DateTime.UtcNow
        });
        context.HotelBookings.Add(new HotelBooking
        {
            Id = 11, ReservaId = 1, HotelName = "Sin resolver", Status = WorkflowStatuses.Solicitado
        });
        await context.SaveChangesAsync();

        var job = NewLifecycleJob(context);
        var reconciled = await job.ReconcileAutoStatesAsync(CancellationToken.None);

        // No curo ningun ESTADO (no regreso nada): el contador queda en 0.
        Assert.Equal(0, reconciled);

        var stored = await context.Reservas.FindAsync(1);
        Assert.Equal(EstadoReserva.Confirmed, stored!.Status);     // NO regreso a En gestion
        Assert.True(stored.HasUnacknowledgedChanges);              // pero quedo marcada para revisar

        // Cura en lote: no notifica.
        Assert.Empty(context.Notifications.Where(n => n.RelatedEntityId == 1));
    }
}
