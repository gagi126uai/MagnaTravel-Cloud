using System;
using System.ComponentModel.DataAnnotations;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Helpers;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Hallazgo H2 (barrido E2E 2026-07-25), segunda vuelta: el chequeo de digito verificador del CUIT solo
/// existia en el alta/edicion de CLIENTE. Todas las OTRAS puertas por donde entra un CUIT al sistema lo
/// aceptaban mal tipeado sin avisar nada:
///
/// <list type="bullet">
///   <item><b>Operador (Supplier)</b>: el CUIT del proveedor viaja a su liquidacion y a los papeles de la agencia.</item>
///   <item><b>Configuracion de ARCA (AfipSettings)</b>: es el CUIT PROPIO, el emisor de TODAS las facturas.
///     Mal tipeado, ARCA rechaza cada comprobante con un error tecnico opaco.</item>
///   <item><b>Datos de la agencia (AgencySettings)</b>: el CUIT que se imprime en recibos y vouchers.</item>
///   <item><b>Cuenta bancaria</b>: el CUIT/CUIL del titular que ve el cliente para transferir.</item>
/// </list>
///
/// Estos tests fijan la MISMA regla en las cuatro puertas: invalido se rechaza con el mensaje unico de
/// <see cref="CuitValidator.InvalidCuitMessage"/>, valido pasa, vacio pasa, y una edicion que NO toca el CUIT
/// no se re-valida (para no trabar por un dato viejo cargado antes de este fix).
///
/// <para>Los CUIT de prueba son los mismos que usa <c>CustomerServiceTests</c>: "20-12345678-6" cierra el
/// modulo 11 y "20-12345678-5" no.</para>
/// </summary>
public class CuitValidationEntryPointsTests
{
    private const string CuitValido = "20-12345678-6";
    private const string CuitInvalido = "20-12345678-5";

    // Version numerica de los mismos CUIT, para AfipSettings.Cuit que es un long (sin guiones posibles).
    private const long CuitValidoNumerico = 20123456786L;
    private const long CuitInvalidoNumerico = 20123456785L;

    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .ConfigureWarnings(warnings => warnings.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    // ===================================================================================================
    // 1) Operador (SupplierService)
    // ===================================================================================================

    [Fact]
    public async Task CreateSupplierAsync_CuitConDigitoVerificadorInvalido_Bloquea()
    {
        await using var context = CreateContext();
        var service = new SupplierService(context);

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => service.CreateSupplierAsync(
                new Supplier { Name = "Operador CUIT mal tipeado", TaxId = CuitInvalido },
                CancellationToken.None));

        Assert.Equal(CuitValidator.InvalidCuitMessage, ex.Message);
        Assert.Equal(0, await context.Suppliers.CountAsync());
    }

    [Fact]
    public async Task CreateSupplierAsync_CuitValido_Permite()
    {
        await using var context = CreateContext();
        var service = new SupplierService(context);

        var result = await service.CreateSupplierAsync(
            new Supplier { Name = "Operador OK", TaxId = CuitValido },
            CancellationToken.None);

        Assert.Equal(CuitValido, result.TaxId);
    }

    [Fact]
    public async Task CreateSupplierAsync_SinCuit_Permite()
    {
        // Hay operadores sin CUIT cargado (tipicamente los del exterior): el gate NO exige que TENGA uno,
        // solo bloquea uno presente pero mal formado.
        await using var context = CreateContext();
        var service = new SupplierService(context);

        var result = await service.CreateSupplierAsync(
            new Supplier { Name = "Operador del exterior", TaxId = null },
            CancellationToken.None);

        Assert.Null(result.TaxId);
    }

    [Fact]
    public async Task UpdateSupplierAsync_NuevoCuitConDigitoVerificadorInvalido_Bloquea()
    {
        await using var context = CreateContext();
        context.Suppliers.Add(new Supplier { Id = 10, Name = "Operador a editar", TaxId = null, IsActive = true });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var service = new SupplierService(context);

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => service.UpdateSupplierAsync(
                10,
                new Supplier { Id = 10, Name = "Operador a editar", TaxId = CuitInvalido, IsActive = true },
                CancellationToken.None));

