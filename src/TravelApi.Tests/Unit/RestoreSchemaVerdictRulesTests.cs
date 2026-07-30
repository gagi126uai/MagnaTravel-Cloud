using System.Collections.Generic;
using System.Linq;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-052 (D2/D5): la regla ÚNICA que decide de qué versión es un resguardo. Es una función pura, así que se
/// prueba directo con listas armadas a mano — sin base de datos y sin archivos. Estos casos son el corazón de la
/// obra: si esta clase se equivoca, o se rechazan resguardos buenos (el problema que vinimos a arreglar) o —peor—
/// se acepta uno que no se puede completar.
/// </summary>
public class RestoreSchemaVerdictRulesTests
{
    /// <summary>
    /// Ids con forma real (timestamp + nombre). OJO: el orden de EF NO es necesariamente el alfabético, y por eso
    /// la regla recibe la lista EN ORDEN DE EF y jamás la re-ordena por texto — el caso
    /// <see cref="ElOrdenQueManda_EsElDeEfNoElAlfabetico"/> lo fija.
    /// </summary>
    private static readonly List<string> SistemaHoy = new()
    {
        "20260322010000_AddOperationalFinanceAndTreasury",
        "20260325003000_AddRefreshTokens",
        "20260530120000_AddRateFuzzyMatching",
        "20260717090000_Adr048_M2",
        "20260729080000_Adr052_Nada",
    };

    private static ISet<string> Conjunto(params string[] ids) => new HashSet<string>(ids);

    [Fact]
    public void ResguardoConLasMismasMigraciones_EsLaVersionActual()
    {
        var verdict = RestoreSchemaVerdictRules.Evaluate(SistemaHoy, Conjunto(SistemaHoy.ToArray()), liveHasPendingMigrations: false);

        Assert.Equal(RestoreSchemaVerdict.Identical, verdict);
        Assert.Equal(0, RestoreSchemaVerdictRules.CountMissingMigrations(SistemaHoy, Conjunto(SistemaHoy.ToArray())));
        Assert.Equal(BackupVersionStates.Actual, RestoreSchemaVerdictRules.ToVersionState(verdict));
    }

    [Fact]
    public void ResguardoAlQueLeFaltaElFinalDeLaFila_EsDeVersionAnteriorYSePuedeActualizarSolo()
    {
        // El caso REAL que motivó ADR-052: el resguardo del 2026-07-27 quedó dos deploys atrás.
        var dump = Conjunto(SistemaHoy[0], SistemaHoy[1], SistemaHoy[2]);

        var verdict = RestoreSchemaVerdictRules.Evaluate(SistemaHoy, dump, liveHasPendingMigrations: false);

        Assert.Equal(RestoreSchemaVerdict.SubsetNeedsUpdate, verdict);
        Assert.Equal(2, RestoreSchemaVerdictRules.CountMissingMigrations(SistemaHoy, dump));
        Assert.Equal(BackupVersionStates.Anterior, RestoreSchemaVerdictRules.ToVersionState(verdict));
    }

    [Fact]
    public void ResguardoConUnaMigracionQueElSistemaNoConoce_EsDeVersionMasNueva()
    {
        var dump = Conjunto(SistemaHoy[0], SistemaHoy[1], "20260801000000_AlgoDelFuturo");

        var verdict = RestoreSchemaVerdictRules.Evaluate(SistemaHoy, dump, liveHasPendingMigrations: false);

        Assert.Equal(RestoreSchemaVerdict.NewerThanSystem, verdict);
        Assert.Equal(BackupVersionStates.Posterior, RestoreSchemaVerdictRules.ToVersionState(verdict));
    }

    [Fact]
    public void ResguardoAlQueLeFaltaUnaDelMedio_EsHistorialConAgujero()
    {
        // Tiene la 1ra, la 3ra y la 4ta: le falta la 2da. No se puede "completar con las que siguen".
        var dump = Conjunto(SistemaHoy[0], SistemaHoy[2], SistemaHoy[3]);

        var verdict = RestoreSchemaVerdictRules.Evaluate(SistemaHoy, dump, liveHasPendingMigrations: false);

        Assert.Equal(RestoreSchemaVerdict.HistoryGap, verdict);
        // En la lista informativa esto NO puede mostrarse como "anterior" (sería mentir): va como desconocida.
        Assert.Equal(BackupVersionStates.Desconocida, RestoreSchemaVerdictRules.ToVersionState(verdict));
    }

    [Fact]
    public void ResguardoSinHistorial_SeRechazaYNoSeConfundeConElSubconjuntoMasViejo()
    {
        var verdict = RestoreSchemaVerdictRules.Evaluate(SistemaHoy, Conjunto(), liveHasPendingMigrations: false);

        Assert.Equal(RestoreSchemaVerdict.DumpHistoryEmpty, verdict);
        Assert.Equal(BackupVersionStates.Desconocida, RestoreSchemaVerdictRules.ToVersionState(verdict));
    }

