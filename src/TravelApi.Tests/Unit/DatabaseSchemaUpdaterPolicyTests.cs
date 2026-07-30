using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.Interfaces;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-052 (D3, cierra B4): la parte NUEVA y peligrosa de <see cref="DatabaseSchemaUpdater"/> no es la secuencia
/// (que es la misma de siempre, movida de <c>Program.cs</c>) sino la POLÍTICA: cuántas veces se reintenta y qué
/// se tolera. Y eso es exactamente lo que decide si una restauración deja la plata derivada en cero o vuelve
/// atrás.
///
/// <para><b>Por qué con subclases de prueba y no contra Postgres real</b>: el historial de migraciones de este
/// repo NO se puede aplicar desde una base vacía (ver el comentario de <c>PostgresIntegrationFixture</c>: la
/// primera migración crea <c>Reservas</c>, que en producción se renombró a mano a <c>TravelFiles</c>). Los tres
/// pasos son <c>protected virtual</c> justamente para poder verificar la política sin depender de eso.</para>
/// </summary>
public class DatabaseSchemaUpdaterPolicyTests
{
    private static AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>
    /// Config de prueba con la espera entre reintentos en CERO: lo que se verifica acá es CUÁNTAS veces reintenta,
    /// no cuánto espera (esperar 5 segundos de verdad por intento haría el test inútilmente lento).
    /// </summary>
    private static IConfiguration ConfigurationSinEspera() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Wipe:MigrateRetryDelaySeconds"] = "0" })
            .Build();

    /// <summary>
    /// Doble de prueba. Reemplaza los bootstrappers y las migraciones (SQL crudo contra Postgres, imposible acá) pero
    /// NO la lógica de tolerancia: cuando <c>backfillsFail</c> está prendido, lo que falla es UN BACKFILL CONCRETO
    /// (<see cref="DatabaseSchemaUpdater.RunMultiCurrencyBackfillAsync"/>), así que el <c>RunBackfillsAsync</c> REAL
    /// —el que decide si se traga el fallo o no— es el que corre. Antes esta clase reimplementaba ese método y el
    /// test terminaba probando su propia copia (hallazgo del reviewer).
    /// </summary>
    private sealed class TestableSchemaUpdater : DatabaseSchemaUpdater
    {
        private readonly bool _migrationsFail;
        private readonly bool _backfillsFail;

        public TestableSchemaUpdater(AppDbContext db, IConfiguration configuration, bool migrationsFail = false, bool backfillsFail = false)
            : base(db, new Mock<ISupplierService>().Object, configuration, NullLoggerFactory.Instance,
                   NullLogger<DatabaseSchemaUpdater>.Instance)
        {
            _migrationsFail = migrationsFail;
            _backfillsFail = backfillsFail;
        }

        public int BootstrapperRuns { get; private set; }
        public int MigrationAttempts { get; private set; }
        public int BackfillRuns { get; private set; }
        public bool? BackfillsWereToldToTolerate { get; private set; }

        protected override Task RunBootstrappersAsync(CancellationToken ct)
        {
            BootstrapperRuns++;
            return Task.CompletedTask;
        }

        protected override Task<int> ApplyMigrationsAsync(CancellationToken ct)
        {
            MigrationAttempts++;
            if (_migrationsFail)
            {
                throw new InvalidOperationException("42P01: relation \"Payments\" does not exist");
            }

            return Task.FromResult(3);
        }

        protected override Task<bool> RunBackfillsAsync(bool toleratesFailure, CancellationToken ct)
        {
            BackfillRuns++;
            BackfillsWereToldToTolerate = toleratesFailure;

            // Se llama a la implementación REAL (la de la clase base): es la que decide tolerar o no.
            return base.RunBackfillsAsync(toleratesFailure, ct);
        }

        protected override Task RunMultiCurrencyBackfillAsync(CancellationToken ct)
        {
            if (_backfillsFail)
            {
                throw new InvalidOperationException("42P01: relation \"ReservaMoneyByCurrency\" does not exist");
            }

            return Task.CompletedTask;
        }

        // Los otros dos no hacen nada: el caso a probar es "UNO falla".
        protected override Task RunCashLedgerBackfillAsync(CancellationToken ct) => Task.CompletedTask;

