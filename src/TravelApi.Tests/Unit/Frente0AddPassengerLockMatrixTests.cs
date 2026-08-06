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
using TravelApi.Domain.Exceptions;
using TravelApi.Domain.Reservations;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Plan del día 2026-08-06, Frente 0 ("el candado de la Confirmada no cubre todo"): regla nueva del dueño que
/// REFINA la decisión 2026-06-17 (ver <see cref="Adr020PassengerCompletionUnderLockTests"/>), no la pisa.
///
/// <para>Los 3 estados del botón "Agregar Pasajero":</para>
/// <list type="number">
///   <item>Candado activo (Confirmada sin autorización viva) + declarados INCOMPLETOS -&gt; agregar sigue
///     pasando sin pedir permiso (decisión 17/06 intacta, es "completar").</item>
///   <item>Candado activo + declarados COMPLETOS -&gt; el motor RECHAZA con la excepción tipada
///     <see cref="PassengerRosterCompleteUnderLockException"/> (agregar de más ya es "alterar").</item>
///   <item>Sin candado (otro estado editable, o Confirmada CON autorización viva) -&gt; agregar sigue el
///     comportamiento de siempre (Regla C, 2026-06-08: tope declarado con mensaje genérico, sin candado).</item>
/// </list>
///
/// <para>Cubre DOS capas: la política pura (<see cref="ReservaCapabilityPolicy.For"/>.CanAddPassenger, la que
/// lee el botón del front) y el guard real de escritura (<see cref="ReservaService.AddPassengerAsync(string,
/// PassengerUpsertRequest,CancellationToken)"/>), que deben decidir EXACTAMENTE lo mismo (T-6: mismo texto,
/// <see cref="ReservaCapabilityPolicy.BuildPassengerRosterCompleteUnderLockReason"/>).</para>
/// </summary>
public class Frente0AddPassengerLockMatrixTests
{
    // =====================================================================================================
    // Capa 1: politica pura (lo que lee el boton del front). Sin EF, sin DB.
    // =====================================================================================================

    private static ReservaCapabilityContext Ctx(
        string status, bool hasLiveEditAuth, bool passengersRosterComplete, int declaredPassengerCount)
        => new(status, Balance: 0m, HasLiveCae: false, HasLiveVoucher: false, HasLiveEditAuth: hasLiveEditAuth,
            HasAnyPayment: false, PassengersRosterComplete: passengersRosterComplete,
            DeclaredPassengerCount: declaredPassengerCount);

    [Fact]
    public void CanAddPassenger_Confirmed_NoLiveAuth_RosterIncomplete_Allowed_EstadoUno()
    {
        // Estado 1: candado activo pero todavia falta gente por cargar -> completar sigue libre.
        var caps = ReservaCapabilityPolicy.For(
            Ctx(EstadoReserva.Confirmed, hasLiveEditAuth: false, passengersRosterComplete: false, declaredPassengerCount: 2));

        Assert.True(caps.CanAddPassenger.Allowed);
        Assert.Null(caps.CanAddPassenger.Reason);
    }

    [Fact]
    public void CanAddPassenger_Confirmed_NoLiveAuth_RosterComplete_Rejected_EstadoDos()
    {
        // Estado 2: candado activo Y roster declarado completo -> agregar de mas se rechaza, con motivo.
        var caps = ReservaCapabilityPolicy.For(
            Ctx(EstadoReserva.Confirmed, hasLiveEditAuth: false, passengersRosterComplete: true, declaredPassengerCount: 1));

        Assert.False(caps.CanAddPassenger.Allowed);
        Assert.Equal(
            ReservaCapabilityPolicy.BuildPassengerRosterCompleteUnderLockReason(1),
            caps.CanAddPassenger.Reason);
    }

    [Fact]
    public void CanAddPassenger_Confirmed_WithLiveAuth_RosterComplete_Allowed_CandadoDestrabado()
    {
        // Con autorizacion viva el candado esta apagado (destrabada): aunque el roster este completo, se
        // permite agregar -- es el mismo criterio que ya usa Editar/Borrar de un pasajero bajo autorizacion.
        var caps = ReservaCapabilityPolicy.For(
            Ctx(EstadoReserva.Confirmed, hasLiveEditAuth: true, passengersRosterComplete: true, declaredPassengerCount: 1));

        Assert.True(caps.CanAddPassenger.Allowed);
    }

