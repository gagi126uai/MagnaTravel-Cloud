using Microsoft.EntityFrameworkCore;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

/// <summary>
/// Implementa la busqueda de pasajeros historicos para la "ficha de pasajero reutilizable".
/// Espeja el enfoque de <see cref="CustomerService.SearchSimilarAsync"/>: trae un conjunto acotado de
/// candidatos en SQL (por documento exacto o nombre parcial) y luego puntua/ordena en memoria. La
/// diferencia clave es la DEDUPLICACION: como cada Passenger es por-reserva, una misma persona aparece
/// N veces; aca se colapsa a un solo resultado por (DocumentType, DocumentNumber) — o por nombre
/// normalizado cuando no hay documento — quedandose con los datos del registro MAS RECIENTE.
/// </summary>
public class PassengerSearchService : IPassengerSearchService
{
    private readonly AppDbContext _dbContext;

    // Tope de filas crudas que traemos de la base antes de deduplicar. Como hay un Passenger por reserva,
    // una sola persona puede ocupar muchas filas; pedimos un colchon generoso para que la deduplicacion
    // no se quede corta de personas distintas. No es el "take" del resultado final (ese lo aplica el
    // llamador sobre las personas ya deduplicadas).
    private const int CandidateFetchLimit = 200;

    public PassengerSearchService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<PassengerSimilarMatchDto>> SearchSimilarAsync(
        string? fullName,
        string? documentType,
        string? documentNumber,
        int take,
        CancellationToken cancellationToken)
    {
        var docType = documentType?.Trim();
        var docNumber = documentNumber?.Trim();
        var nameNorm = NormalizeName(fullName);

        // Sin ningun criterio de busqueda devolvemos vacio: no listamos a todos los pasajeros del sistema.
        var hasAnyCriteria = !string.IsNullOrEmpty(docNumber) || !string.IsNullOrEmpty(nameNorm);
        if (!hasAnyCriteria)
        {
            return Array.Empty<PassengerSimilarMatchDto>();
        }

        // Traemos solo los campos de identidad de la persona (NUNCA ReservaId ni nada de la reserva):
        // la proyeccion en SQL evita materializar la entidad completa y deja claro que no se filtran
        // datos de las reservas ajenas a esta busqueda.
        var candidates = await _dbContext.Passengers
            .AsNoTracking()
            .Where(p =>
                (docNumber != null && p.DocumentNumber == docNumber && (docType == null || p.DocumentType == docType)) ||
                (nameNorm != null && p.FullName.ToLower().Contains(nameNorm)))
            .OrderByDescending(p => p.CreatedAt)
            .Take(CandidateFetchLimit)
            .Select(p => new PassengerCandidate
            {
                FullName = p.FullName,
                DocumentType = p.DocumentType,
                DocumentNumber = p.DocumentNumber,
                BirthDate = p.BirthDate,
                Nationality = p.Nationality,
                Gender = p.Gender,
                Phone = p.Phone,
                Email = p.Email,
                PassportExpiry = p.PassportExpiry,
                CreatedAt = p.CreatedAt
            })
            .ToListAsync(cancellationToken);

        var deduplicated = DeduplicatePeople(candidates, docNumber, docType, nameNorm);

        var resultLimit = take > 0 ? take : 5;
        return deduplicated
            .OrderByDescending(match => match.Score)
            .ThenByDescending(match => match.UsageCount)
            .ThenBy(match => match.FullName)
            .Take(resultLimit)
            .ToList();
    }

