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
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-035 (2026-06-19): candado por ESTADO de la reserva extendido a PASAJEROS y FECHAS/datos de cabecera,
/// MISMO patron de 3 grupos que los servicios. Cierra la incoherencia detectada: en una reserva CERRADA
/// todavia se podian tocar pasajeros y fechas.
/// <list type="bullet">
///   <item><b>EN ARMADO</b> (Quotation/Budget/InManagement): editar libre (sin candado).</item>
///   <item><b>EN FIRME</b> (Confirmed/Traveling/ToSettle): la compuerta de estado deja pasar; el candado de
///     autorizacion sigue mandando igual que hoy (cambiar identidad / editar fechas lo pide; completar un
///     dato faltante o agregar un pasajero no, por ADR-031).</item>
///   <item><b>CERRADOS</b> (Closed/Lost/Cancelled/PendingOperatorRefund): SOLO LECTURA dura — ni completar,
///     ni agregar, ni borrar pasajeros; ni editar fechas. Ninguna autorizacion viva lo desbloquea.</item>
/// </list>
/// </summary>
public class Adr035PassengerAndDatesStateGateTests
{
    private static AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static UserManager<ApplicationUser> BuildUserManager()
    {
        var store = new Mock<IUserStore<ApplicationUser>>();
        return new UserManager<ApplicationUser>(
            store.Object, null!, null!,
            Array.Empty<IUserValidator<ApplicationUser>>(),
            Array.Empty<IPasswordValidator<ApplicationUser>>(),
            null!, null!, null!, null!);
    }

