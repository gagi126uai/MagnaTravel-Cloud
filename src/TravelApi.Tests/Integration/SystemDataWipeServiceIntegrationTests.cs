using System;
using System.Collections.Generic;
using System.Linq;
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
using TravelApi.Infrastructure.Repositories;
using TravelApi.Infrastructure.Services;
using TravelApi.Tests.Fixtures;
using Xunit;

namespace TravelApi.Tests.Integration;

/// <summary>
/// Obra "Empezar de cero" (2026-07-27) + Parte A "Borrado selectivo por grupos" (2026-07-27, firmada): la
/// ÚNICA red real del TRUNCATE de <see cref="SystemDataWipeService"/>. Corre contra Postgres real (via
/// <see cref="PostgresIntegrationFixture"/>) porque el borrado usa SQL crudo (<c>TRUNCATE ... CASCADE</c>) que
/// el proveedor InMemory ni siquiera puede ejecutar. Cubre:
/// <list type="bullet">
///   <item>el contrato completo del wipe "todo el negocio" (con y sin el grupo "configuracion");</item>
///   <item>el caso especial de <c>CommissionRules</c> (general vs. de un operador específico);</item>
///   <item>el candado fiscal;</item>
///   <item>la guarda anti-tabla-nueva-sin-clasificar contra <c>information_schema.tables</c>;</item>
///   <item><b>NUEVO (Parte A)</b>: el borrado SELECTIVO por grupos — que un grupo no pedido sobreviva intacto,
///   y que las referencias cruzadas OPCIONALES se desenganchen (no que sobrevivan huérfanas ni que arrastren
///   por CASCADE un grupo que nadie pidió).</item>
/// </list>
/// </summary>
[Trait("Category", "Integration")]
public sealed class SystemDataWipeServiceIntegrationTests : IClassFixture<PostgresIntegrationFixture>, IAsyncLifetime
{
    private const string AdminUserId = "wipe-admin-1";

    /// <summary>Los 6 grupos de "negocio" (sin "configuracion"), tal como el front tildaria para un "borrar todo lo operativo".</summary>
    private static readonly string[] AllBusinessGroups =
    {
        WipeGroups.ReservasYPlata, WipeGroups.Clientes, WipeGroups.Operadores,
        WipeGroups.Tarifario, WipeGroups.PaisesYDestinos, WipeGroups.PosiblesClientes,
    };

    private static readonly string[] AllGroupsIncludingConfiguracion =
        AllBusinessGroups.Append(WipeGroups.Configuracion).ToArray();

    private readonly PostgresIntegrationFixture _fixture;

