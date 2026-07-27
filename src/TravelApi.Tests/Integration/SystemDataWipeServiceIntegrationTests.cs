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
/// Obra "Empezar de cero" (2026-07-27): la ÚNICA red real del TRUNCATE de <see cref="SystemDataWipeService"/>.
/// Corre contra Postgres real (via <see cref="PostgresIntegrationFixture"/>) porque el borrado usa SQL crudo
/// (<c>TRUNCATE ... CASCADE</c>) que el proveedor InMemory ni siquiera puede ejecutar. Cubre el contrato
/// completo del plan: seed de datos de negocio + settings + BankAccount + usuario -&gt; wipe SIN tilde -&gt;
/// tablas de negocio VACÍAS, AspNetUsers/AuditLogs/settings INTACTOS, AuditLog del wipe escrito, BankAccount de
/// agencia sobrevive; y wipe CON tilde -&gt; settings también vacíos/en default (ApprovalPolicies vuelve a los
/// 7 defaults de fábrica, no al fallback genérico). También cubre los fixes de la revisión 2026-07-27: la
/// regla de comisión GENERAL sobrevive sin el tilde (fix bloqueante #2) y la guarda anti-tabla-nueva-sin-
/// clasificar contra <c>information_schema.tables</c> (fix bloqueante #5).
///
/// <para>El paso de BACKUP se inyecta como fake (<see cref="IWipeBackupPort"/>) — no requiere pg_dump/MinIO
/// reales en el runner de CI. El backup real se prueba por construcción (ver
/// <c>PgDumpAndMinioWipeBackupPort</c>); lo crítico de este test es el candado fiscal y el TRUNCATE contra
/// Postgres real. El <see cref="IAuditService"/> SÍ es el real (<c>AuditService</c> sobre un
/// <c>Repository&lt;AuditLog&gt;</c> del mismo <c>ctx</c>): permite verificar de punta a punta que el audit log
/// de un RECHAZO sobrevive aunque la transacción de borrado se haya deshecho.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class SystemDataWipeServiceIntegrationTests : IClassFixture<PostgresIntegrationFixture>, IAsyncLifetime
{
    private const string AdminUserId = "wipe-admin-1";

    private readonly PostgresIntegrationFixture _fixture;

    public SystemDataWipeServiceIntegrationTests(PostgresIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => ResetAllRelevantTablesAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// Reset propio de este test (NO el <c>ResetDatabaseAsync</c> del fixture, que solo cubre el módulo de
    /// cancelación): trunca TODAS las tablas que este test siembra o que el wipe podría tocar, para que cada
    /// [Fact] arranque de una base limpia dentro del mismo container compartido.
    /// </summary>
    private async Task ResetAllRelevantTablesAsync()
    {
        await using var ctx = _fixture.CreateDbContext();
        await ctx.Database.ExecuteSqlRawAsync("""
            TRUNCATE TABLE
                "TravelFiles", "Reservations", "Customers", "Suppliers", "Passengers", "Payments",
                "Invoices", "InvoiceItem", "InvoiceTribute", "PaymentReceipts", "ManualCashMovements",
                "BankAccounts", "Leads", "Quotes", "QuoteItems", "Countries", "Destinations", "Rates",
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

    /// <summary>Mismo mock de UserManager que la suite unitaria: el candado de contraseña ya está cubierto ahí.</summary>
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

    /// <summary>
    /// Fake de backup: COPIA verificada simulada (no toca MinIO real) + registro de si
    /// <see cref="RemoveOriginalObjectsAsync"/> se llamó, para poder verificar que la limpieza de originales
    /// pasa DESPUÉS del commit (fix bloqueante #1).
    /// </summary>
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
    }

    /// <summary>Servicio bajo prueba con el AuditService REAL (permite verificar rechazos de punta a punta).</summary>
    private static SystemDataWipeService NewWipeService(
        AppDbContext ctx, Mock<UserManager<ApplicationUser>> userManagerMock, FakeBackupPort backupPort)
    {
        var auditService = new AuditService(new Repository<AuditLog>(ctx), NullLogger<AuditService>.Instance);
        return new SystemDataWipeService(ctx, userManagerMock.Object, backupPort, auditService, NullLogger<SystemDataWipeService>.Instance);
    }

    /// <summary>Siembra un caso de negocio "de todo un poco" + settings + un BankAccount de cada dueño.</summary>
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

        ctx.AgencySettings.Add(new AgencySettings { AgencyName = "Agencia de prueba wipe" });
        ctx.AfipSettings.Add(new AfipSettings { IsProduction = false, Cuit = 20111111112 });
        ctx.OperationalFinanceSettings.Add(new OperationalFinanceSettings());
        ctx.WhatsAppBotConfigs.Add(new WhatsAppBotConfig());
        // ApprovalPolicy CUSTOM (distinto de los defaults de fabrica) para poder distinguir "sobrevivio tal
        // cual" (sin tilde) de "se reseteo a defaults de fabrica" (con tilde).
        ctx.ApprovalPolicies.Add(new ApprovalPolicy
        {
            RequestType = "PaymentDeadlineOverride",
            RequiresApproval = true, // el default de fabrica es FALSE - lo distingue del reseed
            UpdatedAt = DateTime.UtcNow,
        });

        // Regla de comision GENERAL (sin proveedor) - fix bloqueante #2: tiene que sobrevivir sin el tilde.
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
    public async Task ExecuteWipeAsync_SinIncluirConfiguracion_BorraNegocioYPreservaConfigYAgencyBankAccount()
    {
        await using var ctx = _fixture.CreateDbContext();
        await SeedBusinessAndConfigDataAsync(ctx);

        var user = await ctx.Set<ApplicationUser>().AsNoTracking().SingleAsync(u => u.Id == AdminUserId);
        var userManagerMock = BuildUserManagerMock(user);
        var backupPort = new FakeBackupPort();
        var service = NewWipeService(ctx, userManagerMock, backupPort);

        var result = await service.ExecuteWipeAsync(AdminUserId, "cualquier-cosa", "BORRAR TODO", incluirConfiguracion: false, CancellationToken.None);

        Assert.False(result.ConfiguracionBorrada);
        Assert.Equal(1, result.Borrado.Clientes);
        Assert.Equal(1, result.Borrado.Operadores);
        Assert.Equal(1, result.Borrado.Reservas);
        Assert.Equal(1, result.Borrado.Facturas);
        Assert.Equal(1, result.Borrado.PosiblesClientes);
        Assert.Equal(1, result.Borrado.Tarifario);
        Assert.Equal(2, result.Borrado.PaisesYDestinos); // 1 pais + 1 destino

        // Fix bloqueante #1: la limpieza de originales de MinIO se llama DESPUES del commit (si llegamos
        // hasta aca sin excepcion, el commit ya paso, asi que esto tiene que ser true).
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

        // Sobreviven SIEMPRE.
        Assert.True(await verifyCtx.Set<ApplicationUser>().AnyAsync(u => u.Id == AdminUserId));

        // Sin el tilde: configuracion INTACTA (la fila custom de ApprovalPolicy sigue tal cual, NO se reseteo).
        Assert.Equal(1, await verifyCtx.AgencySettings.CountAsync());
        Assert.Equal(1, await verifyCtx.AfipSettings.CountAsync());
        Assert.Equal(1, await verifyCtx.OperationalFinanceSettings.CountAsync());
        Assert.Equal(1, await verifyCtx.WhatsAppBotConfigs.CountAsync());
        var survivingPolicy = await verifyCtx.ApprovalPolicies.SingleAsync();
        Assert.Equal("PaymentDeadlineOverride", survivingPolicy.RequestType);
        Assert.True(survivingPolicy.RequiresApproval); // el valor CUSTOM sembrado, no el default de fabrica (false)

        // Fix bloqueante #2: la regla de comision GENERAL sobrevive (se re-inserto dentro de la transaccion),
        // aunque Suppliers (que se trunco) tenga FK fisica hacia CommissionRules.
        var survivingRule = await verifyCtx.CommissionRules.SingleAsync();
        Assert.Null(survivingRule.SupplierId);
        Assert.Equal(8.5m, survivingRule.CommissionPercent);
        Assert.Equal("Regla general de la agencia", survivingRule.Description);

        // BankAccounts: la de Cliente se borro, la de Agencia sobrevive (config).
        var remainingBankAccounts = await verifyCtx.BankAccounts.ToListAsync();
        var remainingAccount = Assert.Single(remainingBankAccounts);
        Assert.Equal(BankAccountOwnerType.Agency, remainingAccount.OwnerType);

        // AuditLog del wipe: exactamente 1 fila, con la accion correcta.
        // AuditLogs NUNCA se trunca (sobrevive siempre) y ademas el interceptor de auditoria automatica de
        // AppDbContext genera sus propias filas para cada SaveChanges del SEED (Customer/Supplier/etc creados
        // normalmente vía EF) — por eso filtramos por la accion del wipe en vez de asumir una unica fila total.
        var auditLog = await verifyCtx.AuditLogs.SingleAsync(a => a.Action == AuditActions.SystemDataWiped);
        Assert.Equal(AuditActions.SystemDataWiped, auditLog.Action);
        Assert.Equal(AuditActions.SystemDataWipeEntityName, auditLog.EntityName);
        // Fix menor #6: Changes es JSON de ESCALARES (nada de objetos anidados tipo "conteosBorrados": {...}).
        Assert.Contains("\"incluirConfiguracion\":false", auditLog.Changes);
        Assert.Contains("\"clientesBorrados\":1", auditLog.Changes);
        Assert.DoesNotContain("conteosBorrados", auditLog.Changes);
    }

    [Fact]
    public async Task ExecuteWipeAsync_ConIncluirConfiguracion_ReseteaConfiguracionADefaultsDeFabricaYBorraTodoBankAccount()
    {
        await using var ctx = _fixture.CreateDbContext();
        await SeedBusinessAndConfigDataAsync(ctx);

        var user = await ctx.Set<ApplicationUser>().AsNoTracking().SingleAsync(u => u.Id == AdminUserId);
        var userManagerMock = BuildUserManagerMock(user);
        var service = NewWipeService(ctx, userManagerMock, new FakeBackupPort());

        var result = await service.ExecuteWipeAsync(AdminUserId, "cualquier-cosa", "BORRAR TODO", incluirConfiguracion: true, CancellationToken.None);

        Assert.True(result.ConfiguracionBorrada);

        await using var verifyCtx = _fixture.CreateDbContext();
        // Configuracion TRUNCADA (settings singleton vuelven a nacer vacios - el GetOrCreate perezoso de
        // AgencySettings/AfipSettings/OperationalFinanceSettings los repuebla solos en el proximo GET/uso).
        Assert.Equal(0, await verifyCtx.AgencySettings.CountAsync());
        Assert.Equal(0, await verifyCtx.AfipSettings.CountAsync());
        Assert.Equal(0, await verifyCtx.OperationalFinanceSettings.CountAsync());
        Assert.Equal(0, await verifyCtx.WhatsAppBotConfigs.CountAsync());

        // ApprovalPolicies: NO queda vacia, vuelve a los 7 defaults de FABRICA (no al fallback generico).
        var policies = await verifyCtx.ApprovalPolicies.ToListAsync();
        Assert.Equal(7, policies.Count);
        var paymentDeadlineOverride = policies.Single(p => p.RequestType == "PaymentDeadlineOverride");
        Assert.False(paymentDeadlineOverride.RequiresApproval); // default de fabrica, no el TRUE custom sembrado
        var partialCreditNoteApproval = policies.Single(p => p.RequestType == "PartialCreditNoteApproval");
        Assert.True(partialCreditNoteApproval.RequiresApproval);
        Assert.Equal(5, partialCreditNoteApproval.ExpirationDaysOverride);

        // Fix bloqueante #2: CON el tilde, CommissionRules es parte de "borrar TODA la configuracion" - la
        // regla general NO se restaura (a diferencia del test sin tilde).
        Assert.Equal(0, await verifyCtx.CommissionRules.CountAsync());

        // BankAccounts: TODAS borradas (incluida la de Agencia).
        Assert.Equal(0, await verifyCtx.BankAccounts.CountAsync());

        // Usuario y auditoria siempre sobreviven.
        Assert.True(await verifyCtx.Set<ApplicationUser>().AnyAsync(u => u.Id == AdminUserId));
        // AuditLogs NUNCA se trunca (sobrevive siempre) y ademas el interceptor de auditoria automatica de
        // AppDbContext genera sus propias filas para cada SaveChanges del SEED (Customer/Supplier/etc creados
        // normalmente vía EF) — por eso filtramos por la accion del wipe en vez de asumir una unica fila total.
        var auditLog = await verifyCtx.AuditLogs.SingleAsync(a => a.Action == AuditActions.SystemDataWiped);
        Assert.Equal(AuditActions.SystemDataWiped, auditLog.Action);
        Assert.Contains("\"incluirConfiguracion\":true", auditLog.Changes);
    }

    [Fact]
    public async Task ExecuteWipeAsync_ConCommissionRuleDeOperadorEspecifico_MuereConSuOperadorAunqueGeneralSobreviva()
    {
        // Fix bloqueante #2 (test dedicado pedido en la revision): sembrar una regla GENERAL + una de
        // OPERADOR especifico, y verificar que solo la de operador muere con su Supplier.
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

        await service.ExecuteWipeAsync(AdminUserId, "cualquier-cosa", "BORRAR TODO", incluirConfiguracion: false, CancellationToken.None);

        await using var verifyCtx = _fixture.CreateDbContext();
        Assert.Equal(0, await verifyCtx.Suppliers.CountAsync());
        var survivingRule = await verifyCtx.CommissionRules.SingleAsync();
        Assert.Null(survivingRule.SupplierId);
        Assert.Equal("Regla general", survivingRule.Description);
    }

    [Fact]
    public async Task ExecuteWipeAsync_ConFacturaMarcadaProduccion_RechazaYNoBorraNada()
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
            service.ExecuteWipeAsync(AdminUserId, "cualquier-cosa", "BORRAR TODO", incluirConfiguracion: false, CancellationToken.None));

        await using var verifyCtx = _fixture.CreateDbContext();
        Assert.Equal(1, await verifyCtx.Customers.CountAsync());
        Assert.Equal(1, await verifyCtx.Invoices.CountAsync());
        Assert.False(backupPort.RemoveOriginalsWasCalled);
        // El candado fiscal frena ANTES de la transaccion de borrado: nunca se inserta el AuditLog del wipe
        // (puede haber OTRAS filas de auditoria automatica del seed de arriba - eso es normal y no es lo que
        // este test verifica).
        Assert.False(await verifyCtx.AuditLogs.AnyAsync(a => a.Action == AuditActions.SystemDataWiped));

        // Fix menor #6: el RECHAZO queda auditado con el motivo, y sobrevive aunque la transaccion de borrado
        // ni siquiera haya llegado a abrirse (este chequeo es el #1, fuera de la transaccion).
        var rejectedLog = await verifyCtx.AuditLogs.SingleAsync(a => a.Action == AuditActions.SystemDataWipeRejected);
        Assert.Equal(AuditActions.SystemDataWipeEntityName, rejectedLog.EntityName);
        Assert.Contains("productivo", rejectedLog.Changes ?? string.Empty);
        Assert.Equal(ex.Message, rejectedLog.Changes);
    }

    /// <summary>
    /// Guarda anti-tabla-#90 (fix bloqueante #5, revisión 2026-07-27): compara TODAS las tablas reales de
    /// <c>information_schema.tables</c> (schema <c>public</c>) contra la unión de la lista blanca de
    /// <c>SystemDataWipeService</c> + los supervivientes conocidos. Si alguien agrega una entidad nueva al
    /// modelo de EF y se olvida de clasificarla acá, este test se pone ROJO — obliga a decidir
    /// explícitamente si la tabla nueva es negocio, configuración o superviviente, en vez de que quede
    /// silenciosamente sin cubrir (o silenciosamente arrastrada por un CASCADE que nadie previó).
    /// </summary>
    [Fact]
    public async Task InformationSchemaTables_CoincideExactamenteConListaBlancaMasSupervivientes()
    {
        var businessTables = new[]
        {
            "PartialCreditNoteReconciliationReceipts", "PartialCreditNoteReconciliations", "ClientCreditWithdrawals",
            "ClientCreditEntries", "SupplierCreditApplications", "SupplierCreditEntries", "DeductionLines",
            "OperatorRefundAllocations", "OperatorRefundsReceived", "BookingCancellationDebitNoteAnnulments",
            "BookingCancellationCreditNotes", "BookingCancellationLineTreasuryFxAdjustments",
            "BookingCancellationLineOperatorCharges", "BookingCancellationLines", "BookingCancellations",
            "ApprovalRequests", "CashLedgerEntries", "ArcaIdempotencyKeys", "ManualCashMovements", "PaymentReceipts",
            "VoucherAuditEntries", "VoucherPassengerAssignments", "Vouchers", "PassengerServiceAssignments",
            "ReservaEditAuthorizationChanges", "ReservaEditAuthorizations", "ReservaStatusChangeLogs",
            "ReservaAttachments", "ReservaPendingChanges", "CommissionAccruals", "CommissionRules", "InvoiceTribute",
            "InvoiceItem", "Invoices", "WhatsAppDeliveries", "MessageDeliveries", "QuoteItems", "Quotes",
            "LeadActivities", "Leads", "UpcomingStartAlertDismissals", "Notifications",
            "SupplierInvoicePaymentApplicationReversals", "SupplierInvoicePaymentApplications",
            "SupplierInvoiceLines", "SupplierInvoices", "SupplierPayments", "RateSupplierSales", "Rates",
            "CatalogPackageDepartures", "CatalogPackages", "HotelBookings", "TransferBookings", "PackageBookings",
            "AssistanceBookings", "CustomerCreditLimitByCurrency", "SupplierBalanceByCurrency",
            "ReservaMoneyByCurrency", "FlightSegments", "Payments", "Passengers", "Reservations", "TravelFiles",
            "Customers", "Suppliers", "Countries", "DestinationDepartures", "Destinations",
            "BnaExchangeRateSnapshots", "BusinessSequences", "RefreshTokens", "OutboxMessage", "OutboxState",
            "InboxState",
        };

        var configTables = new[]
        {
            "AgencySettings", "AfipSettings", "OperationalFinanceSettings", "ApprovalPolicies", "WhatsAppBotConfigs",
        };

        // BankAccounts se maneja aparte (DELETE por OwnerType, no TRUNCATE) - igual tiene que estar clasificada.
        var handledSeparately = new[] { "BankAccounts" };

        // __EFMigrationsHistory no la crea EnsureCreatedAsync (el fixture no usa MigrateAsync, ver su doc
        // comment) - la dejamos en la lista de supervivientes por si el dia de mañana este test corre contra
        // una base migrada de verdad, pero NO es obligatoria en esta base de test.
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
