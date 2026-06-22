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
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-036 (2026-06-21, "prepago puro"): el mapeo pasajero&lt;-&gt;servicio (quien viaja en cada servicio y
/// quien sale en el voucher) es una edicion de pasajeros mas, asi que comparte el MISMO candado por ESTADO.
/// Cierra el agujero detectado en review: los metodos de asignacion NO gateaban por estado, asi que en una
/// reserva "En viaje" (Traveling) o en un terminal (Closed/Lost/Cancelled/PendingOperatorRefund) todavia se
/// podian agregar / quitar / reemplazar asignaciones, violando la regla "Traveling = solo lectura total".
///
/// <para>Mismo patron de prueba que <see cref="Adr035PassengerAndDatesStateGateTests"/>: en los estados de
/// solo lectura los tres metodos deben rechazar con InvalidOperationException (-&gt; 409), incluso con una
/// autorizacion de edicion VIVA (el hard block por estado no se desbloquea con autorizacion).</para>
/// </summary>
public class Adr036AssignmentStateGateTests
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
        var mapper = new Mock<IMapper>();
        return new ReservaService(ctx, mapper.Object, settings.Object,
            BuildUserManager(), NullLogger<ReservaService>.Instance);
    }

    private static readonly Guid PassengerPublicId = Guid.NewGuid();
    private static readonly Guid FlightPublicId = Guid.NewGuid();

    // Reserva + 1 pasajero + 1 segmento de vuelo (Flight no declara capacidad, asi que la rama "estado vivo"
    // no choca con el tope por servicio: aisla la compuerta de ESTADO, que es lo que probamos).
    private static void SeedReservaWithFlight(AppDbContext ctx, string status)
    {
        ctx.Reservas.Add(new Reserva
        {
            Id = 1,
            PublicId = Guid.NewGuid(),
            NumeroReserva = "F-1",
            Name = "Reserva ADR-036 asignaciones",
            Status = status,
            AdultCount = 2,
            ResponsibleUserId = "vendedor-1"
        });
        ctx.Passengers.Add(new Passenger
        {
            Id = 10,
            PublicId = PassengerPublicId,
            ReservaId = 1,
            FullName = "Juan Perez",
            DocumentType = "DNI",
            DocumentNumber = "12345678"
        });
        ctx.FlightSegments.Add(new FlightSegment
        {
            Id = 20,
            PublicId = FlightPublicId,
            ReservaId = 1
        });
    }

    // Autorizacion de edicion VIVA: demuestra que en los estados de solo lectura ni con esto se desbloquea.
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

    // ADR-036: "En viaje" (Traveling) + los terminales son solo lectura dura tambien para las asignaciones.
    public static readonly object[][] ReadOnlyStates =
    {
        new object[] { EstadoReserva.Traveling },
        new object[] { EstadoReserva.Closed },
        new object[] { EstadoReserva.Lost },
        new object[] { EstadoReserva.Cancelled },
        new object[] { EstadoReserva.PendingOperatorRefund },
    };

    [Theory]
    [MemberData(nameof(ReadOnlyStates))]
    public async Task CreateAssignment_OnReadOnlyState_Rejected_EvenWithLiveAuthorization(string status)
    {
        await using var ctx = NewContext();
        SeedReservaWithFlight(ctx, status);
        AddLiveAuthorization(ctx);
        await ctx.SaveChangesAsync();

        var request = new CreatePassengerAssignmentRequest(
            PassengerPublicIdOrLegacyId: PassengerPublicId.ToString(),
            ServiceType: AssignmentServiceType.Flight,
            ServicePublicIdOrLegacyId: FlightPublicId.ToString(),
            RoomNumber: null, SeatNumber: null, Notes: null);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewService(ctx).CreateAssignmentAsync("1", request, CancellationToken.None));

        Assert.Equal(0, await ctx.PassengerServiceAssignments.CountAsync()); // no se creo nada
    }

    [Theory]
    [MemberData(nameof(ReadOnlyStates))]
    public async Task RemoveAssignment_OnReadOnlyState_Rejected_EvenWithLiveAuthorization(string status)
    {
        await using var ctx = NewContext();
        SeedReservaWithFlight(ctx, status);
        AddLiveAuthorization(ctx);
        // La asignacion ya existe (creada en un estado vivo previo): ahora la reserva esta en solo lectura.
        var assignmentPublicId = Guid.NewGuid();
        ctx.PassengerServiceAssignments.Add(new PassengerServiceAssignment
        {
            Id = 30,
            PublicId = assignmentPublicId,
            PassengerId = 10,
            ServiceType = AssignmentServiceType.Flight,
            ServiceId = 20,
            CreatedAt = DateTime.UtcNow
        });
        await ctx.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewService(ctx).RemoveAssignmentAsync(assignmentPublicId.ToString(), CancellationToken.None));

        Assert.Equal(1, await ctx.PassengerServiceAssignments.CountAsync()); // no se borro
    }

    [Theory]
    [MemberData(nameof(ReadOnlyStates))]
    public async Task ReplaceServiceAssignments_OnReadOnlyState_Rejected_EvenWithLiveAuthorization(string status)
    {
        await using var ctx = NewContext();
        SeedReservaWithFlight(ctx, status);
        AddLiveAuthorization(ctx);
        await ctx.SaveChangesAsync();

        var request = new ReplaceServiceAssignmentsRequest(
            PassengerPublicIds: new[] { PassengerPublicId.ToString() });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewService(ctx).ReplaceServiceAssignmentsAsync(
                "1", AssignmentServiceType.Flight, FlightPublicId.ToString(), request, CancellationToken.None));

        Assert.Equal(0, await ctx.PassengerServiceAssignments.CountAsync()); // no se creo nada
    }

    // Sanidad: en un estado vivo (Confirmed) la compuerta de estado DEJA PASAR y la asignacion se crea. Asi
    // probamos que el gate nuevo no rompe el camino feliz (no bloquea de mas).
    [Fact]
    public async Task CreateAssignment_OnConfirmedState_Allowed()
    {
        await using var ctx = NewContext();
        SeedReservaWithFlight(ctx, EstadoReserva.Confirmed);
        await ctx.SaveChangesAsync();

        var request = new CreatePassengerAssignmentRequest(
            PassengerPublicIdOrLegacyId: PassengerPublicId.ToString(),
            ServiceType: AssignmentServiceType.Flight,
            ServicePublicIdOrLegacyId: FlightPublicId.ToString(),
            RoomNumber: null, SeatNumber: null, Notes: null);

        await NewService(ctx).CreateAssignmentAsync("1", request, CancellationToken.None);

        Assert.Equal(1, await ctx.PassengerServiceAssignments.CountAsync());
    }
}
