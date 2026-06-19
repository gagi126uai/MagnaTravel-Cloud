using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.Contracts.Files;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Application.Mappings;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Reservations;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Repositories;
using TravelApi.Infrastructure.Services;
using TravelApi.Infrastructure.Services.Reservations;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-035 (2026-06-19): candado de servicios por ESTADO de la reserva como PRIMERA COMPUERTA de toda
/// mutacion de servicios. Cierra la incoherencia de fondo: una reserva
/// Perdida/Cancelada/Esperando-reembolso o Finalizada dejaba agregar/editar/borrar/cancelar servicios y
/// marcar "Solicitado". Tres grupos del dueño:
/// <list type="bullet">
///   <item><b>EN ARMADO</b> (Quotation/Budget/InManagement): editar servicios libre, sin autorizacion.</item>
///   <item><b>EN FIRME</b> (Confirmed/Traveling/ToSettle): editar servicios SOLO con autorizacion viva
///     (candado <see cref="ReservaLockGuard"/>, que corre DESPUES de la compuerta de estado).</item>
///   <item><b>CERRADOS</b> (Closed/Lost/Cancelled/PendingOperatorRefund): SOLO LECTURA, hard block —
///     ninguna autorizacion lo desbloquea.</item>
/// </list>
/// </summary>
public class Adr035ServiceStateGateTests
{
    private static AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static IMapper NewMapper()
        => new MapperConfiguration(c => c.AddProfile<MappingProfile>()).CreateMapper();

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

    private static BookingService NewBookingService(AppDbContext context)
    {
        var mapper = NewMapper();
        var settings = new Mock<IOperationalFinanceSettingsService>();
        settings.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OperationalFinanceSettings());

        var engine = new ReservaAutoStateService(context, NullLogger<ReservaAutoStateService>.Instance);
        var reservaService = new ReservaService(
            context, mapper, settings.Object,
            BuildUserManager(), NullLogger<ReservaService>.Instance,
            permissionResolver: null, httpContextAccessor: null, autoStateService: engine);

        var supplierService = new Mock<ISupplierService>();
        supplierService
            .Setup(s => s.UpdateBalanceAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        return new BookingService(
            new Repository<FlightSegment>(context),
            new Repository<HotelBooking>(context),
            new Repository<PackageBooking>(context),
            new Repository<TransferBooking>(context),
            new Repository<AssistanceBooking>(context),
            new Repository<Reserva>(context),
            new Repository<Supplier>(context),
            reservaService,
            supplierService.Object,
            context,
            mapper,
            NullLogger<BookingService>.Instance);
    }

