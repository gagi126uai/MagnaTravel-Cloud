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
    {
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

        var known = new HashSet<string>(assemblyMigrations, StringComparer.Ordinal);
        var dumpHasUnknownMigration = dumpMigrations.Any(id => !known.Contains(id));
        if (dumpHasUnknownMigration)
        {
            return RestoreSchemaVerdict.NewerThanSystem;
        }

        if (dumpMigrations.Count == assemblyMigrations.Count)
        {
            // Mismo tamaño y todas conocidas ⇒ son exactamente las mismas.
            return RestoreSchemaVerdict.Identical;
        }

        // Lo que falta tiene que ser el FINAL de la fila: las primeras N del ensamblado (N = las del dump) tienen
        // que estar todas en el dump. Si alguna de esas N no está, falta una del MEDIO.
        for (var i = 0; i < dumpMigrations.Count; i++)
        {
            if (!dumpMigrations.Contains(assemblyMigrations[i]))
            {
                return RestoreSchemaVerdict.HistoryGap;
            }
        }

        return RestoreSchemaVerdict.SubsetNeedsUpdate;
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
