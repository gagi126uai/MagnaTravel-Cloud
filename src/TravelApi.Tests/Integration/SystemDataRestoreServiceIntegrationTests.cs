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
using TravelApi.Infrastructure.Repositories;
using TravelApi.Infrastructure.Services;
using TravelApi.Tests.Fixtures;
using Xunit;

namespace TravelApi.Tests.Integration;

/// <summary>
/// Obra "Restaurar desde la app" (2026-07-27, Parte B firmada) + Parte C "Restaurar TOTAL" (2026-07-28,
/// firmada) + ronda de hardening de seguridad/funcional del mismo día: cubre los tramos de
/// <see cref="SystemDataRestoreService"/> que necesitan SQL real contra Postgres — el forzado de AFIP a
/// homologación (<c>ExecuteSqlRawAsync</c>, tanto en modo <c>real</c> como <c>total</c>) y el armado de la
/// respuesta/auditoría con nombres de negocio. El resto de las validaciones (frase, contraseña, archivo, modo,
/// lista blanca, candado fiscal, concurrencia, desenlace incierto) están cubiertas con InMemory en
/// <c>SystemDataRestoreServiceValidationTests</c>/<c>SystemDataRestoreServiceTotalModeTests</c> — esas
/// consultas son LINQ puro, no necesitan Postgres real. El <see cref="IDatabaseRestorePort"/> se inyecta como
/// fake — el <c>pg_restore</c>/<c>pg_dump</c> reales se prueban por construcción (mismo criterio que
/// <c>PgDumpAndMinioWipeBackupPort</c>).
/// </summary>
[Trait("Category", "Integration")]
public sealed class SystemDataRestoreServiceIntegrationTests : IClassFixture<PostgresIntegrationFixture>, IAsyncLifetime
{
    private const string AdminUserId = "restore-admin-1";
    private const string ValidFileName = "wipe-20260727-120000.dump";
    private const string ValidMotivo = "Recuperar datos borrados por error operativo del 2026-07-28.";

    private readonly PostgresIntegrationFixture _fixture;

