namespace TravelApi.Domain.Reservations;

/// <summary>
/// Obra "PDF de presupuesto" (decisión #1 firmada del dueño, 2026-08-11/12): opciones A/B/C. En etapa
/// Presupuesto, dos o más servicios de la MISMA reserva pueden marcarse como ALTERNATIVAS entre sí
/// (ej. "Hotel A" vs "Hotel B" para el mismo tramo de viaje) usando el mismo texto en
/// <c>OptionGroup</c> (ej. "hoteles"). Mientras haya más de una alternativa viva sin resolver, ese
/// grupo es AMBIGUO: no sabemos todavía cuál eligió el cliente.
///
/// <para>Esta clase es la FUENTE ÚNICA de "qué grupos están ambiguos ahora mismo". La usan DOS
/// lugares que tienen que estar de acuerdo:</para>
/// <list type="bullet">
///   <item><see cref="ReservaMoneyCalculator"/>: un servicio de un grupo ambiguo NO suma a los
///   totales de la reserva (evita contar dos opciones del mismo grupo como si el cliente hubiera
///   comprado ambas).</item>
///   <item><c>ReservaService.EnsureReadinessForSaleAsync</c>: la transición "el cliente aceptó"
///   (Presupuesto -&gt; En gestión) se RECHAZA si queda algún grupo ambiguo — hay que resolverlo
///   (elegir una opción y borrar las otras) antes de avanzar.</item>
/// </list>
///
/// <para>Función PURA: no toca la base de datos, no conoce Reserva ni EF. El caller arma la lista de
/// entradas (una por servicio) con lo que ya tiene cargado.</para>
/// </summary>
public static class OptionGroupRules
{
    /// <summary>
    /// Una entrada por servicio: a qué grupo de opciones pertenece (si pertenece a alguno) y si está
    /// VIVO (no cancelado). Un servicio cancelado deja de "competir" por el grupo — mismo criterio que
    /// "cotizado" en <see cref="ReservaMoneyCalculator"/> (ver <c>IsQuotedHotel</c> y hermanos).
    /// </summary>
    public readonly record struct OptionGroupServiceInfo(string? OptionGroup, bool IsLive);

    /// <summary>
    /// Normaliza el nombre del grupo: recorta espacios, vacío/null pasa a <c>null</c> ("no pertenece a
    /// ningún grupo" = servicio normal, sin alternativas). La comparación entre grupos es
    /// case-insensitive: "Hoteles" y "hoteles" son el MISMO grupo (evita que un typo de mayúscula
    /// arme dos grupos separados sin que nadie se dé cuenta).
    /// </summary>
    public static string? Normalize(string? optionGroup)
    {
        if (string.IsNullOrWhiteSpace(optionGroup))
        {
            return null;
        }

        return optionGroup.Trim();
    }

    /// <summary>
    /// Devuelve los nombres de grupo (normalizados) que tienen MÁS DE UNA alternativa viva. Un grupo
    /// con exactamente 1 alternativa viva (las demás se cancelaron o se borraron al resolver) ya NO es
    /// ambiguo: quedó una sola opción, que es la que el cliente eligió.
    /// </summary>
    public static HashSet<string> FindAmbiguousGroups(IEnumerable<OptionGroupServiceInfo> services)
    {
        var liveCountByGroup = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var service in services)
        {
            if (!service.IsLive)
            {
                continue;
            }

            var group = Normalize(service.OptionGroup);
            if (group is null)
            {
                continue;
            }

            liveCountByGroup[group] = liveCountByGroup.TryGetValue(group, out var count) ? count + 1 : 1;
        }

        var ambiguousGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (group, count) in liveCountByGroup)
        {
            if (count > 1)
            {
                ambiguousGroups.Add(group);
            }
        }

        return ambiguousGroups;
    }

    /// <summary>
    /// True si este servicio pertenece a un grupo que todavía tiene más de una alternativa viva (y por
    /// lo tanto NO debe sumar a los totales comerciales de la reserva todavía).
    /// </summary>
    public static bool BelongsToAmbiguousGroup(string? optionGroup, HashSet<string> ambiguousGroups)
    {
        var normalized = Normalize(optionGroup);
        return normalized is not null && ambiguousGroups.Contains(normalized);
    }
}
