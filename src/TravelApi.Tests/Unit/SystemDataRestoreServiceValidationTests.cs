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
/// Obra "Restaurar desde la app" (2026-07-27, Parte B firmada): cubre las validaciones de
/// <see cref="SystemDataRestoreService"/> que corren ANTES de tocar un archivo o una conexión real — frase
/// exacta, contraseña, nombre de archivo (path traversal), modo válido, y la lista blanca de tablas para el
/// modo <c>real</c>. Todos estos caminos tiran <see cref="SystemDataRestoreRefusedException"/> SIN llamar al
/// <see cref="IDatabaseRestorePort"/> (se verifica con <c>Mock.Verify(..., Times.Never)</c>). El chequeo de
/// "¿la tabla ya tiene datos?" necesita SQL crudo contra Postgres real (InMemory no soporta
/// <c>SqlQueryRaw</c>) — vive en <c>SystemDataRestoreServiceIntegrationTests</c> (Category=Integration).
/// </summary>
public class SystemDataRestoreServiceValidationTests
{
    private const string ValidPhrase = "RESTAURAR TODO";
    private const string ValidPassword = "Correcta123!";
    private const string RequesterUserId = "admin-1";
    private const string ValidFileName = "wipe-20260727-120000.dump";

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

    private static SystemDataRestoreService NewService(
        AppDbContext context,
        Mock<UserManager<ApplicationUser>> userManagerMock,
        Mock<IDatabaseRestorePort> portMock,
        Mock<IAuditService>? auditServiceMock = null,
        Mock<IWipeBackupPort>? backupPortMock = null,
        RecordingMaintenanceModeService? maintenanceModeService = null)
    {
        return new SystemDataRestoreService(
            context,
            userManagerMock.Object,
            portMock.Object,
            (backupPortMock ?? new Mock<IWipeBackupPort>()).Object,
            maintenanceModeService ?? new RecordingMaintenanceModeService(),
            (auditServiceMock ?? new Mock<IAuditService>()).Object,
            NullLogger<SystemDataRestoreService>.Instance);
    }

