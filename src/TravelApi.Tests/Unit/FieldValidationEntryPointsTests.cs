using System;
using System.ComponentModel.DataAnnotations;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Helpers;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Obra "cada campo acepta solo lo que va en ese campo" (firmada por el dueño, 2026-07-31), TANDA 1.
///
/// <para>Mismo formato que <c>CuitValidationEntryPointsTests</c>, pero para los campos de esta tanda:
/// mail, telefono, CBU, punto de venta, porcentaje de comision y condicion fiscal. Por cada PUERTA se
/// fija lo mismo:</para>
/// <list type="bullet">
///   <item>un dato mal cargado se RECHAZA con el mensaje unico del validador;</item>
///   <item>un dato bien cargado (o vacio) PASA;</item>
///   <item>una edicion que NO toca ese campo NO se re-valida — asi un dato viejo mal cargado (de antes de
///     este fix) no traba la edicion de otro campo de la misma ficha.</item>
/// </list>
/// </summary>
public class FieldValidationEntryPointsTests
{
    private const string MailValido = "ventas@magnaviajes.com.ar";
    private const string MailInvalido = "ventas@magnaviajes";
    private const string TelefonoValido = "3511234567";
    private const string TelefonoInvalido = "preguntar a la hermana";

    // CBU con los dos digitos verificadores del BCRA correctos (ver CbuValidator y FieldValidatorsTests).
    private const string CbuValido = "0110599520000001234569";
    // El mismo CBU con el ultimo digito cambiado: 22 digitos, pero el verificador no cierra.
    private const string CbuInvalido = "0110599520000001234568";

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
    // 1) Cliente (CustomerService)
    // ===================================================================================================

    [Fact]
    public async Task CreateCustomerAsync_MailMalEscrito_Bloquea()
    {
        await using var context = CreateContext();
        var service = CreateCustomerService(context);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CreateCustomerAsync(
                new Customer { FullName = "Juan Perez", Email = MailInvalido },
                CancellationToken.None));

