using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using TravelApi.Application.Constants;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using TravelApi.Tests.Fixtures;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Obra "Restaurar TOTAL" (2026-07-28, firmada) + ADR-052 (2026-07-29, "los resguardos de versiones anteriores
/// se aceptan y el sistema se actualiza solo"): cubre la ORQUESTACIÓN completa del modo <c>total</c> de
/// <see cref="SystemDataRestoreService"/> — en qué orden se llama a cada cosa y qué pasa cuando algo falla en
/// cada paso.
///
/// <para><b>Qué NO vive acá</b>: el camino 100% exitoso. Un modo total exitoso termina SIEMPRE con el
/// <c>UPDATE "AfipSettings"</c> por SQL crudo, que el proveedor InMemory no soporta — esos escenarios viven en
/// <c>SystemDataRestoreServiceIntegrationTests</c> (Postgres real). Acá se cubren los caminos que se CORTAN
/// antes, y los de VUELTA ATRÁS (para los que la falla del <c>ExecuteSqlRawAsync</c> de InMemory sirve, a
/// propósito, como disparador realista de "el paso 8 falló").</para>
/// </summary>
public class SystemDataRestoreServiceTotalModeTests
{
    private const string ValidPhrase = "RESTAURAR TODO";
    private const string ValidPassword = "Correcta123!";
    private const string RequesterUserId = "admin-1";
    private const string ValidFileName = "wipe-20260727-120000.dump";
    private const string ValidMotivo = "Recuperar datos borrados por error operativo del 2026-07-28.";

    /// <summary>Nombres INTERNOS de bases: ningún mensaje al usuario ni detalle de auditoría puede contenerlos (T-5).</summary>
    private const string NewDatabaseName = "travel_restore_20260729120000";
    private const string PreviousDatabaseName = "travel_old_20260729120001";

    private static AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static Mock<UserManager<ApplicationUser>> BuildUserManagerMock(bool passwordOk = true)
    {
        var store = new Mock<IUserStore<ApplicationUser>>();
        var mock = new Mock<UserManager<ApplicationUser>>(
            store.Object, null!, null!,
            Array.Empty<IUserValidator<ApplicationUser>>(),
            Array.Empty<IPasswordValidator<ApplicationUser>>(),
            null!, null!, null!, null!);
        var user = new ApplicationUser { Id = RequesterUserId, UserName = "admin", FullName = "Admin Uno" };
        mock.Setup(m => m.FindByIdAsync(RequesterUserId)).ReturnsAsync(user);
        mock.Setup(m => m.CheckPasswordAsync(user, It.IsAny<string>())).ReturnsAsync(passwordOk);
        return mock;
    }

    private static Mock<IWipeBackupPort> NewHappyPathBackupPortMock(string backupFileName = "pre-restore-20260728-090000.dump")
    {
        var backupPortMock = new Mock<IWipeBackupPort>();
        backupPortMock
            .Setup(b => b.CreateBackupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WipeBackupResult(true, backupFileName, "pre-restore-backup-20260728-090000/", null));
        backupPortMock
            .Setup(b => b.RestoreObjectsFromBackupPrefixAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        return backupPortMock;
    }

    /// <summary>
    /// Puerto de base de datos con TODO el camino de ADR-052 en verde (veredicto configurable): privilegios OK,
    /// resguardo restaurado en una base nueva, intercambio OK y vuelta atrás OK si se llegara a pedir. Cada test
    /// re-configura SOLO el paso que quiere hacer fallar — así queda explícito qué está probando.
    /// </summary>
    private static Mock<IDatabaseRestorePort> NewPortMock(
        RestoreSchemaVerdict verdict = RestoreSchemaVerdict.Identical, int missingMigrations = 0)
    {
        var portMock = new Mock<IDatabaseRestorePort>();
        portMock
            .Setup(p => p.CheckSchemaCompatibilityAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SchemaCompatibilityResult(verdict, null, missingMigrations));
        portMock
            .Setup(p => p.CheckDatabaseManagementPrivilegesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DatabasePrivilegeCheckResult(true, null));
        portMock
            .Setup(p => p.CleanupLeftoverRestoreDatabasesAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        portMock
            .Setup(p => p.RestoreIntoNewDatabaseAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NewDatabaseRestoreResult(TotalRestoreOutcome.Completed, true, NewDatabaseName, null));
        portMock
            .Setup(p => p.SwapRestoredDatabaseIntoLiveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DatabaseSwapResult(true, PreviousDatabaseName, null));
        portMock
            .Setup(p => p.RollbackSwapAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DatabaseSwapRollbackResult(true, null));
        portMock
            .Setup(p => p.DropDatabaseAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        // El resguardo previo se verifica antes de dropear la copia anterior de la base (re-review): acá se lee bien.
        portMock
            .Setup(p => p.VerifyBackupAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RestoreVerifyResult(true, null, 120, true));
        return portMock;
    }

    private static Mock<ISchemaUpdatePort> NewSchemaUpdatePortMock(bool success = true, int migrationsApplied = 3)
    {
        var mock = new Mock<ISchemaUpdatePort>();
        mock
            .Setup(s => s.UpdateAsync(It.IsAny<SchemaUpdatePolicy>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SchemaUpdateResult(
                success, success ? migrationsApplied : 0,
                success ? null : "relation \"Payments\" does not exist"));
        return mock;
    }

    /// <summary>B7 + retomo 2026-08-03: mock por default que "purga" 0 jobs sin tocar Hangfire de verdad —
    /// estos tests no levantan un storage de Hangfire real, asi que alcanza con que el metodo no rompa.</summary>
    private static Mock<IHangfireJobQueuePurgePort> NewHangfirePurgePortMock()
    {
        var mock = new Mock<IHangfireJobQueuePurgePort>();
        mock.Setup(p => p.PurgeQueuedAndScheduledJobsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HangfireJobPurgeResult(0, 0, 0, HangfireJobPurgeStatus.Completa));
        return mock;
    }

    private static SystemDataRestoreService NewService(
        AppDbContext context,
        Mock<UserManager<ApplicationUser>> userManagerMock,
        Mock<IDatabaseRestorePort> portMock,
        Mock<IWipeBackupPort> backupPortMock,
        RecordingMaintenanceModeService maintenanceMode,
        Mock<IAuditService>? auditServiceMock = null,
        Mock<ISchemaUpdatePort>? schemaUpdatePortMock = null,
        Mock<IHangfireJobQueuePurgePort>? hangfirePurgePortMock = null)
    {
        return new SystemDataRestoreService(
            context, userManagerMock.Object, portMock.Object, backupPortMock.Object,
            (schemaUpdatePortMock ?? NewSchemaUpdatePortMock()).Object,
            maintenanceMode,
            (auditServiceMock ?? new Mock<IAuditService>()).Object,
            (hangfirePurgePortMock ?? NewHangfirePurgePortMock()).Object,
            NullLogger<SystemDataRestoreService>.Instance);
    }

    /// <summary>T-5: ni ids de migración, ni nombres de tabla/base, ni jerga técnica en lo que ve el usuario.</summary>
    private static void AssertSinInternals(string mensaje)
    {
        Assert.DoesNotContain("travel_", mensaje);
        Assert.DoesNotContain("pg_restore", mensaje);
        Assert.DoesNotContain("pg_dump", mensaje);
        Assert.DoesNotContain("__EFMigrationsHistory", mensaje);
        Assert.DoesNotContain("migration", mensaje, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AfipSettings", mensaje);
        Assert.DoesNotContain("2026", mensaje); // ningún id de migración (que arranca con la fecha) se filtra
    }

    // ============================================================================================
    // Gate de versión (ADR-052 D2): los cinco veredictos
    // ============================================================================================

    [Fact]
    public async Task ModoTotal_ResguardoDeVersionMasNueva_RechazaConTextoPropioSinTocarNada()
    {
        var context = NewContext();
        var maintenanceMode = new RecordingMaintenanceModeService();
        var portMock = NewPortMock(RestoreSchemaVerdict.NewerThanSystem);
        var backupPortMock = new Mock<IWipeBackupPort>();

        var service = NewService(context, BuildUserManagerMock(), portMock, backupPortMock, maintenanceMode);

        var ex = await Assert.ThrowsAsync<SystemDataRestoreRefusedException>(() =>
            service.ExecuteRestoreAsync(RequesterUserId, ValidPassword, ValidPhrase, ValidFileName, RestoreModes.Total, null, ValidMotivo, CancellationToken.None));

        Assert.Contains("MÁS NUEVA", ex.Message);
        AssertSinInternals(ex.Message);
        Assert.False(ex.RolledBack);

        // Nada se tocó: ni base nueva, ni resguardo previo, ni intercambio.
        portMock.Verify(p => p.RestoreIntoNewDatabaseAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        portMock.Verify(p => p.SwapRestoredDatabaseIntoLiveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        backupPortMock.Verify(b => b.CreateBackupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(new[] { "Activate", "Deactivate" }, maintenanceMode.Calls);
    }

    [Fact]
    public async Task ModoTotal_ResguardoConAgujeroEnElHistorial_RechazaConUnTextoDISTINTOAlDeVersionMasNueva()
    {
        // Menor M1 de la re-review: antes había UN solo texto ("es de una versión anterior") que para este caso
        // MENTÍA. Son dos problemas distintos y se dicen distinto.
        var context = NewContext();
        var maintenanceMode = new RecordingMaintenanceModeService();
        var portMock = NewPortMock(RestoreSchemaVerdict.HistoryGap);

        var service = NewService(context, BuildUserManagerMock(), portMock, new Mock<IWipeBackupPort>(), maintenanceMode);

        var ex = await Assert.ThrowsAsync<SystemDataRestoreRefusedException>(() =>
            service.ExecuteRestoreAsync(RequesterUserId, ValidPassword, ValidPhrase, ValidFileName, RestoreModes.Total, null, ValidMotivo, CancellationToken.None));

        Assert.Contains("salto en su historial", ex.Message);
        Assert.DoesNotContain("MÁS NUEVA", ex.Message);
        AssertSinInternals(ex.Message);
        portMock.Verify(p => p.RestoreIntoNewDatabaseAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ModoTotal_BaseVivaConMigracionesPendientes_RechazaSinTocarNada()
    {
        var context = NewContext();
        var maintenanceMode = new RecordingMaintenanceModeService();
        var portMock = NewPortMock(RestoreSchemaVerdict.LiveHasPendingMigrations);

        var service = NewService(context, BuildUserManagerMock(), portMock, new Mock<IWipeBackupPort>(), maintenanceMode);

        var ex = await Assert.ThrowsAsync<SystemDataRestoreRefusedException>(() =>
            service.ExecuteRestoreAsync(RequesterUserId, ValidPassword, ValidPhrase, ValidFileName, RestoreModes.Total, null, ValidMotivo, CancellationToken.None));

        Assert.Contains("a mitad de una actualización", ex.Message);
        AssertSinInternals(ex.Message);
        portMock.Verify(p => p.RestoreIntoNewDatabaseAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(RestoreSchemaVerdict.DumpHistoryEmpty)]
    [InlineData(RestoreSchemaVerdict.CouldNotDetermine)]
    public async Task ModoTotal_SinPoderDeterminarLaVersionDelResguardo_RechazaFailClosed(RestoreSchemaVerdict verdict)
    {
        var context = NewContext();
        var maintenanceMode = new RecordingMaintenanceModeService();
        var portMock = NewPortMock(verdict);

        var service = NewService(context, BuildUserManagerMock(), portMock, new Mock<IWipeBackupPort>(), maintenanceMode);

        var ex = await Assert.ThrowsAsync<SystemDataRestoreRefusedException>(() =>
            service.ExecuteRestoreAsync(RequesterUserId, ValidPassword, ValidPhrase, ValidFileName, RestoreModes.Total, null, ValidMotivo, CancellationToken.None));

        Assert.Contains("no se pudo determinar", ex.Message, StringComparison.OrdinalIgnoreCase);
        AssertSinInternals(ex.Message);
        portMock.Verify(p => p.RestoreIntoNewDatabaseAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ============================================================================================
    // Privilegios (D1.5 + C1) y pasos previos a tocar la base
    // ============================================================================================

    [Fact]
    public async Task ModoTotal_SinPrivilegiosParaAdministrarBases_RechazaEnCriolloAntesDePagarElRestore()
    {
        // Condición C1: el assert (que incluye la PROPIEDAD de la base, no solo "puede crear bases") corre ANTES
        // del pg_restore y del resguardo previo — descubrirlo 15 minutos después sería tirar el trabajo.
        var context = NewContext();
        var maintenanceMode = new RecordingMaintenanceModeService();
        var portMock = NewPortMock();
        portMock
            .Setup(p => p.CheckDatabaseManagementPrivilegesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DatabasePrivilegeCheckResult(false, "Privilegios insuficientes: superusuario=False, puedeCrearBases=True, esDueño=False."));
        var backupPortMock = new Mock<IWipeBackupPort>();

        var service = NewService(context, BuildUserManagerMock(), portMock, backupPortMock, maintenanceMode);

        var ex = await Assert.ThrowsAsync<SystemDataRestoreRefusedException>(() =>
            service.ExecuteRestoreAsync(RequesterUserId, ValidPassword, ValidPhrase, ValidFileName, RestoreModes.Total, null, ValidMotivo, CancellationToken.None));

        Assert.Contains("no permite hacer una restauración total desde la aplicación", ex.Message);
        AssertSinInternals(ex.Message);
        Assert.DoesNotContain("superusuario", ex.Message);
        portMock.Verify(p => p.RestoreIntoNewDatabaseAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        backupPortMock.Verify(b => b.CreateBackupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(new[] { "Activate", "Deactivate" }, maintenanceMode.Calls);
    }

    [Fact]
    public async Task ModoTotal_FallaElRestoreALaBaseNueva_NoIntercambiaNiPideResguardoPrevioYReabreElSistema()
    {
        // Cierre del bloqueante B1 del ADR: un resguardo corrupto ya no puede dañar la base viva. Como la base
        // viva NO se tocó, es seguro reabrir el sistema (mantenimiento OFF) — antes, este mismo fallo llegaba
        // DESPUÉS de haber empezado a reemplazar la base.
        var context = NewContext();
        var maintenanceMode = new RecordingMaintenanceModeService();
        var backupPortMock = NewHappyPathBackupPortMock();
        var portMock = NewPortMock();
        portMock
            .Setup(p => p.RestoreIntoNewDatabaseAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NewDatabaseRestoreResult(TotalRestoreOutcome.Completed, false, null,
                "pg_restore: error: could not read from input file: end of file"));

        var service = NewService(context, BuildUserManagerMock(), portMock, backupPortMock, maintenanceMode);

        var ex = await Assert.ThrowsAsync<SystemDataRestoreRefusedException>(() =>
            service.ExecuteRestoreAsync(RequesterUserId, ValidPassword, ValidPhrase, ValidFileName, RestoreModes.Total, null, ValidMotivo, CancellationToken.None));

        Assert.Contains("el sistema quedó intacto", ex.Message);
        AssertSinInternals(ex.Message);
        Assert.False(ex.RolledBack);
        portMock.Verify(p => p.SwapRestoredDatabaseIntoLiveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        backupPortMock.Verify(b => b.CreateBackupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.False(maintenanceMode.IsActive);
        Assert.Equal(new[] { "Activate", "Touch", "Deactivate" }, maintenanceMode.Calls);
    }

    [Fact]
    public async Task ModoTotal_FallaElResguardoPrevio_NoIntercambiaYDropeaLaBaseNueva()
    {
        var context = NewContext();
        var maintenanceMode = new RecordingMaintenanceModeService();
        var portMock = NewPortMock();
        var backupPortMock = new Mock<IWipeBackupPort>();
        backupPortMock
            .Setup(b => b.CreateBackupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WipeBackupResult(false, null, null, "pg_dump exit code 1: disco lleno"));

        var auditServiceMock = new Mock<IAuditService>();
        var service = NewService(context, BuildUserManagerMock(), portMock, backupPortMock, maintenanceMode, auditServiceMock);

        var ex = await Assert.ThrowsAsync<SystemDataRestoreRefusedException>(() =>
            service.ExecuteRestoreAsync(RequesterUserId, ValidPassword, ValidPhrase, ValidFileName, RestoreModes.Total, null, ValidMotivo, CancellationToken.None));

        Assert.Contains("No se pudo generar el resguardo", ex.Message);
        AssertSinInternals(ex.Message);
        portMock.Verify(p => p.SwapRestoredDatabaseIntoLiveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        // La base nueva que ya se había restaurado es basura: se dropea (no se deja ocupando disco).
        portMock.Verify(p => p.DropDatabaseAsync(NewDatabaseName, It.IsAny<CancellationToken>()), Times.Once);
        Assert.False(maintenanceMode.IsActive);

        auditServiceMock.Verify(a => a.LogBusinessEventAsync(
            AuditActions.SystemDataRestoreRejected, AuditActions.SystemDataRestoreEntityName,
            It.IsAny<string>(), It.Is<string>(d => !d!.Contains("pg_dump")), RequesterUserId, null,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ============================================================================================
    // Vuelta atrás (D4) y doble fallo (D4.5)
    // ============================================================================================

    [Fact]
    public async Task ModoTotal_FallaElIntercambioDeNombres_VuelveAtrasYRechazaEnCriollo()
    {
        var context = NewContext();
        var maintenanceMode = new RecordingMaintenanceModeService();
        var portMock = NewPortMock();
        portMock
            .Setup(p => p.SwapRestoredDatabaseIntoLiveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DatabaseSwapResult(false, PreviousDatabaseName, "database is being accessed by other users"));

        var auditServiceMock = new Mock<IAuditService>();
        var service = NewService(context, BuildUserManagerMock(), portMock, NewHappyPathBackupPortMock(), maintenanceMode, auditServiceMock);

        var ex = await Assert.ThrowsAsync<SystemDataRestoreRefusedException>(() =>
            service.ExecuteRestoreAsync(RequesterUserId, ValidPassword, ValidPhrase, ValidFileName, RestoreModes.Total, null, ValidMotivo, CancellationToken.None));

        Assert.Contains("Quedó todo como estaba antes de intentarlo", ex.Message);
        AssertSinInternals(ex.Message);
        Assert.True(ex.RolledBack);
        portMock.Verify(p => p.RollbackSwapAsync(PreviousDatabaseName, It.IsAny<CancellationToken>()), Times.Once);
        Assert.False(maintenanceMode.IsActive);

        // D7: el rechazo con vuelta atrás queda auditado con el DATO volvioAtras, no solo dentro del texto.
        auditServiceMock.Verify(a => a.LogBusinessEventAsync(
            AuditActions.SystemDataRestoreRejected, AuditActions.SystemDataRestoreEntityName,
            It.IsAny<string>(), It.Is<string>(d => d!.Contains("volvioAtras") && d.Contains("true")),
            RequesterUserId, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ModoTotal_FallaLaActualizacionDeEsquema_VuelveAtrasYNoAuditaExito()
    {
        // Cubre también el caso "falla un BACKFILL" (no la migración): el puerto de actualización no distingue
        // hacia afuera —los dos devuelven Success=false— porque en restore NINGUNO se traga (B4).
        var context = NewContext();
        var maintenanceMode = new RecordingMaintenanceModeService();
        var portMock = NewPortMock(RestoreSchemaVerdict.SubsetNeedsUpdate, missingMigrations: 4);
        var schemaUpdateMock = NewSchemaUpdatePortMock(success: false);

        var auditServiceMock = new Mock<IAuditService>();
        var service = NewService(
            context, BuildUserManagerMock(), portMock, NewHappyPathBackupPortMock(), maintenanceMode,
            auditServiceMock, schemaUpdateMock);

        var ex = await Assert.ThrowsAsync<SystemDataRestoreRefusedException>(() =>
            service.ExecuteRestoreAsync(RequesterUserId, ValidPassword, ValidPhrase, ValidFileName, RestoreModes.Total, null, ValidMotivo, CancellationToken.None));

        Assert.True(ex.RolledBack);
        Assert.Contains("Quedó todo como estaba antes de intentarlo", ex.Message);
        AssertSinInternals(ex.Message);
        schemaUpdateMock.Verify(s => s.UpdateAsync(SchemaUpdatePolicy.Restore, It.IsAny<CancellationToken>()), Times.Once);
        portMock.Verify(p => p.RollbackSwapAsync(PreviousDatabaseName, It.IsAny<CancellationToken>()), Times.Once);
        Assert.False(maintenanceMode.IsActive);

        auditServiceMock.Verify(a => a.LogBusinessEventAsync(
            AuditActions.SystemDataTotallyRestored, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ModoTotal_FallaElAjusteDeAfipDespuesDelIntercambio_VuelveAtras()
    {
        // B3 cerrado por ADR-052: el ajuste de AFIP pasó a estar DENTRO del sobre de vuelta atrás. Antes, no
        // poder confirmarlo era un "desenlace incierto" sin salida; ahora hay a dónde volver.
        // InMemory no soporta ExecuteSqlRawAsync (tira NotSupportedException) — se usa A PROPÓSITO como
        // disparador realista de "el paso de AFIP falló".
        var context = NewContext();
        var maintenanceMode = new RecordingMaintenanceModeService();
        var portMock = NewPortMock(RestoreSchemaVerdict.Identical);
        var schemaUpdateMock = NewSchemaUpdatePortMock();

        var service = NewService(
            context, BuildUserManagerMock(), portMock, NewHappyPathBackupPortMock(), maintenanceMode,
            schemaUpdatePortMock: schemaUpdateMock);

        var ex = await Assert.ThrowsAsync<SystemDataRestoreRefusedException>(() =>
            service.ExecuteRestoreAsync(RequesterUserId, ValidPassword, ValidPhrase, ValidFileName, RestoreModes.Total, null, ValidMotivo, CancellationToken.None));

        Assert.True(ex.RolledBack);
        portMock.Verify(p => p.RollbackSwapAsync(PreviousDatabaseName, It.IsAny<CancellationToken>()), Times.Once);

        // Veredicto "igual": el paso de actualización NO se llama (el camino de siempre queda intacto).
        schemaUpdateMock.Verify(s => s.UpdateAsync(It.IsAny<SchemaUpdatePolicy>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.False(maintenanceMode.IsActive);
    }

    [Fact]
    public async Task ModoTotal_UnaExcepcionINESPERADADespuesDelIntercambio_TambienVuelveAtras()
    {
        // Red de seguridad: el ADR pide que TODO lo que falle entre el intercambio y el final vuelva atrás, no
        // solo los fallos que el código enumera paso por paso. Se simula con un puerto de actualización que TIRA
        // (su contrato dice que nunca tira, justamente por eso es "inesperado").
        var context = NewContext();
        var maintenanceMode = new RecordingMaintenanceModeService();
        var portMock = NewPortMock(RestoreSchemaVerdict.SubsetNeedsUpdate, missingMigrations: 1);
        var schemaUpdateMock = new Mock<ISchemaUpdatePort>();
        schemaUpdateMock
            .Setup(s => s.UpdateAsync(It.IsAny<SchemaUpdatePolicy>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom inesperado en el medio de la actualización"));

        var service = NewService(
            context, BuildUserManagerMock(), portMock, NewHappyPathBackupPortMock(), maintenanceMode,
            schemaUpdatePortMock: schemaUpdateMock);

        var ex = await Assert.ThrowsAsync<SystemDataRestoreRefusedException>(() =>
            service.ExecuteRestoreAsync(RequesterUserId, ValidPassword, ValidPhrase, ValidFileName, RestoreModes.Total, null, ValidMotivo, CancellationToken.None));

        Assert.True(ex.RolledBack);
        AssertSinInternals(ex.Message);
        Assert.DoesNotContain("boom", ex.Message);
        portMock.Verify(p => p.RollbackSwapAsync(PreviousDatabaseName, It.IsAny<CancellationToken>()), Times.Once);
        Assert.False(maintenanceMode.IsActive);
    }

    [Fact]
    public async Task ModoTotal_LaVueltaAtrasFalla_DOBLEFALLO_DejaElSistemaEnMantenimientoSostenido()
    {
        // D4.5: el ÚNICO caso frenado a propósito. NUNCA se apaga el mantenimiento y se pide SuppressAutoExpiry
        // (ni la auto-expiración por tiempo lo reabre: sale solo a mano por el runbook).
        var context = NewContext();
        var maintenanceMode = new RecordingMaintenanceModeService();
        var portMock = NewPortMock(RestoreSchemaVerdict.SubsetNeedsUpdate, missingMigrations: 2);
        portMock
            .Setup(p => p.RollbackSwapAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DatabaseSwapRollbackResult(false, "no se pudo renombrar la base"));

        var service = NewService(
            context, BuildUserManagerMock(), portMock, NewHappyPathBackupPortMock(), maintenanceMode,
            schemaUpdatePortMock: NewSchemaUpdatePortMock(success: false));

        var ex = await Assert.ThrowsAsync<SystemDataRestoreRefusedException>(() =>
            service.ExecuteRestoreAsync(RequesterUserId, ValidPassword, ValidPhrase, ValidFileName, RestoreModes.Total, null, ValidMotivo, CancellationToken.None));

        Assert.Contains("queda en mantenimiento", ex.Message);
        Assert.Contains("URGENTE", ex.Message);
        AssertSinInternals(ex.Message);
        Assert.False(ex.RolledBack); // NO volvió atrás: es doble fallo, no "quedó como antes".
        Assert.True(maintenanceMode.IsActive);
        Assert.Contains("SuppressAutoExpiry", maintenanceMode.Calls);
        Assert.DoesNotContain("Deactivate", maintenanceMode.Calls);
    }

    [Fact]
    public async Task ModoTotal_LaVueltaAtrasTIRA_SeDeclaraDOBLEFALLOIgual()
    {
        // BLOQUEANTE de seguridad de la re-review: la vuelta atrás se conecta a Postgres, así que puede TIRAR en vez
        // de devolver "no pude". Sin el try/catch, esa excepción salía por arriba, el finally apagaba el
        // mantenimiento y el sistema se reabría con la base a medio actualizar y SIN AFIP forzado a homologación.
        // Una excepción tiene que tratarse IDÉNTICO a "no pude volver atrás".
        var context = NewContext();
        var maintenanceMode = new RecordingMaintenanceModeService();
        var portMock = NewPortMock(RestoreSchemaVerdict.SubsetNeedsUpdate, missingMigrations: 2);
        portMock
            .Setup(p => p.RollbackSwapAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NpgsqlException("no se pudo abrir la conexión con Postgres"));

        var service = NewService(
            context, BuildUserManagerMock(), portMock, NewHappyPathBackupPortMock(), maintenanceMode,
            schemaUpdatePortMock: NewSchemaUpdatePortMock(success: false));

        var ex = await Assert.ThrowsAsync<SystemDataRestoreRefusedException>(() =>
            service.ExecuteRestoreAsync(RequesterUserId, ValidPassword, ValidPhrase, ValidFileName, RestoreModes.Total, null, ValidMotivo, CancellationToken.None));

        Assert.True(ex.DoubleFailure);
        Assert.Contains("queda en mantenimiento", ex.Message);
        AssertSinInternals(ex.Message);
        Assert.DoesNotContain("Postgres", ex.Message);
        Assert.True(maintenanceMode.IsActive);
        Assert.Contains("SuppressAutoExpiry", maintenanceMode.Calls);
        Assert.DoesNotContain("Deactivate", maintenanceMode.Calls);
    }

    [Fact]
    public async Task ModoTotal_ElIntercambioTIRA_RechazaLimpioYNoDejaElMantenimientoColgado()
    {
        // Severidad menor del mismo bloqueante: si el intercambio tira, no puede salir un 500 con detalle técnico.
        // Es seguro tratarlo como "no se cambió nada" (el único tramo del puerto que puede tirar corre ANTES de
        // cualquier renombre) → rechazo limpio, auditado, y el sistema se reabre.
        var context = NewContext();
        var maintenanceMode = new RecordingMaintenanceModeService();
        var portMock = NewPortMock();
        portMock
            .Setup(p => p.SwapRestoredDatabaseIntoLiveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NpgsqlException("connection refused"));

        var auditServiceMock = new Mock<IAuditService>();
        var service = NewService(
            context, BuildUserManagerMock(), portMock, NewHappyPathBackupPortMock(), maintenanceMode, auditServiceMock);

        var ex = await Assert.ThrowsAsync<SystemDataRestoreRefusedException>(() =>
            service.ExecuteRestoreAsync(RequesterUserId, ValidPassword, ValidPhrase, ValidFileName, RestoreModes.Total, null, ValidMotivo, CancellationToken.None));

        Assert.False(ex.DoubleFailure);
        Assert.False(ex.RolledBack);
        Assert.Contains("el sistema quedó como estaba", ex.Message);
        AssertSinInternals(ex.Message);
        Assert.DoesNotContain("connection refused", ex.Message);
        Assert.False(maintenanceMode.IsActive);
        portMock.Verify(p => p.RollbackSwapAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        auditServiceMock.Verify(a => a.LogBusinessEventAsync(
            AuditActions.SystemDataRestoreRejected, AuditActions.SystemDataRestoreEntityName,
            It.IsAny<string>(), It.Is<string>(d => !d!.Contains("connection refused")),
            RequesterUserId, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ModoTotal_DobleFallo_AuditaElRechazoAUNQUEElPedidoHttpEsteCANCELADO()
    {
        // BLOQUEANTE 2 de seguridad: justo el PEOR desenlace era el que se quedaba sin registro, porque el audit
        // usaba el ct del pedido (que a esa altura casi siempre está cortado: el proxy corta a los 60s). Ahora todo
        // rechazo posterior al intercambio se audita con CancellationToken.None y con su dato distintivo.
        var context = NewContext();
        var maintenanceMode = new RecordingMaintenanceModeService();
        var portMock = NewPortMock(RestoreSchemaVerdict.SubsetNeedsUpdate, missingMigrations: 1);
        portMock
            .Setup(p => p.RollbackSwapAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DatabaseSwapRollbackResult(false, "no se pudo renombrar"));

        using var requestCts = new CancellationTokenSource();
        portMock
            .Setup(p => p.SwapRestoredDatabaseIntoLiveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                // El pedido HTTP se corta justo después del intercambio, como pasa de verdad.
                requestCts.Cancel();
                return Task.FromResult(new DatabaseSwapResult(true, PreviousDatabaseName, null));
            });

        var auditServiceMock = new Mock<IAuditService>();
        var service = NewService(
            context, BuildUserManagerMock(), portMock, NewHappyPathBackupPortMock(), maintenanceMode,
            auditServiceMock, NewSchemaUpdatePortMock(success: false));

        var ex = await Assert.ThrowsAsync<SystemDataRestoreRefusedException>(() =>
            service.ExecuteRestoreAsync(RequesterUserId, ValidPassword, ValidPhrase, ValidFileName, RestoreModes.Total, null, ValidMotivo, requestCts.Token));

        Assert.True(ex.DoubleFailure);
        Assert.True(maintenanceMode.IsActive);

        // La constancia SE ESCRIBE, con el dato buscable y con un token que NO está cancelado.
        auditServiceMock.Verify(a => a.LogBusinessEventAsync(
            AuditActions.SystemDataRestoreRejected, AuditActions.SystemDataRestoreEntityName,
            It.IsAny<string>(),
            It.Is<string>(d => d!.Contains("dobleFallo") && d.Contains("true")),
            RequesterUserId, null,
            It.Is<CancellationToken>(token => !token.IsCancellationRequested)), Times.Once);
    }

    [Fact]
    public async Task ModoTotal_LosCuatroRechazosNuevos_DevuelvenEXACTAMENTELaFraseYNuncaElMotivoInternoDelMotor()
    {
        // Gate de exposición de datos: el ErrorMessage interno del puerto (jerga de Postgres, ids, nombres de base)
        // no puede aparecer NI COMO PEDAZO del mensaje que ve el usuario.
        const string internalNoise = "42P01: relation \"__EFMigrationsHistory\" travel_restore_20260729120000 does not exist";

        var casos = new (RestoreSchemaVerdict Verdict, string FraseEsperada)[]
        {
            (RestoreSchemaVerdict.NewerThanSystem, "Ese resguardo es de una versión MÁS NUEVA del sistema que la que está instalada: no se puede usar acá. Avisá al equipo técnico."),
            (RestoreSchemaVerdict.HistoryGap, "Ese resguardo tiene un salto en su historial: le falta una parte del medio, así que el sistema no puede completarlo solo. No se tocó nada. Avisá al equipo técnico."),
            (RestoreSchemaVerdict.LiveHasPendingMigrations, "El sistema quedó a mitad de una actualización, así que no se puede restaurar desde acá. No se tocó nada. Avisá al equipo técnico."),
            (RestoreSchemaVerdict.DumpHistoryEmpty, "No se pudo determinar de qué versión del sistema es ese resguardo, así que no se restauró nada. Avisá al equipo técnico."),
        };

        foreach (var (verdict, fraseEsperada) in casos)
        {
            var context = NewContext();
            var maintenanceMode = new RecordingMaintenanceModeService();
            var portMock = new Mock<IDatabaseRestorePort>();
            portMock
                .Setup(p => p.CheckSchemaCompatibilityAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SchemaCompatibilityResult(verdict, internalNoise, 3));

            var service = NewService(context, BuildUserManagerMock(), portMock, new Mock<IWipeBackupPort>(), maintenanceMode);

            var ex = await Assert.ThrowsAsync<SystemDataRestoreRefusedException>(() =>
                service.ExecuteRestoreAsync(RequesterUserId, ValidPassword, ValidPhrase, ValidFileName, RestoreModes.Total, null, ValidMotivo, CancellationToken.None));

            Assert.Equal(fraseEsperada, ex.Message);
            Assert.DoesNotContain("42P01", ex.Message);
            Assert.DoesNotContain("travel_restore_", ex.Message);
            AssertSinInternals(ex.Message);
        }
    }

    // ============================================================================================
    // Candados que ya existían y NO cambian con esta obra (F-16, concurrencia, fiscal)
    // ============================================================================================

    [Fact]
    public async Task ModoTotal_ConFraseIncorrecta_RechazaAntesDeValidarMotivoOActivarMantenimiento()
    {
        var context = NewContext();
        var maintenanceMode = new RecordingMaintenanceModeService();
        var backupPortMock = new Mock<IWipeBackupPort>();
        var portMock = new Mock<IDatabaseRestorePort>();

        var service = NewService(context, BuildUserManagerMock(), portMock, backupPortMock, maintenanceMode);

        await Assert.ThrowsAsync<SystemDataRestoreRefusedException>(() =>
            service.ExecuteRestoreAsync(RequesterUserId, ValidPassword, "frase incorrecta", ValidFileName, RestoreModes.Total, null, null, CancellationToken.None));

        backupPortMock.Verify(b => b.CreateBackupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Empty(maintenanceMode.Calls);
    }

    [Fact]
    public async Task ModoTotal_ConContraseñaIncorrecta_RechazaSinActivarMantenimiento()
    {
        var context = NewContext();
        var maintenanceMode = new RecordingMaintenanceModeService();
        var backupPortMock = new Mock<IWipeBackupPort>();
        var portMock = new Mock<IDatabaseRestorePort>();

        var service = NewService(context, BuildUserManagerMock(passwordOk: false), portMock, backupPortMock, maintenanceMode);

        var ex = await Assert.ThrowsAsync<SystemDataRestoreRefusedException>(() =>
            service.ExecuteRestoreAsync(RequesterUserId, ValidPassword, ValidPhrase, ValidFileName, RestoreModes.Total, null, ValidMotivo, CancellationToken.None));

        Assert.Equal("La contraseña no es correcta.", ex.Message);
        Assert.Empty(maintenanceMode.Calls);
        backupPortMock.Verify(b => b.CreateBackupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("corto")]
    [InlineData("         ")]
    public async Task ModoTotal_SinMotivoOMuyCorto_RechazaSinActivarMantenimiento(string? motivoInvalido)
    {
        // F-16 intacto con ADR-052: el motivo de al menos 10 caracteres sigue siendo la PRIMERA puerta.
        var context = NewContext();
        var maintenanceMode = new RecordingMaintenanceModeService();
        var backupPortMock = new Mock<IWipeBackupPort>();
        var portMock = new Mock<IDatabaseRestorePort>();

        var service = NewService(context, BuildUserManagerMock(), portMock, backupPortMock, maintenanceMode);

        var ex = await Assert.ThrowsAsync<SystemDataRestoreRefusedException>(() =>
            service.ExecuteRestoreAsync(RequesterUserId, ValidPassword, ValidPhrase, ValidFileName, RestoreModes.Total, null, motivoInvalido, CancellationToken.None));

        Assert.Contains("motivo", ex.Message);
        Assert.Empty(maintenanceMode.Calls);
        portMock.Verify(p => p.CheckSchemaCompatibilityAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        backupPortMock.Verify(b => b.CreateBackupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ModoTotal_ConFacturaProductivaEnLaBaseViva_RechazaPorCandadoFiscalSinTocarNada()
    {
        var context = NewContext();
        context.Invoices.Add(new Invoice
        {
            TipoComprobante = 1,
            PuntoDeVenta = 1,
            NumeroComprobante = 1,
            Resultado = "A",
            CAE = "99999999999999",
            WasIssuedInProduction = true,
        });
        await context.SaveChangesAsync();

        var maintenanceMode = new RecordingMaintenanceModeService();
        var backupPortMock = new Mock<IWipeBackupPort>();
        var portMock = NewPortMock();

        var service = NewService(context, BuildUserManagerMock(), portMock, backupPortMock, maintenanceMode);

        var ex = await Assert.ThrowsAsync<SystemDataRestoreRefusedException>(() =>
            service.ExecuteRestoreAsync(RequesterUserId, ValidPassword, ValidPhrase, ValidFileName, RestoreModes.Total, null, ValidMotivo, CancellationToken.None));

        Assert.Contains("productivo", ex.Message);
        backupPortMock.Verify(b => b.CreateBackupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        portMock.Verify(p => p.RestoreIntoNewDatabaseAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ModoTotal_ConAfipEnProduccionAhoraMismo_RechazaPorCandadoFiscal()
    {
        var context = NewContext();
        context.AfipSettings.Add(new AfipSettings { IsProduction = true, Cuit = 20111111112 });
        await context.SaveChangesAsync();

        var maintenanceMode = new RecordingMaintenanceModeService();
        var backupPortMock = new Mock<IWipeBackupPort>();
        var portMock = NewPortMock();

        var service = NewService(context, BuildUserManagerMock(), portMock, backupPortMock, maintenanceMode);

        var ex = await Assert.ThrowsAsync<SystemDataRestoreRefusedException>(() =>
            service.ExecuteRestoreAsync(RequesterUserId, ValidPassword, ValidPhrase, ValidFileName, RestoreModes.Total, null, ValidMotivo, CancellationToken.None));

        Assert.Contains("productivo", ex.Message);
        backupPortMock.Verify(b => b.CreateBackupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(RestoreModes.Prueba)]
    [InlineData(RestoreModes.Real)]
    [InlineData(RestoreModes.Total)]
    public async Task ConMantenimientoYaActivo_CualquierModoQuedaRechazado(string modo)
    {
        var context = NewContext();
        var maintenanceMode = new RecordingMaintenanceModeService();
        maintenanceMode.TryActivate("Restauración total del sistema en curso.");

        var backupPortMock = new Mock<IWipeBackupPort>();
        var portMock = new Mock<IDatabaseRestorePort>();
        var service = NewService(context, BuildUserManagerMock(), portMock, backupPortMock, maintenanceMode);

        var ex = await Assert.ThrowsAsync<SystemDataRestoreRefusedException>(() =>
            service.ExecuteRestoreAsync(RequesterUserId, ValidPassword, ValidPhrase, ValidFileName, modo, null, ValidMotivo, CancellationToken.None));

        Assert.Equal("Ya hay una restauración en curso.", ex.Message);
        portMock.Verify(p => p.RestoreIntoNewDatabaseAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        portMock.Verify(p => p.RestoreToShadowDatabaseAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        portMock.Verify(p => p.RestoreTablesIntoLiveDatabaseAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Never);
        backupPortMock.Verify(b => b.CreateBackupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>Fake que simula la CARRERA real: el chequeo barato "IsActive" todavía da false, pero el intento atómico de activar pierde igual (otro pedido ganó justo en el medio).</summary>
    private sealed class RacingMaintenanceModeService : IMaintenanceModeService
    {
        public bool IsActive => false;
        public string? Reason => null;
        public DateTime? SinceUtc => null;
        public string? CurrentStep => null;
        public bool TryActivate(string reason) => false;
        public void SetStep(string step) { }
        public void Touch() { }
        public void SuppressAutoExpiry(string reason) { }
        public void Deactivate() { }
    }

    [Fact]
    public async Task ModoTotal_PierdeLaCarreraAtomicaDeTryActivate_RechazaSinTocarNada()
    {
        var context = NewContext();
        var userManagerMock = BuildUserManagerMock();
        var maintenanceMode = new RacingMaintenanceModeService();
        var backupPortMock = new Mock<IWipeBackupPort>();
        var portMock = new Mock<IDatabaseRestorePort>();

        var service = new SystemDataRestoreService(
            context, userManagerMock.Object, portMock.Object, backupPortMock.Object,
            NewSchemaUpdatePortMock().Object, maintenanceMode,
            new Mock<IAuditService>().Object, NewHangfirePurgePortMock().Object,
            NullLogger<SystemDataRestoreService>.Instance);

        var ex = await Assert.ThrowsAsync<SystemDataRestoreRefusedException>(() =>
            service.ExecuteRestoreAsync(RequesterUserId, ValidPassword, ValidPhrase, ValidFileName, RestoreModes.Total, null, ValidMotivo, CancellationToken.None));

        Assert.Equal("Ya hay una restauración en curso.", ex.Message);
        backupPortMock.Verify(b => b.CreateBackupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        portMock.Verify(p => p.RestoreIntoNewDatabaseAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ============================================================================================
    // Lista de resguardos (D5): la marca de versión viaja al DTO tal cual la calculó el puerto
    // ============================================================================================

    [Fact]
    public async Task ListBackupsAsync_LlevaLaMarcaDeVersionDeCadaResguardoAlDto()
    {
        var context = NewContext();
        var portMock = new Mock<IDatabaseRestorePort>();
        portMock
            .Setup(p => p.ListBackupsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<BackupFileInfo>
            {
                new("wipe-20260729-120000.dump", new DateTime(2026, 7, 29, 12, 0, 0, DateTimeKind.Utc), 100, BackupVersionStates.Actual, BackupOriginLabels.AfterWipe),
                new("pre-restore-20260727-223313.dump", new DateTime(2026, 7, 27, 22, 33, 13, DateTimeKind.Utc), 90, BackupVersionStates.Anterior, BackupOriginLabels.BeforeRestore),
                new("raro.dump", new DateTime(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc), 10, BackupVersionStates.Desconocida, BackupOriginLabels.Manual),
            });

        var service = NewService(
            context, BuildUserManagerMock(), portMock, new Mock<IWipeBackupPort>(), new RecordingMaintenanceModeService());

        var result = await service.ListBackupsAsync(CancellationToken.None);

        Assert.Equal(BackupVersionStates.Actual, result.Backups[0].VersionResguardo);
        Assert.Equal(BackupVersionStates.Anterior, result.Backups[1].VersionResguardo);
        Assert.Equal(BackupVersionStates.Desconocida, result.Backups[2].VersionResguardo);

        // Rediseño 2026-07-30 (§7 punto 1): el "por qué se guardó" viaja al DTO tal cual lo armó el motor.
        Assert.Equal(BackupOriginLabels.AfterWipe, result.Backups[0].PorQueSeGuardo);
        Assert.Equal(BackupOriginLabels.BeforeRestore, result.Backups[1].PorQueSeGuardo);
        Assert.Equal(BackupOriginLabels.Manual, result.Backups[2].PorQueSeGuardo);
    }

    // ============================================================================================
    // Paso en curso de la restauración total (rediseño 2026-07-30, §7 punto 2)
    // ============================================================================================

    /// <summary>
    /// Los tres pasos se publican EN EL ORDEN EN QUE REALMENTE OCURREN (datos → resguardo → al día), que por
    /// ADR-052 (D1.9) no es el del dibujo firmado: el resguardo del estado actual se toma DESPUÉS de comprobar
    /// que el resguardo elegido se puede restaurar. La pantalla tiene que listarlos en este orden.
    ///
    /// <para>Este escenario llega hasta el ajuste de AFIP y ahí falla (InMemory no soporta SQL crudo), que es
    /// justo lo que necesitamos: el tercer paso se publica APENAS termina el intercambio, así que para cuando
    /// falla ya se publicaron los tres.</para>
    /// </summary>
    [Fact]
    public async Task ModoTotal_PublicaLosTresPasosEnElOrdenRealYLosLimpiaAlTerminar()
    {
        var context = NewContext();
        var maintenanceMode = new RecordingMaintenanceModeService();
        var portMock = NewPortMock();
        var backupPortMock = NewHappyPathBackupPortMock();

        var service = NewService(context, BuildUserManagerMock(), portMock, backupPortMock, maintenanceMode);

        await Assert.ThrowsAsync<SystemDataRestoreRefusedException>(() =>
            service.ExecuteRestoreAsync(
                RequesterUserId, ValidPassword, ValidPhrase, ValidFileName, RestoreModes.Total, null, ValidMotivo, CancellationToken.None));

        Assert.Equal(
            new[] { RestoreProgressSteps.Datos, RestoreProgressSteps.Resguardo, RestoreProgressSteps.Actualizacion },
            maintenanceMode.PublishedSteps);

        // Al apagarse el mantenimiento no queda ningún paso colgado (si no, la pantalla seguiría mostrando
        // "poniendo el sistema al día" para siempre).
        Assert.Null(maintenanceMode.CurrentStep);
    }

    /// <summary>
    /// Los tres pasos tienen un texto en criollo cerrado (no lo escribe el front) y ninguno filtra jerga
    /// técnica (T-5/P-17).
    /// </summary>
    [Fact]
    public void LosTresPasos_TienenTextoEnCriolloYNingunoFiltraJerga()
    {
        foreach (var paso in RestoreProgressSteps.All)
        {
            var texto = RestoreProgressSteps.TextFor(paso);

            Assert.False(string.IsNullOrWhiteSpace(texto));
            AssertSinInternals(texto!);
            Assert.DoesNotContain("base de datos", texto!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("esquema", texto!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("dump", texto!, StringComparison.OrdinalIgnoreCase);
        }

        // Un código que no es de los tres NO tiene texto: nunca se muestra un valor crudo.
        Assert.Null(RestoreProgressSteps.TextFor("cualquier-otra-cosa"));
        Assert.Null(RestoreProgressSteps.TextFor(null));
    }

    /// <summary>Un rechazo ANTES de tocar nada no publica ningún paso: no hay nada en curso que mostrar.</summary>
    [Fact]
    public async Task ModoTotal_RechazadoPorElGateDeVersion_NoPublicaNingunPaso()
    {
        var context = NewContext();
        var maintenanceMode = new RecordingMaintenanceModeService();
        var portMock = NewPortMock(RestoreSchemaVerdict.NewerThanSystem);

        var service = NewService(
            context, BuildUserManagerMock(), portMock, NewHappyPathBackupPortMock(), maintenanceMode);

        await Assert.ThrowsAsync<SystemDataRestoreRefusedException>(() =>
            service.ExecuteRestoreAsync(
                RequesterUserId, ValidPassword, ValidPhrase, ValidFileName, RestoreModes.Total, null, ValidMotivo, CancellationToken.None));

        Assert.Empty(maintenanceMode.PublishedSteps);
    }
}