    /// <summary>
    /// Colapsa las N filas (un Passenger por reserva) en UNA persona por clave de identidad. La clave es
    /// (DocumentType, DocumentNumber) si la persona tiene documento; si no, el nombre normalizado. Por
    /// cada persona se conservan los datos del registro MAS RECIENTE (mayor CreatedAt) y se cuenta en
    /// cuantas reservas figura (UsageCount). El score se calcula una vez por persona.
    /// </summary>
    private static List<PassengerSimilarMatchDto> DeduplicatePeople(
        IReadOnlyList<PassengerCandidate> candidates,
        string? docNumber,
        string? docType,
        string? nameNorm)
    {
        // Agrupamos por la clave de identidad. Las filas ya vienen ordenadas por CreatedAt descendente,
        // asi que la PRIMERA de cada grupo es la mas reciente -> de ahi salen los datos a mostrar.
        var groups = new Dictionary<string, PersonAccumulator>(StringComparer.Ordinal);

        foreach (var candidate in candidates)
        {
            var key = BuildIdentityKey(candidate);
            if (!groups.TryGetValue(key, out var accumulator))
            {
                accumulator = new PersonAccumulator { MostRecent = candidate, UsageCount = 0 };
                groups[key] = accumulator;
            }

            accumulator.UsageCount++;

            // Defensa por si el orden de la base no garantizara el descendente: nos quedamos siempre con
            // el CreatedAt mayor como "dato mas reciente".
            if (candidate.CreatedAt > accumulator.MostRecent.CreatedAt)
            {
                accumulator.MostRecent = candidate;
            }
        }

        var result = new List<PassengerSimilarMatchDto>(groups.Count);
        foreach (var accumulator in groups.Values)
        {
            var person = accumulator.MostRecent;
            result.Add(new PassengerSimilarMatchDto
            {
                FullName = person.FullName,
                DocumentType = person.DocumentType,
                DocumentNumber = person.DocumentNumber,
                BirthDate = person.BirthDate,
                Nationality = person.Nationality,
                Gender = person.Gender,
                Phone = person.Phone,
                Email = person.Email,
                PassportExpiry = person.PassportExpiry,
                UsageCount = accumulator.UsageCount,
                Score = ScoreMatch(person, docNumber, docType, nameNorm)
            });
        }

        return result;
    }

    /// <summary>
    /// Clave de identidad para deduplicar. Con documento: "doc|TIPO|NUMERO" (case-insensitive). Sin
    /// documento: "name|NOMBRE-NORMALIZADO". Asi dos pasajeros de la misma persona se funden, pero dos
    /// personas distintas sin documento y con nombres distintos no se mezclan.
    /// </summary>
    private static string BuildIdentityKey(PassengerCandidate candidate)
    {
        if (!string.IsNullOrWhiteSpace(candidate.DocumentNumber))
        {
            var typePart = (candidate.DocumentType ?? string.Empty).Trim().ToLowerInvariant();
            var numberPart = candidate.DocumentNumber.Trim().ToLowerInvariant();
            return $"doc|{typePart}|{numberPart}";
        }

        return $"name|{NormalizeName(candidate.FullName) ?? string.Empty}";
    }

    /// <summary>
    /// Mismo criterio de puntaje que la busqueda de clientes: documento exacto (con tipo) pesa mas que
    /// solo el numero, y el nombre exacto pesa mas que el nombre parcial. Se calcula una vez por persona
    /// (sobre el registro mas reciente).
    /// </summary>
    private static int ScoreMatch(PassengerCandidate person, string? docNumber, string? docType, string? nameNorm)
    {
        if (docNumber != null && person.DocumentNumber == docNumber)
        {
            return (docType != null && person.DocumentType == docType) ? 100 : 90;
        }

        if (nameNorm != null && NormalizeName(person.FullName) == nameNorm)
        {
            return 70;
        }

        if (nameNorm != null && (person.FullName ?? string.Empty).ToLowerInvariant().Contains(nameNorm))
        {
            return 60;
        }

        return 0;
    }

    private static string? NormalizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        return name.Trim().ToLowerInvariant();
    }

    /// <summary>Fila cruda de pasajero traida de la base (solo identidad). Interna al service.</summary>
    private sealed class PassengerCandidate
    {
        public string FullName { get; set; } = string.Empty;
        public string? DocumentType { get; set; }
        public string? DocumentNumber { get; set; }
        public DateTime? BirthDate { get; set; }
        public string? Nationality { get; set; }
        public string? Gender { get; set; }
        public string? Phone { get; set; }
        public string? Email { get; set; }
        public DateTime? PassportExpiry { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    /// <summary>Acumulador por persona durante la deduplicacion: datos mas recientes + conteo de usos.</summary>
    private sealed class PersonAccumulator
    {
        public PassengerCandidate MostRecent { get; set; } = null!;
        public int UsageCount { get; set; }
    }
}
