using System;
using System.Linq;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.Interfaces;
using TravelApi.Application.Mappings;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Arreglo 1 del retomo 2026-08-03 (review de la obra 2026-07-31 tarde): la comparacion "cambio el
/// campo" de UpdatePassengerAsync ya recortaba espacios (<c>TextValueChanged</c>), pero la ESCRITURA
/// guardaba el string crudo. Resultado: mandar " 30111222 " sobre "30111222" se declaraba "sin cambios"
/// (esquivaba el candado de estado ADR-035/CODE-14) pero el valor CON espacios quedaba persistido —
/// aparecia asi en voucher/PDF. Estos tests fijan que comparar y guardar miran el MISMO valor recortado.
/// </summary>
public class PassengerFieldTrimOnWriteTests
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
        return new UserManager<ApplicationUser>(
            store.Object, null!, null!,
            Array.Empty<IUserValidator<ApplicationUser>>(),
            Array.Empty<IPasswordValidator<ApplicationUser>>(),
            null!, null!, null!, null!);
    }

    private static ReservaService NewReservaService(AppDbContext context)
    {
        var settings = new Mock<IOperationalFinanceSettingsService>();
        settings.Setup(s => s.GetEntityAsync(It.IsAny<System.Threading.CancellationToken>()))
                .ReturnsAsync(new OperationalFinanceSettings());
        return new ReservaService(
            context, NewMapper(), settings.Object, BuildUserManager(), NullLogger<ReservaService>.Instance);
    }

    private static Reserva ReservaConFacturaViva(int id = 1)
        => new()
        {
            Id = id, NumeroReserva = "F-1", Name = "Test",
            Status = EstadoReserva.Confirmed,
            AdultCount = 1, ChildCount = 0, InfantCount = 0
        };

    [Fact]
    public async Task UpdatePassenger_MismoDocumentoConEspaciosDeMas_NoDisparaElCandadoYQuedaGuardadoSinEspacios()
    {
        // La reserva tiene factura con CAE viva: si el motor confundiera esto con un cambio de
        // identidad, CODE-14 (MutationGuards.GetPassengerMutationBlockReasonAsync) tiene que frenarlo.
        await using var ctx = NewContext();
        ctx.Reservas.Add(ReservaConFacturaViva());
        ctx.Passengers.Add(new Passenger
        {
            Id = 1, ReservaId = 1, FullName = "Juan Perez",
            DocumentType = "DNI", DocumentNumber = "30111222",
        });
        ctx.Invoices.Add(new Invoice
        {
            Id = 1, ReservaId = 1, TipoComprobante = 1, CAE = "12345678901234",
            AnnulmentStatus = AnnulmentStatus.None,
        });
        await ctx.SaveChangesAsync();
        ctx.ChangeTracker.Clear();

        var service = NewReservaService(ctx);

        // Mismo documento, pero con espacios de mas alrededor (caso real: copiar-pegar desde
        // WhatsApp/Excel). No debe tirar la excepcion del candado de estado.
        var dto = await service.UpdatePassengerAsync(
            passengerId: 1,
            new Passenger { Id = 1, FullName = "Juan Perez", DocumentType = "DNI", DocumentNumber = " 30111222 " });

        Assert.Equal("30111222", dto.DocumentNumber);
        var stored = await ctx.Passengers.AsNoTracking().SingleAsync(p => p.Id == 1);
        Assert.Equal("30111222", stored.DocumentNumber);
    }

    [Fact]
    public async Task UpdatePassenger_ValorNuevoConEspaciosAlrededor_SeGuardaRecortado()
    {
        await using var ctx = NewContext();
        ctx.Reservas.Add(new Reserva
        {
            Id = 1, NumeroReserva = "F-2", Name = "Test",
            Status = EstadoReserva.Budget,
            AdultCount = 1, ChildCount = 0, InfantCount = 0,
        });
        ctx.Passengers.Add(new Passenger
        {
            Id = 1, ReservaId = 1, FullName = "Juan Perez",
            DocumentType = "DNI", DocumentNumber = "11111111",
        });
        await ctx.SaveChangesAsync();
        ctx.ChangeTracker.Clear();

        var service = NewReservaService(ctx);

        var dto = await service.UpdatePassengerAsync(
            passengerId: 1,
            new Passenger { Id = 1, FullName = "  Nuevo Nombre  ", DocumentType = "DNI", DocumentNumber = " 22222222 " });

        Assert.Equal("Nuevo Nombre", dto.FullName);
        Assert.Equal("22222222", dto.DocumentNumber);
        var stored = await ctx.Passengers.AsNoTracking().SingleAsync(p => p.Id == 1);
        Assert.Equal("Nuevo Nombre", stored.FullName);
        Assert.Equal("22222222", stored.DocumentNumber);
    }

    /// <summary>
    /// Mini-tanda 2026-08-03 (mismo defecto de clase, hallazgo del reviewer sobre AddPassengerAsync): el
    /// ALTA de pasajero no trimeaba antes de persistir (el guard de duplicados de documento SI compara
    /// recortado, no habia bypass de seguridad — era suciedad directa en voucher/PDF).
    /// </summary>
    [Fact]
    public async Task AddPassenger_ValoresConEspaciosAlrededor_SeGuardanRecortados()
    {
        await using var ctx = NewContext();
        ctx.Reservas.Add(new Reserva
        {
            Id = 1, NumeroReserva = "F-3", Name = "Test",
            Status = EstadoReserva.Budget,
            AdultCount = 2, ChildCount = 0, InfantCount = 0,
        });
        await ctx.SaveChangesAsync();
        ctx.ChangeTracker.Clear();

        var service = NewReservaService(ctx);

        var dto = await service.AddPassengerAsync(1, new Passenger
        {
            FullName = "  Juan Perez  ",
            DocumentType = " DNI ",
            DocumentNumber = " 30111222 ",
            Nationality = " Argentina ",
            Gender = " M ",
        });

        Assert.Equal("Juan Perez", dto.FullName);
        Assert.Equal("30111222", dto.DocumentNumber);

        var stored = await ctx.Passengers.AsNoTracking().SingleAsync();
        Assert.Equal("Juan Perez", stored.FullName);
        Assert.Equal("DNI", stored.DocumentType);
        Assert.Equal("30111222", stored.DocumentNumber);
        Assert.Equal("Argentina", stored.Nationality);
        Assert.Equal("M", stored.Gender);
    }
}
