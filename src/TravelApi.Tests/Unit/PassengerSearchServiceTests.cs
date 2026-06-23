using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Tanda 3 (ficha de pasajero reutilizable): tests de PassengerSearchService.SearchSimilarAsync.
/// Cubren: documento exacto deduplicado (una persona, no N filas por reserva), nombre parcial, conteo de
/// usos, datos del registro mas reciente, sin criterios -> vacio, sin resultados -> vacio, y la garantia de
/// PRIVACIDAD (el DTO no expone nada de las reservas, solo identidad de la persona).
/// </summary>
public class PassengerSearchServiceTests
{
    [Fact]
    public async Task SearchSimilarAsync_ByExactDocument_ReturnsOneDeduplicatedPerson_NotOneRowPerReserva()
    {
        using var db = CreateDbContext();
        // La misma persona (DNI 30111222) figura en 3 reservas distintas: 3 filas Passenger.
        AddPassenger(db, reservaId: 1, fullName: "Juan Perez", docType: "DNI", docNumber: "30111222", createdAt: Days(-10));
        AddPassenger(db, reservaId: 2, fullName: "Juan Perez", docType: "DNI", docNumber: "30111222", createdAt: Days(-5));
        AddPassenger(db, reservaId: 3, fullName: "Juan Perez", docType: "DNI", docNumber: "30111222", createdAt: Days(-1));
        // Otra persona distinta que no debe aparecer en esta busqueda.
        AddPassenger(db, reservaId: 4, fullName: "Maria Lopez", docType: "DNI", docNumber: "99888777", createdAt: Days(-2));
        await db.SaveChangesAsync();

        var service = new PassengerSearchService(db);

        var matches = await service.SearchSimilarAsync(
            fullName: null, documentType: "DNI", documentNumber: "30111222", take: 10, CancellationToken.None);

        var match = Assert.Single(matches);
        Assert.Equal("30111222", match.DocumentNumber);
        Assert.Equal("DNI", match.DocumentType);
        Assert.Equal(3, match.UsageCount); // tres reservas, una sola persona
        Assert.Equal(100, match.Score); // documento + tipo exactos
    }

    [Fact]
    public async Task SearchSimilarAsync_DeduplicatedPerson_UsesMostRecentRecordData()
    {
        using var db = CreateDbContext();
        // El telefono/email cambiaron entre cargas: debe ganar el dato del registro MAS reciente.
        AddPassenger(db, reservaId: 1, fullName: "Ana Diaz", docType: "DNI", docNumber: "20111111",
            phone: "1111", email: "viejo@mail.com", createdAt: Days(-30));
        AddPassenger(db, reservaId: 2, fullName: "Ana Diaz", docType: "DNI", docNumber: "20111111",
            phone: "9999", email: "nuevo@mail.com", createdAt: Days(-1));
        await db.SaveChangesAsync();

        var service = new PassengerSearchService(db);

        var matches = await service.SearchSimilarAsync(
            fullName: null, documentType: "DNI", documentNumber: "20111111", take: 10, CancellationToken.None);

        var match = Assert.Single(matches);
        Assert.Equal("9999", match.Phone);
        Assert.Equal("nuevo@mail.com", match.Email);
        Assert.Equal(2, match.UsageCount);
    }

    [Fact]
    public async Task SearchSimilarAsync_ByPartialName_FindsMatchesAndDeduplicates()
    {
        using var db = CreateDbContext();
        // Misma persona sin documento en dos reservas (dedup por nombre normalizado).
        AddPassenger(db, reservaId: 1, fullName: "Carlos Gomez", docType: null, docNumber: null, createdAt: Days(-3));
        AddPassenger(db, reservaId: 2, fullName: "Carlos Gomez", docType: null, docNumber: null, createdAt: Days(-1));
        // Persona que no matchea el nombre buscado.
        AddPassenger(db, reservaId: 3, fullName: "Pedro Ramirez", docType: null, docNumber: null, createdAt: Days(-1));
        await db.SaveChangesAsync();

        var service = new PassengerSearchService(db);

        var matches = await service.SearchSimilarAsync(
            fullName: "gomez", documentType: null, documentNumber: null, take: 10, CancellationToken.None);

        var match = Assert.Single(matches);
        Assert.Equal("Carlos Gomez", match.FullName);
        Assert.Equal(2, match.UsageCount); // dedup por nombre (sin documento)
        Assert.True(match.Score >= 60);
    }

