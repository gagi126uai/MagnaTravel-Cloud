using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TravelApi.Infrastructure.Services;
using TravelApi.Tests.Fixtures;
using Xunit;

namespace TravelApi.Tests.Integration;

/// <summary>
/// ADR-052 (D1.4, D4 y condición C1 de la re-review): prueba, contra un PostgreSQL REAL, la parte más peligrosa
/// de la obra — el intercambio de nombres de bases y su vuelta atrás.
///
/// <para><b>Qué se prueba con SQL real y por qué</b>: renombrar bases, <c>ALLOW_CONNECTIONS</c> y la
/// reconciliación contra <c>pg_database</c> son comportamientos de Postgres que ningún mock puede imitar
/// honestamente. La invariante que más importa es la del riesgo NUEVO de esta obra: <b>ante CUALQUIER fallo, la
/// base que tiene el nombre vivo termina aceptando conexiones</b> — si eso se rompiera, el sistema quedaría
/// muerto para todos con los datos intactos.</para>
///
/// <para><b>Qué NO se prueba acá</b>: el <c>pg_restore</c> real. Igual que en el resto de esta obra, los binarios
/// de <c>postgresql-client</c> se prueban por construcción y en producción (además, en CI el cliente disponible
/// puede ser de una versión distinta a la del servidor de Testcontainers y no podría leer un dump del server 16).
/// Por eso la "base restaurada" de estos tests se crea con <c>CREATE DATABASE</c> + una tabla marca.</para>
///
/// <para><b>Cuidado al agregar tests acá</b>: cada test renombra la base VIVA del contenedor de esta clase, así
/// que tiene que dejar el nombre vivo apuntando a la base original antes de terminar (los helpers lo hacen). xunit
/// corre los tests de una misma clase en SERIE, así que no se pisan entre sí.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class PgDatabaseRestorePortSwapIntegrationTests : IClassFixture<PostgresIntegrationFixture>
{
    private readonly PostgresIntegrationFixture _fixture;

    public PgDatabaseRestorePortSwapIntegrationTests(PostgresIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    private string LiveDatabaseName => new NpgsqlConnectionStringBuilder(_fixture.ConnectionString).Database!;

    private PgDatabaseRestorePort NewPort()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = _fixture.ConnectionString,
                // Reintentos cortos: en estos tests el fallo es determinístico (la base destino no existe), así
                // que esperar 2s por intento solo haría el test lento.
                ["Wipe:SwapRetries"] = "2",
                ["Wipe:SwapRetryDelaySeconds"] = "0",
                ["Wipe:RollbackSwapRetries"] = "2",
                ["Wipe:RollbackSwapRetryDelaySeconds"] = "0",
            })
            .Build();

        return new PgDatabaseRestorePort(
            configuration,
            _fixture.CreateDbContext(),
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<PgDatabaseRestorePort>.Instance);
    }

    /// <summary>
    /// TODAS las conexiones que abre ESTE test van SIN POOL, a propósito.
    ///
    /// <para><b>Por qué</b>: el pool de Npgsql guarda conexiones FÍSICAS por connection string. Después de un
    /// <c>RENAME</c>, una conexión guardada bajo "Database=travel_tests" sigue enchufada a la base física que TENÍA
    /// ese nombre cuando se abrió (ahora <c>travel_tests_old_*</c>). Reusarla haría leer la base equivocada y el test
    /// pasaría o fallaría según el timing — la definición de flaky. El motor hace <c>ClearAllPools()</c> de lo suyo
    /// (y es global al proceso, así que también limpia lo del test), pero no hay que depender de ESE detalle para
    /// que un test diga la verdad: sin pool, cada consulta resuelve el nombre en el momento.</para>
    /// </summary>
    private string UnpooledConnectionStringFor(string databaseName) =>
        new NpgsqlConnectionStringBuilder(_fixture.ConnectionString)
        {
            Database = databaseName,
            Pooling = false,
        }.ConnectionString;

    private async Task<NpgsqlConnection> OpenUnpooledAsync(string databaseName)
    {
        var connection = new NpgsqlConnection(UnpooledConnectionStringFor(databaseName));
        await connection.OpenAsync();
        return connection;
    }

    /// <summary>Conexión a la base de mantenimiento ("postgres"), que existe siempre y nunca se renombra.</summary>
    private Task<NpgsqlConnection> OpenMaintenanceConnectionAsync() => OpenUnpooledAsync("postgres");

    /// <summary>Crea una base "restaurada" de mentira, con una tabla marca para poder reconocerla después del intercambio.</summary>
    private async Task<string> CreateFakeRestoredDatabaseAsync(string marker)
    {
        var name = $"{LiveDatabaseName}_restore_20260729{marker}";

        await using var maintenance = await OpenMaintenanceConnectionAsync();
        await ExecuteAsync(maintenance, $"DROP DATABASE IF EXISTS \"{name}\";");
        await ExecuteAsync(maintenance, $"CREATE DATABASE \"{name}\";");

        await using var inNewDatabase = await OpenUnpooledAsync(name);
        await ExecuteAsync(inNewDatabase, $"CREATE TABLE \"MarcaDeLaCopia\" (\"Marca\" text NOT NULL);");
        await ExecuteAsync(inNewDatabase, $"INSERT INTO \"MarcaDeLaCopia\" VALUES ('{marker}');");

        return name;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<bool> DatabaseExistsAsync(NpgsqlConnection connection, string name)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_database WHERE datname = @db);";
        command.Parameters.AddWithValue("db", name);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<bool> AllowsConnectionsAsync(NpgsqlConnection connection, string name)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT datallowconn FROM pg_database WHERE datname = @db;";
        command.Parameters.AddWithValue("db", name);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// Devuelve la marca de la base que TIENE EL NOMBRE VIVO ahora mismo, o <c>null</c> si esa base no tiene tabla
    /// marca (o sea: es la base ORIGINAL de la app).
    ///
    /// <para><b>Trampa de Postgres que hizo fallar este test en CI</b> (2026-07-29): la versión anterior consultaba
    /// <c>SELECT (SELECT "Marca" FROM "MarcaDeLaCopia" ...) WHERE EXISTS (... information_schema ...)</c> creyendo que
    /// el <c>WHERE</c> "protegía" del caso "la tabla no existe". NO protege: Postgres RESUELVE los nombres de tablas
    /// al PARSEAR la sentencia, antes de ejecutar nada, así que si la tabla no existe tira <c>42P01</c> igual. Por eso
    /// la existencia se pregunta en una sentencia APARTE con <c>to_regclass</c> (que recibe el nombre como TEXTO y
    /// devuelve NULL si no existe, sin parsear ninguna referencia a la tabla).</para>
    /// </summary>
    private async Task<string?> ReadLiveMarkerAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await using var connection = await OpenUnpooledAsync(LiveDatabaseName);

        await using (var exists = connection.CreateCommand())
        {
            exists.CommandText = """SELECT to_regclass('public."MarcaDeLaCopia"') IS NOT NULL;""";
            var hasMarkerTable = (bool)(await exists.ExecuteScalarAsync())!;
            if (!hasMarkerTable)
            {
                return null;
            }
        }

        await using var read = connection.CreateCommand();
        read.CommandText = """SELECT "Marca" FROM "MarcaDeLaCopia" LIMIT 1;""";
        return await read.ExecuteScalarAsync() as string;
    }

    /// <summary>
    /// Deja el contenedor como estaba: la base ORIGINAL (la que tiene el esquema de la app, sin tabla marca) con
    /// el nombre vivo, y sin sobras. Se llama al final de cada test.
    /// </summary>
    private async Task RestoreOriginalLayoutAsync(string originalDatabaseParkedAs)
    {
        NpgsqlConnection.ClearAllPools();
        await using var maintenance = await OpenMaintenanceConnectionAsync();

        if (await DatabaseExistsAsync(maintenance, originalDatabaseParkedAs))
        {
            await ExecuteAsync(maintenance, $"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{LiveDatabaseName}';");
            if (await DatabaseExistsAsync(maintenance, LiveDatabaseName))
            {
                await ExecuteAsync(maintenance, $"DROP DATABASE IF EXISTS \"{LiveDatabaseName}\";");
            }

            await ExecuteAsync(maintenance, $"ALTER DATABASE \"{originalDatabaseParkedAs}\" RENAME TO \"{LiveDatabaseName}\";");
        }

        await ExecuteAsync(maintenance, $"ALTER DATABASE \"{LiveDatabaseName}\" WITH ALLOW_CONNECTIONS true;");
        NpgsqlConnection.ClearAllPools();
    }

    private async Task DropIfExistsAsync(params string[] names)
    {
        await using var maintenance = await OpenMaintenanceConnectionAsync();
        foreach (var name in names)
        {
            await ExecuteAsync(maintenance, $"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{name}';");
            await ExecuteAsync(maintenance, $"DROP DATABASE IF EXISTS \"{name}\";");
        }
    }

    [Fact]
    public async Task Intercambio_CicloFeliz_LaAppSigueLeyendoConLaMISMAConnectionStringYLaBaseVivaAceptaConexiones()
    {
        var port = NewPort();
        var newDatabase = await CreateFakeRestoredDatabaseAsync("aa0001");
        string previous = string.Empty;

        try
        {
            var swap = await port.SwapRestoredDatabaseIntoLiveAsync(newDatabase, CancellationToken.None);
            previous = swap.PreviousDatabaseName;

            Assert.True(swap.Success, swap.ErrorMessage);

            // La razón de ser del intercambio: el NOMBRE vivo no cambió, así que la app sigue conectándose igual.
            Assert.Equal("aa0001", await ReadLiveMarkerAsync());

            await using var maintenance = await OpenMaintenanceConnectionAsync();
            Assert.False(await DatabaseExistsAsync(maintenance, newDatabase)); // ya no existe con su nombre viejo
            Assert.True(await DatabaseExistsAsync(maintenance, previous));     // la original quedó estacionada
            Assert.True(await AllowsConnectionsAsync(maintenance, LiveDatabaseName));
        }
        finally
        {
            await RestoreOriginalLayoutAsync(previous);
            await DropIfExistsAsync(newDatabase);
        }
    }

    [Fact]
    public async Task Intercambio_CuandoFalla_ElFinallyDejaLaBaseVivaACEPTANDOConexiones()
    {
        // EL test que pidió el reviewer: es el ÚNICO camino que, mal hecho, deja el sistema muerto para todos con
        // los datos sanos. Se fuerza el fallo pidiendo el intercambio contra una base que NO existe: el puerto
        // apaga ALLOW_CONNECTIONS, estaciona la original, no encuentra la nueva, agota los reintentos, DESHACE lo
        // que había hecho y —lo que importa— vuelve a prender ALLOW_CONNECTIONS sobre el nombre vivo.
        var port = NewPort();
        var baseQueNoExiste = $"{LiveDatabaseName}_restore_20260729zzzzzz";
        await DropIfExistsAsync(baseQueNoExiste);

        var swap = await port.SwapRestoredDatabaseIntoLiveAsync(baseQueNoExiste, CancellationToken.None);

        try
        {
            Assert.False(swap.Success);

            await using var maintenance = await OpenMaintenanceConnectionAsync();
            Assert.True(await DatabaseExistsAsync(maintenance, LiveDatabaseName));
            Assert.True(
                await AllowsConnectionsAsync(maintenance, LiveDatabaseName),
                "INVARIANTE CRÍTICA de ADR-052: ante cualquier fallo del intercambio, la base con el nombre vivo tiene " +
                "que quedar aceptando conexiones. Si esto falla, el sistema queda inaccesible con los datos intactos.");

            // Y la base original volvió al nombre vivo: no quedó estacionada bajo el nombre "old".
            Assert.False(await DatabaseExistsAsync(maintenance, swap.PreviousDatabaseName));

            // Prueba final de que el sistema sigue usable: se puede abrir una conexión NUEVA con el MISMO nombre de
            // base y hacer una consulta. Sin pool (ver UnpooledConnectionStringFor): con pool, esta aserción podría
            // pasar reusando una conexión vieja incluso si el nombre vivo estuviera roto — o sea, mintiendo.
            NpgsqlConnection.ClearAllPools();
            await using var live = await OpenUnpooledAsync(LiveDatabaseName);
            await using var ping = live.CreateCommand();
            ping.CommandText = "SELECT 1;";
            Assert.Equal(1, Convert.ToInt32(await ping.ExecuteScalarAsync()));
        }
        finally
        {
            // Recomendación N6 de backend (re-review): si UNA aserción falla a mitad de camino, este test no puede
            // dejar el contenedor con el nombre vivo apuntando a otra base — los tests siguientes de la clase
            // fallarían por contaminación y el diagnóstico apuntaría al lugar equivocado.
            await RestoreOriginalLayoutAsync(swap.PreviousDatabaseName);
            await DropIfExistsAsync(baseQueNoExiste);
        }
    }

    [Fact]
    public async Task VueltaAtras_DespuesDeUnIntercambioExitoso_DevuelveLaBaseOriginalAlNombreVivoYConservaLaFallida()
    {
        var port = NewPort();
        var newDatabase = await CreateFakeRestoredDatabaseAsync("bb0002");
        string previous = string.Empty;

        try
        {
            var swap = await port.SwapRestoredDatabaseIntoLiveAsync(newDatabase, CancellationToken.None);
            previous = swap.PreviousDatabaseName;
            Assert.True(swap.Success, swap.ErrorMessage);
            Assert.Equal("bb0002", await ReadLiveMarkerAsync());

            var rollback = await port.RollbackSwapAsync(previous, CancellationToken.None);

            Assert.True(rollback.Success, rollback.ErrorMessage);

            // La base ORIGINAL volvió al nombre vivo (no tiene tabla marca) y la del intento fallido se conserva
            // para diagnóstico bajo el nombre "_fallido_".
            Assert.Null(await ReadLiveMarkerAsync());

            await using var maintenance = await OpenMaintenanceConnectionAsync();
            Assert.False(await DatabaseExistsAsync(maintenance, previous));
            Assert.True(await AllowsConnectionsAsync(maintenance, LiveDatabaseName));

            var fallidas = await ListDatabasesLikeAsync(maintenance, $"{LiveDatabaseName}_fallido_%");
            Assert.NotEmpty(fallidas);
        }
        finally
        {
            // ORDEN IMPORTANTE: primero se devuelve el nombre vivo a la base ORIGINAL (no-op si la vuelta atrás ya
            // corrió), y DESPUÉS se limpia. Al revés, si una aserción hubiera fallado antes de la vuelta atrás, la
            // limpieza dropearía la base original (que estaría estacionada como "_old_") y se llevaría puestos todos
            // los tests siguientes de la clase.
            await RestoreOriginalLayoutAsync(previous);
            await port.CleanupLeftoverRestoreDatabasesAsync(CancellationToken.None);
            await DropIfExistsAsync(newDatabase);
            NpgsqlConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task VueltaAtras_SiLeDanElNombreDeLaBaseVIVA_NoLaRenombraAFallido()
    {
        // Recomendación N4 de seguridad (guard espejo): un caller futuro que pase mal el nombre no puede dejar el
        // sistema sin base viva. Fail-closed: no toca nada y devuelve "no pude" (el caller lo trata como doble fallo,
        // que deja el sistema frenado y avisando, en vez de reabrirlo sobre la nada).
        var port = NewPort();

        var rollback = await port.RollbackSwapAsync(LiveDatabaseName, CancellationToken.None);

        Assert.False(rollback.Success);

        await using var maintenance = await OpenMaintenanceConnectionAsync();
        Assert.True(await DatabaseExistsAsync(maintenance, LiveDatabaseName));
        Assert.True(await AllowsConnectionsAsync(maintenance, LiveDatabaseName));
        Assert.Empty(await ListDatabasesLikeAsync(maintenance, $"{LiveDatabaseName}_fallido_%"));
    }

    [Fact]
    public async Task VueltaAtras_CuandoLaBaseOriginalYATieneElNombreVivo_NoHACENADA()
    {
        // Condición C1: reconciliación POR ESTADO e IDEMPOTENTE. Este es el caso que hace SEGURO llamar a la
        // vuelta atrás "por las dudas": si el intercambio nunca llegó a hacerse, no tiene que tocar NADA. Una
        // secuencia ciega de pasos, en cambio, renombraría la base BUENA a "_fallido_" y dejaría el sistema sin
        // base viva — la catástrofe que este test previene.
        var port = NewPort();
        var nombreQueNoExiste = $"{LiveDatabaseName}_old_20260729000000";
        await DropIfExistsAsync(nombreQueNoExiste);

        var rollback = await port.RollbackSwapAsync(nombreQueNoExiste, CancellationToken.None);

        Assert.True(rollback.Success);

        await using var maintenance = await OpenMaintenanceConnectionAsync();
        Assert.True(await DatabaseExistsAsync(maintenance, LiveDatabaseName));
        Assert.True(await AllowsConnectionsAsync(maintenance, LiveDatabaseName));
        Assert.Empty(await ListDatabasesLikeAsync(maintenance, $"{LiveDatabaseName}_fallido_%"));
    }

    [Fact]
    public async Task AssertDePrivilegios_ConElUsuarioDuenoDeLaBase_Habilita()
    {
        // El usuario que crea la imagen oficial de Postgres es superusuario y dueño de la base: el assert tiene
        // que decir que SÍ. Lo importante que verifica este test es que la consulta de privilegios (superusuario +
        // rolcreatedb + PROPIEDAD, condición C1) corre de verdad contra Postgres sin errores de SQL.
        var port = NewPort();

        var result = await port.CheckDatabaseManagementPrivilegesAsync(CancellationToken.None);

        Assert.True(result.CanManage, result.ErrorMessage);
    }

    [Fact]
    public async Task LimpiezaDeSobras_DropeaLasBasesDeIntentosAnterioresYNoTocaLaViva()
    {
        var port = NewPort();
        var sobraRestore = $"{LiveDatabaseName}_restore_20260101000000";
        var sobraVieja = $"{LiveDatabaseName}_old_20260101000000";
        var sobraFallida = $"{LiveDatabaseName}_fallido_20260101000000";

        await using (var maintenance = await OpenMaintenanceConnectionAsync())
        {
            foreach (var name in new[] { sobraRestore, sobraVieja, sobraFallida })
            {
                await ExecuteAsync(maintenance, $"DROP DATABASE IF EXISTS \"{name}\";");
                await ExecuteAsync(maintenance, $"CREATE DATABASE \"{name}\";");
            }
        }

        await port.CleanupLeftoverRestoreDatabasesAsync(CancellationToken.None);

        await using var verify = await OpenMaintenanceConnectionAsync();
        Assert.False(await DatabaseExistsAsync(verify, sobraRestore));
        Assert.False(await DatabaseExistsAsync(verify, sobraVieja));
        Assert.False(await DatabaseExistsAsync(verify, sobraFallida));
        Assert.True(await DatabaseExistsAsync(verify, LiveDatabaseName));
    }

    [Fact]
    public async Task DropDatabaseAsync_NuncaDropeaLaBaseVIVA()
    {
        // Candado de seguridad del puerto: ni un bug del caller puede hacer que se dropee la base viva.
        var port = NewPort();

        await port.DropDatabaseAsync(LiveDatabaseName, CancellationToken.None);

        await using var maintenance = await OpenMaintenanceConnectionAsync();
        Assert.True(await DatabaseExistsAsync(maintenance, LiveDatabaseName));
    }

    private static async Task<List<string>> ListDatabasesLikeAsync(NpgsqlConnection connection, string pattern)
    {
        var names = new List<string>();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT datname FROM pg_database WHERE datname LIKE @pattern;";
        command.Parameters.AddWithValue("pattern", pattern);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }
}
