namespace TravelApi.Infrastructure.Reservations;

/// <summary>
/// ADR-053 (2026-08-13, "fechas del viaje calculadas y de solo lectura", D5): las 2 sentencias SQL crudas
/// del backfill masivo de <c>Reserva.StartDate</c>/<c>EndDate</c> (decisión del dueño 2026-08-11:
/// recalcular TODAS las reservas existentes al deployar, no solo dejarlas auto-corregirse solas), en UN
/// solo lugar.
///
/// <para><b>Por qué existe esta clase (mismo motivo que <c>Adr048T5BackfillSql</c>, MT1 de aquella tanda)</b>:
/// si el texto de este SQL viviera SOLO adentro de la migración <c>Adr053_M1</c>, la equivalencia
/// "backfill SQL == <c>ReservaScheduleCalculator.ComputeAsync</c> en vivo" quedaría verificada solo por
/// inspección visual. Sacando el texto a constantes compartidas, la migración y el test de integración
/// <c>Adr053BackfillSqlIntegrationTests</c> ejecutan el MISMO SQL — si alguien edita uno sin tocar el
/// otro, compila igual pero el test corre el SQL VIEJO.</para>
///
/// <para><b>ADVERTENCIA (M1 del review round 2, repetida acá a propósito)</b>: el test de integración que
/// corre este SQL contra Postgres real y lo compara contra el C# es un ORÁCULO de consistencia interna,
/// NO una prueba de que el predicado sea correcto para el negocio — si el lado C# de la comparación
/// también estuviera mal (case-sensitive, literal), el test daría VERDE comparando dos versiones
/// igualmente equivocadas. La cobertura real del negocio (que "Cancelada" cuente como cancelado, que "A
/// confirmar" NO cuente) la dan los tests unitarios contra <c>WorkflowStatusHelper</c>.</para>
///
/// <para><b>El predicado "vigente" (D1.1) es la traducción SQL directa del mismo predicado que usa
/// <c>ReservaScheduleCalculator.ComputeAsync</c> en C#</b> — NO el literal case-sensitive
/// <c>Status != "Cancelado"</c> de <c>UpcomingStartCalculator</c>. Genérico (Hotel/Traslado/Paquete/
/// Asistencia/servicio genérico): <c>LOWER(TRIM(COALESCE("Status",''))) LIKE 'cancel%'</c> = cancelado.
/// Vuelo: <c>UPPER(TRIM(COALESCE("Status",''))) IN ('UN','UC','HX','NO')</c> = cancelado.</para>
///
/// <para><b>Por qué el agregado está ANCLADO en <c>"TravelFiles"</c> (LEFT JOIN, no INNER)</b>: una
/// reserva sin NINGÚN servicio vigente tiene que quedar con <c>NULL</c>/<c>NULL</c> (no con la fila
/// ausente del resultado, que la dejaría afuera del UPDATE y el INSERT). Ancorando el agregado en
/// <c>"TravelFiles"</c> con <c>LEFT JOIN</c> hacia la unión de los 6 tipos, <c>MIN</c>/<c>MAX</c> sobre un
/// grupo sin ninguna fila real devuelve <c>NULL</c> por definición SQL estándar — cubre esa reserva sin
/// una tercera sentencia de "fallback" (a diferencia de <c>Adr048T5BackfillSql</c>, acá no hace falta
/// separar "con filas"/"sin filas": el <c>LEFT JOIN</c> resuelve los dos casos en una sola pasada).</para>
/// </summary>
internal static class Adr053BackfillSql
{
    /// <summary>
    /// Fragmento COMPARTIDO (no es una sentencia ejecutable por sí sola): la unión de los 6 tipos de
    /// servicio VIGENTES, cada uno aportando (reserva_id, fecha de inicio, fecha de fin) con las MISMAS
    /// reglas de coalesce que <c>ReservaScheduleCalculator.ComputeAsync</c> (<c>ArrivalTime ?? DepartureTime</c>,
    /// <c>ReturnDateTime ?? PickupDateTime</c>, <c>EndDate ?? StartDate</c> del paquete). Se repite TEXTUAL en
    /// las 2 sentencias de abajo (cada <c>migrationBuilder.Sql(...)</c> es un statement independiente; no hay
    /// forma de compartir un CTE entre dos statements separados).
    /// </summary>
    private const string ServiceWindowsCte = @"
        svc_windows AS (
            SELECT ""ReservaId"" AS reserva_id, ""DepartureTime"" AS start_date,
                   COALESCE(""ArrivalTime"", ""DepartureTime"") AS end_date
            FROM ""FlightSegments""
            WHERE UPPER(TRIM(COALESCE(""Status"", ''))) NOT IN ('UN', 'UC', 'HX', 'NO')

            UNION ALL

            SELECT ""ReservaId"", ""CheckIn"", ""CheckOut""
            FROM ""HotelBookings""
            WHERE NOT (LOWER(TRIM(COALESCE(""Status"", ''))) LIKE 'cancel%')

            UNION ALL

            SELECT ""ReservaId"", ""PickupDateTime"", COALESCE(""ReturnDateTime"", ""PickupDateTime"")
            FROM ""TransferBookings""
            WHERE NOT (LOWER(TRIM(COALESCE(""Status"", ''))) LIKE 'cancel%')

            UNION ALL

            SELECT ""ReservaId"", ""StartDate"", COALESCE(""EndDate"", ""StartDate"")
            FROM ""PackageBookings""
            WHERE NOT (LOWER(TRIM(COALESCE(""Status"", ''))) LIKE 'cancel%')

            UNION ALL

            SELECT ""ReservaId"", ""ValidFrom"", ""ValidTo""
            FROM ""AssistanceBookings""
            WHERE NOT (LOWER(TRIM(COALESCE(""Status"", ''))) LIKE 'cancel%')

            UNION ALL

            -- Servicio generico: ReservaId es NULLABLE en esta tabla (un Servicio puede no estar
            -- ligado a ninguna reserva). Sin el IS NOT NULL, el LEFT JOIN de abajo se rompe.
            SELECT ""ReservaId"", ""DepartureDate"", COALESCE(""ReturnDate"", ""DepartureDate"")
            FROM ""Servicios""
            WHERE ""ReservaId"" IS NOT NULL
              AND NOT (LOWER(TRIM(COALESCE(""Status"", ''))) LIKE 'cancel%')
        ),
        trip_window_agg AS (
            SELECT tf.""Id"" AS reserva_id,
                   MIN(w.start_date) AS new_start,
                   MAX(w.end_date) AS new_end
            FROM ""TravelFiles"" tf
            LEFT JOIN svc_windows w ON w.reserva_id = tf.""Id""
            GROUP BY tf.""Id""
        )";