    [Fact]
    public void BaseVivaConMigracionesPendientes_GanaSobreCualquierOtroVeredicto()
    {
        // Se chequea PRIMERO a propósito: si la base viva quedó a mitad de una actualización, el veredicto se
        // estaría calculando sobre una referencia que ni ella misma tiene al día.
        var verdict = RestoreSchemaVerdictRules.Evaluate(
            SistemaHoy, Conjunto(SistemaHoy.ToArray()), liveHasPendingMigrations: true);

        Assert.Equal(RestoreSchemaVerdict.LiveHasPendingMigrations, verdict);
    }

    [Fact]
    public void ElOrdenQueManda_EsElDeEfNoElAlfabetico()
    {
        // Si alguien "arreglara" la regla ordenando por texto, este caso pasaría a dar SubsetNeedsUpdate (y se
        // intentaría completar un historial que NO se puede completar). El orden de EF es el de la lista recibida.
        var ordenDeEf = new List<string> { "20260101000000_B", "20250101000000_A", "20270101000000_C" };
        var dumpConLasDosPrimerasDeEf = Conjunto("20260101000000_B", "20250101000000_A");

        Assert.Equal(
            RestoreSchemaVerdict.SubsetNeedsUpdate,
            RestoreSchemaVerdictRules.Evaluate(ordenDeEf, dumpConLasDosPrimerasDeEf, liveHasPendingMigrations: false));

        // Las "dos primeras alfabéticamente" NO son un prefijo del orden de EF: eso es un agujero.
        var dumpConLasDosPrimerasAlfabeticas = Conjunto("20250101000000_A", "20260101000000_B");
        Assert.Equal(
            RestoreSchemaVerdict.SubsetNeedsUpdate,
            RestoreSchemaVerdictRules.Evaluate(ordenDeEf, dumpConLasDosPrimerasAlfabeticas, liveHasPendingMigrations: false));

        var dumpQueSalteaLaDelMedioDeEf = Conjunto("20260101000000_B", "20270101000000_C");
        Assert.Equal(
            RestoreSchemaVerdict.HistoryGap,
            RestoreSchemaVerdictRules.Evaluate(ordenDeEf, dumpQueSalteaLaDelMedioDeEf, liveHasPendingMigrations: false));
    }

    [Fact]
    public void SinListaDeMigracionesDelSistema_FailClosed()
    {
        var verdict = RestoreSchemaVerdictRules.Evaluate(new List<string>(), Conjunto("20260101000000_A"), liveHasPendingMigrations: false);

        Assert.Equal(RestoreSchemaVerdict.CouldNotDetermine, verdict);
        Assert.Equal(BackupVersionStates.Desconocida, RestoreSchemaVerdictRules.ToVersionState(verdict));
    }

    // ============================================================================================
    // Filas HUÉRFANAS del historial (bug real de producción, 2026-07-30)
    // ============================================================================================

    /// <summary>
    /// El id huérfano REAL que dejó al dueño sin poder restaurar nada: ese día la migración se regeneró como
    /// <c>20260216191956_AddAttachmentsTable</c> (esa sí está en el código) y la fila vieja quedó anotada en la
    /// base de producción. Como todos los resguardos salen de ahí, todos la traen.
    /// </summary>
    private const string HuerfanaReal = "20260216190818_AddAttachmentsTable";

    [Fact]
    public void ResguardoConUnaFilaHuerfanaVieja_YElRestoIgual_SigueSiendoLaVersionActual()
    {
        var dump = Conjunto(SistemaHoy.Append(HuerfanaReal).ToArray());

        var verdict = RestoreSchemaVerdictRules.Evaluate(
            SistemaHoy, dump, liveHasPendingMigrations: false, out var huerfanasToleradas);

        Assert.Equal(RestoreSchemaVerdict.Identical, verdict);
        Assert.Equal(BackupVersionStates.Actual, RestoreSchemaVerdictRules.ToVersionState(verdict));
        Assert.Equal(new[] { HuerfanaReal }, huerfanasToleradas);
        // La huérfana no es del sistema, así que no cuenta como "le falta aplicar algo".
        Assert.Equal(0, RestoreSchemaVerdictRules.CountMissingMigrations(SistemaHoy, dump));
    }

    [Fact]
    public void ResguardoViejoConUnaFilaHuerfana_SigueSiendoDeVersionAnteriorYSePuedeActualizarSolo()
    {
        var dump = Conjunto(SistemaHoy[0], SistemaHoy[1], SistemaHoy[2], HuerfanaReal);

        var verdict = RestoreSchemaVerdictRules.Evaluate(SistemaHoy, dump, liveHasPendingMigrations: false);

        Assert.Equal(RestoreSchemaVerdict.SubsetNeedsUpdate, verdict);
        Assert.Equal(2, RestoreSchemaVerdictRules.CountMissingMigrations(SistemaHoy, dump));
        Assert.Equal(BackupVersionStates.Anterior, RestoreSchemaVerdictRules.ToVersionState(verdict));
    }

