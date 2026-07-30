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

    private async Task<NpgsqlConnection> OpenMaintenanceConnectionAsync()
    {
        var builder = new NpgsqlConnectionStringBuilder(_fixture.ConnectionString) { Database = "postgres" };
        var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    /// <summary>Crea una base "restaurada" de mentira, con una tabla marca para poder reconocerla después del intercambio.</summary>
    private async Task<string> CreateFakeRestoredDatabaseAsync(string marker)
    {
        var name = $"{LiveDatabaseName}_restore_20260729{marker}";

        await using var maintenance = await OpenMaintenanceConnectionAsync();
        await ExecuteAsync(maintenance, $"DROP DATABASE IF EXISTS \"{name}\";");
        await ExecuteAsync(maintenance, $"CREATE DATABASE \"{name}\";");

        var builder = new NpgsqlConnectionStringBuilder(_fixture.ConnectionString) { Database = name };
        await using var inNewDatabase = new NpgsqlConnection(builder.ConnectionString);
        await inNewDatabase.OpenAsync();
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

    /// <summary>Devuelve la marca de la base que TIENE EL NOMBRE VIVO ahora mismo, o null si esa base no tiene tabla marca.</summary>
    private async Task<string?> ReadLiveMarkerAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT (SELECT "Marca" FROM "MarcaDeLaCopia" LIMIT 1)
            WHERE EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'MarcaDeLaCopia');
            """;
        var result = await command.ExecuteScalarAsync();
        return result as string;
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

            // Prueba final de que el sistema sigue usable: se puede abrir una conexión nueva con la MISMA connection string.
            NpgsqlConnection.ClearAllPools();
            await using var ctx = _fixture.CreateDbContext();
            Assert.True(await ctx.Database.CanConnectAsync());
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
