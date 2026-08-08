using Microsoft.EntityFrameworkCore;
using TravelApi.Domain.Helpers;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services.Reservations;

/// <summary>
/// LA MEMORIA del texto libre (spec firmada 2026-08-07, §5.2 / M-19): los nombres finos de habitación
/// ("Superior", "Vista al mar") y los vehículos ("Van") que ya se escribieron alguna vez.
///
/// <para><b>Por qué existe</b>: el dueño firmó texto libre para esos campos, pero el texto libre sin
/// memoria es una fábrica de repetidos — "Superior", "superior", "SUP" y "Sup." serían cuatro
/// habitaciones distintas y cada una con su propio precio. Con memoria, la primera vez se escribe y las
/// siguientes el sistema unifica solo. Eso NO es duda grande: se resuelve sin preguntar (§4).</para>
///
/// <para><b>Por qué es un helper compartido y no un método del tarifario</b>: la unificación tiene que
/// pasar en los DOS lugares donde nace un nombre — la venta (los 5 altas de servicio) y el tarifario
/// (alta a mano, corrección). Si viviera solo en uno, vender "Sup" seguiría fabricando una habitación
/// nueva, que es exactamente el agujero que se está tapando.</para>
///
/// <para><b>De dónde sale la memoria</b>: de lo que ya está escrito en las ventas y en el tarifario
/// (<c>HotelBooking.RoomCategory</c> / <c>TransferBooking.VehicleType</c> y sus equivalentes en
/// <c>Rates</c>). No hay tabla nueva: la memoria ES el historial.</para>
/// </summary>
public static class CatalogVariantNameMemory
{
    /// <summary>Cuántos nombres distintos se le pueden ofrecer al vendedor mientras escribe.</summary>
    public const int SuggestionLimit = 10;

    /// <summary>
    /// Unifica lo que se acaba de escribir con lo que ya existe. Devuelve el nombre YA CONOCIDO cuando lo
    /// escrito es la misma cosa escrita distinto; si no se parece a nada, devuelve lo escrito tal cual
    /// (con los bordes recortados). Null/vacío devuelve null.
    ///
    /// <para><b>Qué cuenta como "la misma cosa"</b>: (1) el mismo texto ya normalizado —mayúsculas,
    /// tildes y espacios no hacen a otra habitación—; (2) una abreviatura o un recorte de algo conocido
    /// ("sup", "superio" → "Superior"), con al menos 3 letras para no unificar cualquier cosa con
    /// cualquier cosa.</para>
    /// </summary>
    public static async Task<string?> ResolveAsync(
        AppDbContext db, string? serviceType, string? writtenName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(writtenName)) return null;

        var written = TextNormalizer.NormalizeForCatalog(writtenName);
        if (written.Length == 0) return writtenName.Trim();

        // Se consultan TODOS los nombres conocidos del tipo, no una página de sugerencias: si la memoria
        // se limitara a los primeros N, escribir "sup" fabricaría una habitación nueva solo porque
        // "Superior" quedó afuera del recorte.
        var known = await LoadKnownNamesAsync(db, serviceType, ct);

        foreach (var candidate in known)
        {
            if (string.Equals(candidate.Key, written, StringComparison.Ordinal)) return candidate.Original;
        }

        if (written.Length >= 3)
        {
            var prefix = known.FirstOrDefault(candidate =>
                candidate.Key.StartsWith(written, StringComparison.Ordinal));
            if (prefix.Original is not null) return prefix.Original;
        }

        return writtenName.Trim();
    }

    /// <summary>
    /// Los nombres para ofrecer mientras el vendedor escribe. Primero los que EMPIEZAN con lo tipeado,
    /// después los que lo contienen. Se devuelve el texto tal como lo escribió una persona (el más usado),
    /// nunca la clave interna en minúscula ni una versión "Con Todas Las Palabras En Mayúscula".
    /// </summary>
    public static async Task<IReadOnlyList<string>> SuggestAsync(
        AppDbContext db, string? serviceType, string? search, CancellationToken ct)
    {
        var known = await LoadKnownNamesAsync(db, serviceType, ct);
        var normalizedSearch = TextNormalizer.NormalizeForCatalog(search);

        return known
            .Where(candidate => normalizedSearch.Length == 0
                || candidate.Key.Contains(normalizedSearch, StringComparison.Ordinal))
            .OrderBy(candidate => candidate.Key.StartsWith(normalizedSearch, StringComparison.Ordinal) ? 0 : 1)
            .ThenByDescending(candidate => candidate.Uses)
            .ThenBy(candidate => candidate.Original, StringComparer.CurrentCultureIgnoreCase)
            .Take(SuggestionLimit)
            .Select(candidate => candidate.Original)
            .ToList();
    }

    /// <summary>
    /// Cada nombre conocido una sola vez: su clave normalizada (para comparar), la escritura MÁS USADA
    /// (para mostrar) y cuántas veces apareció.
    /// </summary>
    private static async Task<List<(string Key, string Original, int Uses)>> LoadKnownNamesAsync(
        AppDbContext db, string? serviceType, CancellationToken ct)
    {
        var typeKey = TextNormalizer.NormalizeForMatch(serviceType);
        var written = new List<string>();

        // El tipo se compara YA normalizado en los dos lados: "hotel" y "Hotel" son el mismo tipo.
        if (typeKey == "hotel")
        {
            written.AddRange(await db.HotelBookings.AsNoTracking()
                .Where(booking => booking.RoomCategory != null && booking.RoomCategory != "")
                .Select(booking => booking.RoomCategory!)
                .ToListAsync(ct));

            written.AddRange(await db.Rates.AsNoTracking()
                .Where(rate => rate.ServiceType.ToLower() == "hotel"
                    && rate.RoomCategory != null && rate.RoomCategory != "")
                .Select(rate => rate.RoomCategory!)
                .ToListAsync(ct));
        }
        else if (typeKey == "traslado")
        {
            written.AddRange(await db.TransferBookings.AsNoTracking()
                .Where(booking => booking.VehicleType != null && booking.VehicleType != "")
                .Select(booking => booking.VehicleType!)
                .ToListAsync(ct));

            written.AddRange(await db.Rates.AsNoTracking()
                .Where(rate => rate.ServiceType.ToLower() == "traslado"
                    && rate.VehicleType != null && rate.VehicleType != "")
                .Select(rate => rate.VehicleType!)
                .ToListAsync(ct));
        }

        return written
            .Select(name => new { Key = TextNormalizer.NormalizeForCatalog(name), Original = name.Trim() })
            .Where(item => item.Key.Length > 0)
            .GroupBy(item => item.Key, StringComparer.Ordinal)
            .Select(group => (
                Key: group.Key,
                // La escritura que más se repite gana; a igualdad, la primera alfabéticamente (estable).
                Original: group.GroupBy(item => item.Original, StringComparer.Ordinal)
                    .OrderByDescending(spelling => spelling.Count())
                    .ThenBy(spelling => spelling.Key, StringComparer.Ordinal)
                    .First().Key,
                Uses: group.Count()))
            .ToList();
    }
}
