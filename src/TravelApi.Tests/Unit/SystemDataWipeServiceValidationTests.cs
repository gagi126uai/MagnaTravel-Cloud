using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.Constants;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Obra "Empezar de cero" (2026-07-27): cubre las validaciones de <see cref="SystemDataWipeService"/> que
/// corren ANTES de tocar un solo dato — frase exacta, contraseña, candado fiscal y falla del backup. Todos
/// estos caminos tiran <see cref="SystemDataWipeRefusedException"/> y NUNCA llegan al TRUNCATE (SQL crudo que
/// requiere Postgres real); por eso alcanza InMemory acá. El TRUNCATE + reseed de configuración/CommissionRules
/// generales + AuditLog contra Postgres real vive en <c>SystemDataWipeServiceIntegrationTests</c>
/// (Category=Integration).
/// </summary>
public class SystemDataWipeServiceValidationTests
{
    private const string ValidPhrase = "BORRAR TODO";
    private const string ValidPassword = "Correcta123!";
    private const string RequesterUserId = "admin-1";

    private static AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>
    /// Mock DIRECTO de <c>UserManager&lt;ApplicationUser&gt;</c> (Moq puede mockear la clase porque sus
    /// miembros son <c>virtual</c>): permite controlar <c>FindByIdAsync</c>/<c>CheckPasswordAsync</c> sin
    /// levantar el stack completo de ASP.NET Identity. Mismos argumentos "vacios" que el patron
    /// <c>BuildUserManager()</c> ya usado en otros tests del repo (ej. <c>Adr020LockGuardTests</c>).
    /// </summary>
    private static Mock<UserManager<ApplicationUser>> BuildUserManagerMock()
    {
        var store = new Mock<IUserStore<ApplicationUser>>();
        return new Mock<UserManager<ApplicationUser>>(
            store.Object, null!, null!,
            Array.Empty<IUserValidator<ApplicationUser>>(),
            Array.Empty<IPasswordValidator<ApplicationUser>>(),
            null!, null!, null!, null!);
    }

    private static SystemDataWipeService NewService(
        AppDbContext context,
        Mock<UserManager<ApplicationUser>> userManagerMock,
        IWipeBackupPort? backupPort = null,
        Mock<IAuditService>? auditServiceMock = null)
    {
        return new SystemDataWipeService(
            context,
            userManagerMock.Object,
            backupPort ?? new AlwaysSucceedsBackupPort(),
            (auditServiceMock ?? new Mock<IAuditService>()).Object,
            NullLogger<SystemDataWipeService>.Instance);
    }

    /// <summary>Fake de backup que siempre "tiene éxito" — usado en los tests que NO ejercitan el backup en sí.</summary>
    private sealed class AlwaysSucceedsBackupPort : IWipeBackupPort
    {
        public Task<WipeBackupResult> CreateBackupAsync(string backupFileName, string minioPrefix, CancellationToken ct)
            => Task.FromResult(new WipeBackupResult(true, backupFileName, minioPrefix, null));

