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
/// Obra "Restaurar desde la app" (2026-07-27, Parte B firmada) + ronda de fixes de seguridad (mismo día):
/// cubre los tramos de <see cref="SystemDataRestoreService"/> que necesitan SQL real contra Postgres — el
/// forzado de AFIP a homologación (<c>ExecuteSqlRawAsync</c>) y el armado de la respuesta/auditoría con
/// nombres de negocio. El resto de las validaciones (frase, contraseña, archivo, modo, lista blanca) están
/// cubiertas con InMemory en <c>SystemDataRestoreServiceValidationTests</c>. El <see cref="IDatabaseRestorePort"/>
/// se inyecta como fake — el <c>pg_restore</c>/<c>pg_dump</c> reales se prueban por construcción (mismo
/// criterio que <c>PgDumpAndMinioWipeBackupPort</c>).
/// </summary>
[Trait("Category", "Integration")]
public sealed class SystemDataRestoreServiceIntegrationTests : IClassFixture<PostgresIntegrationFixture>, IAsyncLifetime
{
    private const string AdminUserId = "restore-admin-1";
    private const string ValidFileName = "wipe-20260727-120000.dump";

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
            TRUNCATE TABLE "AgencySettings", "AfipSettings", "AuditLogs", "AspNetUsers" RESTART IDENTITY CASCADE;
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
        AppDbContext ctx, Mock<UserManager<ApplicationUser>> userManagerMock, Mock<IDatabaseRestorePort> portMock)
    {
        var auditService = new AuditService(new Repository<AuditLog>(ctx), NullLogger<AuditService>.Instance);
        return new SystemDataRestoreService(ctx, userManagerMock.Object, portMock.Object, auditService, NullLogger<SystemDataRestoreService>.Instance);
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
            AdminUserId, "cualquier-cosa", "RESTAURAR TODO", ValidFileName, RestoreModes.Real, tablasPedidas, CancellationToken.None);

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
            AdminUserId, "cualquier-cosa", "RESTAURAR TODO", ValidFileName, RestoreModes.Real, tablasPedidas, CancellationToken.None);

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
            AdminUserId, "cualquier-cosa", "RESTAURAR TODO", ValidFileName, RestoreModes.Real, tablasPedidas, CancellationToken.None);

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
                AdminUserId, "cualquier-cosa", "RESTAURAR TODO", ValidFileName, RestoreModes.Real, tablasPedidas, CancellationToken.None));

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
}
