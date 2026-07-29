using System;
using System.Collections.Generic;
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
/// Obra "Empezar de cero" (2026-07-27) + Parte A "Borrado selectivo por grupos" (2026-07-27, firmada): cubre
/// las validaciones de <see cref="SystemDataWipeService"/> que corren ANTES de tocar un solo dato — frase
/// exacta, contraseña, grupos válidos/coherentes con sus dependencias, candado fiscal (scoping por grupo) y
/// falla del backup. Todos estos caminos tiran <see cref="SystemDataWipeRefusedException"/> y NUNCA llegan al
/// TRUNCATE (SQL crudo que requiere Postgres real); por eso alcanza InMemory acá. El TRUNCATE + detach de
/// referencias cruzadas + reseed de configuración/CommissionRules generales + AuditLog contra Postgres real
/// vive en <c>SystemDataWipeServiceIntegrationTests</c> (Category=Integration).
/// </summary>
public class SystemDataWipeServiceValidationTests
{
    private const string ValidPhrase = "BORRAR TODO";
    private const string ValidPassword = "Correcta123!";
    private const string RequesterUserId = "admin-1";

    private static readonly string[] ReservasYPlataOnly = { WipeGroups.ReservasYPlata };

    private static AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

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

    private sealed class AlwaysSucceedsBackupPort : IWipeBackupPort
    {
        public Task<WipeBackupResult> CreateBackupAsync(string backupFileName, string minioPrefix, CancellationToken ct)
            => Task.FromResult(new WipeBackupResult(true, backupFileName, minioPrefix, null));

        public Task RemoveOriginalObjectsAsync(WipeBackupResult backupResult, CancellationToken ct) => Task.CompletedTask;

        public Task<int> RestoreObjectsFromBackupPrefixAsync(string minioPrefix, CancellationToken ct) => Task.FromResult(0);
    }

    private sealed class AlwaysFailsBackupPort : IWipeBackupPort
    {
        public Task<WipeBackupResult> CreateBackupAsync(string backupFileName, string minioPrefix, CancellationToken ct)
            => Task.FromResult(new WipeBackupResult(false, null, null, "boom (detalle tecnico que NUNCA debe llegar al usuario)"));

        public Task RemoveOriginalObjectsAsync(WipeBackupResult backupResult, CancellationToken ct) => Task.CompletedTask;

        public Task<int> RestoreObjectsFromBackupPrefixAsync(string minioPrefix, CancellationToken ct) => Task.FromResult(0);
    }

    [Fact]
    public async Task ExecuteWipeAsync_ConFraseIncorrecta_RechazaSinConsultarUsuario()
    {
        var context = NewContext();
        var userManagerMock = BuildUserManagerMock();
        var auditServiceMock = new Mock<IAuditService>();
        var service = NewService(context, userManagerMock, auditServiceMock: auditServiceMock);

        var ex = await Assert.ThrowsAsync<SystemDataWipeRefusedException>(() =>
            service.ExecuteWipeAsync(RequesterUserId, ValidPassword, "borrar todo", ReservasYPlataOnly, CancellationToken.None));

        Assert.Contains("BORRAR TODO", ex.Message);
        userManagerMock.Verify(m => m.FindByIdAsync(It.IsAny<string>()), Times.Never);
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
            service.ExecuteWipeAsync(RequesterUserId, ValidPassword, ValidPhrase, ReservasYPlataOnly, CancellationToken.None));