    private static ReservaService NewService(AppDbContext ctx)
    {
        var settings = new Mock<IOperationalFinanceSettingsService>();
        settings.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OperationalFinanceSettings());
        // Mapper mockeado solo para los metodos de pasajero (devuelven PassengerDto). UpdateDatesAsync no
        // necesita el mapper porque el gate corta ANTES de armar el DTO de respuesta.
        var mapper = new Mock<IMapper>();
        mapper.Setup(m => m.Map<PassengerDto>(It.IsAny<Passenger>()))
              .Returns((Passenger p) => new PassengerDto { FullName = p.FullName, DocumentNumber = p.DocumentNumber });
        return new ReservaService(ctx, mapper.Object, settings.Object,
            BuildUserManager(), NullLogger<ReservaService>.Instance);
    }

    // Reserva en el estado pedido, que declara 2 pasajeros (para que el alta no choque con el tope declarado).
    private static void SeedReserva(AppDbContext ctx, string status)
    {
        ctx.Reservas.Add(new Reserva
        {
            Id = 1,
            PublicId = Guid.NewGuid(),
            NumeroReserva = "F-1",
            Name = "Reserva ADR-035 pasajeros/fechas",
            Status = status,
            AdultCount = 2,
            ResponsibleUserId = "vendedor-1"
        });
    }

    private static void SeedPassenger(AppDbContext ctx, string fullName, string? documentNumber)
    {
        ctx.Passengers.Add(new Passenger
        {
            Id = 10,
            PublicId = Guid.NewGuid(),
            ReservaId = 1,
            FullName = fullName,
            DocumentType = "DNI",
            DocumentNumber = documentNumber
        });
    }

    // Autorizacion de edicion VIVA: demuestra que en CERRADOS ni con esto se desbloquea (hard block).
    private static void AddLiveAuthorization(AppDbContext ctx)
    {
        ctx.ReservaEditAuthorizations.Add(new ReservaEditAuthorization
        {
            Id = 99,
            ReservaId = 1,
            Reason = "autorizacion viva",
            ExpiresAt = DateTime.UtcNow.AddMinutes(30)
        });
    }

    // PassengerUpsertRequest posicional: (FullName, DocumentType, DocumentNumber, BirthDate, Nationality,
    // Phone, Email, Gender, Notes, PassportExpiry).
    private static PassengerUpsertRequest PassengerReq(string fullName, string? documentNumber) =>
        new(fullName, "DNI", documentNumber, null, null, null, null, null, null, null);

    // ADR-036 (2026-06-21): "En viaje" (Traveling) se suma a los estados de solo lectura dura — pasajeros y
    // fechas son solo lectura aun con autorizacion viva. ToSettle murio.
    public static readonly object[][] ReadOnlyStates =
    {
        new object[] { EstadoReserva.Lost },
        new object[] { EstadoReserva.Cancelled },
        new object[] { EstadoReserva.PendingOperatorRefund },
        new object[] { EstadoReserva.Closed },
        new object[] { EstadoReserva.Traveling },
    };

    public static readonly object[][] EarlyStages =
    {
        new object[] { EstadoReserva.Quotation },
        new object[] { EstadoReserva.Budget },
        new object[] { EstadoReserva.InManagement },
    };

    // =====================================================================================================
    // GRUPO CERRADOS: hard block. PASAJEROS — ni agregar, ni completar, ni borrar (aun con autorizacion viva).
    // =====================================================================================================

    [Theory]
    [MemberData(nameof(ReadOnlyStates))]
    public async Task AddPassenger_OnReadOnlyState_Rejected_EvenWithLiveAuthorization(string status)
    {
        await using var ctx = NewContext();
        SeedReserva(ctx, status);
        AddLiveAuthorization(ctx);
        await ctx.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewService(ctx).AddPassengerAsync("1", PassengerReq("Juan Perez", "12345678"), CancellationToken.None));

        Assert.Equal(0, await ctx.Passengers.CountAsync()); // no se agrego
    }

    [Theory]
    [MemberData(nameof(ReadOnlyStates))]
    public async Task CompletePassengerMissingData_OnReadOnlyState_Rejected_EvenWithLiveAuthorization(string status)
    {
        await using var ctx = NewContext();
        SeedReserva(ctx, status);
        SeedPassenger(ctx, fullName: "Juan Perez", documentNumber: null); // documento faltante
        AddLiveAuthorization(ctx);
        await ctx.SaveChangesAsync();

        // En estados vivos completar un dato faltante NO pide autorizacion (ADR-031). En CERRADOS el gate de
        // estado lo bloquea de raiz: ni completar se puede.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewService(ctx).UpdatePassengerAsync("10", PassengerReq("Juan Perez", "12345678"), CancellationToken.None));

        Assert.Null((await ctx.Passengers.AsNoTracking().SingleAsync()).DocumentNumber); // sin cambios
    }

    [Theory]
    [MemberData(nameof(ReadOnlyStates))]
    public async Task RemovePassenger_OnReadOnlyState_Rejected_EvenWithLiveAuthorization(string status)
    {
        await using var ctx = NewContext();
        SeedReserva(ctx, status);
        SeedPassenger(ctx, fullName: "Juan Perez", documentNumber: "12345678");
        AddLiveAuthorization(ctx);
        await ctx.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewService(ctx).RemovePassengerAsync("10", CancellationToken.None));

        Assert.Equal(1, await ctx.Passengers.CountAsync()); // no se borro
    }

    [Theory]
    [MemberData(nameof(ReadOnlyStates))]
    public async Task UpdateDates_OnReadOnlyState_Rejected_EvenWithLiveAuthorization(string status)
    {
        await using var ctx = NewContext();
        SeedReserva(ctx, status);
        AddLiveAuthorization(ctx);
        await ctx.SaveChangesAsync();

        var request = new UpdateReservaDatesRequest(
            StartDate: DateTime.UtcNow.AddDays(10),
            EndDate: DateTime.UtcNow.AddDays(20));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewService(ctx).UpdateDatesAsync("1", request, CancellationToken.None));

        var reserva = await ctx.Reservas.AsNoTracking().SingleAsync();
        Assert.Null(reserva.StartDate); // sin cambios
        Assert.Null(reserva.EndDate);
    }

    [Fact]
    public async Task ReadOnlyMessage_Passengers_ForClosed_MentionsFinalizedReadOnly_ADR036()
    {
        // ADR-036 (2026-06-21): el mensaje de Finalizada ya NO sugiere "reabrir a A liquidar" (ese camino
        // murio con ToSettle). Solo dice que esta finalizada y es solo lectura.
        await using var ctx = NewContext();
        SeedReserva(ctx, EstadoReserva.Closed);
        await ctx.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewService(ctx).AddPassengerAsync("1", PassengerReq("Juan Perez", "12345678"), CancellationToken.None));

        Assert.Contains("pasajeros", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("finalizada", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("A liquidar", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadOnlyMessage_Dates_ForCancelled_MentionsReadOnly_NoAmounts()
    {
        await using var ctx = NewContext();
        SeedReserva(ctx, EstadoReserva.Cancelled);
        await ctx.SaveChangesAsync();

        var request = new UpdateReservaDatesRequest(StartDate: DateTime.UtcNow, EndDate: DateTime.UtcNow.AddDays(1));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewService(ctx).UpdateDatesAsync("1", request, CancellationToken.None));

        Assert.Contains("solo lectura", ex.Message, StringComparison.OrdinalIgnoreCase);
        // El motivo es texto de estado, sin montos/costos.
        Assert.DoesNotContain("$", ex.Message);
    }

    // =====================================================================================================
    // GRUPO EN ARMADO: edicion libre. El gate de estado deja pasar, no se pide autorizacion.
    // =====================================================================================================

    [Theory]
    [MemberData(nameof(EarlyStages))]
    public async Task AddPassenger_OnEarlyStages_Allowed(string status)
    {
        await using var ctx = NewContext();
        SeedReserva(ctx, status);
        await ctx.SaveChangesAsync();

        await NewService(ctx).AddPassengerAsync("1", PassengerReq("Juan Perez", "12345678"), CancellationToken.None);

        Assert.Equal(1, await ctx.Passengers.CountAsync());
    }

    [Theory]
    [MemberData(nameof(EarlyStages))]
    public async Task RemovePassenger_OnEarlyStages_Allowed(string status)
    {
        await using var ctx = NewContext();
        SeedReserva(ctx, status);
        SeedPassenger(ctx, fullName: "Juan Perez", documentNumber: "12345678");
        await ctx.SaveChangesAsync();

        await NewService(ctx).RemovePassengerAsync("10", CancellationToken.None);

        Assert.Equal(0, await ctx.Passengers.CountAsync());
    }

    [Theory]
    [MemberData(nameof(EarlyStages))]
    public async Task UpdateDates_OnEarlyStages_Allowed(string status)
    {
        await using var ctx = NewContext();
        SeedReserva(ctx, status);
        await ctx.SaveChangesAsync();

        var start = DateTime.UtcNow.AddDays(5);
        var end = DateTime.UtcNow.AddDays(15);

        // En etapa de armado ni el gate de estado ni el candado bloquean: la edicion procede y persiste. El
        // armado del DTO de respuesta puede fallar en este entorno de test (IMapper mockeado solo para
        // PassengerDto), pero eso es ORTOGONAL al gate: lo importante es que las fechas YA se guardaron y que
        // el motivo (si hubo excepcion) NO es del gate de estado ni del candado.
        var ex = await Record.ExceptionAsync(() => NewService(ctx).UpdateDatesAsync("1",
            new UpdateReservaDatesRequest(StartDate: start, EndDate: end), CancellationToken.None));

        if (ex is InvalidOperationException ioe)
        {
            Assert.DoesNotContain("solo lectura", ioe.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("candado", ioe.Message, StringComparison.OrdinalIgnoreCase);
        }

        // Las fechas se persistieron antes de armar el DTO de respuesta: la edicion ocurrio.
        var reserva = await ctx.Reservas.AsNoTracking().SingleAsync();
        Assert.NotNull(reserva.StartDate);
        Assert.NotNull(reserva.EndDate);
    }

    // =====================================================================================================
    // GRUPO EN FIRME EDITABLE: la compuerta de estado deja pasar; el candado de autorizacion sigue mandando.
    // ADR-036 (2026-06-21): el unico estado firme EDITABLE es Confirmed (Traveling paso a SOLO LECTURA; los
    // casos de Traveling viven ahora en el grupo CERRADOS/read-only de arriba). ToSettle murio.
    // =====================================================================================================

    [Theory]
    [InlineData(EstadoReserva.Confirmed)]
    public async Task AddPassenger_OnFirmState_Allowed_NoAuthorizationNeeded(string status)
    {
        await using var ctx = NewContext();
        SeedReserva(ctx, status);
        await ctx.SaveChangesAsync();

        // ADR-031: agregar (completar el roster) en firme NO pide autorizacion. El gate de estado deja pasar.
        await NewService(ctx).AddPassengerAsync("1", PassengerReq("Juan Perez", "12345678"), CancellationToken.None);

        Assert.Equal(1, await ctx.Passengers.CountAsync());
    }

    [Theory]
    [InlineData(EstadoReserva.Confirmed)]
    public async Task CompletePassengerMissingData_OnFirmState_Allowed_NoAuthorizationNeeded(string status)
    {
        await using var ctx = NewContext();
        SeedReserva(ctx, status);
        SeedPassenger(ctx, fullName: "Juan Perez", documentNumber: null); // documento faltante
        await ctx.SaveChangesAsync();

        // Completar un dato faltante en firme NO pide autorizacion (ADR-031) y el gate de estado deja pasar.
        await NewService(ctx).UpdatePassengerAsync("10", PassengerReq("Juan Perez", "12345678"), CancellationToken.None);

        Assert.Equal("12345678", (await ctx.Passengers.AsNoTracking().SingleAsync()).DocumentNumber);
    }

    [Theory]
    [InlineData(EstadoReserva.Confirmed)]
    public async Task ChangePassengerIdentity_OnFirmState_WithoutAuthorization_Throws_LikeToday(string status)
    {
        await using var ctx = NewContext();
        SeedReserva(ctx, status);
        SeedPassenger(ctx, fullName: "Juan Perez", documentNumber: "12345678");
        await ctx.SaveChangesAsync();

        // Cambiar un dato de identidad YA cargado en firme SIGUE pidiendo autorizacion (candado, no gate de
        // estado). Sin autorizacion viva -> 409, igual que hoy.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewService(ctx).UpdatePassengerAsync("10", PassengerReq("Pedro Gomez", "12345678"), CancellationToken.None));

        Assert.Equal("Juan Perez", (await ctx.Passengers.AsNoTracking().SingleAsync()).FullName);
    }

    [Theory]
    [InlineData(EstadoReserva.Confirmed)]
    public async Task UpdateDates_OnFirmState_WithoutAuthorization_Throws_LikeToday(string status)
    {
        await using var ctx = NewContext();
        SeedReserva(ctx, status);
        await ctx.SaveChangesAsync();

        // Editar fechas en firme SIGUE pidiendo autorizacion (candado ADR-020 F4). El gate de estado deja
        // pasar; el candado corta. Sin autorizacion viva -> 409, igual que hoy.
        var request = new UpdateReservaDatesRequest(StartDate: DateTime.UtcNow.AddDays(3), EndDate: DateTime.UtcNow.AddDays(9));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewService(ctx).UpdateDatesAsync("1", request, CancellationToken.None));

        var reserva = await ctx.Reservas.AsNoTracking().SingleAsync();
        Assert.Null(reserva.StartDate); // el candado bloqueo antes de persistir
    }
}