    public SystemDataWipeServiceIntegrationTests(PostgresIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => ResetAllRelevantTablesAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task ResetAllRelevantTablesAsync()
    {
        await using var ctx = _fixture.CreateDbContext();
        await ctx.Database.ExecuteSqlRawAsync("""
            TRUNCATE TABLE
                "TravelFiles", "Reservations", "Customers", "Suppliers", "Passengers", "Payments",
                "Invoices", "InvoiceItem", "InvoiceTribute", "PaymentReceipts", "ManualCashMovements",
                "BankAccounts", "Leads", "LeadActivities", "Quotes", "QuoteItems", "Countries", "Destinations",
                "Rates", "RateSupplierSales",
                "SupplierPayments", "SupplierInvoiceLines", "SupplierInvoices", "SupplierBalanceByCurrency",
                "ApprovalPolicies", "AgencySettings", "AfipSettings", "OperationalFinanceSettings",
                "CommissionRules", "WhatsAppBotConfigs", "RefreshTokens", "BusinessSequences",
                "AuditLogs", "AspNetUsers"
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

    private sealed class FakeBackupPort : IWipeBackupPort
    {
        public bool RemoveOriginalsWasCalled { get; private set; }

        public Task<WipeBackupResult> CreateBackupAsync(string backupFileName, string minioPrefix, CancellationToken ct)
            => Task.FromResult(new WipeBackupResult(true, backupFileName, minioPrefix, null, new List<string> { "adjuntos/prueba.pdf" }));

        public Task RemoveOriginalObjectsAsync(WipeBackupResult backupResult, CancellationToken ct)
        {
            RemoveOriginalsWasCalled = true;
            return Task.CompletedTask;
        }

        public Task<int> RestoreObjectsFromBackupPrefixAsync(string minioPrefix, CancellationToken ct) => Task.FromResult(0);
    }

    private static SystemDataWipeService NewWipeService(
        AppDbContext ctx, Mock<UserManager<ApplicationUser>> userManagerMock, FakeBackupPort backupPort)
    {
        var auditService = new AuditService(new Repository<AuditLog>(ctx), NullLogger<AuditService>.Instance);
        return new SystemDataWipeService(ctx, userManagerMock.Object, backupPort, auditService, NullLogger<SystemDataWipeService>.Instance);
    }

    private async Task SeedBusinessAndConfigDataAsync(AppDbContext ctx)
    {
        await SeedAspNetUserAsync(ctx, AdminUserId);

        var country = new Country { Name = "Brasil", Slug = "brasil-" + Guid.NewGuid().ToString("N")[..8] };
        ctx.Countries.Add(country);
        await ctx.SaveChangesAsync();

        ctx.Destinations.Add(new Destination
        {
            CountryId = country.Id,
            Name = "Florianópolis",
            Title = "Florianópolis, Brasil",
            Slug = "floripa-" + Guid.NewGuid().ToString("N")[..8],
        });

        var customer = new Customer { FullName = "Cliente de prueba wipe" };
        var supplier = new Supplier { Name = "Operador de prueba wipe" };
        ctx.Customers.Add(customer);
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();

        ctx.Reservas.Add(new Reserva
        {
            NumeroReserva = "F-WIPE-" + Guid.NewGuid().ToString("N")[..8],
            Name = "Reserva de prueba wipe",
            Status = EstadoReserva.Confirmed,
        });

        ctx.Leads.Add(new Lead { FullName = "Lead de prueba wipe" });

        ctx.Rates.Add(new Rate { ServiceType = "Hotel", ProductName = "Hotel de prueba wipe" });

        ctx.Invoices.Add(new Invoice
        {
            TipoComprobante = 6,
            PuntoDeVenta = 1,
            NumeroComprobante = 1,
            Resultado = "A",
            CAE = "12345678901234",
            WasIssuedInProduction = false,
        });

        ctx.BankAccounts.Add(new BankAccount
        {
            OwnerType = BankAccountOwnerType.Agency,
            OwnerId = 0,
            HolderName = "La Agencia SA",
            Alias = "agencia.wipe.test",
            Currency = "ARS",
        });
        ctx.BankAccounts.Add(new BankAccount
        {
            OwnerType = BankAccountOwnerType.Customer,
            OwnerId = customer.Id,
            HolderName = "Cliente de prueba wipe",
            Alias = "cliente.wipe.test",
            Currency = "ARS",
        });
        ctx.BankAccounts.Add(new BankAccount
        {
            OwnerType = BankAccountOwnerType.Supplier,
            OwnerId = supplier.Id,
            HolderName = "Operador de prueba wipe",
            Alias = "operador.wipe.test",
            Currency = "ARS",
        });

        ctx.AgencySettings.Add(new AgencySettings { AgencyName = "Agencia de prueba wipe" });
        ctx.AfipSettings.Add(new AfipSettings { IsProduction = false, Cuit = 20111111112 });
        ctx.OperationalFinanceSettings.Add(new OperationalFinanceSettings());
        ctx.WhatsAppBotConfigs.Add(new WhatsAppBotConfig());
        ctx.ApprovalPolicies.Add(new ApprovalPolicy
        {
            RequestType = "PaymentDeadlineOverride",
            RequiresApproval = true, // el default de fabrica es FALSE - lo distingue del reseed
            UpdatedAt = DateTime.UtcNow,
        });

        ctx.CommissionRules.Add(new CommissionRule
        {
            SupplierId = null,
            ServiceType = null,
            CommissionPercent = 8.5m,
            Priority = 1,
            IsActive = true,
            Description = "Regla general de la agencia",
        });

        await ctx.SaveChangesAsync();
    }

    [Fact]
    public async Task ExecuteWipeAsync_TodosLosGruposDeNegocioSinConfiguracion_BorraNegocioYPreservaConfigYAgencyBankAccount()
    {
        await using var ctx = _fixture.CreateDbContext();
        await SeedBusinessAndConfigDataAsync(ctx);

        var user = await ctx.Set<ApplicationUser>().AsNoTracking().SingleAsync(u => u.Id == AdminUserId);
        var userManagerMock = BuildUserManagerMock(user);
        var backupPort = new FakeBackupPort();
        var service = NewWipeService(ctx, userManagerMock, backupPort);

        var result = await service.ExecuteWipeAsync(AdminUserId, "cualquier-cosa", "BORRAR TODO", AllBusinessGroups, CancellationToken.None);

        Assert.DoesNotContain(WipeGroups.Configuracion, result.GruposBorrados);
        Assert.Equal(1, result.Borrado.Clientes);
        Assert.Equal(1, result.Borrado.Operadores);
        Assert.Equal(1, result.Borrado.Reservas);
        Assert.Equal(1, result.Borrado.Facturas);
        Assert.Equal(1, result.Borrado.PosiblesClientes);
        Assert.Equal(1, result.Borrado.Tarifario);
        Assert.Equal(2, result.Borrado.PaisesYDestinos); // 1 pais + 1 destino

        Assert.True(backupPort.RemoveOriginalsWasCalled);

        await using var verifyCtx = _fixture.CreateDbContext();
        Assert.Equal(0, await verifyCtx.Customers.CountAsync());
        Assert.Equal(0, await verifyCtx.Suppliers.CountAsync());
        Assert.Equal(0, await verifyCtx.Reservas.CountAsync());
        Assert.Equal(0, await verifyCtx.Invoices.CountAsync());
        Assert.Equal(0, await verifyCtx.Leads.CountAsync());
        Assert.Equal(0, await verifyCtx.Countries.CountAsync());
        Assert.Equal(0, await verifyCtx.Destinations.CountAsync());
        Assert.Equal(0, await verifyCtx.Rates.CountAsync());

        Assert.True(await verifyCtx.Set<ApplicationUser>().AnyAsync(u => u.Id == AdminUserId));

        Assert.Equal(1, await verifyCtx.AgencySettings.CountAsync());
        Assert.Equal(1, await verifyCtx.AfipSettings.CountAsync());
        Assert.Equal(1, await verifyCtx.OperationalFinanceSettings.CountAsync());
        Assert.Equal(1, await verifyCtx.WhatsAppBotConfigs.CountAsync());
        var survivingPolicy = await verifyCtx.ApprovalPolicies.SingleAsync();
        Assert.Equal("PaymentDeadlineOverride", survivingPolicy.RequestType);
        Assert.True(survivingPolicy.RequiresApproval);

        var survivingRule = await verifyCtx.CommissionRules.SingleAsync();
        Assert.Null(survivingRule.SupplierId);
        Assert.Equal(8.5m, survivingRule.CommissionPercent);
        Assert.Equal("Regla general de la agencia", survivingRule.Description);

        // BankAccounts: Cliente y Proveedor se borran (clientes/operadores estaban en el pedido), la de
        // Agencia sobrevive (config NO estaba en el pedido).
        var remainingBankAccounts = await verifyCtx.BankAccounts.ToListAsync();
        var remainingAccount = Assert.Single(remainingBankAccounts);
        Assert.Equal(BankAccountOwnerType.Agency, remainingAccount.OwnerType);

        var auditLog = await verifyCtx.AuditLogs.SingleAsync(a => a.Action == AuditActions.SystemDataWiped);
        Assert.Equal(AuditActions.SystemDataWiped, auditLog.Action);
        Assert.Equal(AuditActions.SystemDataWipeEntityName, auditLog.EntityName);
        Assert.Contains("\"clientesBorrados\":1", auditLog.Changes);
        Assert.DoesNotContain("conteosBorrados", auditLog.Changes);
    }

    [Fact]
    public async Task ExecuteWipeAsync_TodosLosGruposIncluidaConfiguracion_ReseteaConfiguracionADefaultsDeFabricaYBorraTodoBankAccount()
    {
        await using var ctx = _fixture.CreateDbContext();
        await SeedBusinessAndConfigDataAsync(ctx);

        var user = await ctx.Set<ApplicationUser>().AsNoTracking().SingleAsync(u => u.Id == AdminUserId);
        var userManagerMock = BuildUserManagerMock(user);
        var service = NewWipeService(ctx, userManagerMock, new FakeBackupPort());

        var result = await service.ExecuteWipeAsync(AdminUserId, "cualquier-cosa", "BORRAR TODO", AllGroupsIncludingConfiguracion, CancellationToken.None);

        Assert.Contains(WipeGroups.Configuracion, result.GruposBorrados);

        await using var verifyCtx = _fixture.CreateDbContext();
        Assert.Equal(0, await verifyCtx.AgencySettings.CountAsync());
        Assert.Equal(0, await verifyCtx.AfipSettings.CountAsync());
        Assert.Equal(0, await verifyCtx.OperationalFinanceSettings.CountAsync());
        Assert.Equal(0, await verifyCtx.WhatsAppBotConfigs.CountAsync());

        var policies = await verifyCtx.ApprovalPolicies.ToListAsync();
        Assert.Equal(7, policies.Count);
        var paymentDeadlineOverride = policies.Single(p => p.RequestType == "PaymentDeadlineOverride");
        Assert.False(paymentDeadlineOverride.RequiresApproval);
        var partialCreditNoteApproval = policies.Single(p => p.RequestType == "PartialCreditNoteApproval");
        Assert.True(partialCreditNoteApproval.RequiresApproval);
        Assert.Equal(5, partialCreditNoteApproval.ExpirationDaysOverride);

        Assert.Equal(0, await verifyCtx.CommissionRules.CountAsync());
        Assert.Equal(0, await verifyCtx.BankAccounts.CountAsync());

        Assert.True(await verifyCtx.Set<ApplicationUser>().AnyAsync(u => u.Id == AdminUserId));
        var auditLog = await verifyCtx.AuditLogs.SingleAsync(a => a.Action == AuditActions.SystemDataWiped);
        // Hallazgo bloqueante de data-exposure: el audit log guarda el nombre de NEGOCIO ("Configuración"),
        // nunca la clave interna cruda ("configuracion").
        Assert.Contains("Configuración", auditLog.Changes);
        Assert.DoesNotContain("\"configuracion\"", auditLog.Changes);
    }

    [Fact]
    public async Task ExecuteWipeAsync_ConCommissionRuleDeOperadorEspecifico_MuereConSuOperadorAunqueGeneralSobreviva()
    {
        await using var ctx = _fixture.CreateDbContext();
        await SeedAspNetUserAsync(ctx, AdminUserId);

        var supplier = new Supplier { Name = "Operador con regla propia" };
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();

        ctx.CommissionRules.Add(new CommissionRule
        {
            SupplierId = null,
            CommissionPercent = 5m,
            Priority = 1,
            IsActive = true,
            Description = "Regla general",
        });
        ctx.CommissionRules.Add(new CommissionRule
        {
            SupplierId = supplier.Id,
            ServiceType = "Hotel",
            CommissionPercent = 12m,
            Priority = 3,
            IsActive = true,
            Description = "Regla especifica del operador",
        });
        await ctx.SaveChangesAsync();

        var user = await ctx.Set<ApplicationUser>().AsNoTracking().SingleAsync(u => u.Id == AdminUserId);
        var userManagerMock = BuildUserManagerMock(user);
        var service = NewWipeService(ctx, userManagerMock, new FakeBackupPort());

        await service.ExecuteWipeAsync(
            AdminUserId, "cualquier-cosa", "BORRAR TODO",
            new[] { WipeGroups.Operadores, WipeGroups.ReservasYPlata }, CancellationToken.None);

        await using var verifyCtx = _fixture.CreateDbContext();
        Assert.Equal(0, await verifyCtx.Suppliers.CountAsync());
        var survivingRule = await verifyCtx.CommissionRules.SingleAsync();
        Assert.Null(survivingRule.SupplierId);
        Assert.Equal("Regla general", survivingRule.Description);
    }

    [Fact]
    public async Task ExecuteWipeAsync_ConFacturaMarcadaProduccionYReservasYPlataPedido_RechazaYNoBorraNada()
    {
        await using var ctx = _fixture.CreateDbContext();
        await SeedAspNetUserAsync(ctx, AdminUserId);
        ctx.Customers.Add(new Customer { FullName = "Cliente con factura real" });
        ctx.Invoices.Add(new Invoice
        {
            TipoComprobante = 1,
            PuntoDeVenta = 1,
            NumeroComprobante = 1,
            Resultado = "A",
            CAE = "99999999999999",
            WasIssuedInProduction = true, // comprobante REAL: candado fiscal tiene que frenar todo
        });
        await ctx.SaveChangesAsync();

        var user = await ctx.Set<ApplicationUser>().AsNoTracking().SingleAsync(u => u.Id == AdminUserId);
        var userManagerMock = BuildUserManagerMock(user);
        var backupPort = new FakeBackupPort();
        var service = NewWipeService(ctx, userManagerMock, backupPort);

        var ex = await Assert.ThrowsAsync<SystemDataWipeRefusedException>(() =>
            service.ExecuteWipeAsync(AdminUserId, "cualquier-cosa", "BORRAR TODO", AllBusinessGroups, CancellationToken.None));

        await using var verifyCtx = _fixture.CreateDbContext();
        Assert.Equal(1, await verifyCtx.Customers.CountAsync());
        Assert.Equal(1, await verifyCtx.Invoices.CountAsync());
        Assert.False(backupPort.RemoveOriginalsWasCalled);
        Assert.False(await verifyCtx.AuditLogs.AnyAsync(a => a.Action == AuditActions.SystemDataWiped));

        var rejectedLog = await verifyCtx.AuditLogs.SingleAsync(a => a.Action == AuditActions.SystemDataWipeRejected);
        Assert.Equal(AuditActions.SystemDataWipeEntityName, rejectedLog.EntityName);
        Assert.Contains("productivo", rejectedLog.Changes ?? string.Empty);
        Assert.Equal(ex.Message, rejectedLog.Changes);
    }

    // ===== Parte A: borrado SELECTIVO por grupos (2026-07-27) =====

    [Fact]
    public async Task Wipe_SoloTarifario_PreservaReservasYDesenganchaElRateIdDeLosServicios()
    {
        // Prueba el hallazgo mas importante de la auditoria de FKs: HotelBooking/ServicioReserva/etc.
        // referencian Rates via RateId. Sin el desenganche, TRUNCATE "Rates" CASCADE se llevaria puesta TODA
        // la reserva - acá probamos que NO pasa: la reserva y su servicio sobreviven, solo pierden el link.
        await using var ctx = _fixture.CreateDbContext();
        await SeedAspNetUserAsync(ctx, AdminUserId);

        var rate = new Rate { ServiceType = "Hotel", ProductName = "Hotel a desenganchar" };
        ctx.Rates.Add(rate);
        var reserva = new Reserva { NumeroReserva = "F-TAR-" + Guid.NewGuid().ToString("N")[..8], Name = "Reserva con rate", Status = EstadoReserva.Confirmed };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        ctx.Set<ServicioReserva>().Add(new ServicioReserva
        {
            ReservaId = reserva.Id,
            RateId = rate.Id,
            DepartureDate = DateTime.UtcNow.AddDays(30),
        });
        await ctx.SaveChangesAsync();

        var user = await ctx.Set<ApplicationUser>().AsNoTracking().SingleAsync(u => u.Id == AdminUserId);
        var userManagerMock = BuildUserManagerMock(user);
        var service = NewWipeService(ctx, userManagerMock, new FakeBackupPort());

        var result = await service.ExecuteWipeAsync(AdminUserId, "cualquier-cosa", "BORRAR TODO", new[] { WipeGroups.Tarifario }, CancellationToken.None);

        Assert.Equal(new[] { WipeGroups.Tarifario }, result.GruposBorrados);

        // Hallazgo N8 (ronda de seguridad): la respuesta reporta conteos SOLO del grupo pedido. La reserva
        // sigue viva (no se tocó), asi que "Reservas" en el reporte tiene que ser 0 aunque el conteo REAL de
        // reservas en la base sea 1 — informar ese 1 como "borrado" seria informar de mas.
        Assert.Equal(1, result.Borrado.Tarifario);
        Assert.Equal(0, result.Borrado.Reservas);

        await using var verifyCtx = _fixture.CreateDbContext();
        Assert.Equal(0, await verifyCtx.Rates.CountAsync());
        // La reserva y su servicio SIGUEN VIVOS (reservasYPlata no fue pedido) - lo unico que cambia es que
        // el servicio perdio el link al Rate borrado.
        Assert.Equal(1, await verifyCtx.Reservas.CountAsync());
        var survivingServicio = await verifyCtx.Set<ServicioReserva>().SingleAsync();
        Assert.Null(survivingServicio.RateId);
    }

    [Fact]
    public async Task Wipe_SoloPosiblesClientes_PreservaLaReservaYDesenganchaSourceQuoteYSourceLead()
    {
        // Reserva.SourceQuoteId/SourceLeadId apuntan a Quotes/Leads. Sin el desenganche, TRUNCATE
        // "Quotes"/"Leads" CASCADE se llevaria puesta la reserva entera aunque nadie pidio "reservasYPlata".
        await using var ctx = _fixture.CreateDbContext();
        await SeedAspNetUserAsync(ctx, AdminUserId);

        var lead = new Lead { FullName = "Lead origen" };
        ctx.Leads.Add(lead);
        var quote = new Quote { Title = "Presupuesto origen", QuoteNumber = "Q-1" };
        ctx.Quotes.Add(quote);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva
        {
            NumeroReserva = "F-POS-" + Guid.NewGuid().ToString("N")[..8],
            Name = "Reserva originada en lead/quote",
            Status = EstadoReserva.Confirmed,
            SourceLeadId = lead.Id,
            SourceQuoteId = quote.Id,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var user = await ctx.Set<ApplicationUser>().AsNoTracking().SingleAsync(u => u.Id == AdminUserId);
        var userManagerMock = BuildUserManagerMock(user);
        var service = NewWipeService(ctx, userManagerMock, new FakeBackupPort());

        await service.ExecuteWipeAsync(AdminUserId, "cualquier-cosa", "BORRAR TODO", new[] { WipeGroups.PosiblesClientes }, CancellationToken.None);

        await using var verifyCtx = _fixture.CreateDbContext();
        Assert.Equal(0, await verifyCtx.Leads.CountAsync());
        Assert.Equal(0, await verifyCtx.Quotes.CountAsync());
        Assert.Equal(1, await verifyCtx.Reservas.CountAsync());
        var survivingReserva = await verifyCtx.Reservas.SingleAsync();
        Assert.Null(survivingReserva.SourceLeadId);
        Assert.Null(survivingReserva.SourceQuoteId);
    }

    [Fact]
    public async Task Wipe_ClientesYReservasYPlata_PreservaPosiblesClientesYDesenganchaReferenciasHaciaClienteYReserva()
    {
        // Quote.CustomerId (-> Customers) y Quote.ConvertedReservaId (-> TravelFiles) son las dos referencias
        // opuestas: al pedir clientes+reservasYPlata (sin posiblesClientes), Quotes/Leads sobreviven pero
        // pierden ambos links.
        await using var ctx = _fixture.CreateDbContext();
        await SeedAspNetUserAsync(ctx, AdminUserId);

        var customer = new Customer { FullName = "Cliente con presupuesto" };
        ctx.Customers.Add(customer);
        var reserva = new Reserva { NumeroReserva = "F-CLI-" + Guid.NewGuid().ToString("N")[..8], Name = "Reserva convertida", Status = EstadoReserva.Confirmed };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var quote = new Quote
        {
            Title = "Presupuesto de cliente",
            QuoteNumber = "Q-2",
            CustomerId = customer.Id,
            ConvertedReservaId = reserva.Id,
        };
        ctx.Quotes.Add(quote);
        await ctx.SaveChangesAsync();

        var user = await ctx.Set<ApplicationUser>().AsNoTracking().SingleAsync(u => u.Id == AdminUserId);
        var userManagerMock = BuildUserManagerMock(user);
        var service = NewWipeService(ctx, userManagerMock, new FakeBackupPort());

        await service.ExecuteWipeAsync(
            AdminUserId, "cualquier-cosa", "BORRAR TODO",
            new[] { WipeGroups.Clientes, WipeGroups.ReservasYPlata }, CancellationToken.None);

        await using var verifyCtx = _fixture.CreateDbContext();
        Assert.Equal(0, await verifyCtx.Customers.CountAsync());
        Assert.Equal(0, await verifyCtx.Reservas.CountAsync());
        var survivingQuote = await verifyCtx.Quotes.SingleAsync();
        Assert.Null(survivingQuote.CustomerId);
        Assert.Null(survivingQuote.ConvertedReservaId);
    }

    [Fact]
    public async Task Wipe_RateSupplierSales_MuereConOperadoresAunSinPedirTarifarioExplicitamente()
    {
        // RateSupplierSales tiene DOS foreign keys OBLIGATORIAS (RateId, SupplierId): no se puede
        // "desenganchar" — muere apenas Rates O Suppliers muere, sin importar cual de los dos grupos se pidio.
        await RunRateSupplierSaleDiesWithAsync(new[] { WipeGroups.Operadores, WipeGroups.ReservasYPlata });
    }

    [Fact]
    public async Task Wipe_RateSupplierSales_MuereConTarifarioAunSinPedirOperadoresExplicitamente()
    {
        await RunRateSupplierSaleDiesWithAsync(new[] { WipeGroups.Tarifario });
    }

    private async Task RunRateSupplierSaleDiesWithAsync(string[] grupos)
    {
        await using var ctx = _fixture.CreateDbContext();
        await SeedAspNetUserAsync(ctx, AdminUserId);

        var rate = new Rate { ServiceType = "Hotel", ProductName = "Hotel con venta registrada" };
        var supplier = new Supplier { Name = "Operador con venta registrada" };
        ctx.Rates.Add(rate);
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();

        ctx.Set<RateSupplierSale>().Add(new RateSupplierSale
        {
            RateId = rate.Id,
            SupplierId = supplier.Id,
            LastSoldAt = DateTime.UtcNow,
            LastPriceUnit = "noche_habitacion",
        });
        await ctx.SaveChangesAsync();

        var user = await ctx.Set<ApplicationUser>().AsNoTracking().SingleAsync(u => u.Id == AdminUserId);
        var userManagerMock = BuildUserManagerMock(user);
        var service = NewWipeService(ctx, userManagerMock, new FakeBackupPort());

        await service.ExecuteWipeAsync(AdminUserId, "cualquier-cosa", "BORRAR TODO", grupos, CancellationToken.None);

        await using var verifyCtx = _fixture.CreateDbContext();
        Assert.Equal(0, await verifyCtx.Set<RateSupplierSale>().CountAsync());
    }

    [Fact]
    public async Task Wipe_SoloReservasYPlata_PreservaTarifarioYFichaDeOperadorPeroBorraSuPlataYDesenganchaCreatedFromReserva()
    {
        // Decision firmada del dueño (hallazgo B3 de la revision de seguridad): "la plata del operador ligada
        // a reservas se va CON las reservas; la ficha del operador queda". Este test prueba las DOS mitades:
        // Suppliers (ficha) sobrevive, pero SupplierPayments/SupplierInvoices/SupplierInvoiceLines/
        // SupplierBalanceByCurrency mueren igual, aunque el grupo "operadores" NI SIQUIERA fue pedido.
        // Tambien prueba el hallazgo B1: Rate.CreatedFromReservaId se desengancha (Rates sobrevive, tarifario
        // no fue pedido) en vez de arrastrar TODO el tarifario por CASCADE.
        await using var ctx = _fixture.CreateDbContext();
        await SeedAspNetUserAsync(ctx, AdminUserId);

        var country = new Country { Name = "Chile", Slug = "chile-" + Guid.NewGuid().ToString("N")[..8] };
        ctx.Countries.Add(country);
        var supplier = new Supplier { Name = "Operador con plata en reservas" };
        ctx.Suppliers.Add(supplier);
        var reserva = new Reserva { NumeroReserva = "F-RyP-" + Guid.NewGuid().ToString("N")[..8], Name = "Reserva con plata de operador", Status = EstadoReserva.Confirmed };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var rate = new Rate { ServiceType = "Hotel", ProductName = "Hotel nacido de esta reserva", CreatedFromReservaId = reserva.Id };
        ctx.Rates.Add(rate);

        ctx.SupplierPayments.Add(new SupplierPayment { SupplierId = supplier.Id, ReservaId = reserva.Id, Amount = 1000m });

        ctx.Set<SupplierBalanceByCurrency>().Add(new SupplierBalanceByCurrency { SupplierId = supplier.Id, Currency = "ARS", ConfirmedPurchases = 1000m, Balance = 1000m });

        var supplierInvoice = new SupplierInvoice { SupplierId = supplier.Id, Number = "F-0001", CreatedByUserId = AdminUserId };
        ctx.SupplierInvoices.Add(supplierInvoice);
        await ctx.SaveChangesAsync();

        ctx.SupplierInvoiceLines.Add(new SupplierInvoiceLine
        {
            SupplierInvoiceId = supplierInvoice.Id,
            ReservaId = reserva.Id,
            ServiceRecordKind = "hotel",
            ServicePublicId = Guid.NewGuid(),
            Description = "Linea de la reserva",
            Amount = 1000m,
        });
        await ctx.SaveChangesAsync();

        var user = await ctx.Set<ApplicationUser>().AsNoTracking().SingleAsync(u => u.Id == AdminUserId);
        var userManagerMock = BuildUserManagerMock(user);
        var service = NewWipeService(ctx, userManagerMock, new FakeBackupPort());

        await service.ExecuteWipeAsync(AdminUserId, "cualquier-cosa", "BORRAR TODO", new[] { WipeGroups.ReservasYPlata }, CancellationToken.None);

        await using var verifyCtx = _fixture.CreateDbContext();
        Assert.Equal(0, await verifyCtx.Reservas.CountAsync());

        // Decision B3: la plata del operador ligada a reservas se fue con reservasYPlata...
        Assert.Equal(0, await verifyCtx.SupplierPayments.CountAsync());
        Assert.Equal(0, await verifyCtx.SupplierInvoices.CountAsync());
        Assert.Equal(0, await verifyCtx.SupplierInvoiceLines.CountAsync());
        Assert.Equal(0, await verifyCtx.Set<SupplierBalanceByCurrency>().CountAsync());

        // ...pero la FICHA del operador (Suppliers) NO se toco: "operadores" ni siquiera fue pedido.
        Assert.Equal(1, await verifyCtx.Suppliers.CountAsync());

        // Hallazgo B1: Rates sobrevive (tarifario no fue pedido) con CreatedFromReservaId desenganchado.
        var survivingRate = await verifyCtx.Rates.SingleAsync();
        Assert.Null(survivingRate.CreatedFromReservaId);

        // paisesYDestinos ni se toco (sin dependencia forzosa con reservasYPlata).
        Assert.Equal(1, await verifyCtx.Countries.CountAsync());
    }

    [Fact]
    public async Task Wipe_ConForeignKeySinContemplar_AbortaSinBorrarNadaYRestituyeLasConstraintsYaDropeadas()
    {
        // Hallazgo bloqueante B4 (red de seguridad generica fail-closed) + punto 13 de la ronda de seguridad:
        // simulamos una FK que el mapa de DropCrossGroupForeignKeysAsync NO conoce (una tabla de prueba con
        // una columna que apunta a Customers), pedimos {clientes, reservasYPlata} (que SI dropea la FK
        // CONOCIDA Quote.CustomerId), y verificamos que: (a) el wipe aborta con el mensaje generico, (b) NO
        // se borro un solo dato, y (c) la FK CONOCIDA que si se llego a dropear volvio a existir intacta -
        // Postgres deshace el DROP/ADD CONSTRAINT solo con el ROLLBACK de la transaccion, prueba de que
        // "wipe abortado restituye TODAS las constraints" (no hace falta codigo de reattach para el camino de
        // error: el rollback transaccional de DDL de Postgres ya lo hace).
        await using var ctx = _fixture.CreateDbContext();
        await SeedAspNetUserAsync(ctx, AdminUserId);

        var customer = new Customer { FullName = "Cliente con presupuesto y con FK fantasma" };
        ctx.Customers.Add(customer);
        await ctx.SaveChangesAsync();

        var quote = new Quote { Title = "Presupuesto de cliente", QuoteNumber = "Q-FANTASMA", CustomerId = customer.Id };
        ctx.Quotes.Add(quote);
        await ctx.SaveChangesAsync();

        // Tabla + FK de prueba que el mapa de la revision NO conoce (simula una migracion futura que agrego
        // una referencia nueva sin actualizar SystemDataWipeService).
        await ctx.Database.ExecuteSqlRawAsync("""
            DROP TABLE IF EXISTS "TestFkFantasma";
            CREATE TABLE "TestFkFantasma" (
                "Id" SERIAL PRIMARY KEY,
                "CustomerId" INT NOT NULL REFERENCES "Customers" ("Id")
            );
            """);
        try
        {
            var fkCountAntes = await CountForeignKeysOnQuotesCustomerIdAsync(ctx);
            Assert.Equal(1, fkCountAntes); // la FK conocida (Quote.CustomerId) existe antes de intentar

            var user = await ctx.Set<ApplicationUser>().AsNoTracking().SingleAsync(u => u.Id == AdminUserId);
            var userManagerMock = BuildUserManagerMock(user);
            var service = NewWipeService(ctx, userManagerMock, new FakeBackupPort());

            var ex = await Assert.ThrowsAsync<SystemDataWipeRefusedException>(() =>
                service.ExecuteWipeAsync(
                    AdminUserId, "cualquier-cosa", "BORRAR TODO",
                    new[] { WipeGroups.Clientes, WipeGroups.ReservasYPlata }, CancellationToken.None));

            Assert.Contains("avisá al equipo técnico", ex.Message);
            // T-5: el mensaje al usuario NUNCA menciona la tabla tecnica real.
            Assert.DoesNotContain("TestFkFantasma", ex.Message);

            await using var verifyCtx = _fixture.CreateDbContext();
            Assert.Equal(1, await verifyCtx.Customers.CountAsync());
            Assert.Equal(1, await verifyCtx.Quotes.CountAsync());

            var fkCountDespues = await CountForeignKeysOnQuotesCustomerIdAsync(verifyCtx);
            Assert.Equal(1, fkCountDespues); // la FK conocida sigue exactamente igual: el rollback la restituyo
        }
        finally
        {
            await ctx.Database.ExecuteSqlRawAsync("""DROP TABLE IF EXISTS "TestFkFantasma";""");
        }
    }

    private static async Task<int> CountForeignKeysOnQuotesCustomerIdAsync(AppDbContext ctx)
    {
        return await ctx.Database.SqlQueryRaw<int>("""
            SELECT COUNT(*)::int AS "Value"
            FROM information_schema.table_constraints tc
            JOIN information_schema.key_column_usage kcu
              ON tc.constraint_name = kcu.constraint_name AND tc.table_schema = kcu.table_schema
            WHERE tc.constraint_type = 'FOREIGN KEY' AND tc.table_schema = 'public'
              AND tc.table_name = 'Quotes' AND kcu.column_name = 'CustomerId'
            """).FirstAsync();
    }

    /// <summary>
    /// Guarda anti-tabla-#90 (fix bloqueante #5, revisión 2026-07-27) + hallazgo N1 (ronda de seguridad, mismo
    /// día): compara TODAS las tablas reales de <c>information_schema.tables</c> (schema <c>public</c>)
    /// contra la unión de la lista blanca de <c>SystemDataWipeService</c> + los supervivientes conocidos. La
    /// lista blanca se DERIVA de los arrays <c>internal</c> del servicio (<see cref="SystemDataWipeService.ReservasYPlataTables"/>
    /// y compañía, expuestos con <c>InternalsVisibleTo</c>) en vez de mantener una copia paralela hardcodeada
    /// acá — antes, un cambio de clasificación en el servicio (como la reclasificación B3 de las tablas de
    /// plata del operador) podía desincronizar esta copia en silencio sin que ningún test lo detectara. Si
    /// alguien agrega una entidad nueva al modelo de EF y se olvida de clasificarla en el servicio, este test
    /// se pone ROJO.
    /// </summary>
    [Fact]
    public async Task InformationSchemaTables_CoincideExactamenteConListaBlancaMasSupervivientes()
    {
        var businessTables = SystemDataWipeService.ReservasYPlataTables
            .Concat(SystemDataWipeService.ClientesTables)
            .Concat(SystemDataWipeService.OperadoresTables)
            .Concat(SystemDataWipeService.TarifarioTables)
            .Concat(SystemDataWipeService.PaisesYDestinosTables)
            .Concat(SystemDataWipeService.PosiblesClientesTables)
            .Concat(new[] { SystemDataWipeService.CommissionRulesTable, SystemDataWipeService.RateSupplierSalesTable });

        var configTables = WipeGroups.ConfiguracionTables;

        var handledSeparately = new[] { "BankAccounts" };

        var survivors = new[]
        {
            "AspNetUsers", "AspNetRoles", "AspNetUserClaims", "AspNetUserLogins", "AspNetUserRoles",
            "AspNetUserTokens", "AspNetRoleClaims", "RolePermissions", "AuditLogs", "__EFMigrationsHistory",
        };

        var expectedTables = businessTables
            .Concat(configTables)
            .Concat(handledSeparately)
            .Concat(survivors)
            .ToHashSet(StringComparer.Ordinal);

        await using var ctx = _fixture.CreateDbContext();
        var actualTablesRaw = await ctx.Database
            .SqlQueryRaw<string>("""
                SELECT table_name FROM information_schema.tables
                WHERE table_schema = 'public' AND table_type = 'BASE TABLE'
                """)
            .ToListAsync();
        var actualTables = actualTablesRaw.ToHashSet(StringComparer.Ordinal);

        var unclassified = actualTables.Except(expectedTables).ToList();
        var missingFromDb = expectedTables.Except(actualTables).Where(t => t != "__EFMigrationsHistory").ToList();

        Assert.True(unclassified.Count == 0,
            $"Tablas SIN CLASIFICAR en SystemDataWipeService (agregalas a negocio/configuracion/supervivientes): {string.Join(", ", unclassified)}");
        Assert.True(missingFromDb.Count == 0,
            $"Tablas clasificadas que YA NO EXISTEN en la base (revisar la lista blanca, puede haber un nombre viejo): {string.Join(", ", missingFromDb)}");
    }
}
