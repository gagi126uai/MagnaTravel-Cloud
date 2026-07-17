using System;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Application.Mappings;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Repositories;
using TravelApi.Infrastructure.Services;
using TravelApi.Infrastructure.Services.Reservations;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-020 decision #8 (2026-06-08): registrar que el OPERADOR confirmo/cambio/cancelo un servicio
/// (cambio de ESTADO/RESOLUCION) NO pide candado, aunque la reserva este confirmada — es informar
/// algo que paso afuera y es justo lo que dispara la regresion automatica Confirmed->InManagement.
///
/// <para>En cambio, lo que inicia la AGENCIA sobre una reserva confirmada SI sigue bajo candado:
/// borrar/cancelar un servicio por la papelera (Delete*Async) y editar los datos de un servicio
/// (Update*Async). Estos tests fijan ambos lados de la linea para que no se desproteja de mas.</para>
/// </summary>
public class Adr020StatusUnlockTests
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

    /// <summary>
    /// BookingService con un ReservaService REAL al que se le enchufa el motor de estados
    /// (ReservaAutoStateService). Asi, cuando un cambio de estado llama a UpdateBalanceAsync, el
    /// motor corre de verdad y podemos asertar la auto-confirmacion / regresion.
    /// permissionResolver y httpContextAccessor van en null (sin request real): el masking de costo
    /// queda fail-closed, que es lo correcto y no afecta lo que medimos aca.
    /// </summary>
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

    private static Reserva Reserva(int id, string status, string? responsibleUserId = null) => new()
    {
        Id = id, NumeroReserva = $"F-{id}", Name = $"Reserva {id}", Status = status,
        ResponsibleUserId = responsibleUserId
    };

    // ===================== (a) Cambio de ESTADO: exento del candado + marca "con cambios" =====================
    // 2026-06-24: ya NO regresa de estado (la regresion automatica se elimino). Queda Confirmed + marcada.

    [Fact]
    public async Task UpdateHotelStatus_OnConfirmedReserva_WithoutAuthorization_Succeeds_AndMarksChanges()
    {
        await using var ctx = NewContext();
        // Reserva confirmada cuyo unico servicio estaba resuelto (Confirmado por el operador).
        ctx.Reservas.Add(Reserva(1, EstadoReserva.Confirmed, responsibleUserId: "vendedor-1"));
        ctx.HotelBookings.Add(new HotelBooking
        {
            Id = 10, ReservaId = 1, HotelName = "Hotel Test",
            Status = "Confirmado", ConfirmedAt = DateTime.UtcNow, Adults = 2,
            CheckIn = DateTime.UtcNow.AddDays(10), CheckOut = DateTime.UtcNow.AddDays(13)
        });
        await ctx.SaveChangesAsync();

        // El operador des-confirmo el hotel (lo paso a Solicitado). NO hay autorizacion viva:
        // debe pasar igual (no es decision de la agencia) y romper "todo resuelto". La reserva NO regresa de
        // estado: queda Confirmed pero MARCADA "confirmada con cambios / revisar".
        var dto = await NewBookingService(ctx).UpdateHotelStatusAsync("10", "Solicitado", null, CancellationToken.None);

        Assert.Equal("Solicitado", dto.Status);
        Assert.Equal("Solicitado", (await ctx.HotelBookings.FindAsync(10))!.Status);
        var reserva = await ctx.Reservas.FindAsync(1);
        Assert.Equal(EstadoReserva.Confirmed, reserva!.Status);
        Assert.True(reserva.HasUnacknowledgedChanges);
        // No se creo ningun registro de "cambio bajo candado": el candado nunca intervino.
        Assert.Empty(ctx.ReservaEditAuthorizationChanges);
    }

    [Fact]
    public async Task MarkTransferNoConfirmationRequired_OnConfirmedReserva_WithoutAuthorization_Succeeds_AndResolvesService()
    {
        await using var ctx = NewContext();
        // Reserva confirmada con un traslado todavia "Solicitado" (aun sin resolver).
        ctx.Reservas.Add(Reserva(1, EstadoReserva.Confirmed, responsibleUserId: "vendedor-1"));
        ctx.TransferBookings.Add(new TransferBooking
        {
            Id = 30, ReservaId = 1, Status = "Solicitado", NoConfirmationRequired = false, Passengers = 2
        });
        // ADR-031: resolver el traslado exige el TITULAR con nombre (regla Hotel/Traslado).
        ctx.Passengers.Add(new Passenger { Id = 1, ReservaId = 1, FullName = "Titular Traslado" });
        await ctx.SaveChangesAsync();

        // Marcar "no requiere confirmacion" RESUELVE el traslado. Es accion del operador -> sin candado.
        // (El DTO no expone NoConfirmationRequired; verificamos la marca sobre la entidad persistida.)
        var dto = await NewBookingService(ctx).MarkTransferNoConfirmationRequiredAsync("1", "30", CancellationToken.None);

        Assert.NotNull(dto);
        Assert.True((await ctx.TransferBookings.FindAsync(30))!.NoConfirmationRequired);
        // Con el traslado resuelto la reserva queda confirmada (el motor no la regresa).
        Assert.Equal(EstadoReserva.Confirmed, (await ctx.Reservas.FindAsync(1))!.Status);
        Assert.Empty(ctx.ReservaEditAuthorizationChanges);
    }

    [Fact]
    public async Task UpdateHotelStatus_ToCancelled_OnConfirmedReserva_StampsCancelledAt_AndNeedsNoLock()
    {
        // BLOQUEANTE 6: cancelar un servicio via el cambio de estado (realidad del operador) NO pide
        // candado, pero debe dejar rastro auditado (CancelledAt) porque mueve plata (sale de ConfirmedSale).
        await using var ctx = NewContext();
        ctx.Reservas.Add(Reserva(1, EstadoReserva.Confirmed, responsibleUserId: "vendedor-1"));
        ctx.HotelBookings.Add(new HotelBooking
        {
            Id = 10, ReservaId = 1, HotelName = "Hotel Test",
            Status = "Confirmado", ConfirmedAt = DateTime.UtcNow, Adults = 2, SalePrice = 800m,
            CheckIn = DateTime.UtcNow.AddDays(10), CheckOut = DateTime.UtcNow.AddDays(13)
        });
        await ctx.SaveChangesAsync();

        // Sin autorizacion viva: el operador cancelo el hotel -> pasa igual (no es candado) y se estampa.
        await NewBookingService(ctx).UpdateHotelStatusAsync("10", "Cancelado", null, CancellationToken.None);

        var hotel = await ctx.HotelBookings.FindAsync(10);
        Assert.Equal("Cancelado", hotel!.Status);
        Assert.NotNull(hotel.CancelledAt); // rastro de cancelacion estampado
        // El candado nunca intervino (es realidad del operador, no edicion de la agencia).
        Assert.Empty(ctx.ReservaEditAuthorizationChanges);
        // ADR-048 (2026-07-17): el UNICO servicio de la reserva quedo cancelado -> "tuvo servicios y todos
        // anulados" (INV-048-01). Antes (2026-06-24..2026-07-16) esto solo dejaba la reserva "Confirmed"
        // marcada "confirmada con cambios" para siempre (la mentira de F-2026-1046). Ahora el motor la lleva
        // sola al terminal del par: sin ninguna BookingCancellation con reembolso de operador pendiente
        // (no se sembro ninguna en este test), el terminal es "Anulada" (Cancelled), y entrar a un terminal
        // apaga la marca de revision (ReservaStateCleanupRules).
        var reserva = await ctx.Reservas.FindAsync(1);
        Assert.Equal(EstadoReserva.Cancelled, reserva!.Status);
        Assert.False(reserva.HasUnacknowledgedChanges);
    }

    [Fact]
    public async Task UpdateHotelStatus_FromCancelledBackToRequested_ClearsCancelledStamp()
    {
        await using var ctx = NewContext();
        ctx.Reservas.Add(Reserva(1, EstadoReserva.InManagement, responsibleUserId: "vendedor-1"));
        ctx.HotelBookings.Add(new HotelBooking
        {
            Id = 10, ReservaId = 1, HotelName = "Hotel Test", Status = "Cancelado",
            CancelledAt = DateTime.UtcNow, CancelledByUserId = "alguien", Adults = 2,
            CheckIn = DateTime.UtcNow.AddDays(10), CheckOut = DateTime.UtcNow.AddDays(13)
        });
        await ctx.SaveChangesAsync();

        await NewBookingService(ctx).UpdateHotelStatusAsync("10", "Solicitado", null, CancellationToken.None);

        var hotel = await ctx.HotelBookings.FindAsync(10);
        Assert.Equal("Solicitado", hotel!.Status);
        // El servicio dejo de estar cancelado -> la marca se limpia.
        Assert.Null(hotel.CancelledAt);
        Assert.Null(hotel.CancelledByUserId);
    }

    // ===================== (b) Borrar/cancelar por papelera: SIGUE bajo candado =====================

    [Fact]
    public async Task DeleteHotel_ViaTrashRoute_OnConfirmedReserva_WithoutAuthorization_Throws()
    {
        await using var ctx = NewContext();
        ctx.Reservas.Add(Reserva(1, EstadoReserva.Confirmed));
        ctx.HotelBookings.Add(new HotelBooking
        {
            Id = 10, ReservaId = 1, HotelName = "Hotel Test", Status = "Solicitado", Adults = 2,
            CheckIn = DateTime.UtcNow.AddDays(10), CheckOut = DateTime.UtcNow.AddDays(13)
        });
        await ctx.SaveChangesAsync();

        // La ruta de papelera (overload por public/legacy id) SI aplica candado: borrar/cancelar un
        // servicio en una reserva confirmada es decision de la agencia y exige autorizacion.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewBookingService(ctx).DeleteHotelAsync("1", "10", CancellationToken.None));
        Assert.Contains("candado", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, await ctx.HotelBookings.CountAsync());
    }

    [Fact]
    public async Task DeleteHotel_ViaTrashRoute_OnConfirmedReserva_WithLiveAuthorization_Succeeds()
    {
        await using var ctx = NewContext();
        ctx.Reservas.Add(Reserva(1, EstadoReserva.Confirmed));
        ctx.HotelBookings.Add(new HotelBooking
        {
            Id = 10, ReservaId = 1, HotelName = "Hotel Test", Status = "Solicitado", Adults = 2,
            CheckIn = DateTime.UtcNow.AddDays(10), CheckOut = DateTime.UtcNow.AddDays(13)
        });
        // Autorizacion viva: el candado se abre y el borrado (servicio nunca confirmado) procede.
        ctx.ReservaEditAuthorizations.Add(new ReservaEditAuthorization
        {
            Id = 5, ReservaId = 1, Reason = "hay que sacar un hotel cargado de mas",
            ExpiresAt = DateTime.UtcNow.AddMinutes(30)
        });
        await ctx.SaveChangesAsync();

        await NewBookingService(ctx).DeleteHotelAsync("1", "10", CancellationToken.None);

        Assert.Equal(0, await ctx.HotelBookings.CountAsync());
        // El candado registro el borrado bajo la autorizacion (rastro auditable).
        var change = Assert.Single(ctx.ReservaEditAuthorizationChanges);
        Assert.Equal(ReservaEditAuthorizationOperations.ServiceDeleted, change.Operation);
    }

    // ===================== (c) Editar DATOS de un servicio: SIGUE bajo candado =====================

    [Fact]
    public async Task UpdateHotel_EditData_OnConfirmedReserva_WithoutAuthorization_Throws()
    {
        await using var ctx = NewContext();
        ctx.Reservas.Add(Reserva(1, EstadoReserva.Confirmed));
        ctx.HotelBookings.Add(new HotelBooking
        {
            Id = 10, ReservaId = 1, HotelName = "Hotel Test", Status = "Confirmado", Adults = 2,
            CheckIn = DateTime.UtcNow.AddDays(10), CheckOut = DateTime.UtcNow.AddDays(13)
        });
        await ctx.SaveChangesAsync();

        var req = new UpdateHotelRequest(
            SupplierId: "1", HotelName: "Hotel Renombrado", StarRating: 4, City: "BA", Country: "AR",
            CheckIn: DateTime.UtcNow.AddDays(10), CheckOut: DateTime.UtcNow.AddDays(13),
            RoomType: "DBL", MealPlan: "BB", Adults: 2, Children: 0, Rooms: 1, ConfirmationNumber: null,
            NetCost: 100m, SalePrice: 120m, Commission: 0m, Status: "Confirmado", Notes: null);

        // Editar los DATOS (nombre, fechas, etc.) de un servicio en reserva confirmada SI pide candado.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewBookingService(ctx).UpdateHotelAsync("1", "10", req, CancellationToken.None));
        Assert.Contains("candado", ex.Message, StringComparison.OrdinalIgnoreCase);
        // El dato no cambio: la edicion no llego a ejecutarse.
        Assert.Equal("Hotel Test", (await ctx.HotelBookings.FindAsync(10))!.HotelName);
    }
}