        protected override Task RunCancellationLinesBackfillAsync(CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public async Task PoliticaDeRestore_UnSoloIntentoYNoSeTragaElFalloDeUnBackfill()
    {
        // B4: en un restore, dejar los saldos por moneda / libro de caja / líneas de cancelación en cero es
        // exactamente el dato silencioso falso que este ERP no puede mostrar. Por eso el paso FALLA y el caller
        // vuelve atrás.
        var updater = new TestableSchemaUpdater(NewContext(), ConfigurationSinEspera(), backfillsFail: true);

        var result = await updater.UpdateAsync(SchemaUpdatePolicy.Restore, CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
        Assert.False(updater.BackfillsWereToldToTolerate);
        Assert.Equal(1, updater.MigrationAttempts);
        Assert.Equal(1, updater.BootstrapperRuns);
    }

    [Fact]
    public async Task PoliticaDeArranque_ToleraElFalloDeUnBackfillYNoAbortaElArranque()
    {
        // Comportamiento HISTÓRICO que se conserva: en el arranque, un backfill que falla se recupera en el
        // próximo deploy; frenar el arranque entero por eso sería peor.
        var updater = new TestableSchemaUpdater(NewContext(), ConfigurationSinEspera(), backfillsFail: true);

        var result = await updater.UpdateAsync(SchemaUpdatePolicy.Startup, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(updater.BackfillsWereToldToTolerate);
        Assert.Equal(3, result.MigrationsApplied);
    }

    [Fact]
    public async Task PoliticaDeRestore_SiFallaLaMigracion_NoReintenta()
    {
        var updater = new TestableSchemaUpdater(NewContext(), ConfigurationSinEspera(), migrationsFail: true);

        var result = await updater.UpdateAsync(SchemaUpdatePolicy.Restore, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(DatabaseSchemaUpdater.RestoreAttempts, updater.MigrationAttempts);
        Assert.Equal(1, updater.MigrationAttempts);
        Assert.Equal(0, updater.BackfillRuns); // si la migración no salió, los backfills no corren
    }

    [Fact]
    public async Task PoliticaDeArranque_SiFallaLaMigracion_ReintentaLasVecesDeSiempre()
    {
        // Los 5 intentos del arranque son lo que evita que un deploy falle porque la base todavía estaba
        // levantando. Acá se verifica el NÚMERO (la espera va en cero, ver ConfigurationSinEspera).
        var updater = new TestableSchemaUpdater(NewContext(), ConfigurationSinEspera(), migrationsFail: true);

        var result = await updater.UpdateAsync(SchemaUpdatePolicy.Startup, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(DatabaseSchemaUpdater.StartupAttempts, updater.MigrationAttempts);
    }

    [Fact]
    public async Task PoliticaDeArranque_NoLePoneTopeDeTiempoNiCambiaElCommandTimeout()
    {
        // BLOQUEANTE 3(a)/(b) de la re-review: esta obra NO puede cambiarle las reglas al arranque. El tope total de
        // tiempo y el CommandTimeout largo son SOLO del restore (donde el presupuesto de mantenimiento obliga a
        // acotar el peor caso). Se verifica que el token que reciben los pasos del arranque NO tenga vencimiento.
        var updater = new SpyingSchemaUpdater(NewContext(), ConfigurationSinEspera());

        await updater.UpdateAsync(SchemaUpdatePolicy.Startup, CancellationToken.None);
        Assert.False(updater.LastTokenCanBeCanceled);

        await updater.UpdateAsync(SchemaUpdatePolicy.Restore, CancellationToken.None);
        Assert.True(updater.LastTokenCanBeCanceled);
    }

    /// <summary>Espía del token que reciben los pasos: sirve para distinguir "sin tope de tiempo" de "con tope".</summary>
    private sealed class SpyingSchemaUpdater : DatabaseSchemaUpdater
    {
        public SpyingSchemaUpdater(AppDbContext db, IConfiguration configuration)
            : base(db, new Mock<ISupplierService>().Object, configuration, NullLoggerFactory.Instance,
                   NullLogger<DatabaseSchemaUpdater>.Instance)
        {
        }

        public bool LastTokenCanBeCanceled { get; private set; }

        protected override Task RunBootstrappersAsync(CancellationToken ct)
        {
            LastTokenCanBeCanceled = ct.CanBeCanceled;
            return Task.CompletedTask;
        }

        protected override Task<int> ApplyMigrationsAsync(CancellationToken ct) => Task.FromResult(0);

        protected override Task<bool> RunBackfillsAsync(bool toleratesFailure, CancellationToken ct) => Task.FromResult(true);
    }

    [Fact]
    public async Task UpdateAsync_NUNCATira_NiSiUnPasoExplotaDeFormaInesperada()
    {
        // BLOQUEANTE 3(c): el contrato es "nunca tira" porque los dos callers DECIDEN con el resultado (el restore
        // vuelve atrás; el arranque aborta con su log crítico). Si una excepción escapara, el arranque perdía ese log.
        var updater = new ExplodingSchemaUpdater(NewContext(), ConfigurationSinEspera());

        var result = await updater.UpdateAsync(SchemaUpdatePolicy.Restore, CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }

    /// <summary>Explota en el primer paso, ANTES del bucle de intentos (donde no hay try/catch por intento).</summary>
    private sealed class ExplodingSchemaUpdater : DatabaseSchemaUpdater
    {
        public ExplodingSchemaUpdater(AppDbContext db, IConfiguration configuration)
            : base(db, new Mock<ISupplierService>().Object, configuration, NullLoggerFactory.Instance,
                   NullLogger<DatabaseSchemaUpdater>.Instance)
        {
        }

        protected override Task RunBootstrappersAsync(CancellationToken ct) =>
            throw new InvalidOperationException("boom inesperado en los bootstrappers");
    }

    [Fact]
    public async Task CaminoNormal_CorreLosTresPasosUnaVezYDevuelveCuantasMigracionesAplico()
    {
        var updater = new TestableSchemaUpdater(NewContext(), ConfigurationSinEspera());

        var result = await updater.UpdateAsync(SchemaUpdatePolicy.Restore, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(3, result.MigrationsApplied);
        Assert.Equal(1, updater.BootstrapperRuns);
        Assert.Equal(1, updater.MigrationAttempts);
        Assert.Equal(1, updater.BackfillRuns);
    }
}