    /// <summary>
    /// BACKFILL 1/2 — inserta en <c>Adr053TripWindowBackfillLogs</c> UNA fila por reserva cuyo
    /// <c>(NewStart, NewEnd)</c> calculado DIFIERE del <c>StartDate</c>/<c>EndDate</c> que tiene persistido
    /// HOY (comparación <c>IS DISTINCT FROM</c>, segura con <c>NULL</c> de los dos lados). Corre ANTES del
    /// UPDATE de abajo — necesita leer el valor VIEJO todavía sin pisar.
    /// </summary>
    public const string InsertBackfillLog = @"
        WITH " + ServiceWindowsCte + @"
        INSERT INTO ""Adr053TripWindowBackfillLogs""
            (""ReservaId"", ""OldStartDate"", ""OldEndDate"", ""NewStartDate"", ""NewEndDate"", ""MigratedAtUtc"")
        SELECT tf.""Id"", tf.""StartDate"", tf.""EndDate"", agg.new_start, agg.new_end, now()
        FROM ""TravelFiles"" tf
        JOIN trip_window_agg agg ON agg.reserva_id = tf.""Id""
        WHERE tf.""StartDate"" IS DISTINCT FROM agg.new_start
           OR tf.""EndDate"" IS DISTINCT FROM agg.new_end;
    ";

    /// <summary>
    /// BACKFILL 2/2 — pisa <c>"TravelFiles"."StartDate"/"EndDate"</c> con el valor recalculado, para TODAS
    /// las reservas (el <c>LEFT JOIN</c> de <c>trip_window_agg</c> ya cubre tanto las que tienen servicios
    /// vigentes como las que no, dejando <c>NULL</c>/<c>NULL</c> para estas últimas). Corre DESPUÉS del
    /// INSERT de arriba.
    /// </summary>
    public const string UpdateTravelFilesWindow = @"
        WITH " + ServiceWindowsCte + @"
        UPDATE ""TravelFiles"" tf
        SET ""StartDate"" = agg.new_start,
            ""EndDate"" = agg.new_end
        FROM trip_window_agg agg
        WHERE tf.""Id"" = agg.reserva_id;
    ";
}