        Assert.Equal("La contraseña no es correcta.", ex.Message);
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
            service.ExecuteWipeAsync(RequesterUserId, ValidPassword, ValidPhrase, ReservasYPlataOnly, CancellationToken.None));

        Assert.Equal("La contraseña no es correcta.", ex.Message);
    }

    [Fact]
    public async Task ExecuteWipeAsync_SinGrupos_Rechaza()
    {
        var context = NewContext();
        var userManagerMock = BuildUserManagerMock();
        var user = new ApplicationUser { Id = RequesterUserId, UserName = "admin", FullName = "Admin Uno" };
        userManagerMock.Setup(m => m.FindByIdAsync(RequesterUserId)).ReturnsAsync(user);
        userManagerMock.Setup(m => m.CheckPasswordAsync(user, ValidPassword)).ReturnsAsync(true);
        var service = NewService(context, userManagerMock);

        var ex = await Assert.ThrowsAsync<SystemDataWipeRefusedException>(() =>
            service.ExecuteWipeAsync(RequesterUserId, ValidPassword, ValidPhrase, Array.Empty<string>(), CancellationToken.None));

        Assert.Contains("al menos un grupo", ex.Message);
    }

    [Fact]
    public async Task ExecuteWipeAsync_ConGrupoDesconocido_Rechaza()
    {
        var context = NewContext();
        var userManagerMock = BuildUserManagerMock();
        var user = new ApplicationUser { Id = RequesterUserId, UserName = "admin", FullName = "Admin Uno" };
        userManagerMock.Setup(m => m.FindByIdAsync(RequesterUserId)).ReturnsAsync(user);
        userManagerMock.Setup(m => m.CheckPasswordAsync(user, ValidPassword)).ReturnsAsync(true);
        var service = NewService(context, userManagerMock);

        var ex = await Assert.ThrowsAsync<SystemDataWipeRefusedException>(() =>
            service.ExecuteWipeAsync(RequesterUserId, ValidPassword, ValidPhrase, new[] { "algoQueNoExiste" }, CancellationToken.None));

        // Hallazgo bloqueante de data-exposure: el mensaje es GENERICO — ni siquiera repite el token invalido
        // que mando el caller (podria ser cualquier string arbitrario). El detalle completo queda en el log.
        Assert.Equal("Alguno de los grupos elegidos ya no existe. Actualizá la pantalla y probá de nuevo.", ex.Message);
        Assert.DoesNotContain("algoQueNoExiste", ex.Message);
    }

    [Theory]
    [InlineData(WipeGroups.Clientes)]
    [InlineData(WipeGroups.Operadores)]
    public async Task ExecuteWipeAsync_ConGrupoQueArrastraSinSuDependiente_RechazaListandoElFaltante(string grupoSolo)
    {
        var context = NewContext();
        var userManagerMock = BuildUserManagerMock();
        var user = new ApplicationUser { Id = RequesterUserId, UserName = "admin", FullName = "Admin Uno" };
        userManagerMock.Setup(m => m.FindByIdAsync(RequesterUserId)).ReturnsAsync(user);
        userManagerMock.Setup(m => m.CheckPasswordAsync(user, ValidPassword)).ReturnsAsync(true);
        var service = NewService(context, userManagerMock);

        var ex = await Assert.ThrowsAsync<SystemDataWipeRefusedException>(() =>
            service.ExecuteWipeAsync(RequesterUserId, ValidPassword, ValidPhrase, new[] { grupoSolo }, CancellationToken.None));

        // Hallazgo bloqueante de data-exposure: el mensaje tiene que usar el nombre de NEGOCIO ("Reservas y su
        // plata"), NUNCA la clave interna cruda ("reservasYPlata", vocabulario de programador).
        Assert.Contains("Reservas y su plata", ex.Message);
        Assert.DoesNotContain(WipeGroups.ReservasYPlata, ex.Message);
    }

    [Theory]
    [InlineData(WipeGroups.Tarifario)]
    [InlineData(WipeGroups.PaisesYDestinos)]
    [InlineData(WipeGroups.PosiblesClientes)]
    [InlineData(WipeGroups.Configuracion)]
    public async Task ExecuteWipeAsync_ConGrupoSinDependenciasForzosas_NoExigeReservasYPlata(string grupoIndependiente)
    {
        // tarifario/paisesYDestinos/posiblesClientes/configuracion NO arrastran reservasYPlata (a diferencia
        // de clientes/operadores) — verificado contra las FK reales, ver WipeGroups.ForcedDependencies.
        var context = NewContext();
        var userManagerMock = BuildUserManagerMock();
        var user = new ApplicationUser { Id = RequesterUserId, UserName = "admin", FullName = "Admin Uno" };
        userManagerMock.Setup(m => m.FindByIdAsync(RequesterUserId)).ReturnsAsync(user);
        userManagerMock.Setup(m => m.CheckPasswordAsync(user, ValidPassword)).ReturnsAsync(true);
        var service = NewService(context, userManagerMock);

        // No debe tirar SystemDataWipeRefusedException por motivo de "grupo faltante". Puede seguir de largo
        // (backup fake siempre exitoso) hasta el TRUNCATE, que en InMemory tira NotSupportedException — eso
        // ya prueba que la validacion de grupos/candado fiscal paso sin objetar.
        var ex = await Record.ExceptionAsync(() =>
            service.ExecuteWipeAsync(RequesterUserId, ValidPassword, ValidPhrase, new[] { grupoIndependiente }, CancellationToken.None));

        Assert.IsNotType<SystemDataWipeRefusedException>(ex);
    }

    [Fact]
    public async Task ExecuteWipeAsync_ConComprobanteMarcadoProduccionYReservasYPlataPedido_RechazaPorCandadoFiscal()
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
            service.ExecuteWipeAsync(RequesterUserId, ValidPassword, ValidPhrase, ReservasYPlataOnly, CancellationToken.None));

        Assert.Contains("comprobantes emitidos en modo productivo", ex.Message);
    }

    [Fact]
    public async Task ExecuteWipeAsync_ConAfipEnProduccionPeroSinPedirReservasYPlata_NoBloquea()
    {
        // Scoping del candado fiscal (2026-07-27): borrar solo "tarifario" nunca toca un comprobante, asi que
        // el estado de AFIP no tiene por que frenarlo.
        var context = NewContext();
        context.AfipSettings.Add(new AfipSettings { IsProduction = true, Cuit = 20111111112 });
        await context.SaveChangesAsync();

        var userManagerMock = BuildUserManagerMock();
        var user = new ApplicationUser { Id = RequesterUserId, UserName = "admin", FullName = "Admin Uno" };
        userManagerMock.Setup(m => m.FindByIdAsync(RequesterUserId)).ReturnsAsync(user);
        userManagerMock.Setup(m => m.CheckPasswordAsync(user, ValidPassword)).ReturnsAsync(true);
        var service = NewService(context, userManagerMock);

        var ex = await Record.ExceptionAsync(() =>
            service.ExecuteWipeAsync(RequesterUserId, ValidPassword, ValidPhrase, new[] { WipeGroups.Tarifario }, CancellationToken.None));

        Assert.IsNotType<SystemDataWipeRefusedException>(ex);
    }

    [Fact]
    public async Task ExecuteWipeAsync_ConAfipEnProduccionYComprobanteHistoricoSinMarca_RechazaSiPideReservasYPlata()
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
        context.AfipSettings.Add(new AfipSettings { IsProduction = true, Cuit = 20111111112 });
        await context.SaveChangesAsync();

        var userManagerMock = BuildUserManagerMock();
        var user = new ApplicationUser { Id = RequesterUserId, UserName = "admin", FullName = "Admin Uno" };
        userManagerMock.Setup(m => m.FindByIdAsync(RequesterUserId)).ReturnsAsync(user);
        userManagerMock.Setup(m => m.CheckPasswordAsync(user, ValidPassword)).ReturnsAsync(true);
        var service = NewService(context, userManagerMock);

        var ex = await Assert.ThrowsAsync<SystemDataWipeRefusedException>(() =>
            service.ExecuteWipeAsync(RequesterUserId, ValidPassword, ValidPhrase, ReservasYPlataOnly, CancellationToken.None));

        Assert.Contains("AFIP está en modo productivo", ex.Message);
    }

    [Fact]
    public async Task ExecuteWipeAsync_ConAfipEnProduccionYSoloSePideConfiguracion_Rechaza()
    {
        // Hallazgo B5 (ronda de seguridad): el candado fiscal aplica TAMBIEN si se pide "configuracion" (tiene
        // AfipSettings), aunque "reservasYPlata" ni siquiera este en el pedido — borrar la conexion con AFIP
        // mientras esta en modo PRODUCTIVO no puede pasar. Este test faltaba (el codigo ya estaba).
        var context = NewContext();
        context.AfipSettings.Add(new AfipSettings { IsProduction = true, Cuit = 20111111112 });
        await context.SaveChangesAsync();

        var userManagerMock = BuildUserManagerMock();
        var user = new ApplicationUser { Id = RequesterUserId, UserName = "admin", FullName = "Admin Uno" };
        userManagerMock.Setup(m => m.FindByIdAsync(RequesterUserId)).ReturnsAsync(user);
        userManagerMock.Setup(m => m.CheckPasswordAsync(user, ValidPassword)).ReturnsAsync(true);
        var service = NewService(context, userManagerMock);

        var ex = await Assert.ThrowsAsync<SystemDataWipeRefusedException>(() =>
            service.ExecuteWipeAsync(RequesterUserId, ValidPassword, ValidPhrase, new[] { WipeGroups.Configuracion }, CancellationToken.None));

        Assert.Contains("AFIP está en modo productivo", ex.Message);
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
            service.ExecuteWipeAsync(RequesterUserId, ValidPassword, ValidPhrase, new[] { WipeGroups.Clientes, WipeGroups.ReservasYPlata }, CancellationToken.None));

        Assert.DoesNotContain("boom", ex.Message);
        Assert.Contains("No se pudo generar el backup previo", ex.Message);
        Assert.Equal(1, await context.Customers.CountAsync());
    }

    /// <summary>
    /// Guarda del MAPA de clasificación (2026-07-28, a raíz del bug de PROD): cada tabla tiene que estar
    /// clasificada en UN solo lugar. Si alguien agrega una tabla a dos grupos a la vez (o repite una tabla
    /// legacy en la lista de "quedan vivas" y en la de "se borran"), el borrado haría cosas distintas según qué
    /// grupo se pida — este test lo caza en CI en vez de en la base real.
    /// </summary>
    [Fact]
    public void ClasificacionDeTablas_NingunaTablaEstaEnDosLugaresALaVez()
    {
        var listas = new (string Nombre, string[] Tablas)[]
        {
            ("reservas y su plata", SystemDataWipeService.ReservasYPlataTables),
            ("clientes", SystemDataWipeService.ClientesTables),
            ("operadores", SystemDataWipeService.OperadoresTables),
            ("tarifario", SystemDataWipeService.TarifarioTables),
            ("paises y destinos", SystemDataWipeService.PaisesYDestinosTables),
            ("posibles clientes", SystemDataWipeService.PosiblesClientesTables),
            ("configuracion", WipeGroups.ConfiguracionTables),
            ("legacy de reservas y su plata", SystemDataWipeService.ReservasYPlataLegacyTables),
            ("legacy de posibles clientes", SystemDataWipeService.PosiblesClientesLegacyTables),
            ("legacy que quedan vivas", SystemDataWipeService.LegacyTablesThatStayAlive),
        };

        var vistas = new Dictionary<string, string>(StringComparer.Ordinal);
        var duplicadas = new List<string>();

        foreach (var (nombre, tablas) in listas)
        {
            foreach (var tabla in tablas)
            {
                if (vistas.TryGetValue(tabla, out var listaAnterior))
                {
                    duplicadas.Add($"{tabla} (en '{listaAnterior}' y en '{nombre}')");
                    continue;
                }

                vistas[tabla] = nombre;
            }
        }

        Assert.True(duplicadas.Count == 0, $"Tablas clasificadas dos veces: {string.Join(" | ", duplicadas)}");

        // Los dos casos especiales tampoco pueden estar repetidos en un grupo (se agregan aparte).
        Assert.DoesNotContain(SystemDataWipeService.CommissionRulesTable, vistas.Keys);
        Assert.DoesNotContain(SystemDataWipeService.RateSupplierSalesTable, vistas.Keys);
    }

    /// <summary>
    /// Guarda extra (2026-07-29, hallazgo de la revisión): ninguna tabla de las listas QUE SE TRUNCAN puede
    /// llamarse igual que un superviviente crítico. Los supervivientes nunca se tocan por diseño (acá se borra
    /// por lista blanca explícita, nunca "todo lo que haya"), pero un nombre repetido por accidente — sobre
    /// todo en las listas legacy, que se escriben a mano y no las valida el compilador porque no están en el
    /// modelo de EF — convertiría el borrado en una destrucción de usuarios o de la auditoría misma, que es
    /// justo el rastro que quedaría para investigarlo. Este test cuesta nada y cierra esa puerta para siempre.
    /// </summary>
    [Fact]
    public void ClasificacionDeTablas_NingunaListaATruncarPisaUnSupervivienteCritico()
    {
        // Usuarios, auditoría y permisos: si el borrado los tocara, se perdería el acceso al sistema y el
        // rastro de quién borró qué.
        var supervivientesCriticos = new[] { "AspNetUsers", "AuditLogs", "RolePermissions" };

        var listasATruncar = new (string Nombre, string[] Tablas)[]
        {
            ("reservas y su plata", SystemDataWipeService.ReservasYPlataTables),
            ("clientes", SystemDataWipeService.ClientesTables),
            ("operadores", SystemDataWipeService.OperadoresTables),
            ("tarifario", SystemDataWipeService.TarifarioTables),
            ("paises y destinos", SystemDataWipeService.PaisesYDestinosTables),
            ("posibles clientes", SystemDataWipeService.PosiblesClientesTables),
            ("configuracion", WipeGroups.ConfiguracionTables),
            ("legacy de reservas y su plata", SystemDataWipeService.ReservasYPlataLegacyTables),
            ("legacy de posibles clientes", SystemDataWipeService.PosiblesClientesLegacyTables),
            ("caso especial CommissionRules", new[] { SystemDataWipeService.CommissionRulesTable }),
            ("caso especial RateSupplierSales", new[] { SystemDataWipeService.RateSupplierSalesTable }),
        };

        var colisiones = new List<string>();
        foreach (var (nombre, tablas) in listasATruncar)
        {
            foreach (var tabla in tablas)
            {
                if (Array.Exists(supervivientesCriticos, s => string.Equals(s, tabla, StringComparison.Ordinal)))
                {
                    colisiones.Add($"{tabla} (en '{nombre}')");
                }
            }
        }

        Assert.True(colisiones.Count == 0,
            $"Hay supervivientes CRITICOS metidos en una lista que se trunca: {string.Join(" | ", colisiones)}");
    }

    /// <summary>
    /// Verificado contra la base real de producción el 2026-07-29: el renombre de "Reservas"/"Servicios" a
    /// "TravelFiles"/"Reservations" se hizo IN PLACE, así que esas tablas viejas NO existen (ni pueden existir
    /// en una instalación nueva). Se sacaron de la lista legacy porque eran las entradas más riesgosas: dos
    /// nombres genéricos en castellano que, si alguien crea una tabla propia con ese nombre, este borrado se
    /// llevaría puesta sin que nadie lo pidiera. Este test evita que vuelvan "por las dudas".
    /// </summary>
    [Fact]
    public void TablasLegacy_NoIncluyenLosNombresGenericosEnCastellanoQueNoExistenEnLaBaseReal()
    {
        Assert.DoesNotContain("Reservas", SystemDataWipeService.ReservasYPlataLegacyTables);
        Assert.DoesNotContain("Servicios", SystemDataWipeService.ReservasYPlataLegacyTables);
    }

    [Fact]
    public async Task GetPreviewAsync_SinComprobantesProductivos_NoBloqueaYReflejaConteosYDependencias()
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
        Assert.Contains(WipeGroups.ReservasYPlata, preview.Dependencias[WipeGroups.Clientes]);
        Assert.Contains(WipeGroups.ReservasYPlata, preview.Dependencias[WipeGroups.Operadores]);
        Assert.Empty(preview.Dependencias[WipeGroups.PaisesYDestinos]);
    }
}