    [Fact]
    public async Task ExecuteRestoreAsync_ConFraseIncorrecta_RechazaSinConsultarUsuarioNiElPuerto()
    {
        var context = NewContext();
        var userManagerMock = BuildUserManagerMock();
        var portMock = new Mock<IDatabaseRestorePort>();
        var auditServiceMock = new Mock<IAuditService>();
        var service = NewService(context, userManagerMock, portMock, auditServiceMock);

        var ex = await Assert.ThrowsAsync<SystemDataRestoreRefusedException>(() =>
            service.ExecuteRestoreAsync(RequesterUserId, ValidPassword, "restaurar todo", ValidFileName, RestoreModes.Prueba, null, null, CancellationToken.None));

        Assert.Contains("RESTAURAR TODO", ex.Message);
        userManagerMock.Verify(m => m.FindByIdAsync(It.IsAny<string>()), Times.Never);
        portMock.Verify(p => p.RestoreToShadowDatabaseAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        auditServiceMock.Verify(a => a.LogBusinessEventAsync(
            AuditActions.SystemDataRestoreRejected,
            AuditActions.SystemDataRestoreEntityName,
            It.IsAny<string>(),
            It.Is<string>(details => details!.Contains("RESTAURAR TODO") && !details.Contains(ValidPassword)),
            RequesterUserId,
            null,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteRestoreAsync_ConContraseñaIncorrecta_Rechaza()
    {
        var context = NewContext();
        var userManagerMock = BuildUserManagerMock(passwordOk: false);
        var portMock = new Mock<IDatabaseRestorePort>();
        var service = NewService(context, userManagerMock, portMock);

        var ex = await Assert.ThrowsAsync<SystemDataRestoreRefusedException>(() =>
            service.ExecuteRestoreAsync(RequesterUserId, ValidPassword, ValidPhrase, ValidFileName, RestoreModes.Prueba, null, null, CancellationToken.None));

        Assert.Equal("La contraseña no es correcta.", ex.Message);
        portMock.Verify(p => p.RestoreToShadowDatabaseAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("../secreto.dump")]
    [InlineData("sub/archivo.dump")]
    [InlineData("")]
    public async Task ExecuteRestoreAsync_ConNombreDeArchivoInvalido_RechazaSinLlamarAlPuerto(string archivoMalicioso)
    {
        var context = NewContext();
        var userManagerMock = BuildUserManagerMock();
        var portMock = new Mock<IDatabaseRestorePort>();
        var service = NewService(context, userManagerMock, portMock);

        await Assert.ThrowsAsync<SystemDataRestoreRefusedException>(() =>
            service.ExecuteRestoreAsync(RequesterUserId, ValidPassword, ValidPhrase, archivoMalicioso, RestoreModes.Prueba, null, null, CancellationToken.None));

        portMock.Verify(p => p.RestoreToShadowDatabaseAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteRestoreAsync_ConModoDesconocido_Rechaza()
    {
        var context = NewContext();
        var userManagerMock = BuildUserManagerMock();
        var portMock = new Mock<IDatabaseRestorePort>();
        var service = NewService(context, userManagerMock, portMock);

        var ex = await Assert.ThrowsAsync<SystemDataRestoreRefusedException>(() =>
            service.ExecuteRestoreAsync(RequesterUserId, ValidPassword, ValidPhrase, ValidFileName, "produccion", null, null, CancellationToken.None));

        Assert.Contains("prueba", ex.Message);
        Assert.Contains("real", ex.Message);
        Assert.Contains("total", ex.Message);
    }

    [Fact]
    public async Task ExecuteRestoreAsync_ModoRealSinTablas_Rechaza()
    {
        var context = NewContext();
        var userManagerMock = BuildUserManagerMock();
        var portMock = new Mock<IDatabaseRestorePort>();
        var service = NewService(context, userManagerMock, portMock);

        var ex = await Assert.ThrowsAsync<SystemDataRestoreRefusedException>(() =>
            service.ExecuteRestoreAsync(RequesterUserId, ValidPassword, ValidPhrase, ValidFileName, RestoreModes.Real, null, null, CancellationToken.None));

        Assert.Contains("al menos una tabla", ex.Message);
        portMock.Verify(p => p.RestoreTablesIntoLiveDatabaseAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteRestoreAsync_ModoRealConTablaFueraDeListaBlanca_RechazaSinLlamarAlPuerto()
    {
        var context = NewContext();
        var userManagerMock = BuildUserManagerMock();
        var portMock = new Mock<IDatabaseRestorePort>();
        var service = NewService(context, userManagerMock, portMock);

        var ex = await Assert.ThrowsAsync<SystemDataRestoreRefusedException>(() =>
            service.ExecuteRestoreAsync(
                RequesterUserId, ValidPassword, ValidPhrase, ValidFileName, RestoreModes.Real,
                new List<string> { "Customers" }, null, CancellationToken.None));

        Assert.Contains("configuración", ex.Message);
        portMock.Verify(p => p.RestoreTablesIntoLiveDatabaseAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task VerifyBackupAsync_ConNombreDeArchivoInvalido_RechazaSinLlamarAlPuertoYAuditaElRechazo()
    {
        var context = NewContext();
        var userManagerMock = BuildUserManagerMock();
        var portMock = new Mock<IDatabaseRestorePort>();
        var auditServiceMock = new Mock<IAuditService>();
        var service = NewService(context, userManagerMock, portMock, auditServiceMock);

        await Assert.ThrowsAsync<SystemDataRestoreRefusedException>(() =>
            service.VerifyBackupAsync(RequesterUserId, "../fuera-del-directorio.dump", CancellationToken.None));

        portMock.Verify(p => p.VerifyBackupAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        // Hallazgo menor de seguridad: un intento de verificacion invalido tambien queda auditado.
        auditServiceMock.Verify(a => a.LogBusinessEventAsync(
            AuditActions.SystemDataRestoreRejected,
            AuditActions.SystemDataRestoreEntityName,
            It.IsAny<string>(),
            It.IsAny<string>(),
            RequesterUserId,
            null,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task VerifyBackupAsync_ConArchivoValido_DelegaAlPuertoYMapeaElResultado()
    {
        var context = NewContext();
        var userManagerMock = BuildUserManagerMock();
        var portMock = new Mock<IDatabaseRestorePort>();
        portMock.Setup(p => p.VerifyBackupAsync(ValidFileName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RestoreVerifyResult(true, null, 42, true));
        var service = NewService(context, userManagerMock, portMock);

        var result = await service.VerifyBackupAsync(RequesterUserId, ValidFileName, CancellationToken.None);

        Assert.True(result.Valido);
        Assert.Null(result.Motivo);
        Assert.Equal(42, result.CantidadTablas);
        Assert.True(result.TieneTablasClave);
    }

    [Fact]
    public async Task VerifyBackupAsync_ConArchivoCorrupto_NuncaExponeElMotivoTecnicoDelPuerto()
    {
        var context = NewContext();
        var userManagerMock = BuildUserManagerMock();
        var portMock = new Mock<IDatabaseRestorePort>();
        portMock.Setup(p => p.VerifyBackupAsync(ValidFileName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RestoreVerifyResult(false, "pg_restore: error: input file appears to be a text format dump", 0, false));
        var service = NewService(context, userManagerMock, portMock);

        var result = await service.VerifyBackupAsync(RequesterUserId, ValidFileName, CancellationToken.None);

        Assert.False(result.Valido);
        Assert.DoesNotContain("pg_restore", result.Motivo);
    }

    [Fact]
    public async Task ListBackupsAsync_MapeaLosArchivosDelPuerto()
    {
        var context = NewContext();
        var userManagerMock = BuildUserManagerMock();
        var portMock = new Mock<IDatabaseRestorePort>();
        var fecha = new DateTime(2026, 7, 27, 12, 0, 0, DateTimeKind.Utc);
        portMock.Setup(p => p.ListBackupsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<BackupFileInfo> { new(ValidFileName, fecha, 12345) });
        var service = NewService(context, userManagerMock, portMock);

        var result = await service.ListBackupsAsync(CancellationToken.None);

        var backup = Assert.Single(result.Backups);
        Assert.Equal(ValidFileName, backup.Archivo);
        Assert.Equal(fecha, backup.FechaUtc);
        Assert.Equal(12345, backup.TamanioBytes);
    }
}