        Assert.Equal(EmailValidator.InvalidEmailMessage, ex.Message);
        Assert.Equal(0, await context.Customers.CountAsync());
    }

    [Fact]
    public async Task CreateCustomerAsync_TelefonoQueNoEsUnNumero_Bloquea()
    {
        await using var context = CreateContext();
        var service = CreateCustomerService(context);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CreateCustomerAsync(
                new Customer { FullName = "Juan Perez", Phone = TelefonoInvalido },
                CancellationToken.None));

        Assert.Equal(PhoneValidator.InvalidPhoneMessage, ex.Message);
        Assert.Equal(0, await context.Customers.CountAsync());
    }

    [Fact]
    public async Task CreateCustomerAsync_CondicionFiscalQueNoExiste_Bloquea()
    {
        await using var context = CreateContext();
        var service = CreateCustomerService(context);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CreateCustomerAsync(
                new Customer { FullName = "Juan Perez", TaxConditionId = 99 },
                CancellationToken.None));

        Assert.Equal(TaxConditionValidator.InvalidTaxConditionMessage, ex.Message);
        Assert.Equal(0, await context.Customers.CountAsync());
    }

    [Fact]
    public async Task CreateCustomerAsync_DatosBienCargados_Permite()
    {
        await using var context = CreateContext();
        var service = CreateCustomerService(context);

        var result = await service.CreateCustomerAsync(
            new Customer
            {
                FullName = "Juan Perez",
                Email = MailValido,
                Phone = TelefonoValido,
                TaxConditionId = CustomerTaxConditionCatalog.Monotributo,
            },
            CancellationToken.None);

        Assert.Equal(MailValido, result.Email);
        Assert.Equal(TelefonoValido, result.Phone);
    }

    [Fact]
    public async Task CreateCustomerAsync_SinMailNiTelefono_Permite()
    {
        // Los dos campos son opcionales: el gate frena un dato MAL cargado, no exige que exista.
        await using var context = CreateContext();
        var service = CreateCustomerService(context);

        var result = await service.CreateCustomerAsync(
            new Customer { FullName = "Juan Perez", Email = null, Phone = null },
            CancellationToken.None);

        Assert.Null(result.Email);
        Assert.Null(result.Phone);
    }

    [Fact]
    public async Task UpdateCustomerAsync_MailLegacyMalEscritoSinTocarlo_NoBloqueaOtrosCampos()
    {
        // Cliente cargado ANTES de este fix con un mail invalido: editarle la direccion tiene que seguir
        // funcionando. La regla "solo si cambia" es lo que evita trabar fichas viejas.
        await using var context = CreateContext();
        context.Customers.Add(new Customer
        {
            Id = 5,
            FullName = "Juan Perez",
            Email = MailInvalido,
            IsActive = true,
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var service = CreateCustomerService(context);

        var result = await service.UpdateCustomerAsync(
            5,
            new Customer { Id = 5, FullName = "Juan Perez", Email = MailInvalido, Address = "Av. Colon 123", IsActive = true },
            CancellationToken.None);

        Assert.Equal("Av. Colon 123", result.Address);
    }

    [Fact]
    public async Task UpdateCustomerAsync_MailNuevoMalEscrito_Bloquea()
    {
        await using var context = CreateContext();
        context.Customers.Add(new Customer { Id = 5, FullName = "Juan Perez", Email = MailValido, IsActive = true });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var service = CreateCustomerService(context);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.UpdateCustomerAsync(
                5,
                new Customer { Id = 5, FullName = "Juan Perez", Email = MailInvalido, IsActive = true },
                CancellationToken.None));

        Assert.Equal(EmailValidator.InvalidEmailMessage, ex.Message);

        var persisted = await context.Customers.AsNoTracking().SingleAsync();
        Assert.Equal(MailValido, persisted.Email); // no se piso el mail bueno
    }

    [Fact]
    public async Task UpdateCustomerAsync_CondicionFiscalQueNoExiste_Bloquea()
    {
        // Sin este gate el codigo desconocido se degradaba EN SILENCIO al valor viejo (regla 1 de
        // CustomerTaxConditionCatalog.ResolveIncoming) y el usuario creia haber guardado algo que nunca
        // se guardo.
        await using var context = CreateContext();
        context.Customers.Add(new Customer
        {
            Id = 5,
            FullName = "Juan Perez",
            TaxConditionId = CustomerTaxConditionCatalog.ConsumidorFinal,
            TaxCondition = "Consumidor Final",
            IsActive = true,
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var service = CreateCustomerService(context);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.UpdateCustomerAsync(
                5,
                new Customer { Id = 5, FullName = "Juan Perez", TaxConditionId = 77, IsActive = true },
                CancellationToken.None));

        Assert.Equal(TaxConditionValidator.InvalidTaxConditionMessage, ex.Message);
    }

    // ===================================================================================================
    // 2) Operador (SupplierService)
    // ===================================================================================================

    [Fact]
    public async Task CreateSupplierAsync_MailMalEscrito_Bloquea()
    {
        await using var context = CreateContext();
        var service = new SupplierService(context);

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => service.CreateSupplierAsync(
                new Supplier { Name = "Operador", Email = MailInvalido },
                CancellationToken.None));

        Assert.Equal(EmailValidator.InvalidEmailMessage, ex.Message);
        Assert.Equal(0, await context.Suppliers.CountAsync());
    }

    [Fact]
    public async Task CreateSupplierAsync_CondicionFiscalQueNoExiste_Bloquea()
    {
        await using var context = CreateContext();
        var service = new SupplierService(context);

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => service.CreateSupplierAsync(
                new Supplier { Name = "Operador", TaxCondition = "Responsable" },
                CancellationToken.None));

        Assert.Equal(TaxConditionValidator.InvalidTaxConditionMessage, ex.Message);
    }

    [Fact]
    public async Task CreateSupplierAsync_DatosBienCargados_Permite()
    {
        await using var context = CreateContext();
        var service = new SupplierService(context);

        var result = await service.CreateSupplierAsync(
            new Supplier
            {
                Name = "Operador",
                Email = MailValido,
                Phone = TelefonoValido,
                TaxCondition = TaxConditions.IvaResponsableInscripto,
            },
            CancellationToken.None);

        Assert.Equal(MailValido, result.Email);
    }

    [Fact]
    public async Task UpdateSupplierAsync_TelefonoLegacyInvalidoSinTocarlo_NoBloqueaOtrosCampos()
    {
        await using var context = CreateContext();
        context.Suppliers.Add(new Supplier
        {
            Id = 10,
            Name = "Operador",
            Phone = TelefonoInvalido, // dato cargado ANTES de este fix
            IsActive = true,
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var service = new SupplierService(context);

        var result = await service.UpdateSupplierAsync(
            10,
            new Supplier { Id = 10, Name = "Operador renombrado", Phone = TelefonoInvalido, IsActive = true },
            CancellationToken.None);

        Assert.Equal("Operador renombrado", result.Name);
    }

    [Fact]
    public async Task UpdateSupplierAsync_TelefonoNuevoInvalido_Bloquea()
    {
        await using var context = CreateContext();
        context.Suppliers.Add(new Supplier { Id = 10, Name = "Operador", Phone = TelefonoValido, IsActive = true });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var service = new SupplierService(context);

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => service.UpdateSupplierAsync(
                10,
                new Supplier { Id = 10, Name = "Operador", Phone = TelefonoInvalido, IsActive = true },
                CancellationToken.None));

        Assert.Equal(PhoneValidator.InvalidPhoneMessage, ex.Message);
    }

    // ===================================================================================================
    // 3) Pasajero (ReservaService)
    // ===================================================================================================

    [Fact]
    public async Task AddPassengerAsync_MailMalEscrito_Bloquea()
    {
        await using var context = CreateContext();
        SeedReservaWithDeclaredPassengers(context);
        await context.SaveChangesAsync();

        var service = CreateReservaService(context);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.AddPassengerAsync(
                reservaId: 1,
                new Passenger { FullName = "Juan Perez", Email = MailInvalido }));

        Assert.Equal(EmailValidator.InvalidEmailMessage, ex.Message);
        Assert.Equal(0, await context.Passengers.CountAsync());
    }

    [Fact]
    public async Task AddPassengerAsync_ContactoBienCargado_Permite()
    {
        await using var context = CreateContext();
        SeedReservaWithDeclaredPassengers(context);
        await context.SaveChangesAsync();

        var service = CreateReservaService(context);

        await service.AddPassengerAsync(
            reservaId: 1,
            new Passenger { FullName = "Juan Perez", Email = MailValido, Phone = TelefonoValido });

        var persisted = await context.Passengers.SingleAsync();
        Assert.Equal(MailValido, persisted.Email);
    }

    [Fact]
    public async Task UpdatePassengerAsync_TelefonoLegacyInvalidoSinTocarlo_NoBloqueaOtrosCampos()
    {
        await using var context = CreateContext();
        SeedReservaWithDeclaredPassengers(context);
        context.Passengers.Add(new Passenger
        {
            Id = 50,
            ReservaId = 1,
            FullName = "Juan Perez",
            Phone = TelefonoInvalido, // dato cargado ANTES de este fix
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var service = CreateReservaService(context);

        await service.UpdatePassengerAsync(
            passengerId: 50,
            new Passenger { Id = 50, FullName = "Juan Perez", Phone = TelefonoInvalido, Notes = "Pide asiento pasillo" });

        var persisted = await context.Passengers.AsNoTracking().SingleAsync();
        Assert.Equal("Pide asiento pasillo", persisted.Notes);
    }

    [Fact]
    public async Task UpdatePassengerAsync_TelefonoNuevoInvalido_Bloquea()
    {
        await using var context = CreateContext();
        SeedReservaWithDeclaredPassengers(context);
        context.Passengers.Add(new Passenger { Id = 50, ReservaId = 1, FullName = "Juan Perez", Phone = TelefonoValido });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var service = CreateReservaService(context);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.UpdatePassengerAsync(
                passengerId: 50,
                new Passenger { Id = 50, FullName = "Juan Perez", Phone = TelefonoInvalido }));

        Assert.Equal(PhoneValidator.InvalidPhoneMessage, ex.Message);
    }

    // ===================================================================================================
    // 4) Datos de la agencia (ReportService)
    // ===================================================================================================

    [Fact]
    public async Task UpdateAgencySettingsAsync_MailMalEscrito_Bloquea()
    {
        await using var context = CreateContext();
        var service = CreateReportService(context);

        var ex = await Assert.ThrowsAsync<ValidationException>(
            () => service.UpdateAgencySettingsAsync(
                new AgencySettings { AgencyName = "Magna Travel", Email = MailInvalido },
                CancellationToken.None));

        Assert.Equal(EmailValidator.InvalidEmailMessage, ex.Message);
        Assert.Equal(0, await context.AgencySettings.CountAsync());
    }

    [Fact]
    public async Task UpdateAgencySettingsAsync_CondicionFiscalQueNoExiste_Bloquea()
    {
        await using var context = CreateContext();
        var service = CreateReportService(context);

        var ex = await Assert.ThrowsAsync<ValidationException>(
            () => service.UpdateAgencySettingsAsync(
                new AgencySettings { AgencyName = "Magna Travel", TaxCondition = "RI" },
                CancellationToken.None));

        Assert.Equal(TaxConditionValidator.InvalidTaxConditionMessage, ex.Message);
    }

    [Fact]
    public async Task UpdateAgencySettingsAsync_PorcentajeDeComisionFueraDeRango_Bloquea()
    {
        await using var context = CreateContext();
        var service = CreateReportService(context);

        var ex = await Assert.ThrowsAsync<ValidationException>(
            () => service.UpdateAgencySettingsAsync(
                new AgencySettings { AgencyName = "Magna Travel", DefaultCommissionPercent = 150m },
                CancellationToken.None));

        Assert.Equal(CommissionPercentValidator.InvalidPercentMessage, ex.Message);
    }

    [Fact]
    public async Task UpdateAgencySettingsAsync_DatosBienCargados_Permite()
    {
        await using var context = CreateContext();
        var service = CreateReportService(context);

        var result = await service.UpdateAgencySettingsAsync(
            new AgencySettings
            {
                AgencyName = "Magna Travel",
                Email = MailValido,
                Phone = TelefonoValido,
                TaxCondition = "Responsable Inscripto",
                DefaultCommissionPercent = 12m,
            },
            CancellationToken.None);

        Assert.Equal(MailValido, result.Email);
        Assert.Equal(12m, result.DefaultCommissionPercent);
    }

    [Fact]
    public async Task UpdateAgencySettingsAsync_MailLegacyInvalidoSinTocarlo_NoBloqueaOtrosCampos()
    {
        await using var context = CreateContext();
        context.AgencySettings.Add(new AgencySettings { Id = 1, AgencyName = "Magna Travel", Email = MailInvalido });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var service = CreateReportService(context);

        var result = await service.UpdateAgencySettingsAsync(
            new AgencySettings { AgencyName = "Magna Travel", Email = MailInvalido, Address = "Av. Colon 123" },
            CancellationToken.None);

        Assert.Equal("Av. Colon 123", result.Address);
    }

    // Mejora #3 (review de seguridad "PDF de presupuesto", 2026-08-12): sin este chequeo, un legajo EVT
    // largo revienta con un error crudo de Npgsql ("value too long for type character varying(50)") en
    // vez de un mensaje criollo. La columna es varchar(50).
    [Fact]
    public async Task UpdateAgencySettingsAsync_LegajoDemasiadoLargo_Bloquea()
    {
        await using var context = CreateContext();
        var service = CreateReportService(context);
        var legajoDemasiadoLargo = new string('9', 51);

        var ex = await Assert.ThrowsAsync<ValidationException>(
            () => service.UpdateAgencySettingsAsync(
                new AgencySettings { AgencyName = "Magna Travel", AgencyLicenseNumber = legajoDemasiadoLargo },
                CancellationToken.None));

        Assert.Equal("El legajo no puede superar los 50 caracteres.", ex.Message);
        Assert.Equal(0, await context.AgencySettings.CountAsync());
    }

    [Fact]
    public async Task UpdateAgencySettingsAsync_LegajoDeLongitudValida_Permite()
    {
        await using var context = CreateContext();
        var service = CreateReportService(context);
        var legajoValido = new string('9', 50);

        var result = await service.UpdateAgencySettingsAsync(
            new AgencySettings { AgencyName = "Magna Travel", AgencyLicenseNumber = legajoValido },
            CancellationToken.None);

        Assert.Equal(legajoValido, result.AgencyLicenseNumber);
    }

    // ===================================================================================================
    // 5) Configuracion de ARCA (AfipService) — punto de venta y condicion fiscal
    // ===================================================================================================

    [Fact]
    public async Task UpdateAfipSettingsAsync_PuntoDeVentaEnCero_Bloquea()
    {
        await using var context = CreateContext();
        context.AfipSettings.Add(new AfipSettings { Id = 1, Cuit = 20123456786L, PuntoDeVenta = 3 });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var service = CreateAfipService(context);

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => service.UpdateSettingsAsync(
                20123456786L, puntoDeVenta: 0, isProduction: false, taxCondition: "Responsable Inscripto",
                certificateData: null, certificateFileName: null, password: null,
                prodCertificateData: null, prodCertificateFileName: null, prodPassword: null));

        Assert.Equal(AfipPointOfSaleValidator.InvalidPointOfSaleMessage, ex.Message);

        var persisted = await context.AfipSettings.AsNoTracking().SingleAsync();
        Assert.Equal(3, persisted.PuntoDeVenta);
    }

    [Fact]
    public async Task UpdateAfipSettingsAsync_PuntoDeVentaFueraDeRango_Bloquea()
    {
        await using var context = CreateContext();
        var service = CreateAfipService(context);

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => service.UpdateSettingsAsync(
                20123456786L, puntoDeVenta: 100000, isProduction: false, taxCondition: "Responsable Inscripto",
                certificateData: null, certificateFileName: null, password: null,
                prodCertificateData: null, prodCertificateFileName: null, prodPassword: null));

        Assert.Equal(AfipPointOfSaleValidator.InvalidPointOfSaleMessage, ex.Message);
    }

    [Fact]
    public async Task UpdateAfipSettingsAsync_PuntoDeVentaValido_Permite()
    {
        await using var context = CreateContext();
        var service = CreateAfipService(context);

        var result = await service.UpdateSettingsAsync(
            20123456786L, puntoDeVenta: 3, isProduction: false, taxCondition: "Monotributo",
            certificateData: null, certificateFileName: null, password: null,
            prodCertificateData: null, prodCertificateFileName: null, prodPassword: null);

        Assert.Equal(3, result.PuntoDeVenta);
    }

    [Fact]
    public async Task UpdateAfipSettingsAsync_PuntoDeVentaLegacyFueraDeRangoSinTocarlo_NoBloqueaOtrosCampos()
    {
        // Configuracion guardada antes de este fix con un punto de venta invalido: subir un certificado o
        // cambiar la condicion fiscal no queda trabado.
        await using var context = CreateContext();
        context.AfipSettings.Add(new AfipSettings { Id = 1, Cuit = 20123456786L, PuntoDeVenta = 99999 });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var service = CreateAfipService(context);

        var result = await service.UpdateSettingsAsync(
            20123456786L, puntoDeVenta: 99999, isProduction: false, taxCondition: "Monotributo",
            certificateData: null, certificateFileName: null, password: null,
            prodCertificateData: null, prodCertificateFileName: null, prodPassword: null);

        Assert.Equal("Monotributo", result.TaxCondition);
    }

    [Fact]
    public async Task UpdateAfipSettingsAsync_CondicionFiscalQueNoExiste_Bloquea()
    {
        await using var context = CreateContext();
        var service = CreateAfipService(context);

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => service.UpdateSettingsAsync(
                20123456786L, puntoDeVenta: 3, isProduction: false, taxCondition: "Responsable",
                certificateData: null, certificateFileName: null, password: null,
                prodCertificateData: null, prodCertificateFileName: null, prodPassword: null));

        Assert.Equal(TaxConditionValidator.InvalidTaxConditionMessage, ex.Message);
    }

    // ===================================================================================================
    // 6) Cuenta bancaria (BankAccountService) — CBU
    // ===================================================================================================

    [Fact]
    public async Task CreateBankAccountAsync_CbuConDigitoVerificadorMal_Bloquea()
    {
        await using var context = CreateContext();
        var service = new BankAccountService(context, Mock.Of<IAuditService>());

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => service.CreateAsync(
                BuildBankAccountRequest(cbu: CbuInvalido),
                actorUserId: "user-1",
                actorUserName: "Tester",
                CancellationToken.None));

        Assert.Equal(CbuValidator.InvalidCbuMessage, ex.Message);
        Assert.Equal(0, await context.BankAccounts.CountAsync());
    }

    [Fact]
    public async Task CreateBankAccountAsync_CbuValido_Permite()
    {
        await using var context = CreateContext();
        var service = new BankAccountService(context, Mock.Of<IAuditService>());

        await service.CreateAsync(
            BuildBankAccountRequest(cbu: CbuValido),
            actorUserId: "user-1",
            actorUserName: "Tester",
            CancellationToken.None);

        var persisted = await context.BankAccounts.SingleAsync();
        Assert.Equal(CbuValido, persisted.Cbu);
    }

    [Fact]
    public async Task UpdateBankAccountAsync_CbuLegacyInvalidoSinTocarlo_NoBloqueaOtrosCampos()
    {
        await using var context = CreateContext();
        var publicId = Guid.NewGuid();
        context.BankAccounts.Add(new BankAccount
        {
            Id = 1,
            PublicId = publicId,
            OwnerType = BankAccountOwnerType.Agency,
            OwnerId = 0,
            Cbu = CbuInvalido, // cargado ANTES de este fix (cuando solo se contaban 22 digitos)
            HolderName = "Magna Travel",
            Currency = Monedas.ARS,
            IsActive = true,
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var service = new BankAccountService(context, Mock.Of<IAuditService>());

        var result = await service.UpdateAsync(
            publicId,
            BuildBankAccountRequest(cbu: CbuInvalido) with { Bank = "Banco Nacion" },
            actorUserId: "user-1",
            actorUserName: "Tester",
            CancellationToken.None);

        Assert.Equal("Banco Nacion", result.Bank);
    }

    [Fact]
    public async Task UpdateBankAccountAsync_CbuNuevoInvalido_Bloquea()
    {
        await using var context = CreateContext();
        var publicId = Guid.NewGuid();
        context.BankAccounts.Add(new BankAccount
        {
            Id = 1,
            PublicId = publicId,
            OwnerType = BankAccountOwnerType.Agency,
            OwnerId = 0,
            Cbu = CbuValido,
            HolderName = "Magna Travel",
            Currency = Monedas.ARS,
            IsActive = true,
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var service = new BankAccountService(context, Mock.Of<IAuditService>());

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => service.UpdateAsync(
                publicId,
                BuildBankAccountRequest(cbu: CbuInvalido),
                actorUserId: "user-1",
                actorUserName: "Tester",
                CancellationToken.None));

        Assert.Equal(CbuValidator.InvalidCbuMessage, ex.Message);

        var persisted = await context.BankAccounts.AsNoTracking().SingleAsync();
        Assert.Equal(CbuValido, persisted.Cbu); // el CBU bueno sigue intacto
    }

    // ===================================================================================================
    // 7) Reglas de comision (CommissionService)
    // ===================================================================================================

    [Fact]
    public async Task CreateCommissionRuleAsync_PorcentajeFueraDeRango_Bloquea()
    {
        await using var context = CreateContext();
        var service = new CommissionService(context);

        var ex = await Assert.ThrowsAsync<ValidationException>(
            () => service.CreateRuleAsync(
                new CreateCommissionRuleRequest(null, "Hotel", 150m, 1, null),
                CancellationToken.None));

        Assert.Equal(CommissionPercentValidator.InvalidPercentMessage, ex.Message);
        Assert.Equal(0, await context.CommissionRules.CountAsync());
    }

    [Fact]
    public async Task CreateCommissionRuleAsync_PorcentajeNegativo_Bloquea()
    {
        await using var context = CreateContext();
        var service = new CommissionService(context);

        var ex = await Assert.ThrowsAsync<ValidationException>(
            () => service.CreateRuleAsync(
                new CreateCommissionRuleRequest(null, "Hotel", -5m, 1, null),
                CancellationToken.None));

        Assert.Equal(CommissionPercentValidator.InvalidPercentMessage, ex.Message);
    }

    [Fact]
    public async Task CreateCommissionRuleAsync_PorcentajeValido_Permite()
    {
        await using var context = CreateContext();
        var service = new CommissionService(context);

        await service.CreateRuleAsync(
            new CreateCommissionRuleRequest(null, "Hotel", 12m, 1, null),
            CancellationToken.None);

        var persisted = await context.CommissionRules.SingleAsync();
        Assert.Equal(12m, persisted.CommissionPercent);
    }

    [Fact]
    public async Task UpdateCommissionRuleAsync_PorcentajeFueraDeRango_Bloquea()
    {
        await using var context = CreateContext();
        context.CommissionRules.Add(new CommissionRule { Id = 1, ServiceType = "Hotel", CommissionPercent = 10m, IsActive = true });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var service = new CommissionService(context);

        var ex = await Assert.ThrowsAsync<ValidationException>(
            () => service.UpdateRuleAsync(
                1,
                new UpdateCommissionRuleRequest(150m, 1, null, true),
                CancellationToken.None));

        Assert.Equal(CommissionPercentValidator.InvalidPercentMessage, ex.Message);

        var persisted = await context.CommissionRules.AsNoTracking().SingleAsync();
        Assert.Equal(10m, persisted.CommissionPercent);
    }

    // ===================================================================================================
    // TANDA 2 — 8) Documento del cliente (CustomerService)
    // ===================================================================================================

    [Fact]
    public async Task CreateCustomerAsync_DniConPuntos_Bloquea()
    {
        await using var context = CreateContext();
        var service = CreateCustomerService(context);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CreateCustomerAsync(
                new Customer { FullName = "Juan Perez", DocumentType = "DNI", DocumentNumber = "12.345.678" },
                CancellationToken.None));

        Assert.Equal(DocumentNumberValidator.InvalidDniMessage, ex.Message);
        Assert.Equal(0, await context.Customers.CountAsync());
    }

    [Fact]
    public async Task CreateCustomerAsync_PasaporteConLetras_Permite()
    {
        // El pasaporte es texto libre: cada pais tiene su formato y no se puede exigir uno.
        await using var context = CreateContext();
        var service = CreateCustomerService(context);

        var result = await service.CreateCustomerAsync(
            new Customer { FullName = "Juan Perez", DocumentType = "Pasaporte", DocumentNumber = "AB123456" },
            CancellationToken.None);

        Assert.Equal("AB123456", result.DocumentNumber);
    }

    [Fact]
    public async Task UpdateCustomerAsync_DocumentoLegacyInvalidoSinTocarlo_NoBloqueaOtrosCampos()
    {
        // Cliente cargado ANTES de este fix con un DNI imposible: editarle la direccion tiene que andar.
        await using var context = CreateContext();
        context.Customers.Add(new Customer
        {
            Id = 7,
            FullName = "Juan Perez",
            DocumentType = "DNI",
            DocumentNumber = "12.345.678",
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var service = CreateCustomerService(context);

        await service.UpdateCustomerAsync(
            7,
            new Customer { Id = 7, FullName = "Juan Perez", Address = "Av. Colon 1234" },
            CancellationToken.None);

        var persisted = await context.Customers.AsNoTracking().SingleAsync();
        Assert.Equal("Av. Colon 1234", persisted.Address);
        Assert.Equal("12.345.678", persisted.DocumentNumber); // el dato viejo queda como estaba
    }

    [Fact]
    public async Task UpdateCustomerAsync_DniNuevoInvalido_Bloquea()
    {
        await using var context = CreateContext();
        context.Customers.Add(new Customer
        {
            Id = 7,
            FullName = "Juan Perez",
            DocumentType = "DNI",
            DocumentNumber = "12345678",
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var service = CreateCustomerService(context);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.UpdateCustomerAsync(
                7,
                new Customer { Id = 7, FullName = "Juan Perez", DocumentType = "DNI", DocumentNumber = "99" },
                CancellationToken.None));

        Assert.Equal(DocumentNumberValidator.InvalidDniMessage, ex.Message);
    }

    [Fact]
    public async Task UpdateCustomerAsync_CambiarSoloElTipoDejandoElNumeroViejo_Bloquea()
    {
        // El par (tipo + numero) es lo que importa: pasar un pasaporte a "DNI" deja una combinacion
        // imposible aunque el numero no se haya tocado en este PUT.
        await using var context = CreateContext();
        context.Customers.Add(new Customer
        {
            Id = 7,
            FullName = "Juan Perez",
            DocumentType = "Pasaporte",
            DocumentNumber = "AB123456",
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var service = CreateCustomerService(context);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.UpdateCustomerAsync(
                7,
                new Customer { Id = 7, FullName = "Juan Perez", DocumentType = "DNI" },
                CancellationToken.None));

        Assert.Equal(DocumentNumberValidator.InvalidDniMessage, ex.Message);
    }

    // ===================================================================================================
    // TANDA 2 — 9) Documento, nacimiento y pasaporte del pasajero (ReservaService)
    // ===================================================================================================

    [Fact]
    public async Task AddPassengerAsync_DniMalCargado_Bloquea()
    {
        await using var context = CreateContext();
        SeedReservaWithDeclaredPassengers(context);
        await context.SaveChangesAsync();

        var service = CreateReservaService(context);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.AddPassengerAsync(
                reservaId: 1,
                new Passenger { FullName = "Juan Perez", DocumentType = "DNI", DocumentNumber = "no lo trajo" }));

        Assert.Equal(DocumentNumberValidator.InvalidDniMessage, ex.Message);
        Assert.Equal(0, await context.Passengers.CountAsync());
    }

    [Fact]
    public async Task AddPassengerAsync_FechaDeNacimientoFutura_Bloquea()
    {
        await using var context = CreateContext();
        SeedReservaWithDeclaredPassengers(context);
        await context.SaveChangesAsync();

        var service = CreateReservaService(context);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.AddPassengerAsync(
                reservaId: 1,
                new Passenger { FullName = "Juan Perez", BirthDate = DateTime.UtcNow.AddYears(1) }));

        Assert.Equal(BirthDateValidator.InvalidBirthDateMessage, ex.Message);
        Assert.Equal(0, await context.Passengers.CountAsync());
    }

    [Fact]
    public async Task AddPassengerAsync_PasaporteVencido_GuardaIgualYAvisa()
    {
        // Decision firmada del dueño: el pasaporte vencido es AVISO, no candado. La operacion sale bien y
        // el aviso viaja en la respuesta para que la pantalla lo muestre.
        await using var context = CreateContext();
        SeedReservaWithDeclaredPassengers(context);
        await context.SaveChangesAsync();

        var service = CreateReservaService(context);

        var result = await service.AddPassengerAsync(
            reservaId: 1,
            new Passenger
            {
                FullName = "Juan Perez",
                DocumentType = "Pasaporte",
                DocumentNumber = "AB123456",
                PassportExpiry = DateTime.SpecifyKind(new DateTime(2020, 1, 1), DateTimeKind.Utc),
            });

        Assert.Equal(PassportExpiryRules.ExpiredPassportWarning, result.Warning);
        Assert.Equal(1, await context.Passengers.CountAsync());
    }

    [Fact]
    public async Task AddPassengerAsync_PasaporteVigente_NoAvisaNada()
    {
        await using var context = CreateContext();
        SeedReservaWithDeclaredPassengers(context);
        await context.SaveChangesAsync();

        var service = CreateReservaService(context);

        var result = await service.AddPassengerAsync(
            reservaId: 1,
            new Passenger
            {
                FullName = "Juan Perez",
                PassportExpiry = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddYears(3), DateTimeKind.Utc),
            });

        Assert.Null(result.Warning);
    }

    [Fact]
    public async Task UpdatePassengerAsync_DocumentoLegacyInvalidoSinTocarlo_NoBloqueaOtrosCampos()
    {
        await using var context = CreateContext();
        SeedReservaWithDeclaredPassengers(context);
        context.Passengers.Add(new Passenger
        {
            Id = 50,
            ReservaId = 1,
            FullName = "Juan Perez",
            DocumentType = "DNI",
            DocumentNumber = "12.345.678", // dato cargado ANTES de este fix
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var service = CreateReservaService(context);

        await service.UpdatePassengerAsync(
            passengerId: 50,
            new Passenger
            {
                Id = 50,
                FullName = "Juan Perez",
                DocumentType = "DNI",
                DocumentNumber = "12.345.678",
                Notes = "Pide asiento pasillo",
            });

        var persisted = await context.Passengers.AsNoTracking().SingleAsync();
        Assert.Equal("Pide asiento pasillo", persisted.Notes);
    }

    [Fact]
    public async Task UpdatePassengerAsync_DniNuevoInvalido_Bloquea()
    {
        await using var context = CreateContext();
        SeedReservaWithDeclaredPassengers(context);
        context.Passengers.Add(new Passenger
        {
            Id = 50, ReservaId = 1, FullName = "Juan Perez", DocumentType = "DNI", DocumentNumber = "12345678",
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var service = CreateReservaService(context);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.UpdatePassengerAsync(
                passengerId: 50,
                new Passenger { Id = 50, FullName = "Juan Perez", DocumentType = "DNI", DocumentNumber = "20345678901" }));

        Assert.Equal(DocumentNumberValidator.InvalidDniMessage, ex.Message);
    }

    [Fact]
    public async Task UpdatePassengerAsync_FechaDeNacimientoFutura_Bloquea()
    {
        await using var context = CreateContext();
        SeedReservaWithDeclaredPassengers(context);
        context.Passengers.Add(new Passenger { Id = 50, ReservaId = 1, FullName = "Juan Perez" });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var service = CreateReservaService(context);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.UpdatePassengerAsync(
                passengerId: 50,
                new Passenger { Id = 50, FullName = "Juan Perez", BirthDate = DateTime.UtcNow.AddDays(2) }));

        Assert.Equal(BirthDateValidator.InvalidBirthDateMessage, ex.Message);
    }

    [Fact]
    public async Task UpdatePassengerAsync_CargarPasaporteVencido_SaleBienYAvisa()
    {
        await using var context = CreateContext();
        SeedReservaWithDeclaredPassengers(context);
        context.Passengers.Add(new Passenger { Id = 50, ReservaId = 1, FullName = "Juan Perez" });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var service = CreateReservaService(context);

        var result = await service.UpdatePassengerAsync(
            passengerId: 50,
            new Passenger
            {
                Id = 50,
                FullName = "Juan Perez",
                PassportExpiry = DateTime.SpecifyKind(new DateTime(2019, 5, 20), DateTimeKind.Utc),
            });

        Assert.Equal(PassportExpiryRules.ExpiredPassportWarning, result.Warning);

        var persisted = await context.Passengers.AsNoTracking().SingleAsync();
        Assert.Equal(new DateTime(2019, 5, 20), persisted.PassportExpiry!.Value.Date);
    }

    // ===================================================================================================
    // MINI-TANDA FINAL (firmada 2026-07-31) — "campo vacio = no tocar" en el pasajero, paridad con el
    // cliente (ADR-023 T1). Un formulario que manda un payload PARCIAL ya no borra en silencio lo guardado.
    // ===================================================================================================

    [Fact]
    public async Task UpdatePassengerAsync_PayloadSinVencimientoDePasaporte_NoLoBorra()
    {
        // Bug real que motivo la mini-tanda: el mini-formulario en linea de pasajeros NUNCA manda
        // passportExpiry, asi que cada edicion rapida borraba el vencimiento que alguien habia cargado.
        await using var context = CreateContext();
        SeedReservaWithDeclaredPassengers(context);
        context.Passengers.Add(new Passenger
        {
            Id = 50,
            ReservaId = 1,
            FullName = "Juan Perez",
            DocumentType = "Pasaporte",
            DocumentNumber = "AB123456",
            PassportExpiry = DateTime.SpecifyKind(new DateTime(2030, 4, 10), DateTimeKind.Utc),
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var service = CreateReservaService(context);

        await service.UpdatePassengerAsync(
            passengerId: 50,
            new Passenger
            {
                Id = 50,
                FullName = "Juan Perez",
                DocumentType = "Pasaporte",
                DocumentNumber = "AB123456",
                // PassportExpiry NO viaja en el payload (asi lo manda el formulario en linea).
            });

        var persisted = await context.Passengers.AsNoTracking().SingleAsync();
        Assert.Equal(new DateTime(2030, 4, 10), persisted.PassportExpiry!.Value.Date);
    }

    [Fact]
    public async Task UpdatePassengerAsync_VencimientoDePasaporteNuevo_SiSePisa()
    {
        // La contracara: si el request TRAE el dato, se guarda. "Vacio = no tocar" no significa "nunca se
        // puede cambiar".
        await using var context = CreateContext();
        SeedReservaWithDeclaredPassengers(context);
        context.Passengers.Add(new Passenger
        {
            Id = 50,
            ReservaId = 1,
            FullName = "Juan Perez",
            PassportExpiry = DateTime.SpecifyKind(new DateTime(2030, 4, 10), DateTimeKind.Utc),
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var service = CreateReservaService(context);

        await service.UpdatePassengerAsync(
            passengerId: 50,
            new Passenger
            {
                Id = 50,
                FullName = "Juan Perez",
                PassportExpiry = DateTime.SpecifyKind(new DateTime(2032, 9, 1), DateTimeKind.Utc),
            });

        var persisted = await context.Passengers.AsNoTracking().SingleAsync();
        Assert.Equal(new DateTime(2032, 9, 1), persisted.PassportExpiry!.Value.Date);
    }

    [Fact]
    public async Task UpdatePassengerAsync_DocumentoVacio_NoBorraElGuardado()
    {
        await using var context = CreateContext();
        SeedReservaWithDeclaredPassengers(context);
        context.Passengers.Add(new Passenger
        {
            Id = 50,
            ReservaId = 1,
            FullName = "Juan Perez",
            DocumentType = "DNI",
            DocumentNumber = "12345678",
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var service = CreateReservaService(context);

        // El formulario en linea de hotel/traslado no muestra el documento y lo manda vacio.
        await service.UpdatePassengerAsync(
            passengerId: 50,
            new Passenger { Id = 50, FullName = "Juan Perez", DocumentType = null, DocumentNumber = null });

        var persisted = await context.Passengers.AsNoTracking().SingleAsync();
        Assert.Equal("DNI", persisted.DocumentType);
        Assert.Equal("12345678", persisted.DocumentNumber);
    }

    [Fact]
    public async Task UpdatePassengerAsync_FechaDeNacimientoNoEnviada_NoSeBorra()
    {
        await using var context = CreateContext();
        SeedReservaWithDeclaredPassengers(context);
        context.Passengers.Add(new Passenger
        {
            Id = 50,
            ReservaId = 1,
            FullName = "Juan Perez",
            BirthDate = DateTime.SpecifyKind(new DateTime(1985, 3, 20), DateTimeKind.Utc),
            Nationality = "Argentina",
            Gender = "M",
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var service = CreateReservaService(context);

        await service.UpdatePassengerAsync(
            passengerId: 50,
            new Passenger { Id = 50, FullName = "Juan Perez", Notes = "Pide asiento pasillo" });

        var persisted = await context.Passengers.AsNoTracking().SingleAsync();
        Assert.Equal(new DateTime(1985, 3, 20), persisted.BirthDate!.Value.Date);
        Assert.Equal("Argentina", persisted.Nationality);
        Assert.Equal("M", persisted.Gender);
        Assert.Equal("Pide asiento pasillo", persisted.Notes);
    }

    [Fact]
    public async Task UpdatePassengerAsync_CambiarSoloElTipoDeDocumento_ValidaElParEfectivo_Bloquea()
    {
        // El "solo si cambio" de la TANDA 2 tiene que seguir mirando el PAR (tipo + numero) YA RESUELTO,
        // igual que en el cliente: pasar el tipo a DNI dejando el numero de pasaporte guardado arma una
        // combinacion imposible, aunque el numero no venga en el request.
        await using var context = CreateContext();
        SeedReservaWithDeclaredPassengers(context);
        context.Passengers.Add(new Passenger
        {
            Id = 50,
            ReservaId = 1,
            FullName = "Juan Perez",
            DocumentType = "Pasaporte",
            DocumentNumber = "AB123456",
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var service = CreateReservaService(context);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.UpdatePassengerAsync(
                passengerId: 50,
                new Passenger { Id = 50, FullName = "Juan Perez", DocumentType = "DNI", DocumentNumber = null }));

        Assert.Equal(DocumentNumberValidator.InvalidDniMessage, ex.Message);

        var persisted = await context.Passengers.AsNoTracking().SingleAsync();
        Assert.Equal("Pasaporte", persisted.DocumentType);
    }

    [Fact]
    public async Task UpdatePassengerAsync_ConVoucherEmitido_PayloadParcialQueNoCambiaNada_NoDisparaElCandadoFiscal()
    {
        // El candado fiscal (voucher entregado / factura con CAE) NO tiene que dispararse de mas: con la
        // regla "vacio = no tocar", un payload parcial que omite nacionalidad/genero/nacimiento no cambia
        // ningun dato personal, asi que la edicion de contacto tiene que salir bien igual.
        await using var context = CreateContext();
        SeedReservaWithDeclaredPassengers(context);
        context.Passengers.Add(new Passenger
        {
            Id = 50,
            ReservaId = 1,
            FullName = "Juan Perez",
            DocumentType = "DNI",
            DocumentNumber = "12345678",
            BirthDate = DateTime.SpecifyKind(new DateTime(1985, 3, 20), DateTimeKind.Utc),
            Nationality = "Argentina",
            Gender = "M",
        });
        context.Vouchers.Add(new Voucher { Id = 90, ReservaId = 1, FileName = "v.pdf", Status = VoucherStatuses.Issued });
        context.VoucherPassengerAssignments.Add(new VoucherPassengerAssignment { Id = 1, VoucherId = 90, PassengerId = 50 });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var service = CreateReservaService(context);

        await service.UpdatePassengerAsync(
            passengerId: 50,
            new Passenger
            {
                Id = 50,
                FullName = "Juan Perez",
                DocumentType = "DNI",
                DocumentNumber = "12345678",
                Phone = TelefonoValido,
                // Nacionalidad, genero y fecha de nacimiento NO viajan en el payload.
            });

        var persisted = await context.Passengers.AsNoTracking().SingleAsync();
        Assert.Equal(TelefonoValido, persisted.Phone);
        Assert.Equal("Argentina", persisted.Nationality);
        Assert.Equal("M", persisted.Gender);
        Assert.Equal(new DateTime(1985, 3, 20), persisted.BirthDate!.Value.Date);
    }

    [Fact]
    public async Task UpdatePassengerAsync_ConVoucherEmitido_CambioRealDeNacionalidad_SigueBloqueando()
    {
        // La contracara del test anterior: el candado fiscal SIGUE VIVO. Si el request cambia de verdad un
        // dato personal (la nacionalidad), el voucher emitido lo frena con su mensaje.
        await using var context = CreateContext();
        SeedReservaWithDeclaredPassengers(context);
        context.Passengers.Add(new Passenger
        {
            Id = 50,
            ReservaId = 1,
            FullName = "Juan Perez",
            DocumentType = "DNI",
            DocumentNumber = "12345678",
            Nationality = "Argentina",
        });
        context.Vouchers.Add(new Voucher { Id = 90, ReservaId = 1, FileName = "v.pdf", Status = VoucherStatuses.Issued });
        context.VoucherPassengerAssignments.Add(new VoucherPassengerAssignment { Id = 1, VoucherId = 90, PassengerId = 50 });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var service = CreateReservaService(context);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.UpdatePassengerAsync(
                passengerId: 50,
                new Passenger
                {
                    Id = 50,
                    FullName = "Juan Perez",
                    DocumentType = "DNI",
                    DocumentNumber = "12345678",
                    Nationality = "Uruguaya",
                }));

        Assert.Contains("voucher", ex.Message, StringComparison.OrdinalIgnoreCase);

        var persisted = await context.Passengers.AsNoTracking().SingleAsync();
        Assert.Equal("Argentina", persisted.Nationality); // no se guardo nada
    }

    // ===================================================================================================
    // TANDA 2 — 10) Lead del bot de WhatsApp (LeadService)
    // ===================================================================================================

    [Fact]
    public async Task ConvertToCustomerAsync_LeadConMailBasura_CreaElClienteConElMailVacio()
    {
        // Decision firmada del dueño: el lead NUNCA se rechaza (perder una consulta que llego sola es peor
        // que un dato sucio), pero el dato invalido no entra al campo y queda anotado en la ficha.
        await using var context = CreateContext();
        context.Leads.Add(new Lead
        {
            Id = 3,
            FullName = "Consulta WhatsApp",
            Email = "no tengo mail",
            Phone = TelefonoValido,
        });
        await context.SaveChangesAsync();

        var service = CreateLeadService(context);

        var customerId = await service.ConvertToCustomerAsync(3, CancellationToken.None);

        var customer = await context.Customers.AsNoTracking().SingleAsync(c => c.Id == customerId);
        Assert.True(string.IsNullOrEmpty(customer.Email));
        Assert.Equal(TelefonoValido, customer.Phone); // el telefono si era valido: se guarda
        Assert.Contains("Mail recibido por WhatsApp no parecía válido", customer.Notes ?? string.Empty);

        // La conversion se completo: el lead quedo linkeado al cliente nuevo.
        var lead = await context.Leads.AsNoTracking().SingleAsync();
        Assert.Equal(customerId, lead.ConvertedCustomerId);
    }

    [Fact]
    public async Task ConvertToCustomerAsync_LeadConTelefonoQueNoEsNumero_LoDejaVacioYLoAnota()
    {
        await using var context = CreateContext();
        context.Leads.Add(new Lead
        {
            Id = 4,
            FullName = "Consulta WhatsApp",
            Email = MailValido,
            Phone = TelefonoInvalido,
        });
        await context.SaveChangesAsync();

        var service = CreateLeadService(context);

        var customerId = await service.ConvertToCustomerAsync(4, CancellationToken.None);

        var customer = await context.Customers.AsNoTracking().SingleAsync(c => c.Id == customerId);
        Assert.True(string.IsNullOrEmpty(customer.Phone));
        Assert.Equal(MailValido, customer.Email);
        Assert.Contains("Teléfono recibido por WhatsApp no parecía válido", customer.Notes ?? string.Empty);
    }

    [Fact]
    public async Task ConvertToCustomerAsync_LeadConContactoBienCargado_NoAnotaNada()
    {
        await using var context = CreateContext();
        context.Leads.Add(new Lead
        {
            Id = 5,
            FullName = "Consulta WhatsApp",
            Email = MailValido,
            Phone = TelefonoValido,
            Notes = "Quiere Bariloche en julio",
        });
        await context.SaveChangesAsync();

        var service = CreateLeadService(context);

        var customerId = await service.ConvertToCustomerAsync(5, CancellationToken.None);

        var customer = await context.Customers.AsNoTracking().SingleAsync(c => c.Id == customerId);
        Assert.Equal(MailValido, customer.Email);
        Assert.Equal(TelefonoValido, customer.Phone);
        Assert.Equal("Quiere Bariloche en julio", customer.Notes);
    }

    // ===================================================================================================
    // TANDA 2 — 11) Respuesta de las reglas de comision (CommissionService)
    // ===================================================================================================

    [Fact]
    public async Task CreateRuleAsync_DevuelveSoloLosCamposDeLaPantalla()
    {
        // Deuda cerrada: antes se devolvia la entidad de base tal cual (con el numero interno del
        // proveedor). Ahora sale el mismo DTO que ya usa el listado, con el identificador publico.
        await using var context = CreateContext();
        var supplier = new Supplier { Id = 9, Name = "Operador Test", PublicId = Guid.NewGuid() };
        context.Suppliers.Add(supplier);
        await context.SaveChangesAsync();

        var service = new CommissionService(context);

        var rule = await service.CreateRuleAsync(
            new CreateCommissionRuleRequest(
                SupplierId: supplier.PublicId.ToString(),
                ServiceType: "Hotel",
                CommissionPercent: 12m,
                Priority: 3,
                Description: "Hoteles del operador"),
            CancellationToken.None);

        Assert.Equal(supplier.PublicId, rule.SupplierPublicId);
        Assert.Equal("Operador Test", rule.SupplierName);
        Assert.Equal(12m, rule.CommissionPercent);
    }

    // ===================================================================================================
    // Helpers
    // ===================================================================================================

    private static CustomerService CreateCustomerService(AppDbContext context)
        => new(context, new FinancePositionService(context));

    /// <summary>
    /// LeadService para los tests de conversion. El resolver de referencias solo lo usa la sobrecarga que
    /// recibe el identificador publico; estos tests llaman a la que recibe el Id, asi que alcanza con un
    /// doble inerte.
    /// </summary>
    private static LeadService CreateLeadService(AppDbContext context)
        => new(context, Mock.Of<IEntityReferenceResolver>());

    private static ReportService CreateReportService(AppDbContext context)
        => new(context, Mock.Of<IBnaExchangeRateService>());

    private static AfipService CreateAfipService(AppDbContext context)
        => new(context, NullLogger<AfipService>.Instance, new HttpClient(), new NoopProtector());

    private static ReservaService CreateReservaService(AppDbContext context)
    {
        var settings = new Mock<IOperationalFinanceSettingsService>();
        settings.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OperationalFinanceSettings());

        var mapper = new Mock<IMapper>();
        mapper.Setup(m => m.Map<PassengerDto>(It.IsAny<Passenger>()))
              .Returns((Passenger passenger) => new PassengerDto { FullName = passenger.FullName });

        return new ReservaService(
            context, mapper.Object, settings.Object, BuildUserManager(), NullLogger<ReservaService>.Instance);
    }

    private static UserManager<ApplicationUser> BuildUserManager()
    {
        var store = new Mock<IUserStore<ApplicationUser>>();
        return new UserManager<ApplicationUser>(
            store.Object, null!, null!,
            Array.Empty<IUserValidator<ApplicationUser>>(),
            Array.Empty<IPasswordValidator<ApplicationUser>>(),
            null!, null!, null!, null!);
    }

    /// <summary>
    /// Reserva EN ARMADO que declara 2 pasajeros (el tope de nominales sale de la cantidad declarada, ver
    /// <c>AddPassengerAsync</c>). Sin esto el alta rebota por capacidad antes de llegar al gate de contacto.
    /// </summary>
    private static void SeedReservaWithDeclaredPassengers(AppDbContext context)
    {
        context.Reservas.Add(new Reserva
        {
            Id = 1,
            PublicId = Guid.NewGuid(),
            NumeroReserva = "F-1",
            Name = "Reserva de prueba",
            Status = EstadoReserva.Budget,
            AdultCount = 2,
        });
    }

    private static BankAccountUpsertRequest BuildBankAccountRequest(string cbu) => new(
        OwnerType: BankAccountOwnerType.Agency,
        OwnerId: "0",
        Cbu: cbu,
        Alias: null,
        HolderName: "Magna Travel",
        Currency: Monedas.ARS,
        Bank: null,
        AccountType: null,
        HolderTaxId: null,
        Notes: null);

    /// <summary>Protector inerte: ninguno de estos caminos llama a ARCA ni usa certificados reales.</summary>
    private sealed class NoopProtector : ISensitiveDataProtector
    {
        public string? ProtectString(string? value) => value;
        public string? UnprotectString(string? value) => value;
        public byte[]? ProtectBytes(byte[]? value) => value;
        public byte[]? UnprotectBytes(byte[]? value) => value;
    }
}