    private static ReservaService NewReservaService(AppDbContext context)
    {
        var mapper = NewMapper();
        var settings = new Mock<IOperationalFinanceSettingsService>();
        settings.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OperationalFinanceSettings());
        var engine = new ReservaAutoStateService(context, NullLogger<ReservaAutoStateService>.Instance);
        return new ReservaService(
            context, mapper, settings.Object,
            BuildUserManager(), NullLogger<ReservaService>.Instance,
            permissionResolver: null, httpContextAccessor: null, autoStateService: engine);
    }

    /// <summary>
    /// Reserva con un hotel cargado, en el estado pedido. id fijo para resolver por legacy id.
    /// El hotel arranca "Confirmado" (lo normal en una venta firme); para los tests de borrado se usa
    /// <see cref="SeedReservaWithRequestedHotelAsync"/> (el borrado de un servicio confirmado tiene su
    /// propio guard, independiente del gate de estado de ADR-035).
    /// </summary>
    private static async Task<Reserva> SeedReservaWithHotelAsync(AppDbContext ctx, string status)
    {
        var reserva = new Reserva
        {
            Id = 1, NumeroReserva = "F-1", Name = "Reserva ADR-035 servicios", Status = status,
            ResponsibleUserId = "vendedor-1"
        };
        ctx.Reservas.Add(reserva);
        // Supplier id=1 para que el SupplierId:"1" de los requests de edicion resuelva (el gate de estado
        // corre ANTES, pero cuando el gate y el candado dejan pasar, la edicion real necesita el proveedor).
        ctx.Suppliers.Add(new Supplier { Id = 1, Name = "Operador Test" });
        ctx.HotelBookings.Add(new HotelBooking
        {
            Id = 10, ReservaId = 1, SupplierId = 1, HotelName = "Hotel Test", Status = "Confirmado", Adults = 2,
            CheckIn = DateTime.UtcNow.AddDays(10), CheckOut = DateTime.UtcNow.AddDays(13)
        });
        ctx.Passengers.Add(new Passenger { Id = 1, ReservaId = 1, FullName = "Titular Hotel" });
        await ctx.SaveChangesAsync();
        return reserva;
    }

    /// <summary>Igual que <see cref="SeedReservaWithHotelAsync"/> pero con el hotel en "Solicitado"
    /// (sin confirmar con el operador), para poder borrarlo sin chocar con el guard de borrado de servicios
    /// confirmados.</summary>
    private static async Task<Reserva> SeedReservaWithRequestedHotelAsync(AppDbContext ctx, string status)
    {
        var reserva = new Reserva
        {
            Id = 1, NumeroReserva = "F-1", Name = "Reserva ADR-035 servicios", Status = status,
            ResponsibleUserId = "vendedor-1"
        };
        ctx.Reservas.Add(reserva);
        ctx.Suppliers.Add(new Supplier { Id = 1, Name = "Operador Test" });
        ctx.HotelBookings.Add(new HotelBooking
        {
            Id = 10, ReservaId = 1, SupplierId = 1, HotelName = "Hotel Test", Status = "Solicitado", Adults = 2,
            CheckIn = DateTime.UtcNow.AddDays(10), CheckOut = DateTime.UtcNow.AddDays(13)
        });
        ctx.Passengers.Add(new Passenger { Id = 1, ReservaId = 1, FullName = "Titular Hotel" });
        await ctx.SaveChangesAsync();
        return reserva;
    }

    private static void AddLiveAuthorization(AppDbContext ctx)
    {
        ctx.ReservaEditAuthorizations.Add(new ReservaEditAuthorization
        {
            Id = 99, ReservaId = 1, Reason = "autorizacion viva para editar servicios",
            ExpiresAt = DateTime.UtcNow.AddMinutes(30)
        });
        ctx.SaveChanges();
    }

    private static UpdateHotelRequest SampleHotelEdit() => new(
        SupplierId: "1", HotelName: "Hotel Renombrado", StarRating: 4, City: "BA", Country: "AR",
        CheckIn: DateTime.UtcNow.AddDays(10), CheckOut: DateTime.UtcNow.AddDays(13),
        RoomType: "DBL", MealPlan: "BB", Adults: 2, Children: 0, Rooms: 1, ConfirmationNumber: null,
        NetCost: 100m, SalePrice: 120m, Commission: 0m, Status: "Confirmado", Notes: null);

    private static CreateHotelRequest SampleHotelCreate() => new(
        SupplierId: "1", HotelName: "Hotel Nuevo", StarRating: 3, City: "BA", Country: "AR",
        CheckIn: DateTime.UtcNow.AddDays(10), CheckOut: DateTime.UtcNow.AddDays(13),
        RoomType: "DBL", MealPlan: "BB", Adults: 2, Children: 0, Rooms: 1, ConfirmationNumber: null,
        NetCost: 100m, SalePrice: 120m, Commission: 0m, Notes: null, WorkflowStatus: "Solicitado");

    // =====================================================================================================
    // GRUPO CERRADOS: hard block. NINGUNA autorizacion viva lo desbloquea.
    // =====================================================================================================

    public static readonly object[][] ReadOnlyStates =
    {
        new object[] { EstadoReserva.Lost },
        new object[] { EstadoReserva.Cancelled },
        new object[] { EstadoReserva.PendingOperatorRefund },
        new object[] { EstadoReserva.Closed },
    };

    [Theory]
    [MemberData(nameof(ReadOnlyStates))]
    public async Task AddService_OnReadOnlyState_Rejected_EvenWithLiveAuthorization(string status)
    {
        await using var ctx = NewContext();
        await SeedReservaWithHotelAsync(ctx, status);
        AddLiveAuthorization(ctx); // ni siquiera con autorizacion viva: hard block

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewBookingService(ctx).CreateHotelAsync("1", SampleHotelCreate(), CancellationToken.None));

        Assert.Equal(1, await ctx.HotelBookings.CountAsync()); // no se agrego el nuevo
    }

    [Theory]
    [MemberData(nameof(ReadOnlyStates))]
    public async Task EditService_OnReadOnlyState_Rejected_EvenWithLiveAuthorization(string status)
    {
        await using var ctx = NewContext();
        await SeedReservaWithHotelAsync(ctx, status);
        AddLiveAuthorization(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewBookingService(ctx).UpdateHotelAsync("1", "10", SampleHotelEdit(), CancellationToken.None));

        Assert.Equal("Hotel Test", (await ctx.HotelBookings.FindAsync(10))!.HotelName); // sin cambios
    }

    [Theory]
    [MemberData(nameof(ReadOnlyStates))]
    public async Task DeleteService_OnReadOnlyState_Rejected_EvenWithLiveAuthorization(string status)
    {
        await using var ctx = NewContext();
        await SeedReservaWithHotelAsync(ctx, status);
        AddLiveAuthorization(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewBookingService(ctx).DeleteHotelAsync("1", "10", CancellationToken.None));

        Assert.Equal(1, await ctx.HotelBookings.CountAsync()); // no se borro
    }

    [Theory]
    [MemberData(nameof(ReadOnlyStates))]
    public async Task ChangeServiceStatus_OnReadOnlyState_Rejected_EvenWithLiveAuthorization(string status)
    {
        await using var ctx = NewContext();
        await SeedReservaWithHotelAsync(ctx, status);
        AddLiveAuthorization(ctx);

        // Cambiar el status del servicio (cancelarlo via status) en una reserva de solo lectura: rechazado.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewBookingService(ctx).UpdateHotelStatusAsync("10", "Cancelado", null, CancellationToken.None));

        Assert.Equal("Confirmado", (await ctx.HotelBookings.FindAsync(10))!.Status); // sin cambios
    }

    [Theory]
    [MemberData(nameof(ReadOnlyStates))]
    public async Task MarkSolicitado_OnReadOnlyState_Rejected(string status)
    {
        await using var ctx = NewContext();
        await SeedReservaWithHotelAsync(ctx, status);

        // Marcar "Solicitado" tambien es una mutacion de status: bloqueada por el gate de estado.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewBookingService(ctx).UpdateHotelStatusAsync("10", "Solicitado", null, CancellationToken.None));

        Assert.Equal("Confirmado", (await ctx.HotelBookings.FindAsync(10))!.Status);
    }

    [Fact]
    public async Task ReadOnlyMessage_ForClosed_PointsToReopenAsToSettle()
    {
        // Verifica que el motivo de Finalizada guie a reabrir a "A liquidar" (sin montos/costos).
        await using var ctx = NewContext();
        await SeedReservaWithHotelAsync(ctx, EstadoReserva.Closed);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewBookingService(ctx).DeleteHotelAsync("1", "10", CancellationToken.None));

        Assert.Contains("finalizada", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("A liquidar", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // =====================================================================================================
    // GRUPO EN ARMADO: edicion libre (sin autorizacion). El gate de estado deja pasar.
    // =====================================================================================================

    [Theory]
    [InlineData(EstadoReserva.Quotation)]
    [InlineData(EstadoReserva.Budget)]
    [InlineData(EstadoReserva.InManagement)]
    public async Task EditService_OnEarlyStages_Allowed_NoAuthorizationNeeded(string status)
    {
        await using var ctx = NewContext();
        await SeedReservaWithHotelAsync(ctx, status);

        // Editar datos en etapa de armado no requiere autorizacion: ni el gate de estado ni el candado deben
        // bloquear. (La edicion real puede chocar mas adelante con la resolucion del proveedor en este
        // entorno de test; eso es ortogonal al gate.)
        var ex = await Record.ExceptionAsync(
            () => NewBookingService(ctx).UpdateHotelAsync("1", "10", SampleHotelEdit(), CancellationToken.None));

        if (ex is InvalidOperationException ioe)
        {
            Assert.DoesNotContain("candado", ioe.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("solo lectura", ioe.Message, StringComparison.OrdinalIgnoreCase);
        }

        // En etapa de armado el candado NUNCA interviene (no se registra ningun cambio bajo autorizacion).
        Assert.False(ctx.ChangeTracker.Entries<ReservaEditAuthorizationChange>().Any());
    }

    [Fact]
    public async Task DeleteService_OnBudget_Allowed_NoAuthorizationNeeded()
    {
        await using var ctx = NewContext();
        // Hotel en "Solicitado": el gate de estado deja pasar (Budget editable) y el borrado procede
        // (el guard de "no borrar servicio confirmado" no aplica porque no esta confirmado).
        await SeedReservaWithRequestedHotelAsync(ctx, EstadoReserva.Budget);

        await NewBookingService(ctx).DeleteHotelAsync("1", "10", CancellationToken.None);

        Assert.Equal(0, await ctx.HotelBookings.CountAsync());
    }

    // =====================================================================================================
    // GRUPO EN FIRME: el candado de autorizacion sigue mandando (no se rompe lo de ADR-020).
    // El gate de estado deja pasar (Confirmed/Traveling/ToSettle son editables conceptualmente).
    // =====================================================================================================

    [Theory]
    [InlineData(EstadoReserva.Confirmed)]
    [InlineData(EstadoReserva.Traveling)]
    [InlineData(EstadoReserva.ToSettle)]
    public async Task EditService_OnFirmState_WithoutAuthorization_RejectedByLock(string status)
    {
        await using var ctx = NewContext();
        await SeedReservaWithHotelAsync(ctx, status);

        // El gate de estado PASA (estado firme editable); el que rechaza es el candado de autorizacion.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewBookingService(ctx).UpdateHotelAsync("1", "10", SampleHotelEdit(), CancellationToken.None));
        Assert.Contains("candado", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Hotel Test", (await ctx.HotelBookings.FindAsync(10))!.HotelName);
    }

    [Theory]
    [InlineData(EstadoReserva.Confirmed)]
    [InlineData(EstadoReserva.Traveling)]
    [InlineData(EstadoReserva.ToSettle)]
    public async Task EditService_OnFirmState_WithLiveAuthorization_PassesStateGateAndLock(string status)
    {
        await using var ctx = NewContext();
        await SeedReservaWithHotelAsync(ctx, status);
        AddLiveAuthorization(ctx);

        // Con autorizacion viva, ni el gate de estado ni el candado deben bloquear. (La edicion real puede
        // chocar mas adelante con la resolucion del proveedor en este entorno de test; eso es ortogonal.)
        var ex = await Record.ExceptionAsync(
            () => NewBookingService(ctx).UpdateHotelAsync("1", "10", SampleHotelEdit(), CancellationToken.None));

        if (ex is InvalidOperationException ioe)
        {
            Assert.DoesNotContain("candado", ioe.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("solo lectura", ioe.Message, StringComparison.OrdinalIgnoreCase);
        }

        // Prueba positiva de que el candado SE EJECUTO y dejo pasar (registro el cambio bajo la
        // autorizacion). Como la edicion puede no llegar al SaveChanges en test, miramos el ChangeTracker.
        var trackedChange = ctx.ChangeTracker.Entries<ReservaEditAuthorizationChange>().Any();
        Assert.True(trackedChange, "el candado de autorizacion deberia haber registrado el cambio");
    }

    // =====================================================================================================
    // Servicio GENERICO (ReservaService): mismo gate de estado en add/update/remove.
    // =====================================================================================================

    private static async Task<Reserva> SeedReservaWithGenericServiceAsync(AppDbContext ctx, string status)
    {
        var reserva = new Reserva
        {
            Id = 1, NumeroReserva = "F-1", Name = "Reserva generica", Status = status,
            ResponsibleUserId = "vendedor-1"
        };
        ctx.Reservas.Add(reserva);
        var servicio = new ServicioReserva
        {
            Id = 50, ReservaId = 1, ServiceType = "Otro", ProductType = "Otro",
            Description = "Servicio generico", Status = "Confirmado",
            DepartureDate = DateTime.UtcNow.AddDays(10), SalePrice = 100m, CreatedAt = DateTime.UtcNow
        };
        ctx.Servicios.Add(servicio);
        await ctx.SaveChangesAsync();
        return reserva;
    }

    [Theory]
    [MemberData(nameof(ReadOnlyStates))]
    public async Task GenericAddService_OnReadOnlyState_Rejected(string status)
    {
        await using var ctx = NewContext();
        await SeedReservaWithGenericServiceAsync(ctx, status);

        var request = new AddServiceRequest(
            ServiceType: "Otro", SupplierId: null, Description: "Nuevo", ConfirmationNumber: null,
            DepartureDate: DateTime.UtcNow.AddDays(10), ReturnDate: null, SalePrice: 50m, NetCost: 0m);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewReservaService(ctx).AddServiceAsync("1", request, CancellationToken.None));

        Assert.Equal(1, await ctx.Servicios.CountAsync());
    }

    [Theory]
    [MemberData(nameof(ReadOnlyStates))]
    public async Task GenericRemoveService_OnReadOnlyState_Rejected(string status)
    {
        await using var ctx = NewContext();
        var reserva = await SeedReservaWithGenericServiceAsync(ctx, status);
        var servicePublicId = (await ctx.Servicios.FirstAsync(s => s.Id == 50)).PublicId.ToString();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewReservaService(ctx).RemoveServiceAsync(servicePublicId, CancellationToken.None));

        Assert.Equal(1, await ctx.Servicios.CountAsync()); // no se borro
    }

    // =====================================================================================================
    // TASK 2: matriz de transicion y capacidad de cancelacion (En viaje ya no se cancela).
    // =====================================================================================================

    [Fact]
    public void Forward_FromTraveling_NoLongerAllowsCancelled()
    {
        Assert.True(ReservaStatusTransitions.Forward.TryGetValue(EstadoReserva.Traveling, out var targets));
        Assert.DoesNotContain(EstadoReserva.Cancelled, targets!, StringComparer.OrdinalIgnoreCase);
        // El cierre y el desvio a liquidar siguen disponibles.
        Assert.Contains(EstadoReserva.Closed, targets!, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(EstadoReserva.ToSettle, targets!, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Forward_FromConfirmedAndInManagement_StillAllowCancelled()
    {
        Assert.Contains(EstadoReserva.Cancelled,
            ReservaStatusTransitions.Forward[EstadoReserva.Confirmed], StringComparer.OrdinalIgnoreCase);
        Assert.Contains(EstadoReserva.Cancelled,
            ReservaStatusTransitions.Forward[EstadoReserva.InManagement], StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void CanCancel_Traveling_ReturnsNo_WithReason()
    {
        var caps = ReservaCapabilityPolicy.For(new ReservaCapabilityContext(
            EstadoReserva.Traveling, Balance: 0m, false, false, false, false));
        Assert.False(caps.CanCancel.Allowed);
        Assert.Equal(ReservaCapabilityPolicy.TravelingNotCancellableReason, caps.CanCancel.Reason);
    }

    [Theory]
    [InlineData(EstadoReserva.Confirmed)]
    [InlineData(EstadoReserva.InManagement)]
    public void CanCancel_ConfirmedAndInManagement_ReturnsYes(string status)
    {
        var caps = ReservaCapabilityPolicy.For(new ReservaCapabilityContext(
            status, Balance: 0m, false, false, false, false));
        Assert.True(caps.CanCancel.Allowed);
    }

    [Fact]
    public async Task UpdateStatus_TravelingToCancelled_Rejected()
    {
        await using var ctx = NewContext();
        await SeedReservaWithHotelAsync(ctx, EstadoReserva.Traveling);

        // El cambio manual de estado a Cancelled desde Traveling ya no es una transicion legal.
        // actorUserId null = camino legacy: se saltean los chequeos de PERMISO (que dependen del actor)
        // y corre el gate de MATRIZ (ApplyTransitionAsync), que es justo lo que queremos verificar.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewReservaService(ctx).UpdateStatusAsync(
                "1", EstadoReserva.Cancelled, actorUserId: null, CancellationToken.None));

        Assert.Equal(EstadoReserva.Traveling, (await ctx.Reservas.FindAsync(1))!.Status);
    }

    [Fact]
    public void CanEditServices_MatchesThreeGroups()
    {
        // Fija la matriz pura de la capacidad CanEditServices por estado (fuente del gate).
        foreach (var editable in new[]
                 {
                     EstadoReserva.Quotation, EstadoReserva.Budget, EstadoReserva.InManagement,
                     EstadoReserva.Confirmed, EstadoReserva.Traveling, EstadoReserva.ToSettle
                 })
        {
            var caps = ReservaCapabilityPolicy.For(new ReservaCapabilityContext(
                editable, 0m, false, false, false, false));
            Assert.True(caps.CanEditServices.Allowed, $"{editable} deberia permitir editar servicios");
        }

        foreach (var readOnly in new[]
                 {
                     EstadoReserva.Closed, EstadoReserva.Lost,
                     EstadoReserva.Cancelled, EstadoReserva.PendingOperatorRefund
                 })
        {
            var caps = ReservaCapabilityPolicy.For(new ReservaCapabilityContext(
                readOnly, 0m, false, false, false, false));
            Assert.False(caps.CanEditServices.Allowed, $"{readOnly} deberia ser solo lectura");
        }
    }
}
