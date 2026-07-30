using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;

namespace TravelApi.Infrastructure.Services;

/// <summary>
/// ADR-052 (D2): la regla ÚNICA que decide "¿de qué versión es este resguardo y se puede usar?". Es una función
/// PURA (no toca base de datos ni archivos) a propósito: la usan los DOS caminos —el gate autoritativo que
/// habilita o rechaza una restauración real, y la marca informativa de la lista de resguardos— así que si
/// vivieran separados podrían decir cosas distintas del mismo archivo.
///
/// <para><b>Contra qué se compara</b>: contra la lista de migraciones del ENSAMBLADO (lo que EF Core trae
/// compilado), nunca contra el historial de la base viva. El que aplica las migraciones es EF con esa lista; si
/// la base viva quedó atrás (deploy a medias), compararla sería comparar contra la referencia equivocada.</para>
/// </summary>
public static class RestoreSchemaVerdictRules
{
    /// <summary>
    /// Decide el veredicto. <paramref name="assemblyMigrations"/> tiene que venir EN EL ORDEN DE EF
    /// (<c>Database.GetMigrations()</c>): el orden importa para distinguir "le falta el final de la fila"
    /// (resguardo viejo, se puede actualizar solo) de "le falta una del medio" (historial con agujero, no se
    /// puede completar). Nunca ordenar por texto a mano.
    /// </summary>
    /// <param name="assemblyMigrations">Migraciones que conoce el sistema, en orden de EF.</param>
    /// <param name="dumpMigrations">Migraciones que trae el resguardo (conjunto, el orden del dump no importa).</param>
    /// <param name="liveHasPendingMigrations">
    /// Si la base VIVA tiene migraciones sin aplicar. Cuando no se puede saber (camino informativo de la lista,
    /// que no consulta la base), va en <c>false</c>: ese camino no habilita nada.
    /// </param>
    public static RestoreSchemaVerdict Evaluate(
        IReadOnlyList<string> assemblyMigrations,
        ISet<string> dumpMigrations,
        bool liveHasPendingMigrations)
        => Evaluate(assemblyMigrations, dumpMigrations, liveHasPendingMigrations, out _);

    /// <summary>
    /// Igual que la otra <c>Evaluate</c>, pero además devuelve las migraciones HUÉRFANAS que se toleraron (ver
    /// abajo). Sirve SOLO para el log interno del motor; esos nombres jamás se le muestran al usuario (T-5).
    /// </summary>
    /// <param name="toleratedOrphanMigrations">
    /// Filas del historial del resguardo que el sistema no conoce pero que son VIEJAS (anteriores o iguales a la
    /// última migración del ensamblado), así que no bloquean.
    /// </param>
    public static RestoreSchemaVerdict Evaluate(
        IReadOnlyList<string> assemblyMigrations,
        ISet<string> dumpMigrations,
        bool liveHasPendingMigrations,
        out IReadOnlyList<string> toleratedOrphanMigrations)
    {
        var orphans = new List<string>();
        toleratedOrphanMigrations = orphans;

        // (1) La base viva a medio actualizar se rechaza ANTES que todo: sin esto, el veredicto se calcularía
        // para una base que ni ella misma está al día, y la actualización posterior arrastraría migraciones que
        // el deploy dejó a medias.
        if (liveHasPendingMigrations)
        {
            return RestoreSchemaVerdict.LiveHasPendingMigrations;
        }

        // (2) Un resguardo sin historial se rechaza acá, ANTES de clasificarlo. Si no, "todas las migraciones
        // faltan" se leería como el subconjunto más viejo posible y se intentaría restaurar un dump que no
        // tiene con qué demostrar de qué versión es.
        if (dumpMigrations.Count == 0)
        {
            return RestoreSchemaVerdict.DumpHistoryEmpty;
        }

        if (assemblyMigrations.Count == 0)
        {
            // Defensa: el ensamblado siempre tiene migraciones. Si no las tuviera, no hay con qué comparar.
            return RestoreSchemaVerdict.CouldNotDetermine;
        }

        // (3) Migraciones del resguardo que el sistema NO conoce. Hasta 2026-07-30 cualquiera de ellas se leía
        // como "resguardo del futuro" y bloqueaba TODA restauración. Eso resultó falso y dejó al dueño sin poder
        // restaurar nada: el historial de producción tenía una fila HUÉRFANA —una migración que ese mismo día se
        // regeneró con otro timestamp, y la fila vieja quedó anotada— y como todos los resguardos salen de esa
        // base, todos la traían.
        //
        // La fecha de la propia migración es lo que separa un caso del otro (los ids de EF empiezan con
        // yyyyMMddHHmmss):
        //   • posterior a la última que conoce el sistema ⇒ el peligro REAL (esquema de una versión más nueva);
        //   • anterior o igual ⇒ huérfana: se aparta y no bloquea. Restaurarla es INOFENSIVO para EF, porque EF
        //     calcula lo pendiente como "ensamblado MENOS lo aplicado" e ignora las filas que no conoce;
        //   • sin fecha legible ⇒ no se puede DEMOSTRAR que sea vieja, así que se avisa honestamente.
        //
        // Se clasifican TODAS antes de decidir (en vez de cortar en la primera rara) para que el veredicto no
        // dependa del orden en que el conjunto devuelva sus elementos: un mismo resguardo tiene que dar siempre
        // el mismo resultado. Si hay de los dos tipos, manda "más nueva", que es el diagnóstico más grave.
        var known = new HashSet<string>(assemblyMigrations, StringComparer.Ordinal);
        var newestAssemblyTimestamp = FindNewestTimestamp(assemblyMigrations);
        var recognizedDumpMigrations = new HashSet<string>(StringComparer.Ordinal);
        var foundNewerThanSystem = false;
        var foundUnknownWithoutReadableDate = false;

        foreach (var id in dumpMigrations)
        {
            if (known.Contains(id))
            {
                recognizedDumpMigrations.Add(id);
                continue;
            }

            var timestamp = TryGetTimestamp(id);
            if (timestamp is null || newestAssemblyTimestamp is null)
            {
                foundUnknownWithoutReadableDate = true;
            }
            else if (string.CompareOrdinal(timestamp, newestAssemblyTimestamp) > 0)
            {
                // Mismo largo fijo (14 dígitos) ⇒ comparar el texto es comparar la fecha.
                foundNewerThanSystem = true;
            }
            else
            {
                orphans.Add(id);
            }
        }

        // Orden estable para que el log interno (y los tests) no dependan del orden del conjunto.
        orphans.Sort(StringComparer.Ordinal);

        if (foundNewerThanSystem)
        {
            return RestoreSchemaVerdict.NewerThanSystem;
        }

        if (foundUnknownWithoutReadableDate)
        {
            return RestoreSchemaVerdict.CouldNotDetermine;
        }

        if (recognizedDumpMigrations.Count == 0)
        {
            // Trajo historial, pero NADA que el sistema reconozca: no hay con qué ubicar de qué versión es.
            return RestoreSchemaVerdict.CouldNotDetermine;
        }

        if (recognizedDumpMigrations.Count == assemblyMigrations.Count)
        {
            // Mismo tamaño y todas conocidas ⇒ son exactamente las mismas.
            return RestoreSchemaVerdict.Identical;
        }

        // Lo que falta tiene que ser el FINAL de la fila: las primeras N del ensamblado (N = las conocidas del
        // dump) tienen que estar todas en el dump. Si alguna de esas N no está, falta una del MEDIO.
        for (var i = 0; i < recognizedDumpMigrations.Count; i++)
        {
            if (!recognizedDumpMigrations.Contains(assemblyMigrations[i]))
            {
                return RestoreSchemaVerdict.HistoryGap;
            }
        }

        return RestoreSchemaVerdict.SubsetNeedsUpdate;
    }

