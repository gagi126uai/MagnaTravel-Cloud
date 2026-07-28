using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
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
/// Obra "Restaurar TOTAL" (2026-07-28, firmada por el dueño) + ronda de hardening de seguridad/funcional del
/// mismo día: cubre la orquestación completa del modo <c>total</c> de <see cref="SystemDataRestoreService"/> —
/// motivo obligatorio (B6/F-16), candado de concurrencia (B4), guard de esquema (B7), candado fiscal (B2),
/// backup previo obligatorio, desenlace incierto del <c>pg_restore</c> (B1), forzado de AFIP a homologación
/// (B3), reposición de archivos de MinIO (B5) y auditoría best-effort (B6). El <see cref="IDatabaseRestorePort"/>
/// y el <see cref="IWipeBackupPort"/> se inyectan como fakes (el <c>pg_dump</c>/<c>pg_restore</c> reales se
/// prueban por construcción, mismo criterio que el resto de esta obra) — acá se verifica la ORQUESTACIÓN: en
/// qué orden se llama a cada cosa y qué pasa cuando algo falla en cada paso. El candado fiscal (Invoices/
/// AfipSettings) usa LINQ puro, así que corre perfecto contra InMemory — no hace falta Postgres real para
/// estos casos (a diferencia del forzado de AFIP a homologación, que sí necesita <c>ExecuteSqlRawAsync</c>
/// real y vive en <c>SystemDataRestoreServiceIntegrationTests</c>).
/// </summary>
public class SystemDataRestoreServiceTotalModeTests
{
    private const string ValidPhrase = "RESTAURAR TODO";
    private const string ValidPassword = "Correcta123!";
    private const string RequesterUserId = "admin-1";
    private const string ValidFileName = "wipe-20260727-120000.dump";
    private const string ValidMotivo = "Recuperar datos borrados por error operativo del 2026-07-28.";

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

    /// <summary>
    /// Backup previo listo para el camino feliz. NOTA: un modo total EXITOSO de punta a punta (schema
    /// compatible + pg_restore ok) SIEMPRE corre después el UPDATE crudo que fuerza AFIP a homologación
    /// (hallazgo B3) — <c>ExecuteSqlRawAsync</c> no lo soporta el proveedor InMemory, así que esos escenarios
    /// (éxito completo) viven en <c>SystemDataRestoreServiceIntegrationTests</c> (Postgres real). Acá solo se
    /// cubren los caminos que SE CORTAN antes de llegar a ese UPDATE (rechazos en cualquier paso previo).
    /// </summary>
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

    private static SystemDataRestoreService NewService(
        AppDbContext context,
        Mock<UserManager<ApplicationUser>> userManagerMock,
        Mock<IDatabaseRestorePort> portMock,
        Mock<IWipeBackupPort> backupPortMock,
        RecordingMaintenanceModeService maintenanceMode,
        Mock<IAuditService>? auditServiceMock = null)
    {
        return new SystemDataRestoreService(
            context, userManagerMock.Object, portMock.Object, backupPortMock.Object, maintenanceMode,
            (auditServiceMock ?? new Mock<IAuditService>()).Object, NullLogger<SystemDataRestoreService>.Instance);
    }