        public Task RemoveOriginalObjectsAsync(WipeBackupResult backupResult, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class AlwaysFailsBackupPort : IWipeBackupPort
    {
        public Task<WipeBackupResult> CreateBackupAsync(string backupFileName, string minioPrefix, CancellationToken ct)
            => Task.FromResult(new WipeBackupResult(false, null, null, "boom (detalle tecnico que NUNCA debe llegar al usuario)"));

        public Task RemoveOriginalObjectsAsync(WipeBackupResult backupResult, CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public async Task ExecuteWipeAsync_ConFraseIncorrecta_RechazaSinConsultarUsuario()
    {
        var context = NewContext();
        var userManagerMock = BuildUserManagerMock();
        var auditServiceMock = new Mock<IAuditService>();
        var service = NewService(context, userManagerMock, auditServiceMock: auditServiceMock);

        var ex = await Assert.ThrowsAsync<SystemDataWipeRefusedException>(() =>
            service.ExecuteWipeAsync(RequesterUserId, ValidPassword, "borrar todo", incluirConfiguracion: false, CancellationToken.None));

        Assert.Contains("BORRAR TODO", ex.Message);
        // La frase se valida ANTES que nada: ni siquiera se busca al usuario.
        userManagerMock.Verify(m => m.FindByIdAsync(It.IsAny<string>()), Times.Never);
        // Fix menor #6: TODO rechazo queda auditado (accion SystemDataWipeRejected, motivo, jamas la contraseña).
        auditServiceMock.Verify(a => a.LogBusinessEventAsync(
            AuditActions.SystemDataWipeRejected,
            AuditActions.SystemDataWipeEntityName,
            It.IsAny<string>(),
            It.Is<string>(details => details!.Contains("BORRAR TODO") && !details.Contains(ValidPassword)),
            RequesterUserId,
            null,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteWipeAsync_ConContraseñaIncorrecta_Rechaza()
    {
        var context = NewContext();
        var userManagerMock = BuildUserManagerMock();
        var user = new ApplicationUser { Id = RequesterUserId, UserName = "admin", FullName = "Admin Uno" };
        userManagerMock.Setup(m => m.FindByIdAsync(RequesterUserId)).ReturnsAsync(user);
        userManagerMock.Setup(m => m.CheckPasswordAsync(user, ValidPassword)).ReturnsAsync(false);
        var auditServiceMock = new Mock<IAuditService>();
        var service = NewService(context, userManagerMock, auditServiceMock: auditServiceMock);

        var ex = await Assert.ThrowsAsync<SystemDataWipeRefusedException>(() =>
            service.ExecuteWipeAsync(RequesterUserId, ValidPassword, ValidPhrase, incluirConfiguracion: false, CancellationToken.None));

        Assert.Equal("La contraseña no es correcta.", ex.Message);
        // La contraseña provista NUNCA debe llegar al detalle del audit log (ni la real ni la incorrecta).
        auditServiceMock.Verify(a => a.LogBusinessEventAsync(
            AuditActions.SystemDataWipeRejected,
            AuditActions.SystemDataWipeEntityName,
            It.IsAny<string>(),
            It.Is<string>(details => !details!.Contains(ValidPassword)),
            RequesterUserId,
            null,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteWipeAsync_ConUsuarioInexistente_RechazaConMensajeGenerico()
    {
        var context = NewContext();
        var userManagerMock = BuildUserManagerMock();
        userManagerMock.Setup(m => m.FindByIdAsync(RequesterUserId)).ReturnsAsync((ApplicationUser?)null);
        var service = NewService(context, userManagerMock);

        var ex = await Assert.ThrowsAsync<SystemDataWipeRefusedException>(() =>
            service.ExecuteWipeAsync(RequesterUserId, ValidPassword, ValidPhrase, incluirConfiguracion: false, CancellationToken.None));

        // Mismo mensaje generico que "contraseña incorrecta": no delata si el userId existe o no.
        Assert.Equal("La contraseña no es correcta.", ex.Message);
    }

    [Fact]
    public async Task ExecuteWipeAsync_ConComprobanteMarcadoProduccion_RechazaPorCandadoFiscal()
    {
        var context = NewContext();
        context.Invoices.Add(new Invoice
        {
            TipoComprobante = 1,
            PuntoDeVenta = 1,
            NumeroComprobante = 1,
            CAE = "12345678901234",
            Resultado = "A",
            WasIssuedInProduction = true,
        });
        await context.SaveChangesAsync();

        var userManagerMock = BuildUserManagerMock();
        var user = new ApplicationUser { Id = RequesterUserId, UserName = "admin", FullName = "Admin Uno" };
        userManagerMock.Setup(m => m.FindByIdAsync(RequesterUserId)).ReturnsAsync(user);
        userManagerMock.Setup(m => m.CheckPasswordAsync(user, ValidPassword)).ReturnsAsync(true);
        var service = NewService(context, userManagerMock);

        var ex = await Assert.ThrowsAsync<SystemDataWipeRefusedException>(() =>
            service.ExecuteWipeAsync(RequesterUserId, ValidPassword, ValidPhrase, incluirConfiguracion: false, CancellationToken.None));

        Assert.Contains("comprobantes emitidos en modo productivo", ex.Message);
    }

    [Fact]
    public async Task ExecuteWipeAsync_ConAfipEnProduccionYComprobanteHistoricoSinMarca_Rechaza()
    {
        var context = NewContext();
        // Historico SIN la marca (emitido antes de que existiera WasIssuedInProduction): CAE no nulo,
        // WasIssuedInProduction en null. El candado tiene que agarrarlo igual via AfipSettings.IsProduction.
        context.Invoices.Add(new Invoice
        {
            TipoComprobante = 1,
            PuntoDeVenta = 1,
            NumeroComprobante = 1,
            CAE = "12345678901234",
            Resultado = "A",
            WasIssuedInProduction = null,
        });
        context.AfipSettings.Add(new AfipSettings { IsProduction = true, Cuit = 20111111112 });
        await context.SaveChangesAsync();

        var userManagerMock = BuildUserManagerMock();
        var user = new ApplicationUser { Id = RequesterUserId, UserName = "admin", FullName = "Admin Uno" };
        userManagerMock.Setup(m => m.FindByIdAsync(RequesterUserId)).ReturnsAsync(user);
        userManagerMock.Setup(m => m.CheckPasswordAsync(user, ValidPassword)).ReturnsAsync(true);
        var service = NewService(context, userManagerMock);

        var ex = await Assert.ThrowsAsync<SystemDataWipeRefusedException>(() =>
            service.ExecuteWipeAsync(RequesterUserId, ValidPassword, ValidPhrase, incluirConfiguracion: false, CancellationToken.None));

        Assert.Contains("AFIP está en modo productivo", ex.Message);
    }

    [Fact]
    public async Task ExecuteWipeAsync_ConAfipEnProduccionSinNingunaFactura_RechazaIgual()
    {
        // Hardening final (revision 2026-07-27, prescripto por seguridad): el ambiente productivo de AFIP
        // basta por si solo para frenar el wipe, sin necesidad de que exista ningun comprobante todavia.
        var context = NewContext();
        context.AfipSettings.Add(new AfipSettings { IsProduction = true, Cuit = 20111111112 });
        await context.SaveChangesAsync();

        var userManagerMock = BuildUserManagerMock();
        var user = new ApplicationUser { Id = RequesterUserId, UserName = "admin", FullName = "Admin Uno" };
        userManagerMock.Setup(m => m.FindByIdAsync(RequesterUserId)).ReturnsAsync(user);
        userManagerMock.Setup(m => m.CheckPasswordAsync(user, ValidPassword)).ReturnsAsync(true);
        var service = NewService(context, userManagerMock);

        var ex = await Assert.ThrowsAsync<SystemDataWipeRefusedException>(() =>
            service.ExecuteWipeAsync(RequesterUserId, ValidPassword, ValidPhrase, incluirConfiguracion: false, CancellationToken.None));

        Assert.Equal(
            "AFIP está en modo productivo: pasá a homologación antes de borrar datos. Los comprobantes reales no se tocan.",
            ex.Message);
    }

    [Fact]
    public async Task ExecuteWipeAsync_ConAfipEnHomologacionSinMarca_NoBloquea()
    {
        var context = NewContext();
        context.Invoices.Add(new Invoice
        {
            TipoComprobante = 1,
            PuntoDeVenta = 1,
            NumeroComprobante = 1,
            CAE = "12345678901234",
            Resultado = "A",
            WasIssuedInProduction = null,
        });
        context.AfipSettings.Add(new AfipSettings { IsProduction = false, Cuit = 20111111112 });
        await context.SaveChangesAsync();

        var (bloqueado, motivo) = await EvaluateLockViaPreviewAsync(context);
        Assert.False(bloqueado);
        Assert.Null(motivo);
    }

    private static async Task<(bool Bloqueado, string? Motivo)> EvaluateLockViaPreviewAsync(AppDbContext context)
    {
        var userManagerMock = BuildUserManagerMock();
        var service = NewService(context, userManagerMock);
        var preview = await service.GetPreviewAsync(CancellationToken.None);
        return (preview.Bloqueado, preview.MotivoBloqueo);
    }

    [Fact]
    public async Task ExecuteWipeAsync_ConBackupQueFalla_RechazaSinTocarDatos()
    {
        var context = NewContext();
        context.Customers.Add(new Customer { FullName = "Cliente de prueba" });
        await context.SaveChangesAsync();

        var userManagerMock = BuildUserManagerMock();
        var user = new ApplicationUser { Id = RequesterUserId, UserName = "admin", FullName = "Admin Uno" };
        userManagerMock.Setup(m => m.FindByIdAsync(RequesterUserId)).ReturnsAsync(user);
        userManagerMock.Setup(m => m.CheckPasswordAsync(user, ValidPassword)).ReturnsAsync(true);
        var service = NewService(context, userManagerMock, new AlwaysFailsBackupPort());

        var ex = await Assert.ThrowsAsync<SystemDataWipeRefusedException>(() =>
            service.ExecuteWipeAsync(RequesterUserId, ValidPassword, ValidPhrase, incluirConfiguracion: false, CancellationToken.None));

        Assert.DoesNotContain("boom", ex.Message);
        Assert.Contains("No se pudo generar el backup previo", ex.Message);
        // Nada se toco: el cliente sigue ahi.
        Assert.Equal(1, await context.Customers.CountAsync());
    }

    [Fact]
    public async Task GetPreviewAsync_SinComprobantesProductivos_NoBloqueaYReflejaConteos()
    {
        var context = NewContext();
        context.Customers.Add(new Customer { FullName = "Cliente 1" });
        context.Customers.Add(new Customer { FullName = "Cliente 2" });
        context.Suppliers.Add(new Supplier { Name = "Operador 1" });
        await context.SaveChangesAsync();

        var userManagerMock = BuildUserManagerMock();
        var service = NewService(context, userManagerMock);

        var preview = await service.GetPreviewAsync(CancellationToken.None);

        Assert.False(preview.Bloqueado);
        Assert.Null(preview.MotivoBloqueo);
        Assert.Equal(2, preview.Conteos.Clientes);
        Assert.Equal(1, preview.Conteos.Operadores);
    }
}