    [Theory]
    [InlineData(EstadoReserva.Quotation)]
    [InlineData(EstadoReserva.Budget)]
    [InlineData(EstadoReserva.InManagement)]
    public void CanAddPassenger_SinCandado_RosterComplete_Allowed_EstadoTres(string status)
    {
        // Estado 3: fuera de Confirmada no hay candado de autorizacion que evaluar -- "Agregar Pasajero"
        // sigue permitido tal cual siempre estuvo, mas alla de que el roster ya este completo (esa
        // coherencia la sigue cuidando la Regla C del guard de escritura, ORTOGONAL a esta capacidad).
        var caps = ReservaCapabilityPolicy.For(
            Ctx(status, hasLiveEditAuth: false, passengersRosterComplete: true, declaredPassengerCount: 1));

        Assert.True(caps.CanAddPassenger.Allowed);
    }

    [Theory]
    [InlineData(EstadoReserva.Closed)]
    [InlineData(EstadoReserva.Cancelled)]
    [InlineData(EstadoReserva.Lost)]
    [InlineData(EstadoReserva.PendingOperatorRefund)]
    [InlineData(EstadoReserva.Traveling)]
    public void CanAddPassenger_EstadosTerminales_SiempreRechazado_MismoPisoQueCanEditPassengers(string status)
    {
        // El piso de estado (terminales / en viaje) es el MISMO que CanEditPassengers: ninguna autorizacion
        // desbloquea un estado de solo lectura dura.
        var caps = ReservaCapabilityPolicy.For(
            Ctx(status, hasLiveEditAuth: true, passengersRosterComplete: false, declaredPassengerCount: 1));

        Assert.False(caps.CanAddPassenger.Allowed);
        Assert.Equal(caps.CanEditPassengers.Reason, caps.CanAddPassenger.Reason);
    }

    [Theory]
    [InlineData(1, "1 pasajero")]
    [InlineData(2, "2 pasajeros")]
    public void BuildPassengerRosterCompleteUnderLockReason_TextoLiteral(int count, string fragmentoEsperado)
    {
        var mensaje = ReservaCapabilityPolicy.BuildPassengerRosterCompleteUnderLockReason(count);

        Assert.Contains(fragmentoEsperado, mensaje);
        Assert.Contains("destrabá la reserva", mensaje);
    }