    [Fact]
    public async Task ModoTotal_BackupPrevioFalla_RechazaSinLlamarAlPuertoDeRestore()
    {
        var context = NewContext();
        var userManagerMock = BuildUserManagerMock();
        var portMock = new Mock<IDatabaseRestorePort>();
        portMock
            .Setup(p => p.CheckSchemaCompatibilityAsync(ValidFileName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SchemaCompatibilityResult(true, null));
        var backupPortMock = new Mock<IWipeBackupPort>();
        backupPortMock
            .Setup(b => b.CreateBackupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WipeBackupResult(false, null, null, "pg_dump exit code 1: disco lleno"));
        var maintenanceMode = new RecordingMaintenanceModeService();
        var auditServiceMock = new Mock<IAuditService>();

        var service = NewService(context, userManagerMock, portMock, backupPortMock, maintenanceMode, auditServiceMock);

        var ex = await Assert.ThrowsAsync<SystemDataRestoreRefusedException>(() =>
            service.ExecuteRestoreAsync(RequesterUserId, ValidPassword, ValidPhrase, ValidFileName, RestoreModes.Total, null, ValidMotivo, CancellationToken.None));

        Assert.Contains("No se pudo generar el resguardo", ex.Message);
        Assert.DoesNotContain("pg_dump", ex.Message);
        // El mantenimiento SI se activa (es el candado de concurrencia, PRIMER paso) pero se desactiva igual al fallar.
        Assert.Equal(new[] { "Activate", "Deactivate" }, maintenanceMode.Calls);
        Assert.False(maintenanceMode.IsActive);
        portMock.Verify(p => p.RestoreTotalAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        auditServiceMock.Verify(a => a.LogBusinessEventAsync(
            AuditActions.SystemDataRestoreRejected,
            AuditActions.SystemDataRestoreEntityName,
            It.IsAny<string>(),
            It.Is<string>(details => !details!.Contains("pg_dump")),
            RequesterUserId,
            null,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ModoTotal_PgRestoreFalla_DesactivaMantenimientoIgualYRechazaSinExponerElStderrNiElNombreDelArchivo()
    {
        var context = NewContext();
        var userManagerMock = BuildUserManagerMock();
        var maintenanceMode = new RecordingMaintenanceModeService();
        var backupPortMock = NewHappyPathBackupPortMock("pre-restore-20260728-090000.dump");

        var portMock = new Mock<IDatabaseRestorePort>();
        portMock
            .Setup(p => p.CheckSchemaCompatibilityAsync(ValidFileName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SchemaCompatibilityResult(true, null));
        portMock
            .Setup(p => p.RestoreTotalAsync(ValidFileName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TotalRestoreResult(TotalRestoreOutcome.Completed, false,
                "pg_restore: error: could not execute query: server closed the connection unexpectedly"));

        var auditServiceMock = new Mock<IAuditService>();
        var service = NewService(context, userManagerMock, portMock, backupPortMock, maintenanceMode, auditServiceMock);

        var ex = await Assert.ThrowsAsync<SystemDataRestoreRefusedException>(() =>
            service.ExecuteRestoreAsync(RequesterUserId, ValidPassword, ValidPhrase, ValidFileName, RestoreModes.Total, null, ValidMotivo, CancellationToken.None));

        Assert.DoesNotContain("pg_restore", ex.Message);
        // Hallazgo de data-exposure (2026-07-28): el nombre del resguardo previo NUNCA va interpolado en un
        // mensaje de error crudo.
        Assert.DoesNotContain(".dump", ex.Message);
        Assert.Contains("Volver atrás", ex.Message);

        // "Touch" (hallazgo B-N2(c)) se llama SIEMPRE justo antes de RestoreTotalAsync, haya salido bien o mal.
        Assert.Equal(new[] { "Activate", "Touch", "Deactivate" }, maintenanceMode.Calls);
        Assert.False(maintenanceMode.IsActive);

        auditServiceMock.Verify(a => a.LogBusinessEventAsync(
            AuditActions.SystemDataTotallyRestored,
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ModoTotal_DesenlaceIncierto_NuncaDesactivaMantenimientoNiAuditaExito()
    {
        // Hallazgo BLOQUEANTE B1 de seguridad: si pg_restore excedio su propio timeout y tuvo que ser matado,
        // el desenlace queda INCIERTO — el sistema TIENE que seguir en mantenimiento, nunca "mentirle" al
        // usuario de que ya es seguro reabrir el sistema.
        var context = NewContext();
        var userManagerMock = BuildUserManagerMock();
        var maintenanceMode = new RecordingMaintenanceModeService();
        var backupPortMock = NewHappyPathBackupPortMock();

        var portMock = new Mock<IDatabaseRestorePort>();
        portMock
            .Setup(p => p.CheckSchemaCompatibilityAsync(ValidFileName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SchemaCompatibilityResult(true, null));
        portMock
            .Setup(p => p.RestoreTotalAsync(ValidFileName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TotalRestoreResult(
                TotalRestoreOutcome.UnknownMayStillBeRunning, false,
                "pg_restore excedio el timeout de 15 minutos y tuvo que ser terminado a la fuerza."));

        var auditServiceMock = new Mock<IAuditService>();
        var service = NewService(context, userManagerMock, portMock, backupPortMock, maintenanceMode, auditServiceMock);

        var ex = await Assert.ThrowsAsync<SystemDataRestoreRefusedException>(() =>
            service.ExecuteRestoreAsync(RequesterUserId, ValidPassword, ValidPhrase, ValidFileName, RestoreModes.Total, null, ValidMotivo, CancellationToken.None));

        Assert.Contains("sigue en mantenimiento", ex.Message);
        Assert.DoesNotContain("pg_restore", ex.Message);

        // LO MAS IMPORTANTE: nunca se desactiva el mantenimiento en este camino, Y ademas queda marcado
        // "SuppressAutoExpiry" (hallazgo B-N2(a)) - ni siquiera la auto-expiracion por tiempo lo va a reabrir
        // solo, requiere confirmacion manual (ver el runbook en docs/db-operations.md).
        Assert.Equal(new[] { "Activate", "Touch", "SuppressAutoExpiry" }, maintenanceMode.Calls);
        Assert.True(maintenanceMode.IsActive);

        auditServiceMock.Verify(a => a.LogBusinessEventAsync(
            AuditActions.SystemDataTotallyRestored,
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ModoTotal_ElUpdateDeAfipFallaTrasElRestoreExitoso_NuncaDesactivaMantenimientoNiAuditaExito()
    {
        // Hallazgo BLOQUEANTE B-N1 de seguridad (2026-07-28): si el pg_restore YA terminó con éxito pero el
        // UPDATE que fuerza AFIP a homologación no se puede CONFIRMAR, no hay forma segura de saber si el
        // sistema quedaría facturando en modo PRODUCTIVO real (CAE) — se trata igual que un desenlace
        // incierto (B1): el mantenimiento NUNCA se apaga sin esa confirmación. InMemory no soporta
        // ExecuteSqlRawAsync (tira NotSupportedException) — se aprovecha A PROPÓSITO como disparador realista
        // de "el UPDATE falló", sin necesitar Postgres real para probar la ORQUESTACIÓN (qué hace el service
        // ante CUALQUIER excepción de ese UPDATE es lo que importa acá).
        var context = NewContext();
        var userManagerMock = BuildUserManagerMock();
        var maintenanceMode = new RecordingMaintenanceModeService();
        var backupPortMock = NewHappyPathBackupPortMock();

        var portMock = new Mock<IDatabaseRestorePort>();
        portMock
            .Setup(p => p.CheckSchemaCompatibilityAsync(ValidFileName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SchemaCompatibilityResult(true, null));
        portMock
            .Setup(p => p.RestoreTotalAsync(ValidFileName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TotalRestoreResult(TotalRestoreOutcome.Completed, true, null));

        var auditServiceMock = new Mock<IAuditService>();
        var service = NewService(context, userManagerMock, portMock, backupPortMock, maintenanceMode, auditServiceMock);

        var ex = await Assert.ThrowsAsync<SystemDataRestoreRefusedException>(() =>
            service.ExecuteRestoreAsync(RequesterUserId, ValidPassword, ValidPhrase, ValidFileName, RestoreModes.Total, null, ValidMotivo, CancellationToken.None));

        Assert.Contains("AFIP", ex.Message);
        Assert.Contains("sigue en mantenimiento", ex.Message);
        Assert.True(maintenanceMode.IsActive);
        Assert.Equal(new[] { "Activate", "Touch", "SuppressAutoExpiry" }, maintenanceMode.Calls);

        auditServiceMock.Verify(a => a.LogBusinessEventAsync(
            AuditActions.SystemDataTotallyRestored,
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ModoTotal_ConFraseIncorrecta_RechazaAntesDeValidarMotivoOActivarMantenimiento()
    {
        var context = NewContext();
        var userManagerMock = BuildUserManagerMock();
        var maintenanceMode = new RecordingMaintenanceModeService();
        var backupPortMock = new Mock<IWipeBackupPort>();
        var portMock = new Mock<IDatabaseRestorePort>();

        var service = NewService(context, userManagerMock, portMock, backupPortMock, maintenanceMode, new Mock<IAuditService>());

        await Assert.ThrowsAsync<SystemDataRestoreRefusedException>(() =>
            service.ExecuteRestoreAsync(RequesterUserId, ValidPassword, "frase incorrecta", ValidFileName, RestoreModes.Total, null, null, CancellationToken.None));

        backupPortMock.Verify(b => b.CreateBackupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Empty(maintenanceMode.Calls);
    }

    [Fact]
    public async Task ModoTotal_ConContraseñaIncorrecta_RechazaSinActivarMantenimiento()
    {
        var context = NewContext();
        var userManagerMock = BuildUserManagerMock(passwordOk: false);
        var maintenanceMode = new RecordingMaintenanceModeService();
        var backupPortMock = new Mock<IWipeBackupPort>();
        var portMock = new Mock<IDatabaseRestorePort>();

        var service = NewService(context, userManagerMock, portMock, backupPortMock, maintenanceMode, new Mock<IAuditService>());

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
        var context = NewContext();
        var userManagerMock = BuildUserManagerMock();
        var maintenanceMode = new RecordingMaintenanceModeService();
        var backupPortMock = new Mock<IWipeBackupPort>();
        var portMock = new Mock<IDatabaseRestorePort>();

        var service = NewService(context, userManagerMock, portMock, backupPortMock, maintenanceMode, new Mock<IAuditService>());

        var ex = await Assert.ThrowsAsync<SystemDataRestoreRefusedException>(() =>
            service.ExecuteRestoreAsync(RequesterUserId, ValidPassword, ValidPhrase, ValidFileName, RestoreModes.Total, null, motivoInvalido, CancellationToken.None));

        Assert.Contains("motivo", ex.Message);
        Assert.Empty(maintenanceMode.Calls);
        backupPortMock.Verify(b => b.CreateBackupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ModoTotal_EsquemaIncompatible_RechazaSinLlamarAlBackupNiAlRestore()
    {
        var context = NewContext();
        var userManagerMock = BuildUserManagerMock();
        var maintenanceMode = new RecordingMaintenanceModeService();
        var backupPortMock = new Mock<IWipeBackupPort>();
        var portMock = new Mock<IDatabaseRestorePort>();
        portMock
            .Setup(p => p.CheckSchemaCompatibilityAsync(ValidFileName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SchemaCompatibilityResult(false, "El resguardo no tiene información de versión de esquema."));

        var service = NewService(context, userManagerMock, portMock, backupPortMock, maintenanceMode, new Mock<IAuditService>());

        var ex = await Assert.ThrowsAsync<SystemDataRestoreRefusedException>(() =>
            service.ExecuteRestoreAsync(RequesterUserId, ValidPassword, ValidPhrase, ValidFileName, RestoreModes.Total, null, ValidMotivo, CancellationToken.None));

        Assert.Contains("versión anterior del sistema", ex.Message);
        backupPortMock.Verify(b => b.CreateBackupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        portMock.Verify(p => p.RestoreTotalAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        // El mantenimiento se activa (es el candado de concurrencia, PRIMERO que todo) pero se desactiva al rechazar.
        Assert.Equal(new[] { "Activate", "Deactivate" }, maintenanceMode.Calls);
    }

    [Fact]
    public async Task ModoTotal_ConFacturaProductivaEnLaBaseViva_RechazaPorCandadoFiscalSinTocarNada()
    {
        // Hallazgo B2 de seguridad: MISMO candado fiscal que "Empezar de cero" — un comprobante REAL (emitido
        // en produccion) tiene que frenar TODO, incluso una restauracion total.
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

        var userManagerMock = BuildUserManagerMock();
        var maintenanceMode = new RecordingMaintenanceModeService();
        var backupPortMock = new Mock<IWipeBackupPort>();
        var portMock = new Mock<IDatabaseRestorePort>();
        portMock
            .Setup(p => p.CheckSchemaCompatibilityAsync(ValidFileName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SchemaCompatibilityResult(true, null));

        var service = NewService(context, userManagerMock, portMock, backupPortMock, maintenanceMode, new Mock<IAuditService>());

        var ex = await Assert.ThrowsAsync<SystemDataRestoreRefusedException>(() =>
            service.ExecuteRestoreAsync(RequesterUserId, ValidPassword, ValidPhrase, ValidFileName, RestoreModes.Total, null, ValidMotivo, CancellationToken.None));

        Assert.Contains("productivo", ex.Message);
        backupPortMock.Verify(b => b.CreateBackupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ModoTotal_ConAfipEnProduccionAhoraMismo_RechazaPorCandadoFiscal()
    {
        var context = NewContext();
        context.AfipSettings.Add(new AfipSettings { IsProduction = true, Cuit = 20111111112 });
        await context.SaveChangesAsync();

        var userManagerMock = BuildUserManagerMock();
        var maintenanceMode = new RecordingMaintenanceModeService();
        var backupPortMock = new Mock<IWipeBackupPort>();
        var portMock = new Mock<IDatabaseRestorePort>();
        portMock
            .Setup(p => p.CheckSchemaCompatibilityAsync(ValidFileName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SchemaCompatibilityResult(true, null));

        var service = NewService(context, userManagerMock, portMock, backupPortMock, maintenanceMode, new Mock<IAuditService>());

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
        // Hallazgo B4 de seguridad ("dos restauraciones a la vez se pisan"), aplicado a LOS TRES MODOS: si ya
        // hay una restauracion total en curso, ningun otro pedido (de NINGUN modo) puede arrancar mientras tanto.
        var context = NewContext();
        var userManagerMock = BuildUserManagerMock();
        var maintenanceMode = new RecordingMaintenanceModeService();
        maintenanceMode.TryActivate("Restauración total del sistema en curso.");

        var backupPortMock = new Mock<IWipeBackupPort>();
        var portMock = new Mock<IDatabaseRestorePort>();
        var service = NewService(context, userManagerMock, portMock, backupPortMock, maintenanceMode, new Mock<IAuditService>());

        var ex = await Assert.ThrowsAsync<SystemDataRestoreRefusedException>(() =>
            service.ExecuteRestoreAsync(RequesterUserId, ValidPassword, ValidPhrase, ValidFileName, modo, null, ValidMotivo, CancellationToken.None));

        Assert.Equal("Ya hay una restauración en curso.", ex.Message);
        portMock.Verify(p => p.RestoreTotalAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
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
        public bool TryActivate(string reason) => false;
        public void Touch() { }
        public void SuppressAutoExpiry(string reason) { }
        public void Deactivate() { }
    }

    [Fact]
    public async Task ModoTotal_PierdeLaCarreraAtomicaDeTryActivate_RechazaSinTocarNada()
    {
        // Cubre el candado ATOMICO (no solo el chequeo barato de arriba): aunque "IsActive" diera false en el
        // instante del chequeo previo, TryActivate puede perder la carrera igual - este es el caso que de
        // verdad resuelve la condicion de carrera entre DOS pedidos de modo total simultaneos.
        var context = NewContext();
        var userManagerMock = BuildUserManagerMock();
        var maintenanceMode = new RacingMaintenanceModeService();
        var backupPortMock = new Mock<IWipeBackupPort>();
        var portMock = new Mock<IDatabaseRestorePort>();

        var service = new SystemDataRestoreService(
            context, userManagerMock.Object, portMock.Object, backupPortMock.Object, maintenanceMode,
            new Mock<IAuditService>().Object, NullLogger<SystemDataRestoreService>.Instance);

        var ex = await Assert.ThrowsAsync<SystemDataRestoreRefusedException>(() =>
            service.ExecuteRestoreAsync(RequesterUserId, ValidPassword, ValidPhrase, ValidFileName, RestoreModes.Total, null, ValidMotivo, CancellationToken.None));

        Assert.Equal("Ya hay una restauración en curso.", ex.Message);
        backupPortMock.Verify(b => b.CreateBackupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        portMock.Verify(p => p.RestoreTotalAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

}
