using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TravelApi.Application.DTOs;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-052 (D5): la lista de resguardos con su marca de versión. Se prueba SIN Postgres y SIN los binarios de
/// <c>pg_restore</c>:
/// <list type="bullet">
///   <item>La lista de migraciones del sistema sale del ENSAMBLADO (<c>Database.GetMigrations()</c>), que no toca la
///   base — por eso alcanza un <c>AppDbContext</c> apuntado a una connection string que nunca se abre.</item>
///   <item>La lectura del historial de cada archivo es un <c>protected virtual</c>, así que la subclase de prueba
///   cuenta cuántas veces se lee DE VERDAD cada archivo (que es justo lo que la caché tiene que evitar).</item>
/// </list>
/// </summary>
public class PgDatabaseRestorePortListingTests : IDisposable
{
    private readonly string _backupDirectory;

    public PgDatabaseRestorePortListingTests()
    {
        _backupDirectory = Path.Combine(Path.GetTempPath(), "adr052-listado-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_backupDirectory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_backupDirectory, recursive: true);
        }
        catch
        {
            // Basura en el temp del sistema operativo: nunca puede hacer fallar un test.
        }
    }

    /// <summary>
    /// Contexto con proveedor RELACIONAL pero sin servidor: <c>GetMigrations()</c> lee la lista compilada en el
    /// ensamblado, no la base, así que nunca se abre una conexión.
    /// </summary>
    private static AppDbContext NewRelationalContextWithoutServer() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Port=1;Database=no_existe;Username=x;Password=y")
            .Options);

    private string WriteFakeDump(string fileName, string content)
    {
        var path = Path.Combine(_backupDirectory, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>Puerto con la lectura del historial reemplazada por un contador (y un resultado configurable por archivo).</summary>
    private sealed class CountingPort : PgDatabaseRestorePort
    {
        private readonly Func<string, ISet<string>?> _historyByPath;

        public CountingPort(
            IConfiguration configuration, AppDbContext context, IMemoryCache cache, Func<string, ISet<string>?> historyByPath)
            : base(configuration, context, cache, NullLogger<PgDatabaseRestorePort>.Instance)
        {
            _historyByPath = historyByPath;
        }

        public Dictionary<string, int> ReadsByFile { get; } = new(StringComparer.Ordinal);

        protected override Task<ISet<string>?> TryReadDumpMigrationIdsAsync(string fullPath, CancellationToken ct)
        {
            var name = Path.GetFileName(fullPath);
            ReadsByFile[name] = ReadsByFile.TryGetValue(name, out var current) ? current + 1 : 1;
            return Task.FromResult(_historyByPath(name));
        }
    }

    private CountingPort NewCountingPort(AppDbContext context, Func<string, ISet<string>?> historyByPath)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Wipe:BackupDirectory"] = _backupDirectory })
            .Build();

        return new CountingPort(configuration, context, new MemoryCache(new MemoryCacheOptions()), historyByPath);
    }

    [Fact]
    public async Task Listado_DosVecesSeguidas_NoVuelveALeerElArchivo_YSiCambiaElArchivoSiLoRelee()
    {
        // La clave de caché es (nombre, tamaño, fecha de modificación): los dumps son inmutables una vez escritos,
        // así que se auto-invalida sola y no necesita TTL. Este test fija ese contrato.
        await using var context = NewRelationalContextWithoutServer();
        var primeraMigracion = context.Database.GetMigrations().First();

        WriteFakeDump("wipe-20260729-120000.dump", "contenido original");
        var port = NewCountingPort(context, _ => new HashSet<string> { primeraMigracion });

        await port.ListBackupsAsync(CancellationToken.None);
        await port.ListBackupsAsync(CancellationToken.None);

        Assert.Equal(1, port.ReadsByFile["wipe-20260729-120000.dump"]);

        // Cambiar el archivo (tamaño + fecha) invalida la clave: se vuelve a leer.
        WriteFakeDump("wipe-20260729-120000.dump", "contenido original MÁS LARGO, otra fecha");
        File.SetLastWriteTimeUtc(
            Path.Combine(_backupDirectory, "wipe-20260729-120000.dump"), DateTime.UtcNow.AddSeconds(5));

        await port.ListBackupsAsync(CancellationToken.None);

        Assert.Equal(2, port.ReadsByFile["wipe-20260729-120000.dump"]);
    }

    [Fact]
    public async Task Listado_ConHistorialIlegible_MarcaDesconocidaYNuncaActual()
    {
        // Degradación honesta: si no se pudo leer el historial, el archivo va como "desconocida". JAMÁS "actual"
        // (eso sería habilitar a ciegas la operación más destructiva del sistema).
        await using var context = NewRelationalContextWithoutServer();
        WriteFakeDump("resguardo-ilegible.dump", "esto no es un dump de Postgres");
        var port = NewCountingPort(context, _ => null);

        var backups = await port.ListBackupsAsync(CancellationToken.None);

        var soloArchivo = Assert.Single(backups);
        Assert.Equal(BackupVersionStates.Desconocida, soloArchivo.VersionState);
        // T-5: la marca es UNA palabra del contrato — ni ids de migración ni conteos viajan en la lista.
        Assert.Contains(soloArchivo.VersionState, BackupVersionStates.All);
    }

    [Fact]
    public async Task Listado_ConElMismoHistorialQueElSistema_MarcaActual_YConUnSubconjuntoFinal_MarcaAnterior()
    {
        await using var context = NewRelationalContextWithoutServer();
        var migracionesDelSistema = context.Database.GetMigrations().ToList();

        WriteFakeDump("wipe-al-dia.dump", "x");
        WriteFakeDump("wipe-viejo.dump", "x");

        var port = NewCountingPort(context, nombre => nombre == "wipe-al-dia.dump"
            ? new HashSet<string>(migracionesDelSistema)
            : new HashSet<string>(migracionesDelSistema.Take(migracionesDelSistema.Count - 1)));

        var backups = await port.ListBackupsAsync(CancellationToken.None);

        Assert.Equal(BackupVersionStates.Actual, backups.Single(b => b.FileName == "wipe-al-dia.dump").VersionState);
        Assert.Equal(BackupVersionStates.Anterior, backups.Single(b => b.FileName == "wipe-viejo.dump").VersionState);
    }

    /// <summary>
    /// Recomendación N1 de seguridad (re-review): el nombre del resguardo termina como argumento ENTRECOMILLADO de
    /// <c>pg_restore</c>. La lista blanca es lo que impide que un nombre cierre ese entrecomillado y agregue flags al
    /// comando. Se prueba la regla compartida por el servicio y el puerto.
    /// </summary>
    [Theory]
    [InlineData("wipe-20260727-223313.dump", true)]
    [InlineData("pre-restore-20260728-090000.dump", true)]
    [InlineData("backup_manual.2026.dump", true)]
    [InlineData("wipe\" --clean --dbname=travel x.dump", false)]   // cerraría las comillas y metería flags
    [InlineData("wipe';DROP DATABASE travel;--.dump", false)]
    [InlineData("../../etc/passwd.dump", false)]
    [InlineData("carpeta/wipe.dump", false)]
    [InlineData("wipe.dump.txt", false)]
    [InlineData("wipe .dump", false)]                                // el espacio parte el argumento
    [InlineData("$(rm -rf /).dump", false)]
    [InlineData(".dump", false)]
    [InlineData("", false)]
    public void NombreDeResguardo_SoloPasaLaListaBlanca(string fileName, bool esperado)
    {
        Assert.Equal(esperado, SafeBackupFileNameRules.IsSafe(fileName));
        // El servicio usa la MISMA regla (no dos definiciones distintas de "nombre seguro").
        Assert.Equal(esperado, SystemDataRestoreService.IsSafeFileName(fileName));
    }

    [Fact]
    public async Task Listado_SinDirectorioDeResguardos_DevuelveListaVaciaSinTirar()
    {
        await using var context = NewRelationalContextWithoutServer();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Wipe:BackupDirectory"] = Path.Combine(_backupDirectory, "no-existe"),
            })
            .Build();
        var port = new PgDatabaseRestorePort(
            configuration, context, new MemoryCache(new MemoryCacheOptions()), NullLogger<PgDatabaseRestorePort>.Instance);

        var backups = await port.ListBackupsAsync(CancellationToken.None);

        Assert.Empty(backups);
    }
}