    [Fact]
    public void ResguardoConHuerfanaVieja_PeroConAgujeroEnElMedio_SigueSiendoHistorialConAgujero()
    {
        // Tolerar la huérfana no puede tapar el problema de verdad: falta la 2da de la fila.
        var dump = Conjunto(SistemaHoy[0], SistemaHoy[2], SistemaHoy[3], HuerfanaReal);

        var verdict = RestoreSchemaVerdictRules.Evaluate(SistemaHoy, dump, liveHasPendingMigrations: false);

        Assert.Equal(RestoreSchemaVerdict.HistoryGap, verdict);
    }

    [Fact]
    public void ResguardoConUnaMigracionPOSTERIORALaUltimaDelSistema_SigueSiendoDeVersionMasNueva()
    {
        // El peligro REAL que la tolerancia de huérfanas no debe ablandar: esquema de una versión más nueva.
        // Va mezclado con una huérfana vieja para fijar que gana el caso peligroso.
        var dump = Conjunto(SistemaHoy.Append(HuerfanaReal).Append("20260801000000_AlgoDelFuturo").ToArray());

        var verdict = RestoreSchemaVerdictRules.Evaluate(SistemaHoy, dump, liveHasPendingMigrations: false);

        Assert.Equal(RestoreSchemaVerdict.NewerThanSystem, verdict);
        Assert.Equal(BackupVersionStates.Posterior, RestoreSchemaVerdictRules.ToVersionState(verdict));
    }

    [Fact]
    public void ResguardoConUnaFilaDesconocidaSinFechaLegible_NoSePuedeDeterminar()
    {
        // Sin fecha no se puede DEMOSTRAR que sea vieja ⇒ nunca se acepta a la fuerza: se avisa honestamente.
        var dump = Conjunto(SistemaHoy.Append("SinFechaAlPrincipio_LoQueSea").ToArray());

        var verdict = RestoreSchemaVerdictRules.Evaluate(SistemaHoy, dump, liveHasPendingMigrations: false);

        Assert.Equal(RestoreSchemaVerdict.CouldNotDetermine, verdict);
        Assert.Equal(BackupVersionStates.Desconocida, RestoreSchemaVerdictRules.ToVersionState(verdict));
    }

    [Fact]
    public void ResguardoQueSoloTraeFilasQueElSistemaNoConoce_NoSePuedeDeterminar()
    {
        // Trae historial, pero nada reconocible: no hay con qué ubicar de qué versión es.
        var verdict = RestoreSchemaVerdictRules.Evaluate(
            SistemaHoy, Conjunto(HuerfanaReal), liveHasPendingMigrations: false);

        Assert.Equal(RestoreSchemaVerdict.CouldNotDetermine, verdict);
        Assert.Equal(BackupVersionStates.Desconocida, RestoreSchemaVerdictRules.ToVersionState(verdict));
    }

    // ============================================================================================
    // Lectura BARATA del historial de un dump (D5): parseo del texto que imprime pg_restore
    // ============================================================================================

    [Fact]
    public void LecturaBarata_ParseaElBloqueCopyDeUnDumpReal()
    {
        // Forma exacta que imprime "pg_restore --data-only --table=__EFMigrationsHistory -f -".
        const string salidaDePgRestore = """
            --
            -- PostgreSQL database dump
            --
            SET statement_timeout = 0;

            COPY public."__EFMigrationsHistory" ("MigrationId", "ProductVersion") FROM stdin;
            20260322010000_AddOperationalFinanceAndTreasury	8.0.13
            20260325003000_AddRefreshTokens	8.0.13
            \.

            --
            -- PostgreSQL database dump complete
            --
            """;

        var ids = PgDatabaseRestorePort.ParseMigrationIdsFromDumpText(salidaDePgRestore);

        Assert.Equal(2, ids.Count);
        Assert.Contains("20260322010000_AddOperationalFinanceAndTreasury", ids);
        Assert.Contains("20260325003000_AddRefreshTokens", ids);
    }

    [Fact]
    public void LecturaBarata_TambienEntiendeLaVarianteConInserts()
    {
        const string salidaConInserts = """
            INSERT INTO public."__EFMigrationsHistory" VALUES ('20260530120000_AddRateFuzzyMatching', '8.0.13');
            INSERT INTO public."__EFMigrationsHistory" VALUES ('20260717090000_Adr048_M2', '8.0.13');
            """;

        var ids = PgDatabaseRestorePort.ParseMigrationIdsFromDumpText(salidaConInserts);

        Assert.Equal(2, ids.Count);
        Assert.Contains("20260530120000_AddRateFuzzyMatching", ids);
    }

    [Fact]
    public void LecturaBarata_ConTextoInesperado_DevuelveVacioYNuncaTira()
    {
        // Parsear la salida de pg_restore ya nos falló una vez (los nombres del índice venían SIN comillas). Acá
        // un formato inesperado tiene que degradar a "no sé", nunca a una excepción ni a un falso "compatible".
        Assert.Empty(PgDatabaseRestorePort.ParseMigrationIdsFromDumpText("pg_restore: error: could not open input file"));
        Assert.Empty(PgDatabaseRestorePort.ParseMigrationIdsFromDumpText(string.Empty));
        Assert.Empty(PgDatabaseRestorePort.ParseMigrationIdsFromDumpText("COPY public.\"OtraTabla\" (\"Id\") FROM stdin;\n1\n\\.\n"));
    }
}