    // =====================================================================================================
    // Capa 2: guard real de escritura (ReservaService.AddPassengerAsync). InMemory DB, igual que
    // Adr020PassengerCompletionUnderLockTests.
    // =====================================================================================================

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
        mapper.Setup(m => m.Map<PassengerDto>(It.IsAny<Passenger>()))
              .Returns((Passenger p) => new PassengerDto { FullName = p.FullName, DocumentNumber = p.DocumentNumber });
        return new ReservaService(ctx, mapper.Object, settings.Object,
            BuildUserManager(), NullLogger<ReservaService>.Instance);
    }

    private static PassengerUpsertRequest Req(string fullName, string? documentNumber) =>
        new(fullName, "DNI", documentNumber, null, null, null, null, null, null, null);

    private static void SeedPassenger(AppDbContext ctx, int id, string fullName, string documentNumber)
    {
        ctx.Passengers.Add(new Passenger
        {
            Id = id,
            PublicId = Guid.NewGuid(),
            ReservaId = 1,
            FullName = fullName,
            DocumentType = "DNI",
            DocumentNumber = documentNumber
        });
    }

    [Fact]
    public async Task AddPassenger_Confirmed_NoLiveAuth_RosterComplete_ThrowsTypedException_EstadoDos()
    {
        // "1 de 1 nombres cargados" (la captura exacta de Gaston): 1 declarado, 1 ya cargado, candado activo.
        await using var ctx = NewContext();
        ctx.Reservas.Add(new Reserva
        {
            Id = 1, PublicId = Guid.NewGuid(), NumeroReserva = "F-1", Name = "Reserva confirmada",
            Status = EstadoReserva.Confirmed, AdultCount = 1
        });
        SeedPassenger(ctx, 10, "Juan Perez", "12345678");
        await ctx.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<PassengerRosterCompleteUnderLockException>(() =>
            NewService(ctx).AddPassengerAsync("1", Req("Pedro Gomez", "99999999"), CancellationToken.None));

        Assert.Equal("RESERVA_PASAJEROS_COMPLETOS_BAJO_CANDADO", ex.Code);
        Assert.Equal(ReservaCapabilityPolicy.BuildPassengerRosterCompleteUnderLockReason(1), ex.Message);
        Assert.Equal(1, await ctx.Passengers.CountAsync()); // no se agrego nada de mas
    }

    [Fact]
    public async Task AddPassenger_Confirmed_WithLiveAuth_RosterComplete_Throws_OldGenericException_NotTyped()
    {
        // Con la reserva destrabada (autorizacion viva) el candado esta apagado: el tope declarado se sigue
        // respetando (Regla C, 2026-06-08 -- es un dato de coherencia del roster, no de autorizacion), pero
        // con el mensaje de SIEMPRE (no el nuevo, tipado, de candado).
        await using var ctx = NewContext();
        ctx.Reservas.Add(new Reserva
        {
            Id = 1, PublicId = Guid.NewGuid(), NumeroReserva = "F-1", Name = "Reserva confirmada destrabada",
            Status = EstadoReserva.Confirmed, AdultCount = 1
        });
        SeedPassenger(ctx, 10, "Juan Perez", "12345678");
        ctx.ReservaEditAuthorizations.Add(new ReservaEditAuthorization
        {
            Id = 5, ReservaId = 1, Reason = "correccion autorizada", ExpiresAt = DateTime.UtcNow.AddMinutes(30)
        });
        await ctx.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewService(ctx).AddPassengerAsync("1", Req("Pedro Gomez", "99999999"), CancellationToken.None));

        Assert.IsNotType<PassengerRosterCompleteUnderLockException>(ex);
        Assert.Contains("aumentá la cantidad declarada", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, await ctx.Passengers.CountAsync());
    }

    [Fact]
    public async Task AddPassenger_InManagement_SinCandado_RosterComplete_Throws_OldGenericException_EstadoTres()
    {
        // Estado 3 (sin candado: InManagement nunca tuvo candado de autorizacion): el tope declarado sigue
        // frenando IGUAL que siempre (Regla C es ortogonal al candado), con el mensaje de SIEMPRE.
        await using var ctx = NewContext();
        ctx.Reservas.Add(new Reserva
        {
            Id = 1, PublicId = Guid.NewGuid(), NumeroReserva = "F-1", Name = "Reserva en gestion",
            Status = EstadoReserva.InManagement, AdultCount = 1
        });
        SeedPassenger(ctx, 10, "Juan Perez", "12345678");
        await ctx.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewService(ctx).AddPassengerAsync("1", Req("Pedro Gomez", "99999999"), CancellationToken.None));

        Assert.IsNotType<PassengerRosterCompleteUnderLockException>(ex);
        Assert.Contains("aumentá la cantidad declarada", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AddPassenger_Confirmed_NoLiveAuth_RosterIncomplete_Succeeds_EstadoUno()
    {
        // Estado 1 (decision 17/06 intacta): con 2 declarados y 1 solo cargado, agregar el segundo sigue
        // sin pedir autorizacion -- ya cubierto por Adr020PassengerCompletionUnderLockTests, se repite aca
        // como parte de la matriz completa de los 3 estados en un solo lugar.
        await using var ctx = NewContext();
        ctx.Reservas.Add(new Reserva
        {
            Id = 1, PublicId = Guid.NewGuid(), NumeroReserva = "F-1", Name = "Reserva confirmada",
            Status = EstadoReserva.Confirmed, AdultCount = 2
        });
        SeedPassenger(ctx, 10, "Juan Perez", "12345678");
        await ctx.SaveChangesAsync();

        await NewService(ctx).AddPassengerAsync("1", Req("Pedro Gomez", "99999999"), CancellationToken.None);

        Assert.Equal(2, await ctx.Passengers.CountAsync());
    }
}