    /// <summary>Largo del sello de fecha con el que EF Core arranca cada id de migración (<c>yyyyMMddHHmmss</c>).</summary>
    private const int EfTimestampLength = 14;

    /// <summary>
    /// Devuelve el sello de fecha del id, o <c>null</c> si no lo tiene con la forma esperada. Que sea
    /// <c>null</c> nunca se interpreta como "está bien": el que llama elige el camino conservador.
    /// </summary>
    private static string? TryGetTimestamp(string migrationId)
    {
        if (string.IsNullOrEmpty(migrationId) || migrationId.Length < EfTimestampLength)
        {
            return null;
        }

        for (var i = 0; i < EfTimestampLength; i++)
        {
            if (!char.IsAsciiDigit(migrationId[i]))
            {
                return null;
            }
        }

        return migrationId[..EfTimestampLength];
    }

    /// <summary>
    /// Sello de fecha MÁS ALTO del ensamblado. Se busca el máximo en vez de mirar el último de la lista a
    /// propósito: la regla recibe la lista en el orden de EF y nunca asume que ese orden sea cronológico
    /// (ver el comentario de <see cref="Evaluate(IReadOnlyList{string}, ISet{string}, bool)"/> sobre el orden).
    /// </summary>
    private static string? FindNewestTimestamp(IReadOnlyList<string> assemblyMigrations)
    {
        string? newest = null;
        foreach (var id in assemblyMigrations)
        {
            var timestamp = TryGetTimestamp(id);
            if (timestamp is null)
            {
                continue;
            }

            if (newest is null || string.CompareOrdinal(timestamp, newest) > 0)
            {
                newest = timestamp;
            }
        }

        return newest;
    }

    /// <summary>
    /// Cuántas migraciones habría que aplicar después de restaurar ese resguardo. Es un NÚMERO para el log y la
    /// auditoría; jamás se exponen los ids (T-5).
    /// </summary>
    public static int CountMissingMigrations(IReadOnlyList<string> assemblyMigrations, ISet<string> dumpMigrations)
    {
        var missing = 0;
        foreach (var id in assemblyMigrations)
        {
            if (!dumpMigrations.Contains(id))
            {
                missing++;
            }
        }

        return missing;
    }

    /// <summary>
    /// Traduce el veredicto a la marca INFORMATIVA que viaja en la lista de resguardos
    /// (<see cref="BackupVersionStates"/>).
    ///
    /// <para><b>Por qué "historial con agujero" y "sin historial" caen en "desconocida"</b> y no en un estado
    /// propio: el contrato firmado tiene cuatro valores y ninguno apaga el botón. Para esos dos casos lo honesto
    /// en la lista es "no pudimos determinar de qué versión es; el sistema lo verifica antes de tocar nada" — el
    /// rechazo con el texto exacto lo da el motor, que es el único autoritativo.</para>
    /// </summary>
    public static string ToVersionState(RestoreSchemaVerdict verdict) => verdict switch
    {
        RestoreSchemaVerdict.Identical => BackupVersionStates.Actual,
        RestoreSchemaVerdict.SubsetNeedsUpdate => BackupVersionStates.Anterior,
        RestoreSchemaVerdict.NewerThanSystem => BackupVersionStates.Posterior,
        _ => BackupVersionStates.Desconocida,
    };
}
