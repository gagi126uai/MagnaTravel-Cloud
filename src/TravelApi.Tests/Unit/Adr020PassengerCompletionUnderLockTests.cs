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
/// Decision 2026-06-17 (pasajeros bajo candado): con la reserva confirmada (candado de estado), COMPLETAR
/// el roster nominal que el sistema exige para emitir NO pide autorizacion; CAMBIAR un dato de identidad YA
/// cargado (o borrar un pasajero) SI la pide. El candado FISCAL (voucher/CAE) sigue aparte.
/// </summary>
public class Adr020PassengerCompletionUnderLockTests
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
        mapper.Setup(m => m.Map<PassengerDto>(It.IsAny<Passenger>()))
              .Returns((Passenger p) => new PassengerDto { FullName = p.FullName, DocumentNumber = p.DocumentNumber });
        return new ReservaService(ctx, mapper.Object, settings.Object,
            BuildUserManager(), NullLogger<ReservaService>.Instance);
    }

    // Reserva CONFIRMADA (con candado) que declara 2 pasajeros.
    private static void SeedLockedReserva(AppDbContext ctx)
    {
        ctx.Reservas.Add(new Reserva
        {
            Id = 1,
            PublicId = Guid.NewGuid(),
            NumeroReserva = "F-1",
            Name = "Reserva confirmada",
            Status = EstadoReserva.Confirmed,
            AdultCount = 2
        });
    }

    // PassengerUpsertRequest es un record posicional (FullName, DocumentType, DocumentNumber, BirthDate,
    // Nationality, Phone, Email, Gender, Notes, PassportExpiry). Helper para los campos que usan los tests.
    private static PassengerUpsertRequest Req(string fullName, string? documentNumber) =>
        new(fullName, "DNI", documentNumber, null, null, null, null, null, null, null);

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

    [Fact]
    public async Task AddPassenger_UnderLock_WithoutAuthorization_Succeeds()
    {
        await using var ctx = NewContext();
        SeedLockedReserva(ctx);
        await ctx.SaveChangesAsync();

        // Agregar un pasajero a una reserva confirmada YA no pide autorizacion (es completar el roster).
        await NewService(ctx).AddPassengerAsync("1", Req("Juan Perez", "12345678"), CancellationToken.None);

        var passenger = await ctx.Passengers.SingleAsync();
        Assert.Equal("Juan Perez", passenger.FullName);
    }

    [Fact]
    public async Task UpdatePassenger_CompletingEmptyDocument_UnderLock_WithoutAuthorization_Succeeds()
    {
        await using var ctx = NewContext();
        SeedLockedReserva(ctx);
        SeedPassenger(ctx, fullName: "Juan Perez", documentNumber: null); // documento vacio
        await ctx.SaveChangesAsync();

        // Completar el documento que faltaba (el sistema lo exige para emitir) NO pide autorizacion.
        // FullName sin cambios; se completa el documento que estaba vacio.
        await NewService(ctx).UpdatePassengerAsync("10", Req("Juan Perez", "12345678"), CancellationToken.None);

        var passenger = await ctx.Passengers.SingleAsync();
        Assert.Equal("12345678", passenger.DocumentNumber);
    }

    [Fact]
    public async Task UpdatePassenger_ChangingExistingName_UnderLock_WithoutAuthorization_Throws()
    {
        await using var ctx = NewContext();
        SeedLockedReserva(ctx);
        SeedPassenger(ctx, fullName: "Juan Perez", documentNumber: "12345678");
        await ctx.SaveChangesAsync();

        // Cambiar un dato de identidad YA cargado (el nombre) SI pide autorizacion -> sin ella, 409.
        // Cambia un nombre ya cargado -> requiere autorizacion.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewService(ctx).UpdatePassengerAsync("10", Req("Pedro Gomez", "12345678"), CancellationToken.None));

        var passenger = await ctx.Passengers.AsNoTracking().SingleAsync();
        Assert.Equal("Juan Perez", passenger.FullName); // no se modifico
    }

    [Fact]
    public async Task UpdatePassenger_ChangingExistingDocumentType_UnderLock_WithoutAuthorization_Throws()
    {
        await using var ctx = NewContext();
        SeedLockedReserva(ctx);
        SeedPassenger(ctx, fullName: "Juan Perez", documentNumber: "12345678"); // DocumentType "DNI"
        await ctx.SaveChangesAsync();

        // Cambiar SOLO el tipo de documento (DNI -> Pasaporte) ya cargado tambien es cambio de identidad.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewService(ctx).UpdatePassengerAsync("10",
                new PassengerUpsertRequest("Juan Perez", "Pasaporte", "12345678", null, null, null, null, null, null, null),
                CancellationToken.None));
    }

    [Fact]
    public async Task UpdatePassenger_ClearingExistingDocument_UnderLock_WithoutAuthorization_Throws()
    {
        await using var ctx = NewContext();
        SeedLockedReserva(ctx);
        SeedPassenger(ctx, fullName: "Juan Perez", documentNumber: "12345678");
        await ctx.SaveChangesAsync();

        // Borrar (limpiar) un dato de identidad YA cargado es cambio -> pide autorizacion.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewService(ctx).UpdatePassengerAsync("10", Req("Juan Perez", null), CancellationToken.None));
    }

    [Fact]
    public async Task UpdatePassenger_ContactOnly_UnderLock_WithoutAuthorization_Succeeds()
    {
        await using var ctx = NewContext();
        SeedLockedReserva(ctx);
        SeedPassenger(ctx, fullName: "Juan Perez", documentNumber: "12345678");
        await ctx.SaveChangesAsync();

        // Editar SOLO contacto (telefono/email/notas) NO es identidad -> no pide autorizacion.
        await NewService(ctx).UpdatePassengerAsync("10",
            new PassengerUpsertRequest("Juan Perez", "DNI", "12345678", null, null, "1122334455", "a@b.com", null, "llamar tarde", null),
            CancellationToken.None);

        var passenger = await ctx.Passengers.AsNoTracking().SingleAsync();
        Assert.Equal("1122334455", passenger.Phone);
    }

    [Fact]
    public async Task UpdatePassenger_ChangingExistingName_UnderLock_WithLiveAuthorization_Succeeds()
    {
        await using var ctx = NewContext();
        SeedLockedReserva(ctx);
        SeedPassenger(ctx, fullName: "Juan Perez", documentNumber: "12345678");
        ctx.ReservaEditAuthorizations.Add(new ReservaEditAuthorization
        {
            Id = 5,
            ReservaId = 1,
            Reason = "correccion autorizada por admin",
            ExpiresAt = DateTime.UtcNow.AddMinutes(30)
        });
        await ctx.SaveChangesAsync();

        // Con autorizacion viva, cambiar el nombre ya cargado se permite.
        await NewService(ctx).UpdatePassengerAsync("10", Req("Pedro Gomez", "12345678"), CancellationToken.None);

        var passenger = await ctx.Passengers.AsNoTracking().SingleAsync();
        Assert.Equal("Pedro Gomez", passenger.FullName);
    }
}