    [Fact]
    public async Task SearchSimilarAsync_TwoDifferentPeopleWithoutDocument_AreNotMerged()
    {
        using var db = CreateDbContext();
        // Dos personas distintas, ambas sin documento, nombres distintos que contienen "perez":
        // no se deben fundir (la clave de dedup sin documento es el nombre normalizado).
        AddPassenger(db, reservaId: 1, fullName: "Juan Perez", docType: null, docNumber: null, createdAt: Days(-1));
        AddPassenger(db, reservaId: 2, fullName: "Ana Perez", docType: null, docNumber: null, createdAt: Days(-1));
        await db.SaveChangesAsync();

        var service = new PassengerSearchService(db);

        var matches = await service.SearchSimilarAsync(
            fullName: "perez", documentType: null, documentNumber: null, take: 10, CancellationToken.None);

        Assert.Equal(2, matches.Count);
    }

    [Fact]
    public async Task SearchSimilarAsync_WithNoCriteria_ReturnsEmpty_DoesNotListEveryone()
    {
        using var db = CreateDbContext();
        AddPassenger(db, reservaId: 1, fullName: "Juan Perez", docType: "DNI", docNumber: "30111222", createdAt: Days(-1));
        await db.SaveChangesAsync();

        var service = new PassengerSearchService(db);

        var matches = await service.SearchSimilarAsync(
            fullName: null, documentType: null, documentNumber: null, take: 10, CancellationToken.None);

        Assert.Empty(matches);
    }

    [Fact]
    public async Task SearchSimilarAsync_WithNoMatches_ReturnsEmpty()
    {
        using var db = CreateDbContext();
        AddPassenger(db, reservaId: 1, fullName: "Juan Perez", docType: "DNI", docNumber: "30111222", createdAt: Days(-1));
        await db.SaveChangesAsync();

        var service = new PassengerSearchService(db);

        var matches = await service.SearchSimilarAsync(
            fullName: null, documentType: "DNI", documentNumber: "00000000", take: 10, CancellationToken.None);

        Assert.Empty(matches);
    }

    [Fact]
    public async Task SearchSimilarAsync_Result_DoesNotExposeReservaData()
    {
        // El DTO no tiene NINGUNA propiedad de reserva (ReservaId/numero). Esta verificacion por reflexion
        // congela la garantia de privacidad: si alguien agrega un campo de reserva al DTO, el test falla.
        var properties = typeof(TravelApi.Application.DTOs.PassengerSimilarMatchDto)
            .GetProperties()
            .Select(p => p.Name.ToLowerInvariant())
            .ToList();

        Assert.DoesNotContain("reservaid", properties);
        Assert.DoesNotContain("reservapublicid", properties);
        Assert.DoesNotContain("numeroreserva", properties);
        Assert.DoesNotContain("reserva", properties);
    }

    [Fact]
    public async Task SearchSimilarAsync_ExactDocumentScoresHigherThanPartialName()
    {
        using var db = CreateDbContext();
        AddPassenger(db, reservaId: 1, fullName: "Roberto Sanchez", docType: "DNI", docNumber: "27333444", createdAt: Days(-1));
        // Otra persona que solo matchea por nombre parcial.
        AddPassenger(db, reservaId: 2, fullName: "Roberto Ledesma", docType: "DNI", docNumber: "27999000", createdAt: Days(-1));
        await db.SaveChangesAsync();

        var service = new PassengerSearchService(db);

        // Busca por documento exacto de uno Y nombre parcial que matchea a ambos.
        var matches = await service.SearchSimilarAsync(
            fullName: "roberto", documentType: "DNI", documentNumber: "27333444", take: 10, CancellationToken.None);

        Assert.Equal(2, matches.Count);
        // El primero debe ser el del documento exacto (score 100), no el de nombre parcial (60).
        Assert.Equal("27333444", matches[0].DocumentNumber);
        Assert.Equal(100, matches[0].Score);
        Assert.True(matches[1].Score < matches[0].Score);
    }

    private static AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(warnings =>
                warnings.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        return new AppDbContext(options);
    }

    private static void AddPassenger(
        AppDbContext db,
        int reservaId,
        string fullName,
        string? docType,
        string? docNumber,
        DateTime createdAt,
        string? phone = null,
        string? email = null)
    {
        db.Passengers.Add(new Passenger
        {
            ReservaId = reservaId,
            FullName = fullName,
            DocumentType = docType,
            DocumentNumber = docNumber,
            Phone = phone,
            Email = email,
            CreatedAt = createdAt
        });
    }

    private static DateTime Days(int offset) => DateTime.UtcNow.AddDays(offset);
}