        Assert.Equal(CuitValidator.InvalidCuitMessage, ex.Message);

        var persisted = await context.Suppliers.FindAsync(10);
        Assert.Null(persisted!.TaxId); // no se guardo el CUIT invalido
    }

    [Fact]
    public async Task UpdateSupplierAsync_CuitLegacyInvalidoSinCambiarlo_NoBloqueaOtrosCampos()
    {
        // Operador cargado con un CUIT invalido ANTES de este fix: editarle el telefono no queda trabado.
        await using var context = CreateContext();
        context.Suppliers.Add(new Supplier { Id = 11, Name = "Operador legacy", TaxId = CuitInvalido, IsActive = true });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var service = new SupplierService(context);

        var result = await service.UpdateSupplierAsync(
            11,
            new Supplier { Id = 11, Name = "Operador legacy", TaxId = CuitInvalido, Phone = "3511234567", IsActive = true },
            CancellationToken.None);

        Assert.Equal("3511234567", result.Phone);
    }

    // ===================================================================================================
    // 2) Configuracion de ARCA (AfipService.UpdateSettingsAsync) — el CUIT PROPIO de la agencia
    // ===================================================================================================

    [Fact]
    public async Task UpdateAfipSettingsAsync_CuitConDigitoVerificadorInvalido_Bloquea()
    {
        await using var context = CreateContext();
        var service = CreateAfipService(context);

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => service.UpdateSettingsAsync(
                CuitInvalidoNumerico, puntoDeVenta: 3, isProduction: false, taxCondition: "Responsable Inscripto",
                certificateData: null, certificateFileName: null, password: null,
                prodCertificateData: null, prodCertificateFileName: null, prodPassword: null));

        Assert.Equal(CuitValidator.InvalidCuitMessage, ex.Message);
        // Tampoco quedo una fila de configuracion a medio crear: la validacion corre ANTES del Add().
        Assert.Equal(0, await context.AfipSettings.CountAsync());
    }

    [Fact]
    public async Task UpdateAfipSettingsAsync_CuitValido_Permite()
    {
        await using var context = CreateContext();
        var service = CreateAfipService(context);

        var result = await service.UpdateSettingsAsync(
            CuitValidoNumerico, puntoDeVenta: 3, isProduction: false, taxCondition: "Responsable Inscripto",
            certificateData: null, certificateFileName: null, password: null,
            prodCertificateData: null, prodCertificateFileName: null, prodPassword: null);

        Assert.Equal(CuitValidoNumerico, result.Cuit);
    }

    [Fact]
    public async Task UpdateAfipSettingsAsync_PrimeraCargaConCuitEnCero_Permite()
    {
        // PRIMERA carga (no hay ninguna configuracion guardada todavia): el 0 se trata como "todavia no
        // configurado", igual que el CUIT vacio de un cliente, para no trabar el arranque de una agencia
        // nueva que guarda la pantalla antes de tener el numero a mano. El candado de borrado NO aplica aca
        // justamente porque no habia nada que borrar.
        await using var context = CreateContext();
        var service = CreateAfipService(context);

        var result = await service.UpdateSettingsAsync(
            0L, puntoDeVenta: 1, isProduction: false, taxCondition: "Responsable Inscripto",
            certificateData: null, certificateFileName: null, password: null,
            prodCertificateData: null, prodCertificateFileName: null, prodPassword: null);

        Assert.Equal(0L, result.Cuit);
    }

    [Fact]
    public async Task UpdateAfipSettingsAsync_BorrarElCuitYaConfigurado_Bloquea()
    {
        // Candado del borde "CUIT en cero" (firmado 2026-07-30): si la agencia YA tenia su CUIT cargado y el
        // formulario manda 0 (el admin borro el campo), se rechaza. Guardar ese 0 dejaria a la agencia sin
        // emisor y romperia TODA la facturacion en silencio.
        await using var context = CreateContext();
        context.AfipSettings.Add(new AfipSettings { Id = 1, Cuit = CuitValidoNumerico, PuntoDeVenta = 3 });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var service = CreateAfipService(context);

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => service.UpdateSettingsAsync(
                0L, puntoDeVenta: 3, isProduction: false, taxCondition: "Responsable Inscripto",
                certificateData: null, certificateFileName: null, password: null,
                prodCertificateData: null, prodCertificateFileName: null, prodPassword: null));

        Assert.Equal(AfipService.CuitEraseBlockedMessage, ex.Message);

        var persisted = await context.AfipSettings.SingleAsync();
        Assert.Equal(CuitValidoNumerico, persisted.Cuit); // el CUIT configurado sigue intacto
    }

    [Fact]
    public async Task UpdateAfipSettingsAsync_ReemplazarElCuitConfiguradoPorOtroValido_Permite()
    {
        // El candado bloquea BORRAR, no CAMBIAR: reemplazar el CUIT por otro bien formado sigue permitido.
        await using var context = CreateContext();
        context.AfipSettings.Add(new AfipSettings { Id = 1, Cuit = 20111111112L, PuntoDeVenta = 3 });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var service = CreateAfipService(context);

        var result = await service.UpdateSettingsAsync(
            CuitValidoNumerico, puntoDeVenta: 3, isProduction: false, taxCondition: "Responsable Inscripto",
            certificateData: null, certificateFileName: null, password: null,
            prodCertificateData: null, prodCertificateFileName: null, prodPassword: null);

        Assert.Equal(CuitValidoNumerico, result.Cuit);
    }

    [Fact]
    public async Task UpdateAfipSettingsAsync_CuitLegacyInvalidoSinCambiarlo_NoBloqueaOtrosCampos()
    {
        // Configuracion guardada con un CUIT invalido ANTES de este fix: cambiar el punto de venta (o subir
        // un certificado nuevo) no queda trabado.
        await using var context = CreateContext();
        context.AfipSettings.Add(new AfipSettings { Id = 1, Cuit = CuitInvalidoNumerico, PuntoDeVenta = 1 });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var service = CreateAfipService(context);

        var result = await service.UpdateSettingsAsync(
            CuitInvalidoNumerico, puntoDeVenta: 7, isProduction: false, taxCondition: "Responsable Inscripto",
            certificateData: null, certificateFileName: null, password: null,
            prodCertificateData: null, prodCertificateFileName: null, prodPassword: null);

        Assert.Equal(7, result.PuntoDeVenta);
    }

    // ===================================================================================================
    // 3) Datos de la agencia (ReportService.UpdateAgencySettingsAsync)
    // ===================================================================================================

    [Fact]
    public async Task UpdateAgencySettingsAsync_CuitConDigitoVerificadorInvalido_Bloquea()
    {
        await using var context = CreateContext();
        var service = CreateReportService(context);

        var ex = await Assert.ThrowsAsync<ValidationException>(
            () => service.UpdateAgencySettingsAsync(
                new AgencySettings { AgencyName = "Magna Travel", TaxId = CuitInvalido },
                CancellationToken.None));

        Assert.Equal(CuitValidator.InvalidCuitMessage, ex.Message);
        Assert.Equal(0, await context.AgencySettings.CountAsync());
    }

    [Fact]
    public async Task UpdateAgencySettingsAsync_CuitValido_Permite()
    {
        await using var context = CreateContext();
        var service = CreateReportService(context);

        var result = await service.UpdateAgencySettingsAsync(
            new AgencySettings { AgencyName = "Magna Travel", TaxId = CuitValido },
            CancellationToken.None);

        Assert.Equal(CuitValido, result.TaxId);
    }

    [Fact]
    public async Task UpdateAgencySettingsAsync_SinCuit_Permite()
    {
        await using var context = CreateContext();
        var service = CreateReportService(context);

        var result = await service.UpdateAgencySettingsAsync(
            new AgencySettings { AgencyName = "Magna Travel", TaxId = null },
            CancellationToken.None);

        Assert.Null(result.TaxId);
    }

    [Fact]
    public async Task UpdateAgencySettingsAsync_CuitLegacyInvalidoSinCambiarlo_NoBloqueaOtrosCampos()
    {
        await using var context = CreateContext();
        context.AgencySettings.Add(new AgencySettings { Id = 1, AgencyName = "Magna Travel", TaxId = CuitInvalido });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var service = CreateReportService(context);

        var result = await service.UpdateAgencySettingsAsync(
            new AgencySettings { AgencyName = "Magna Travel", TaxId = CuitInvalido, Phone = "3511234567" },
            CancellationToken.None);

        Assert.Equal("3511234567", result.Phone);
    }

    // ===================================================================================================
    // 4) Cuenta bancaria (BankAccountService) — CUIT/CUIL del titular
    // ===================================================================================================

    [Fact]
    public async Task CreateBankAccountAsync_HolderTaxIdInvalido_Bloquea()
    {
        await using var context = CreateContext();
        var service = new BankAccountService(context, Mock.Of<IAuditService>());

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => service.CreateAsync(
                BuildBankAccountRequest(holderTaxId: CuitInvalido),
                actorUserId: "user-1",
                actorUserName: "Tester",
                CancellationToken.None));

        Assert.Equal(CuitValidator.InvalidCuitMessage, ex.Message);
        Assert.Equal(0, await context.BankAccounts.CountAsync());
    }

    [Fact]
    public async Task CreateBankAccountAsync_HolderTaxIdValido_Permite()
    {
        await using var context = CreateContext();
        var service = new BankAccountService(context, Mock.Of<IAuditService>());

        var result = await service.CreateAsync(
            BuildBankAccountRequest(holderTaxId: CuitValido),
            actorUserId: "user-1",
            actorUserName: "Tester",
            CancellationToken.None);

        Assert.NotEqual(Guid.Empty, result.PublicId);
        var persisted = await context.BankAccounts.SingleAsync();
        Assert.Equal(CuitValido, persisted.HolderTaxId);
    }

    [Fact]
    public async Task UpdateBankAccountAsync_HolderTaxIdLegacyInvalidoSinCambiarlo_NoBloqueaOtrosCampos()
    {
        await using var context = CreateContext();
        var publicId = Guid.NewGuid();
        context.BankAccounts.Add(new BankAccount
        {
            Id = 1,
            PublicId = publicId,
            OwnerType = BankAccountOwnerType.Agency,
            OwnerId = 0,
            // CBU con los digitos verificadores del BCRA correctos (obra 2026-07-31, TANDA 1): desde que
            // el alta/edicion de cuenta corre CbuValidator, un numero inventado de 22 digitos se rechaza.
            Cbu = "0110599520000001234569",
            HolderName = "Magna Travel",
            Currency = Monedas.ARS,
            HolderTaxId = CuitInvalido, // dato cargado ANTES de este fix
            IsActive = true,
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var service = new BankAccountService(context, Mock.Of<IAuditService>());

        var result = await service.UpdateAsync(
            publicId,
            BuildBankAccountRequest(holderTaxId: CuitInvalido) with { Bank = "Banco Nacion" },
            actorUserId: "user-1",
            actorUserName: "Tester",
            CancellationToken.None);

        Assert.Equal("Banco Nacion", result.Bank);
    }

    // ===================================================================================================
    // Helpers
    // ===================================================================================================

    private static BankAccountUpsertRequest BuildBankAccountRequest(string? holderTaxId) => new(
        OwnerType: BankAccountOwnerType.Agency,
        OwnerId: "0",
        Cbu: "0110599520000001234569",
        Alias: null,
        HolderName: "Magna Travel",
        Currency: Monedas.ARS,
        Bank: null,
        AccountType: null,
        HolderTaxId: holderTaxId,
        Notes: null);

    private static AfipService CreateAfipService(AppDbContext context) =>
        new(context, NullLogger<AfipService>.Instance, new HttpClient(), new NoopProtector());

    private static ReportService CreateReportService(AppDbContext context) =>
        new(context, Mock.Of<IBnaExchangeRateService>());

    /// <summary>
    /// Protector inerte: ninguno de estos caminos llama a ARCA ni usa certificados reales.
    /// Mismo patron que <c>InvoicePdfItemsPersistenceIntegrationTests</c>.
    /// </summary>
    private sealed class NoopProtector : ISensitiveDataProtector
    {
        public string? ProtectString(string? value) => value;
        public string? UnprotectString(string? value) => value;
        public byte[]? ProtectBytes(byte[]? value) => value;
        public byte[]? UnprotectBytes(byte[]? value) => value;
    }
}