    public SystemDataRestoreServiceIntegrationTests(PostgresIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => ResetRelevantTablesAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task ResetRelevantTablesAsync()
    {
        await using var ctx = _fixture.CreateDbContext();
        await ctx.Database.ExecuteSqlRawAsync("""
            TRUNCATE TABLE
                "AgencySettings", "AfipSettings", "AuditLogs", "AspNetUsers", "Invoices", "Customers"
            RESTART IDENTITY CASCADE;
            """);
    }

    private static async Task SeedAspNetUserAsync(AppDbContext ctx, string userId)
    {
        await ctx.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "AspNetUsers"
              ("Id", "UserName", "NormalizedUserName", "Email", "NormalizedEmail",
               "EmailConfirmed", "PasswordHash", "SecurityStamp", "ConcurrencyStamp",
               "PhoneNumberConfirmed", "TwoFactorEnabled", "LockoutEnabled",
               "AccessFailedCount", "FullName", "IsActive")
            VALUES
              ({userId}, {userId}, {userId.ToUpperInvariant()},
               {userId + "@test.local"}, {(userId + "@test.local").ToUpperInvariant()},
               true, 'test-hash', {Guid.NewGuid().ToString()}, {Guid.NewGuid().ToString()},
               false, false, false,
               0, {"Admin de prueba"}, true)
            ON CONFLICT ("Id") DO NOTHING;
            """);
    }

    private static Mock<UserManager<ApplicationUser>> BuildUserManagerMock(ApplicationUser user)
    {
        var store = new Mock<IUserStore<ApplicationUser>>();
        var mock = new Mock<UserManager<ApplicationUser>>(
            store.Object, null!, null!,
            Array.Empty<IUserValidator<ApplicationUser>>(),
            Array.Empty<IPasswordValidator<ApplicationUser>>(),
            null!, null!, null!, null!);
        mock.Setup(m => m.FindByIdAsync(user.Id)).ReturnsAsync(user);
        mock.Setup(m => m.CheckPasswordAsync(user, It.IsAny<string>())).ReturnsAsync(true);
        return mock;
    }

    private static SystemDataRestoreService NewRestoreService(
        AppDbContext ctx,
        Mock<UserManager<ApplicationUser>> userManagerMock,
        Mock<IDatabaseRestorePort> portMock,
        Mock<IWipeBackupPort>? backupPortMock = null,
        RecordingMaintenanceModeService? maintenanceModeService = null)
    {
        var auditService = new AuditService(new Repository<AuditLog>(ctx), NullLogger<AuditService>.Instance);
        return new SystemDataRestoreService(
            ctx,
            userManagerMock.Object,
            portMock.Object,
            (backupPortMock ?? new Mock<IWipeBackupPort>()).Object,
            maintenanceModeService ?? new RecordingMaintenanceModeService(),
            auditService,
            NullLogger<SystemDataRestoreService>.Instance);
    }

    /// <summary>Mock de puerto listo para un modo total EXITOSO: schema compatible + pg_restore ok.</summary>
    private static Mock<IDatabaseRestorePort> NewHappyPathTotalPortMock(RecordingMaintenanceModeService? maintenanceMode = null)
    {
        var portMock = new Mock<IDatabaseRestorePort>();
        portMock
            .Setup(p => p.CheckSchemaCompatibilityAsync(ValidFileName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SchemaCompatibilityResult(true, null));
        var setup = portMock
            .Setup(p => p.RestoreTotalAsync(ValidFileName, It.IsAny<CancellationToken>()));
        if (maintenanceMode is not null)
        {
            setup.Callback(() => maintenanceMode.Calls.Add("RestoreTotalAsync"));
        }
        setup.ReturnsAsync(new TotalRestoreResult(TotalRestoreOutcome.Completed, true, null));
        return portMock;
    }

    private static Mock<IWipeBackupPort> NewHappyPathBackupPortMock(string backupFileName = "pre-restore-20260728-100000.dump")
    {
        var backupPortMock = new Mock<IWipeBackupPort>();
        backupPortMock
            .Setup(b => b.CreateBackupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WipeBackupResult(true, backupFileName, "pre-restore-backup-20260728-100000/", null));
        return backupPortMock;
    }

    [Fact]
    public async Task ExecuteRestoreAsync_ModoRealConUnaTablaConDatos_LaSalteaYRestauraElRestoEnNombresDeNegocio()
    {
        // Hallazgo de usabilidad (punto 11 de la revision): ya NO se rechaza todo el pedido si una tabla
        // tiene datos. El puerto (acá, un fake) decide que salteo por tener datos y que restauro; el service
        // solo tiene que reflejar ese resultado en nombres de NEGOCIO (T-5), nunca "AgencySettings" crudo.
        await using var ctx = _fixture.CreateDbContext();
        await SeedAspNetUserAsync(ctx, AdminUserId);

        var user = await ctx.Set<ApplicationUser>().AsNoTracking().SingleAsync(u => u.Id == AdminUserId);
        var userManagerMock = BuildUserManagerMock(user);
        var portMock = new Mock<IDatabaseRestorePort>();
        var tablasPedidas = new List<string> { "AgencySettings", "AfipSettings" };
        portMock.Setup(p => p.RestoreTablesIntoLiveDatabaseAsync(ValidFileName, It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LiveTableRestoreResult(
                true, null,
                RestoredTables: new List<string> { "AfipSettings" },
                SkippedNonEmptyTables: new List<string> { "AgencySettings" }));
        var service = NewRestoreService(ctx, userManagerMock, portMock);

        var result = await service.ExecuteRestoreAsync(
            AdminUserId, "cualquier-cosa", "RESTAURAR TODO", ValidFileName, RestoreModes.Real, tablasPedidas, null, CancellationToken.None);

        portMock.Verify(p => p.RestoreTablesIntoLiveDatabaseAsync(
            ValidFileName, It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Once);

        Assert.Contains("la conexión con AFIP", result.TablasRestauradas!);
        Assert.Contains("los datos generales de la agencia", result.TablasSalteadas!);
        Assert.DoesNotContain("AgencySettings", result.Mensaje);
        Assert.DoesNotContain("AfipSettings", result.Mensaje);
        Assert.Contains("ya tenía datos", result.Mensaje);

        await using var verifyCtx = _fixture.CreateDbContext();
        var successLog = await verifyCtx.AuditLogs.SingleAsync(a => a.Action == AuditActions.SystemDataRestored);
        // T-5 tambien en el audit log: ni ahi va un nombre tecnico de tabla.
        Assert.DoesNotContain("\"AgencySettings\"", successLog.Changes);
        Assert.DoesNotContain("\"AfipSettings\"", successLog.Changes);
    }

    [Fact]
    public async Task ExecuteRestoreAsync_ModoRealConTablasVacias_LlamaAlPuertoYAuditaElExitoEnNombresDeNegocio()
    {
        await using var ctx = _fixture.CreateDbContext();
        await SeedAspNetUserAsync(ctx, AdminUserId);
        // AgencySettings/AfipSettings estan vacias (el reset de este fixture las deja asi).

        var user = await ctx.Set<ApplicationUser>().AsNoTracking().SingleAsync(u => u.Id == AdminUserId);
        var userManagerMock = BuildUserManagerMock(user);
        var portMock = new Mock<IDatabaseRestorePort>();
        var tablasPedidas = new List<string> { "AgencySettings" };
        portMock.Setup(p => p.RestoreTablesIntoLiveDatabaseAsync(ValidFileName, It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LiveTableRestoreResult(true, null, tablasPedidas, Array.Empty<string>()));
        var service = NewRestoreService(ctx, userManagerMock, portMock);

        var result = await service.ExecuteRestoreAsync(
            AdminUserId, "cualquier-cosa", "RESTAURAR TODO", ValidFileName, RestoreModes.Real, tablasPedidas, null, CancellationToken.None);

        Assert.Equal(RestoreModes.Real, result.Modo);
        Assert.Equal(1, result.TablasRestauradas!.Count);
        Assert.Equal("los datos generales de la agencia", result.TablasRestauradas![0]);
        Assert.Empty(result.TablasSalteadas!);

        await using var verifyCtx = _fixture.CreateDbContext();
        var successLog = await verifyCtx.AuditLogs.SingleAsync(a => a.Action == AuditActions.SystemDataRestored);
        Assert.Equal(AuditActions.SystemDataRestoreEntityName, successLog.EntityName);
    }

    [Fact]
    public async Task ExecuteRestoreAsync_ModoRealRestauraAfipSettings_FuerzaHomologacionYAvisaAlUsuario()
    {
        // Decision firmada del dueño (punto 9 de la revision de seguridad): restaurar AfipSettings JAMAS
        // puede dejar el sistema en modo PRODUCTIVO sin que nadie se entere. Simulamos que el backup traia
        // una fila productiva (IsProduction=true, como si el pg_restore real la hubiera insertado) y
        // verificamos que el service la fuerza a homologacion en la MISMA operacion.
        await using var ctx = _fixture.CreateDbContext();
        await SeedAspNetUserAsync(ctx, AdminUserId);
        ctx.AfipSettings.Add(new AfipSettings { IsProduction = true, Cuit = 20111111112 });
        await ctx.SaveChangesAsync();

        var user = await ctx.Set<ApplicationUser>().AsNoTracking().SingleAsync(u => u.Id == AdminUserId);
        var userManagerMock = BuildUserManagerMock(user);
        var portMock = new Mock<IDatabaseRestorePort>();
        var tablasPedidas = new List<string> { "AfipSettings" };
        portMock.Setup(p => p.RestoreTablesIntoLiveDatabaseAsync(ValidFileName, It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LiveTableRestoreResult(true, null, tablasPedidas, Array.Empty<string>()));
        var service = NewRestoreService(ctx, userManagerMock, portMock);

        var result = await service.ExecuteRestoreAsync(
            AdminUserId, "cualquier-cosa", "RESTAURAR TODO", ValidFileName, RestoreModes.Real, tablasPedidas, null, CancellationToken.None);

        Assert.Contains("homologación", result.Mensaje);

        await using var verifyCtx = _fixture.CreateDbContext();
        var afipSettings = await verifyCtx.AfipSettings.AsNoTracking().SingleAsync();
        Assert.False(afipSettings.IsProduction);

        var successLog = await verifyCtx.AuditLogs.SingleAsync(a => a.Action == AuditActions.SystemDataRestored);
        Assert.Contains("afipForzadoAHomologacion", successLog.Changes);
        Assert.Contains("true", successLog.Changes);
    }

    [Fact]
    public async Task ExecuteRestoreAsync_ModoRealConAfipRepuestaYOtraTablaFallaDespues_FuerzaHomologacionIgual()
    {
        // BLOQUEANTE de seguridad (hallazgo B-N1, ronda de revision): el puerto restaura tabla por tabla y
        // aborta en la PRIMERA que falla. Si AfipSettings es la 2da de varias y la 3ra falla, "Success" da
        // false pero AfipSettings YA quedo repuesta (result.RestoredTables la incluye igual). Antes de este
        // fix, el codigo tiraba la excepcion ANTES de llegar al UPDATE que fuerza homologacion — el sistema
        // podia quedar habilitado para facturar en modo PRODUCTIVO real (CAE) sin que nadie se enterara.
        await using var ctx = _fixture.CreateDbContext();
        await SeedAspNetUserAsync(ctx, AdminUserId);
        // Simulamos que el backup traia una fila productiva, como si pg_restore ya la hubiera insertado antes
        // de que la restauracion de OTRA tabla fallara despues.
        ctx.AfipSettings.Add(new AfipSettings { IsProduction = true, Cuit = 20111111112 });
        await ctx.SaveChangesAsync();

        var user = await ctx.Set<ApplicationUser>().AsNoTracking().SingleAsync(u => u.Id == AdminUserId);
        var userManagerMock = BuildUserManagerMock(user);
        var portMock = new Mock<IDatabaseRestorePort>();
        var tablasPedidas = new List<string> { "AfipSettings", "AgencySettings" };
        portMock.Setup(p => p.RestoreTablesIntoLiveDatabaseAsync(ValidFileName, It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LiveTableRestoreResult(
                false, "pg_restore: error de prueba en AgencySettings",
                RestoredTables: new List<string> { "AfipSettings" }, // AFIP YA se repuso antes de que fallara
                SkippedNonEmptyTables: Array.Empty<string>()));
        var service = NewRestoreService(ctx, userManagerMock, portMock);

        var ex = await Assert.ThrowsAsync<SystemDataRestoreRefusedException>(() =>
            service.ExecuteRestoreAsync(
                AdminUserId, "cualquier-cosa", "RESTAURAR TODO", ValidFileName, RestoreModes.Real, tablasPedidas, null, CancellationToken.None));

        // El mensaje avisa que igual se forzo homologacion, sin nombres tecnicos de tabla ni el stderr crudo.
        Assert.Contains("homologación", ex.Message);
        Assert.DoesNotContain("AfipSettings", ex.Message);
        Assert.DoesNotContain("pg_restore", ex.Message);

        // LO MAS IMPORTANTE: aunque la restauracion fallo, AfipSettings QUEDO en homologacion — nunca en
        // productivo, pase lo que pase con el resto del pedido.
        await using var verifyCtx = _fixture.CreateDbContext();
        var afipSettings = await verifyCtx.AfipSettings.AsNoTracking().SingleAsync();
        Assert.False(afipSettings.IsProduction);

        // El audit del RECHAZO tambien deja constancia de que alcanzo a reponerse (mismo texto que ex.Message).
        var rejectedLog = await verifyCtx.AuditLogs.SingleAsync(a => a.Action == AuditActions.SystemDataRestoreRejected);
        Assert.Contains("homologación", rejectedLog.Changes ?? string.Empty);
        Assert.Equal(ex.Message, rejectedLog.Changes);
    }

    [Fact]
    public async Task ExecuteRestoreAsync_ModoTotal_Exitoso_ElAuditLogDelEventoExisteRecienDespuesDelRestore()
    {
        // Obra "Restaurar TOTAL" (2026-07-28, firmada): la razon de ser de este test contra Postgres REAL (no
        // InMemory) es probar que insertar el AuditLog DESPUES del (fake) restore funciona con la semantica
        // real de EF+Postgres — el IDatabaseRestorePort sigue siendo un fake (el pg_restore real se prueba por
        // construccion, mismo criterio que el resto de esta obra); lo que valida Postgres real es el ORDEN:
        // el evento SystemDataTotallyRestored tiene que EXISTIR reciendespues de que el restore "termino".
        await using var ctx = _fixture.CreateDbContext();
        await SeedAspNetUserAsync(ctx, AdminUserId);

        var user = await ctx.Set<ApplicationUser>().AsNoTracking().SingleAsync(u => u.Id == AdminUserId);
        var userManagerMock = BuildUserManagerMock(user);

        var backupPortMock = NewHappyPathBackupPortMock("pre-restore-20260728-100000.dump");
        var maintenanceMode = new RecordingMaintenanceModeService();
        var portMock = NewHappyPathTotalPortMock(maintenanceMode);

        var service = NewRestoreService(ctx, userManagerMock, portMock, backupPortMock, maintenanceMode);

        var result = await service.ExecuteRestoreAsync(
            AdminUserId, "cualquier-cosa", "RESTAURAR TODO", ValidFileName, RestoreModes.Total, null, ValidMotivo, CancellationToken.None);

        Assert.Equal(RestoreModes.Total, result.Modo);
        Assert.Equal("pre-restore-20260728-100000.dump", result.BackupPrevio);
        Assert.Equal(ValidFileName, result.RestauradoDe);
        Assert.Contains("restauró todo el sistema", result.Mensaje);
        Assert.Contains("sesiones", result.Mensaje); // aviso de que sesiones/contraseñas tambien vuelven a ese dia.
        Assert.False(maintenanceMode.IsActive);
        // "Touch" (hallazgo B-N2(c)) se llama SIEMPRE justo antes de RestoreTotalAsync.
        Assert.Equal(new[] { "Activate", "Touch", "RestoreTotalAsync", "Deactivate" }, maintenanceMode.Calls);

        await using var verifyCtx = _fixture.CreateDbContext();
        var successLog = await verifyCtx.AuditLogs.SingleAsync(a => a.Action == AuditActions.SystemDataTotallyRestored);
        Assert.Equal(AuditActions.SystemDataRestoreEntityName, successLog.EntityName);
        Assert.Contains(ValidFileName, successLog.Changes);
        Assert.Contains("pre-restore-20260728-100000.dump", successLog.Changes);
        Assert.Contains(ValidMotivo, successLog.Changes);
    }

    [Fact]
    public async Task ExecuteRestoreAsync_ModoTotal_PedidoCanceladoDespuesDeUnRestoreExitoso_AfipQuedaEnHomologacionYMantenimientoNoSeApaga()
    {
        // Hallazgo BLOQUEANTE B-N1 de seguridad (2026-07-28): AdminDangerController le pasa
        // HttpContext.RequestAborted como "ct" a ExecuteRestoreAsync. Si el pedido HTTP se cancela DESPUÉS de
        // que el pg_restore YA terminó con éxito (nginx corta la conexión, el admin cierra la pestaña), los
        // pasos posteriores (forzar AFIP a homologación, reponer MinIO, auditar) tienen que completarse
        // IGUAL — nunca abortarse por la cancelación del pedido original, porque la base YA se reemplazó.
        // Antes de este fix, el UPDATE de AFIP heredaba ese "ct" cancelado y tiraba OperationCanceledException,
        // dejando la base con AFIP posiblemente en modo PRODUCTIVO Y el sistema reabierto igual (el "finally"
        // desactivaba el mantenimiento sin saber que la confirmación de AFIP nunca llegó a correr).
        await using var ctx = _fixture.CreateDbContext();
        await SeedAspNetUserAsync(ctx, AdminUserId);
        ctx.AfipSettings.Add(new AfipSettings { IsProduction = false, Cuit = 20111111112 });
        await ctx.SaveChangesAsync();

        var user = await ctx.Set<ApplicationUser>().AsNoTracking().SingleAsync(u => u.Id == AdminUserId);
        var userManagerMock = BuildUserManagerMock(user);
        var backupPortMock = NewHappyPathBackupPortMock();
        var maintenanceMode = new RecordingMaintenanceModeService();

        var portMock = new Mock<IDatabaseRestorePort>();
        portMock
            .Setup(p => p.CheckSchemaCompatibilityAsync(ValidFileName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SchemaCompatibilityResult(true, null));

        using var requestCts = new CancellationTokenSource();
        portMock
            .Setup(p => p.RestoreTotalAsync(ValidFileName, It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                maintenanceMode.Calls.Add("RestoreTotalAsync");

                // Simula: el pg_restore real YA TERMINÓ con éxito, pero justo en este instante el pedido HTTP
                // original se cancela (nginx corta la conexión / se cierra la pestaña) — el "ct" que le pasamos
                // a ExecuteRestoreAsync queda cancelado DESDE ACÁ en adelante.
                requestCts.Cancel();

                // Simula que el backup restaurado traía AfipSettings en modo productivo (como si pg_restore ya
                // lo hubiera insertado así) — para verificar que el UPDATE que fuerza homologación corre IGUAL.
                await using var simulationCtx = _fixture.CreateDbContext();
                await simulationCtx.Database.ExecuteSqlRawAsync("""UPDATE "AfipSettings" SET "IsProduction" = true;""");

                return new TotalRestoreResult(TotalRestoreOutcome.Completed, true, null);
            });

        var service = NewRestoreService(ctx, userManagerMock, portMock, backupPortMock, maintenanceMode);

        // El "ct" que recibe ExecuteRestoreAsync se cancela DURANTE la llamada (dentro del mock de arriba) —
        // simula exactamente el escenario real: el pedido se cancela DESPUÉS de que el pg_restore ya terminó.
        var result = await service.ExecuteRestoreAsync(
            AdminUserId, "cualquier-cosa", "RESTAURAR TODO", ValidFileName, RestoreModes.Total, null, ValidMotivo, requestCts.Token);

        // A pesar de que el "ct" quedó cancelado, el resultado es EXITOSO (no una OperationCanceledException).
        Assert.Equal(RestoreModes.Total, result.Modo);

        await using var verifyCtx = _fixture.CreateDbContext();
        var afipSettings = await verifyCtx.AfipSettings.AsNoTracking().SingleAsync();
        Assert.False(afipSettings.IsProduction); // el UPDATE que fuerza homologacion corrio IGUAL (CancellationToken.None).

        // El mantenimiento se desactiva igual: el restore SÍ fue exitoso Y el UPDATE de AFIP SÍ se pudo confirmar.
        Assert.False(maintenanceMode.IsActive);
        Assert.Equal(new[] { "Activate", "Touch", "RestoreTotalAsync", "Deactivate" }, maintenanceMode.Calls);

        // La auditoria tambien corrio (CancellationToken.None), pese a la cancelacion del pedido original.
        var successLog = await verifyCtx.AuditLogs.SingleAsync(a => a.Action == AuditActions.SystemDataTotallyRestored);
        Assert.Equal(AuditActions.SystemDataRestoreEntityName, successLog.EntityName);
    }

    [Fact]
    public async Task ExecuteRestoreAsync_ModoTotal_ArchivoConNombreDerivable_ReponeLosArchivosDeMinioYAvisaEnElMensaje()
    {
        // Hallazgo B5 de seguridad: si el nombre del backup sigue el esquema propio de esta obra, se intenta
        // reponer los archivos de MinIO respaldados junto con esa foto de la base.
        await using var ctx = _fixture.CreateDbContext();
        await SeedAspNetUserAsync(ctx, AdminUserId);

        var user = await ctx.Set<ApplicationUser>().AsNoTracking().SingleAsync(u => u.Id == AdminUserId);
        var userManagerMock = BuildUserManagerMock(user);

        var backupPortMock = NewHappyPathBackupPortMock();
        backupPortMock
            .Setup(b => b.RestoreObjectsFromBackupPrefixAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(3);

        var maintenanceMode = new RecordingMaintenanceModeService();
        var portMock = NewHappyPathTotalPortMock(maintenanceMode);
        var service = NewRestoreService(ctx, userManagerMock, portMock, backupPortMock, maintenanceMode);

        var result = await service.ExecuteRestoreAsync(
            AdminUserId, "cualquier-cosa", "RESTAURAR TODO", ValidFileName, RestoreModes.Total, null, ValidMotivo, CancellationToken.None);

        Assert.Contains("se repusieron", result.Mensaje);
    }

    [Fact]
    public async Task ExecuteRestoreAsync_ModoTotal_ArchivoConNombreNoDerivable_AvisaClaroQueLosArchivosNoSeRecuperan()
    {
        // Un backup de otro origen (ej. el sidecar diario de docker-compose, o un backup manual) no tiene un
        // resguardo de MinIO asociado con un nombre que se pueda derivar - el mensaje tiene que avisarlo claro,
        // nunca sugerir falsamente que los archivos volvieron.
        const string archivoDeOtroOrigen = "backup-manual-2026-07-28.dump";

        await using var ctx = _fixture.CreateDbContext();
        await SeedAspNetUserAsync(ctx, AdminUserId);

        var user = await ctx.Set<ApplicationUser>().AsNoTracking().SingleAsync(u => u.Id == AdminUserId);
        var userManagerMock = BuildUserManagerMock(user);

        var portMock = new Mock<IDatabaseRestorePort>();
        portMock
            .Setup(p => p.CheckSchemaCompatibilityAsync(archivoDeOtroOrigen, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SchemaCompatibilityResult(true, null));
        portMock
            .Setup(p => p.RestoreTotalAsync(archivoDeOtroOrigen, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TotalRestoreResult(TotalRestoreOutcome.Completed, true, null));

        var backupPortMock = NewHappyPathBackupPortMock();
        var maintenanceMode = new RecordingMaintenanceModeService();
        var service = NewRestoreService(ctx, userManagerMock, portMock, backupPortMock, maintenanceMode);

        var result = await service.ExecuteRestoreAsync(
            AdminUserId, "cualquier-cosa", "RESTAURAR TODO", archivoDeOtroOrigen, RestoreModes.Total, null, ValidMotivo, CancellationToken.None);

        Assert.Contains("no se pudo determinar su resguardo", result.Mensaje);
        backupPortMock.Verify(b => b.RestoreObjectsFromBackupPrefixAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteRestoreAsync_ModoTotal_LaAuditoriaDeExitoFalla_NoConvierteElRestoreExitosoEnUnErrorParaElUsuario()
    {
        // Hallazgo B6 de seguridad ("auditoria fragil"): un restore que YA fue exitoso no puede convertirse en
        // un 500 para el usuario solo porque el INSERT del AuditLog fallo - eso queda logueado, no tirado. Acá
        // se fuerza la falla apuntando el AuditService a una tabla inexistente (real Postgres, error real).
        await using var ctx = _fixture.CreateDbContext();
        await SeedAspNetUserAsync(ctx, AdminUserId);

        var user = await ctx.Set<ApplicationUser>().AsNoTracking().SingleAsync(u => u.Id == AdminUserId);
        var userManagerMock = BuildUserManagerMock(user);

        var backupPortMock = NewHappyPathBackupPortMock();
        var maintenanceMode = new RecordingMaintenanceModeService();
        var portMock = NewHappyPathTotalPortMock(maintenanceMode);

        var auditServiceMock = new Mock<IAuditService>();
        auditServiceMock
            .Setup(a => a.LogBusinessEventAsync(
                AuditActions.SystemDataTotallyRestored,
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom: la base de auditoria no responde"));

        var service = new SystemDataRestoreService(
            ctx, userManagerMock.Object, portMock.Object, backupPortMock.Object, maintenanceMode,
            auditServiceMock.Object, NullLogger<SystemDataRestoreService>.Instance);

        // NO debe tirar - el restore ya fue exitoso, el fallo de auditoria queda solo en el log.
        var result = await service.ExecuteRestoreAsync(
            AdminUserId, "cualquier-cosa", "RESTAURAR TODO", ValidFileName, RestoreModes.Total, null, ValidMotivo, CancellationToken.None);

        Assert.Equal(RestoreModes.Total, result.Modo);
        Assert.False(maintenanceMode.IsActive);
    }

    [Fact]
    public async Task ExecuteRestoreAsync_ModoTotal_PgRestoreFalla_DesactivaMantenimientoYAuditaElRechazoConPostgresReal()
    {
        await using var ctx = _fixture.CreateDbContext();
        await SeedAspNetUserAsync(ctx, AdminUserId);

        var user = await ctx.Set<ApplicationUser>().AsNoTracking().SingleAsync(u => u.Id == AdminUserId);
        var userManagerMock = BuildUserManagerMock(user);

        var backupPortMock = NewHappyPathBackupPortMock();

        var portMock = new Mock<IDatabaseRestorePort>();
        portMock
            .Setup(p => p.CheckSchemaCompatibilityAsync(ValidFileName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SchemaCompatibilityResult(true, null));
        portMock
            .Setup(p => p.RestoreTotalAsync(ValidFileName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TotalRestoreResult(TotalRestoreOutcome.Completed, false, "pg_restore: error de prueba"));

        var maintenanceMode = new RecordingMaintenanceModeService();
        var service = NewRestoreService(ctx, userManagerMock, portMock, backupPortMock, maintenanceMode);

        var ex = await Assert.ThrowsAsync<SystemDataRestoreRefusedException>(() =>
            service.ExecuteRestoreAsync(
                AdminUserId, "cualquier-cosa", "RESTAURAR TODO", ValidFileName, RestoreModes.Total, null, ValidMotivo, CancellationToken.None));

        Assert.DoesNotContain("pg_restore", ex.Message);
        Assert.DoesNotContain(".dump", ex.Message); // hallazgo de data-exposure: nunca el nombre del backup previo en el mensaje de error.
        Assert.False(maintenanceMode.IsActive);
        Assert.Equal(new[] { "Activate", "Touch", "Deactivate" }, maintenanceMode.Calls);

        await using var verifyCtx = _fixture.CreateDbContext();
        Assert.False(await verifyCtx.AuditLogs.AnyAsync(a => a.Action == AuditActions.SystemDataTotallyRestored));
        var rejectedLog = await verifyCtx.AuditLogs.SingleAsync(a => a.Action == AuditActions.SystemDataRestoreRejected);
        Assert.Equal(ex.Message, rejectedLog.Changes);
    }

    [Fact]
    public async Task ExecuteRestoreAsync_ModoTotal_ConAfipProductivoEnLaBaseYaRestaurada_FuerzaHomologacionYAvisa()
    {
        // Hallazgo B3 de seguridad (2026-07-28): un resguardo viejo puede traer AfipSettings.IsProduction=true.
        // Para simular esto de verdad (sin un pg_restore real): arrancamos con AfipSettings en homologacion
        // (para que el candado fiscal B2 NO bloquee el intento) y el CALLBACK del (fake) RestoreTotalAsync es
        // el que muta la fila a IsProduction=true CON SQL CRUDO, en una conexion aparte — asi imitamos lo que
        // haria el pg_restore real ANTES de que el service corra el UPDATE que fuerza homologacion despues.
        await using var ctx = _fixture.CreateDbContext();
        await SeedAspNetUserAsync(ctx, AdminUserId);
        ctx.AfipSettings.Add(new AfipSettings { IsProduction = false, Cuit = 20111111112 });
        await ctx.SaveChangesAsync();

        var user = await ctx.Set<ApplicationUser>().AsNoTracking().SingleAsync(u => u.Id == AdminUserId);
        var userManagerMock = BuildUserManagerMock(user);
        var backupPortMock = NewHappyPathBackupPortMock();
        var maintenanceMode = new RecordingMaintenanceModeService();

        var portMock = new Mock<IDatabaseRestorePort>();
        portMock
            .Setup(p => p.CheckSchemaCompatibilityAsync(ValidFileName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SchemaCompatibilityResult(true, null));
        portMock
            .Setup(p => p.RestoreTotalAsync(ValidFileName, It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                maintenanceMode.Calls.Add("RestoreTotalAsync");
                await using var restoreSimulationCtx = _fixture.CreateDbContext();
                await restoreSimulationCtx.Database.ExecuteSqlRawAsync(
                    """UPDATE "AfipSettings" SET "IsProduction" = true;""");
                return new TotalRestoreResult(TotalRestoreOutcome.Completed, true, null);
            });

        var service = NewRestoreService(ctx, userManagerMock, portMock, backupPortMock, maintenanceMode);

        var result = await service.ExecuteRestoreAsync(
            AdminUserId, "cualquier-cosa", "RESTAURAR TODO", ValidFileName, RestoreModes.Total, null, ValidMotivo, CancellationToken.None);

        Assert.Contains("sesiones", result.Mensaje);

        await using var verifyCtx = _fixture.CreateDbContext();
        var afipSettings = await verifyCtx.AfipSettings.AsNoTracking().SingleAsync();
        Assert.False(afipSettings.IsProduction);

        var successLog = await verifyCtx.AuditLogs.SingleAsync(a => a.Action == AuditActions.SystemDataTotallyRestored);
        Assert.Contains("afipForzadoAHomologacion", successLog.Changes);
    }

    [Fact]
    public async Task ExecuteRestoreAsync_ModoTotal_ConFacturaProductivaEnLaBaseVivaAntesDeArrancar_RechazaSinTocarNiBackupNiRestore()
    {
        // Hallazgo B2 de seguridad (2026-07-28): MISMO candado fiscal que "Empezar de cero" — un comprobante
        // REAL (emitido en produccion) en la base VIVA (antes de arrancar, no en el dump) tiene que frenar
        // TODO, incluso una restauracion total.
        await using var ctx = _fixture.CreateDbContext();
        await SeedAspNetUserAsync(ctx, AdminUserId);
        ctx.Invoices.Add(new Invoice
        {
            TipoComprobante = 1,
            PuntoDeVenta = 1,
            NumeroComprobante = 1,
            Resultado = "A",
            CAE = "99999999999999",
            WasIssuedInProduction = true,
        });
        await ctx.SaveChangesAsync();

        var user = await ctx.Set<ApplicationUser>().AsNoTracking().SingleAsync(u => u.Id == AdminUserId);
        var userManagerMock = BuildUserManagerMock(user);
        var backupPortMock = new Mock<IWipeBackupPort>();
        var maintenanceMode = new RecordingMaintenanceModeService();
        var portMock = new Mock<IDatabaseRestorePort>();
        portMock
            .Setup(p => p.CheckSchemaCompatibilityAsync(ValidFileName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SchemaCompatibilityResult(true, null));

        var service = NewRestoreService(ctx, userManagerMock, portMock, backupPortMock, maintenanceMode);

        var ex = await Assert.ThrowsAsync<SystemDataRestoreRefusedException>(() =>
            service.ExecuteRestoreAsync(
                AdminUserId, "cualquier-cosa", "RESTAURAR TODO", ValidFileName, RestoreModes.Total, null, ValidMotivo, CancellationToken.None));

        Assert.Contains("productivo", ex.Message);
        // El candado de concurrencia (mantenimiento) SI se activa (es el primer paso) y se desactiva al
        // rechazar por el candado fiscal - nunca queda "colgado".
        Assert.Equal(new[] { "Activate", "Deactivate" }, maintenanceMode.Calls);
        Assert.False(maintenanceMode.IsActive);
        backupPortMock.Verify(b => b.CreateBackupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        portMock.Verify(p => p.RestoreTotalAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
