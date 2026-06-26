using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.Constants;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Application.Mappings;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Reservations;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// (2026-06-25) Caso (3) del flujo unificado de "Anular reserva": anular una reserva EN FIRME, SIN factura con
/// CAE vivo pero CON cobros vivos -> pasa a Cancelled Y la plata cobrada queda como SALDO A FAVOR del cliente
/// (un <see cref="ClientCreditEntry"/> por moneda con cobros), sin emitir Nota de Credito.
///
/// <para>Cubre: (1) una moneda -> reserva Cancelled + credito por el monto pagado + saldo de reserva 0 + plata
/// conservada; (2) multimoneda -> un credito POR moneda; (3) con factura CAE viva -> RECHAZA (deriva al camino
/// formal) sin tocar nada; (4) el discriminador <c>CancellationCase</c> devuelve el caso correcto para los 4
/// escenarios. Tambien: sin cobros -> rechaza (es baja simple); estado no firme -> rechaza.</para>
///
/// <para><b>Nota InMemory</b>: usa el flujo real del service. InMemory no soporta transacciones (la rama
/// IsRelational corre el mismo cuerpo sin transaccion); la atomicidad REAL se valida en integracion Postgres.
/// La cuenta (saldo, credito, puente) si se verifica aca.</para>
/// </summary>
public class AnnulWithPaymentsToCreditTests
{
    private readonly DbContextOptions<AppDbContext> _dbOptions;
    private readonly IMapper _mapper;
    private readonly Mock<IOperationalFinanceSettingsService> _settingsServiceMock;

    // Motivo valido (>= 10 chars) para los casos felices. Sin la palabra "cost" para no chocar con la
    // asercion del test de auditoria de que el detail no expone costos.
    private const string ValidReason = "Cliente desistio del viaje por fuerza mayor";

    public AnnulWithPaymentsToCreditTests()
    {
        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        _mapper = new MapperConfiguration(c => c.AddProfile<MappingProfile>()).CreateMapper();

        _settingsServiceMock = new Mock<IOperationalFinanceSettingsService>();
        _settingsServiceMock
            .Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings());
    }

    private static UserManager<ApplicationUser> BuildUserManager()
    {
        var store = new Mock<IUserStore<ApplicationUser>>();
        store.Setup(s => s.FindByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync((ApplicationUser?)null);
        return new UserManager<ApplicationUser>(
            store.Object, null!, null!,
            Array.Empty<IUserValidator<ApplicationUser>>(),
            Array.Empty<IPasswordValidator<ApplicationUser>>(),
            null!, null!, null!, null!);
    }

    /// <summary>
    /// HttpContext con el rol indicado (y opcionalmente el userId). Sirve para ejercitar el guard de permisos
    /// (reservas.cancel / reservas.cancel_with_payment) que ahora corre dentro de AnnulWithPaymentsToCreditAsync.
    /// </summary>
    private static IHttpContextAccessor BuildContextAccessor(string userId, params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };
        foreach (var role in roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }
        var ctx = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) };
        return new HttpContextAccessor { HttpContext = ctx };
    }

    /// <summary>Resolver de permisos fake: devuelve EXACTAMENTE el set indicado para ese userId.</summary>
    private static IUserPermissionResolver BuildResolver(string userId, params string[] permissions)
    {
        var mock = new Mock<IUserPermissionResolver>();
        IReadOnlySet<string> set = new HashSet<string>(permissions);
        mock.Setup(r => r.GetPermissionsAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(set);
        return mock.Object;
    }

    // La mayoria de los tests se enfocan en la LOGICA de plata/estado, no en permisos. Para que el guard de
    // permisos quede inerte, construimos el service con un HttpContext de rol Admin (Admin bypassa la authz).
    // Los tests de PERMISO usan BuildReservaServiceWithAuthz con un Vendedor y un resolver concreto.
    private ReservaService BuildReservaService(AppDbContext context, IAuditService? auditService = null)
        => new(context, _mapper, _settingsServiceMock.Object, BuildUserManager(), NullLogger<ReservaService>.Instance,
            permissionResolver: BuildResolver("u1"), httpContextAccessor: BuildContextAccessor("u1", "Admin"),
            auditService: auditService);

    /// <summary>
    /// Service para los tests de PERMISO: inyecta un HttpContext NO-Admin (Vendedor) y un resolver con el set de
    /// permisos indicado, para que el guard de authz dentro de AnnulWithPaymentsToCreditAsync efectivamente corra.
    /// </summary>
    private ReservaService BuildReservaServiceWithAuthz(
        AppDbContext context, string userId, params string[] permissions)
        => new(context, _mapper, _settingsServiceMock.Object, BuildUserManager(), NullLogger<ReservaService>.Instance,
            permissionResolver: BuildResolver(userId, permissions),
            httpContextAccessor: BuildContextAccessor(userId, "Vendedor"));

    /// <summary>
    /// Fake de IAuditService que CAPTURA lo que se stagea (no toca la base). Solo nos interesa
    /// <see cref="StageBusinessEvent"/> para verificar el evento de "anulada con cobros a saldo a favor"; el
    /// resto de los miembros no se ejercitan en estos tests.
    /// </summary>
    private sealed class CapturingAuditService : IAuditService
    {
        public readonly System.Collections.Generic.List<(string Action, string EntityName, string EntityId, string? Details)> Staged = new();

        public void StageBusinessEvent(string action, string entityName, string entityId, string? details, string userId, string? userName)
            => Staged.Add((action, entityName, entityId, details));

        public Task LogBusinessEventAsync(string action, string entityName, string entityId, string? details, string userId, string? userName, CancellationToken ct)
            => Task.CompletedTask;

        public Task<System.Collections.Generic.IEnumerable<TravelApi.Domain.Entities.AuditLog>> GetAuditLogsAsync(
            string? entityName, string? entityId, string? alternateEntityId, DateTime? dateFrom, DateTime? dateTo, string? userId, CancellationToken ct)
            => Task.FromResult<System.Collections.Generic.IEnumerable<TravelApi.Domain.Entities.AuditLog>>(Array.Empty<TravelApi.Domain.Entities.AuditLog>());

        public Task<TravelApi.Application.Contracts.PagedResult<TravelApi.Domain.Entities.AuditLog>> GetGlobalAuditLogsAsync(
            string? entityName, string? action, string? userId, DateTime? dateFrom, DateTime? dateTo, string? searchTerm, string? category, int page, int pageSize, CancellationToken ct)
            => Task.FromResult(new TravelApi.Application.Contracts.PagedResult<TravelApi.Domain.Entities.AuditLog>(
                Array.Empty<TravelApi.Domain.Entities.AuditLog>(), 0, page, pageSize));
    }

    /// <summary>
    /// Siembra un operador (Supplier) con su saldo INFLADO + una fila SupplierBalanceByCurrency, y le ata el
    /// servicio ARS de la reserva con NetCost &gt; 0. Sirve para verificar que tras anular, la deuda del
    /// operador se RECALCULA a 0 (los servicios anulados dejan de contar).
    /// </summary>
    private static async Task SeedSupplierWithDebtForArsServiceAsync(AppDbContext context, decimal netCost)
    {
        context.Suppliers.Add(new Supplier { Id = 1, Name = "Operador Test", CurrentBalance = netCost });
        context.SupplierBalanceByCurrency.Add(new SupplierBalanceByCurrency
        {
            SupplierId = 1, Currency = "ARS", ConfirmedPurchases = netCost, TotalPaid = 0m, Balance = netCost,
        });
        // El servicio ARS sembrado por SeedFirmReservaAsync (Id=1) pasa a tener costo contra el operador.
        var arsService = await context.Servicios.FirstAsync(s => s.Id == 1);
        arsService.SupplierId = 1;
        arsService.NetCost = netCost;
        await context.SaveChangesAsync();
    }

    // ============================ Seeding ============================

    private static async Task SeedFirmReservaAsync(
        AppDbContext context,
        string status = EstadoReserva.Confirmed,
        decimal arsSale = 100m,
        decimal usdSale = 0m)
    {
        context.Customers.Add(new Customer { Id = 1, FullName = "Cliente Test" });
        context.Reservas.Add(new Reserva
        {
            Id = 1,
            NumeroReserva = "F-2026-0001",
            Name = "Reserva test",
            Status = status,
            PayerId = 1,
            TotalSale = arsSale + usdSale,
            TotalCost = 0m,
            Balance = arsSale + usdSale,
            TotalPaid = 0m,
        });
        if (arsSale > 0m)
        {
            context.Servicios.Add(new ServicioReserva
            {
                Id = 1, ReservaId = 1, ServiceType = "Hotel", ProductType = "Hotel",
                Description = "S-ARS", ConfirmationNumber = "ABC", Status = "Confirmado",
                Currency = "ARS", DepartureDate = DateTime.UtcNow.AddDays(15),
                SalePrice = arsSale, NetCost = 0m, Commission = arsSale,
                ConfirmedAt = DateTime.UtcNow.AddDays(-1), CreatedAt = DateTime.UtcNow,
            });
        }
        if (usdSale > 0m)
        {
            context.Servicios.Add(new ServicioReserva
            {
                Id = 2, ReservaId = 1, ServiceType = "Hotel", ProductType = "Hotel",
                Description = "S-USD", ConfirmationNumber = "DEF", Status = "Confirmado",
                Currency = "USD", DepartureDate = DateTime.UtcNow.AddDays(15),
                SalePrice = usdSale, NetCost = 0m, Commission = usdSale,
                ConfirmedAt = DateTime.UtcNow.AddDays(-1), CreatedAt = DateTime.UtcNow,
            });
        }
        await context.SaveChangesAsync();
    }

    private static void AddLivePayment(AppDbContext context, decimal amount, string currency)
    {
        context.Payments.Add(new Payment
        {
            ReservaId = 1, Amount = amount, Currency = currency, Method = "Transfer",
            Status = "Paid", EntryType = PaymentEntryTypes.Payment, AffectsCash = true,
            PaidAt = DateTime.UtcNow,
        });
    }

    // ============================ Test 1 — una moneda ============================

    [Fact]
    public async Task AnnulWithCredit_SingleCurrency_CancelsReserva_AndCreatesCredit_AndZeroesBalance()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedFirmReservaAsync(context, EstadoReserva.Confirmed, arsSale: 100m);
        AddLivePayment(context, 100m, "ARS");
        await context.SaveChangesAsync();

        var reservaPublicId = await context.Reservas.AsNoTracking()
            .Where(r => r.Id == 1).Select(r => r.PublicId).FirstAsync();

        var dto = await BuildReservaService(context).AnnulWithPaymentsToCreditAsync(
            reservaPublicId.ToString(), reason: ValidReason, actorUserId: "u1", actorUserName: "User One");

        // La reserva quedo Cancelled.
        Assert.Equal(EstadoReserva.Cancelled, dto.Status);

        // Saldo a favor del cliente por lo pagado (100 ARS).
        var credit = await context.ClientCreditEntries.AsNoTracking()
            .SingleAsync(c => c.CustomerId == 1);
        Assert.Equal(100m, credit.CreditedAmount);
        Assert.Equal(100m, credit.RemainingBalance);
        Assert.Equal(Monedas.Normalizar("ARS"), credit.Currency);
        // Origen ANULACION: sin BC ni allocation; con rastro de la reserva origen.
        Assert.Null(credit.BookingCancellationId);
        Assert.Null(credit.OperatorRefundAllocationId);
        Assert.Equal(1, credit.SourceReservaId);

        // Puente negativo que saca la plata de la reserva.
        var bridge = await context.Payments.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(p => p.ReservaId == 1 && p.Method == CancellationToClientCreditConverter.BridgeMethod);
        Assert.Equal(-100m, bridge.Amount);
        Assert.False(bridge.AffectsCash);

        // Plata conservada: saldo de la reserva en 0 (servicios cancelados + plata trasladada).
        var reserva = await context.Reservas.AsNoTracking().FirstAsync(r => r.Id == 1);
        Assert.Equal(0m, reserva.Balance);
    }

    // ============================ Test 2 — multimoneda ============================

    [Fact]
    public async Task AnnulWithCredit_MultiCurrency_CreatesOneCreditPerCurrency()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedFirmReservaAsync(context, EstadoReserva.Confirmed, arsSale: 100m, usdSale: 50m);
        AddLivePayment(context, 100m, "ARS");
        AddLivePayment(context, 50m, "USD");
        await context.SaveChangesAsync();

        var reservaPublicId = await context.Reservas.AsNoTracking()
            .Where(r => r.Id == 1).Select(r => r.PublicId).FirstAsync();

        var dto = await BuildReservaService(context).AnnulWithPaymentsToCreditAsync(
            reservaPublicId.ToString(), reason: ValidReason, actorUserId: "u1", actorUserName: "User One");

        Assert.Equal(EstadoReserva.Cancelled, dto.Status);

        var credits = await context.ClientCreditEntries.AsNoTracking()
            .Where(c => c.CustomerId == 1).ToListAsync();
        Assert.Equal(2, credits.Count);

        var ars = credits.Single(c => c.Currency == Monedas.Normalizar("ARS"));
        Assert.Equal(100m, ars.RemainingBalance);
        var usd = credits.Single(c => c.Currency == Monedas.Normalizar("USD"));
        Assert.Equal(50m, usd.RemainingBalance);

        // Un puente negativo por cada moneda.
        var bridges = await context.Payments.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.ReservaId == 1 && p.Method == CancellationToClientCreditConverter.BridgeMethod)
            .ToListAsync();
        Assert.Equal(2, bridges.Count);
        Assert.Contains(bridges, b => b.Currency == "ARS" && b.Amount == -100m);
        Assert.Contains(bridges, b => b.Currency == "USD" && b.Amount == -50m);

        // Ambas monedas saldadas en 0.
        var rows = await context.ReservaMoneyByCurrency.AsNoTracking().Where(m => m.ReservaId == 1).ToListAsync();
        Assert.All(rows, r => Assert.Equal(0m, r.Balance));
    }

    // ============================ Test 3 — con factura CAE viva: rechaza ============================

    [Fact]
    public async Task AnnulWithCredit_WithLiveInvoice_Rejects_AndDoesNotTouchAnything()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedFirmReservaAsync(context, EstadoReserva.Confirmed, arsSale: 100m);
        AddLivePayment(context, 100m, "ARS");
        // Factura con CAE vivo (no NC): TipoComprobante 1 = Factura A.
        context.Invoices.Add(new Invoice
        {
            Id = 1, ReservaId = 1, TipoComprobante = 1, CAE = "12345678901234",
            AnnulmentStatus = AnnulmentStatus.None,
        });
        await context.SaveChangesAsync();

        var reservaPublicId = await context.Reservas.AsNoTracking()
            .Where(r => r.Id == 1).Select(r => r.PublicId).FirstAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BuildReservaService(context).AnnulWithPaymentsToCreditAsync(
                reservaPublicId.ToString(), reason: ValidReason, actorUserId: "u1", actorUserName: "User One"));

        // No se creo credito ni puente, y la reserva sigue Confirmed (no se anulo).
        Assert.Empty(await context.ClientCreditEntries.AsNoTracking().ToListAsync());
        Assert.Empty(await context.Payments.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.Method == CancellationToClientCreditConverter.BridgeMethod).ToListAsync());
        var reserva = await context.Reservas.AsNoTracking().FirstAsync(r => r.Id == 1);
        Assert.Equal(EstadoReserva.Confirmed, reserva.Status);
    }

    // =============== Test 3b — sin cobros (DirectCancel): baja directa, sin saldo a favor ===============

    [Fact]
    public async Task AnnulWithCredit_WithoutPayments_CancelsDirectly_WithoutCreatingCredit()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedFirmReservaAsync(context, EstadoReserva.Confirmed, arsSale: 100m);
        // sin cobros -> caso DirectCancel: el mismo endpoint ahora hace la baja DIRECTA (antes rechazaba).

        var reservaPublicId = await context.Reservas.AsNoTracking()
            .Where(r => r.Id == 1).Select(r => r.PublicId).FirstAsync();

        var dto = await BuildReservaService(context).AnnulWithPaymentsToCreditAsync(
            reservaPublicId.ToString(), reason: ValidReason, actorUserId: "u1", actorUserName: "User One");

        // La reserva quedo Cancelled.
        Assert.Equal(EstadoReserva.Cancelled, dto.Status);

        // NO se creo ningun saldo a favor (no habia plata que trasladar) ni puente.
        Assert.Empty(await context.ClientCreditEntries.AsNoTracking().ToListAsync());
        Assert.Empty(await context.Payments.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.Method == CancellationToClientCreditConverter.BridgeMethod).ToListAsync());

        var reserva = await context.Reservas.AsNoTracking().FirstAsync(r => r.Id == 1);
        Assert.Equal(EstadoReserva.Cancelled, reserva.Status);
    }

    // =============== Test 3b' — sin cobros + sin pagador (PayerId null): se permite igual ===============

    [Fact]
    public async Task AnnulWithCredit_WithoutPayments_NullPayer_IsAllowed_AndCancels()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedFirmReservaAsync(context, EstadoReserva.Confirmed, arsSale: 100m);
        // Sin cobros y sin pagador: PayerId null es ACEPTABLE porque no se genera ningun saldo a favor.
        var seeded = await context.Reservas.FirstAsync(r => r.Id == 1);
        seeded.PayerId = null;
        await context.SaveChangesAsync();

        var reservaPublicId = await context.Reservas.AsNoTracking()
            .Where(r => r.Id == 1).Select(r => r.PublicId).FirstAsync();

        var dto = await BuildReservaService(context).AnnulWithPaymentsToCreditAsync(
            reservaPublicId.ToString(), reason: ValidReason, actorUserId: "u1", actorUserName: "User One");

        Assert.Equal(EstadoReserva.Cancelled, dto.Status);
        Assert.Empty(await context.ClientCreditEntries.AsNoTracking().ToListAsync());
    }

    // =============== Test 3b'' — sin cobros: el audit usa la accion de baja directa (sin saldo a favor) =====

    [Fact]
    public async Task AnnulWithCredit_WithoutPayments_StagesDirectAnnulAudit_WithEmptyCredits()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedFirmReservaAsync(context, EstadoReserva.Confirmed, arsSale: 100m);

        var reservaPublicId = await context.Reservas.AsNoTracking()
            .Where(r => r.Id == 1).Select(r => r.PublicId).FirstAsync();

        var audit = new CapturingAuditService();
        await BuildReservaService(context, audit).AnnulWithPaymentsToCreditAsync(
            reservaPublicId.ToString(), ValidReason, "u1", "User One");

        // Se stageo la accion de BAJA DIRECTA (no la de saldo a favor), con la lista de creditos VACIA.
        var staged = Assert.Single(audit.Staged,
            e => e.Action == AuditActions.ReservaAnnulledDirectlyWithoutCredit);
        Assert.DoesNotContain(audit.Staged, e => e.Action == AuditActions.ReservaCancelledWithPaymentsToClientCredit);
        Assert.NotNull(staged.Details);
        Assert.Contains("\"creditsByCurrency\":[]", staged.Details);
        Assert.Contains($"\"reason\":\"{ValidReason}\"", staged.Details);
    }

    // ============================ Test 3c — estado no firme: rechaza ============================

    [Fact]
    public async Task AnnulWithCredit_NonFirmState_Rejects()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedFirmReservaAsync(context, EstadoReserva.Budget, arsSale: 100m);
        AddLivePayment(context, 100m, "ARS");
        await context.SaveChangesAsync();

        var reservaPublicId = await context.Reservas.AsNoTracking()
            .Where(r => r.Id == 1).Select(r => r.PublicId).FirstAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BuildReservaService(context).AnnulWithPaymentsToCreditAsync(
                reservaPublicId.ToString(), reason: ValidReason, actorUserId: "u1", actorUserName: "User One"));

        Assert.Empty(await context.ClientCreditEntries.AsNoTracking().ToListAsync());
    }

    // ============================ Test 4 — el discriminador devuelve el caso correcto ============================

    [Fact]
    public async Task CancellationCase_PreSale_ForBudget()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedFirmReservaAsync(context, EstadoReserva.Budget, arsSale: 100m);

        var dto = await BuildReservaService(context).GetReservaByIdAsync(1);
        Assert.Equal(ReservaCancellationCases.PreSale, dto.CancellationCase);
        Assert.Empty(dto.CancellationCreditByCurrency);
    }

    [Fact]
    public async Task CancellationCase_DirectCancel_ForFirmWithoutMoney()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedFirmReservaAsync(context, EstadoReserva.Confirmed, arsSale: 100m);

        var dto = await BuildReservaService(context).GetReservaByIdAsync(1);
        Assert.Equal(ReservaCancellationCases.DirectCancel, dto.CancellationCase);
        Assert.Empty(dto.CancellationCreditByCurrency);
    }

    [Fact]
    public async Task CancellationCase_PaymentsToCredit_ForFirmWithPaymentsNoInvoice_WithAmountByCurrency()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedFirmReservaAsync(context, EstadoReserva.Confirmed, arsSale: 100m, usdSale: 50m);
        AddLivePayment(context, 100m, "ARS");
        AddLivePayment(context, 50m, "USD");
        await context.SaveChangesAsync();

        var dto = await BuildReservaService(context).GetReservaByIdAsync(1);
        Assert.Equal(ReservaCancellationCases.PaymentsToCredit, dto.CancellationCase);

        Assert.Equal(2, dto.CancellationCreditByCurrency.Count);
        Assert.Contains(dto.CancellationCreditByCurrency, l => l.Currency == "ARS" && l.Amount == 100m);
        Assert.Contains(dto.CancellationCreditByCurrency, l => l.Currency == "USD" && l.Amount == 50m);
    }

    [Fact]
    public async Task CancellationCase_CreditNote_ForFirmWithLiveInvoice()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedFirmReservaAsync(context, EstadoReserva.Confirmed, arsSale: 100m);
        AddLivePayment(context, 100m, "ARS");
        context.Invoices.Add(new Invoice
        {
            Id = 1, ReservaId = 1, TipoComprobante = 1, CAE = "12345678901234",
            AnnulmentStatus = AnnulmentStatus.None,
        });
        await context.SaveChangesAsync();

        var dto = await BuildReservaService(context).GetReservaByIdAsync(1);
        // Con factura viva manda el camino formal de NC, aunque haya cobros.
        Assert.Equal(ReservaCancellationCases.CreditNote, dto.CancellationCase);
    }

    [Fact]
    public async Task CancellationCase_NotApplicable_ForTerminalState()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedFirmReservaAsync(context, EstadoReserva.Closed, arsSale: 100m);

        var dto = await BuildReservaService(context).GetReservaByIdAsync(1);
        Assert.Equal(ReservaCancellationCases.NotApplicable, dto.CancellationCase);
    }

    // ===================== BLOQUEANTE 1 — recalcular la deuda del operador =====================

    [Fact]
    public async Task AnnulWithCredit_RecalculatesOperatorDebtToZero_AfterCancellingServices()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedFirmReservaAsync(context, EstadoReserva.Confirmed, arsSale: 100m);
        AddLivePayment(context, 100m, "ARS");
        await context.SaveChangesAsync();
        // El servicio ARS tiene costo 80 contra el operador 1, que arranca con saldo INFLADO en 80.
        await SeedSupplierWithDebtForArsServiceAsync(context, netCost: 80m);

        var reservaPublicId = await context.Reservas.AsNoTracking()
            .Where(r => r.Id == 1).Select(r => r.PublicId).FirstAsync();

        await BuildReservaService(context).AnnulWithPaymentsToCreditAsync(
            reservaPublicId.ToString(), reason: ValidReason, actorUserId: "u1", actorUserName: "User One");

        // Tras anular (reserva Cancelled + servicio cancelado), la deuda del operador se recalcula a 0:
        // el servicio anulado deja de contar. Antes del fix quedaba INFLADA en 80.
        var supplier = await context.Suppliers.AsNoTracking().FirstAsync(s => s.Id == 1);
        Assert.Equal(0m, supplier.CurrentBalance);

        // La tabla por moneda del operador tambien queda saldada (sin filas vivas o en 0).
        var supplierRows = await context.SupplierBalanceByCurrency.AsNoTracking()
            .Where(r => r.SupplierId == 1).ToListAsync();
        Assert.All(supplierRows, r => Assert.Equal(0m, r.Balance));
    }

    // ===================== BLOQUEANTE 2 — idempotencia (no duplica saldo a favor) =====================

    [Fact]
    public async Task AnnulWithCredit_SecondInvocation_IsNoOp_DoesNotDuplicateCreditOrBridge()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedFirmReservaAsync(context, EstadoReserva.Confirmed, arsSale: 100m);
        AddLivePayment(context, 100m, "ARS");
        await context.SaveChangesAsync();

        var reservaPublicId = await context.Reservas.AsNoTracking()
            .Where(r => r.Id == 1).Select(r => r.PublicId).FirstAsync();
        var service = BuildReservaService(context);

        // Primera anulacion: convierte 100 ARS a saldo a favor.
        await service.AnnulWithPaymentsToCreditAsync(reservaPublicId.ToString(), ValidReason, "u1", "User One");

        // Segunda invocacion (simula doble clic / retry de la ExecutionStrategy): debe ser NO-OP, no lanzar
        // (la reserva ya esta Cancelled) y NO duplicar el credito ni el puente.
        var dto = await service.AnnulWithPaymentsToCreditAsync(reservaPublicId.ToString(), ValidReason, "u1", "User One");
        Assert.Equal(EstadoReserva.Cancelled, dto.Status);

        // Sigue habiendo UN solo credito y UN solo puente (la idempotencia funciono).
        // NOTA: la concurrencia real Serializable (dos requests a la vez -> 40001 -> reintento) se valida en
        // integracion Postgres; InMemory no soporta transacciones. Aca cubrimos la idempotencia LOGICA.
        var credits = await context.ClientCreditEntries.AsNoTracking().Where(c => c.CustomerId == 1).ToListAsync();
        Assert.Single(credits);
        Assert.Equal(100m, credits[0].RemainingBalance);

        var bridges = await context.Payments.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.ReservaId == 1 && p.Method == CancellationToClientCreditConverter.BridgeMethod).ToListAsync();
        Assert.Single(bridges);
    }

    // ============ BLOQUEANTE 2-bis — idempotencia DirectCancel (rama alreadyCancelled, SIN puente) ============

    [Fact]
    public async Task AnnulWithCredit_DirectCancel_SecondInvocation_IsNoOp_ViaAlreadyCancelledBranch()
    {
        // Baja DIRECTA (sin cobros, sin factura): el converter NO crea puente, asi que la rama de idempotencia
        // por `alreadyConverted` (existe Payment con BridgeMethod) NUNCA dispara aca. El segundo clic debe cortar
        // por la rama `alreadyCancelled` (la reserva ya quedo Cancelled): no-op, NO un 409 de "estado no firme".
        await using var context = new AppDbContext(_dbOptions);
        await SeedFirmReservaAsync(context, EstadoReserva.Confirmed, arsSale: 100m);
        // sin cobros, sin factura

        var reservaPublicId = await context.Reservas.AsNoTracking()
            .Where(r => r.Id == 1).Select(r => r.PublicId).FirstAsync();
        var service = BuildReservaService(context);

        // Primera anulacion: baja directa -> Cancelled, servicios cancelados, sin credito ni puente.
        var first = await service.AnnulWithPaymentsToCreditAsync(reservaPublicId.ToString(), ValidReason, "u1", "User One");
        Assert.Equal(EstadoReserva.Cancelled, first.Status);
        Assert.Empty(await context.ClientCreditEntries.AsNoTracking().ToListAsync());
        Assert.Empty(await context.Payments.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.Method == CancellationToClientCreditConverter.BridgeMethod).ToListAsync());

        // Capturamos el conteo de servicios cancelados tras el 1er llamado, para verificar que el 2do no toca nada.
        var cancelledServicesAfterFirst = await context.Servicios.AsNoTracking()
            .CountAsync(s => s.ReservaId == 1 && s.Status == "Cancelado");

        // Segundo llamado (doble clic): NO lanza (no 409 por estado no firme), devuelve la reserva Cancelled,
        // y NO crea entidades nuevas (rama alreadyCancelled = no-op).
        var second = await service.AnnulWithPaymentsToCreditAsync(reservaPublicId.ToString(), ValidReason, "u1", "User One");
        Assert.Equal(EstadoReserva.Cancelled, second.Status);

        // Sigue sin credito ni puente (no se genero nada de la nada en el reintento).
        Assert.Empty(await context.ClientCreditEntries.AsNoTracking().ToListAsync());
        Assert.Empty(await context.Payments.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.Method == CancellationToClientCreditConverter.BridgeMethod).ToListAsync());

        // El estado no cambio y la cancelacion de servicios no se duplico ni revirtio.
        var reserva = await context.Reservas.AsNoTracking().FirstAsync(r => r.Id == 1);
        Assert.Equal(EstadoReserva.Cancelled, reserva.Status);
        var cancelledServicesAfterSecond = await context.Servicios.AsNoTracking()
            .CountAsync(s => s.ReservaId == 1 && s.Status == "Cancelado");
        Assert.Equal(cancelledServicesAfterFirst, cancelledServicesAfterSecond);
    }

    // ===================== BLOQUEANTE 3 — cobros pero sin pagador: rechaza, no pierde plata =====================

    [Fact]
    public async Task AnnulWithCredit_WithPaymentsButNullPayer_Rejects_AndDoesNotMutate()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedFirmReservaAsync(context, EstadoReserva.Confirmed, arsSale: 100m);
        // Quitamos el pagador: hay cobros pero no hay bolsillo de cliente al que mover la plata.
        var reserva = await context.Reservas.FirstAsync(r => r.Id == 1);
        reserva.PayerId = null;
        AddLivePayment(context, 100m, "ARS");
        await context.SaveChangesAsync();

        var reservaPublicId = await context.Reservas.AsNoTracking()
            .Where(r => r.Id == 1).Select(r => r.PublicId).FirstAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BuildReservaService(context).AnnulWithPaymentsToCreditAsync(
                reservaPublicId.ToString(), ValidReason, "u1", "User One"));

        // NADA mutado: sigue Confirmed, sin credito, sin puente. La plata NO desaparece.
        var after = await context.Reservas.AsNoTracking().FirstAsync(r => r.Id == 1);
        Assert.Equal(EstadoReserva.Confirmed, after.Status);
        Assert.Empty(await context.ClientCreditEntries.AsNoTracking().ToListAsync());
        Assert.Empty(await context.Payments.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.Method == CancellationToClientCreditConverter.BridgeMethod).ToListAsync());
    }

    // ===================== I1 — auditoria del evento, por moneda y sin datos sensibles =====================

    [Fact]
    public async Task AnnulWithCredit_StagesAuditEvent_WithCreditByCurrency_AndNoSensitiveCost()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedFirmReservaAsync(context, EstadoReserva.Confirmed, arsSale: 100m, usdSale: 50m);
        AddLivePayment(context, 100m, "ARS");
        AddLivePayment(context, 50m, "USD");
        await context.SaveChangesAsync();

        var reservaPublicId = await context.Reservas.AsNoTracking()
            .Where(r => r.Id == 1).Select(r => r.PublicId).FirstAsync();

        var audit = new CapturingAuditService();
        await BuildReservaService(context, audit).AnnulWithPaymentsToCreditAsync(
            reservaPublicId.ToString(), ValidReason, "u1", "User One");

        // Se stageo exactamente el evento de "anulada con cobros a saldo a favor".
        var staged = Assert.Single(audit.Staged,
            e => e.Action == AuditActions.ReservaCancelledWithPaymentsToClientCredit);
        Assert.Equal(AuditActions.ReservaEntityName, staged.EntityName);
        Assert.Equal("1", staged.EntityId);

        // El detail lleva el monto POR MONEDA (currency + amount) de cada saldo a favor generado...
        Assert.NotNull(staged.Details);
        Assert.Contains("\"currency\":\"ARS\"", staged.Details);
        Assert.Contains("\"amount\":100", staged.Details);
        Assert.Contains("\"currency\":\"USD\"", staged.Details);
        Assert.Contains("\"amount\":50", staged.Details);
        // ...y el MOTIVO declarado por el operador (justificacion de negocio que cierra el hueco de auditoria).
        Assert.Contains($"\"reason\":\"{ValidReason}\"", staged.Details);
        // ...y NO expone costo del operador ni otros datos sensibles.
        Assert.DoesNotContain("cost", staged.Details, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("netcost", staged.Details, StringComparison.OrdinalIgnoreCase);
    }

    // ===================== Motivo obligatorio (>= 10 chars) — validacion server-side =====================

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("corto")]          // < 10 chars
    [InlineData("123456789")]      // 9 chars, justo por debajo del minimo
    public async Task AnnulWithCredit_InvalidReason_Throws_AndDoesNotMutate(string invalidReason)
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedFirmReservaAsync(context, EstadoReserva.Confirmed, arsSale: 100m);
        AddLivePayment(context, 100m, "ARS");
        await context.SaveChangesAsync();

        var reservaPublicId = await context.Reservas.AsNoTracking()
            .Where(r => r.Id == 1).Select(r => r.PublicId).FirstAsync();

        // Motivo vacio/corto -> ArgumentException (el controller la mapea a 400). Se valida ANTES de tocar nada.
        await Assert.ThrowsAsync<ArgumentException>(() =>
            BuildReservaService(context).AnnulWithPaymentsToCreditAsync(
                reservaPublicId.ToString(), invalidReason, "u1", "User One"));

        // NADA mutado: sigue Confirmed, sin credito, sin puente.
        var after = await context.Reservas.AsNoTracking().FirstAsync(r => r.Id == 1);
        Assert.Equal(EstadoReserva.Confirmed, after.Status);
        Assert.Empty(await context.ClientCreditEntries.AsNoTracking().ToListAsync());
        Assert.Empty(await context.Payments.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.Method == CancellationToClientCreditConverter.BridgeMethod).ToListAsync());
    }

    // ===================== PERMISOS — mismo criterio condicional que UpdateStatusAsync =====================
    // Base reservas.cancel; SOLO si la reserva tiene cobros/facturas, ademas reservas.cancel_with_payment.

    [Fact]
    public async Task AnnulWithCredit_DirectCancel_WithCancelPermissionOnly_Succeeds()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedFirmReservaAsync(context, EstadoReserva.Confirmed, arsSale: 100m);
        // Sin cobros (DirectCancel): a un Vendedor con SOLO reservas.cancel le alcanza.

        var reservaPublicId = await context.Reservas.AsNoTracking()
            .Where(r => r.Id == 1).Select(r => r.PublicId).FirstAsync();

        var service = BuildReservaServiceWithAuthz(context, "vendedor-1", Permissions.ReservasCancel);
        var dto = await service.AnnulWithPaymentsToCreditAsync(
            reservaPublicId.ToString(), ValidReason, "vendedor-1", "Vendedor Uno");

        Assert.Equal(EstadoReserva.Cancelled, dto.Status);
    }

    [Fact]
    public async Task AnnulWithCredit_WithoutCancelPermission_Throws403_EvenForDirectCancel()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedFirmReservaAsync(context, EstadoReserva.Confirmed, arsSale: 100m);

        var reservaPublicId = await context.Reservas.AsNoTracking()
            .Where(r => r.Id == 1).Select(r => r.PublicId).FirstAsync();

        // Sin reservas.cancel -> 403, no toca nada.
        var service = BuildReservaServiceWithAuthz(context, "vendedor-1" /* sin permisos */);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.AnnulWithPaymentsToCreditAsync(reservaPublicId.ToString(), ValidReason, "vendedor-1", "Vendedor Uno"));

        var reserva = await context.Reservas.AsNoTracking().FirstAsync(r => r.Id == 1);
        Assert.Equal(EstadoReserva.Confirmed, reserva.Status);
    }

    [Fact]
    public async Task AnnulWithCredit_PaymentsToCredit_WithCancelPermissionOnly_Throws403()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedFirmReservaAsync(context, EstadoReserva.Confirmed, arsSale: 100m);
        AddLivePayment(context, 100m, "ARS");
        await context.SaveChangesAsync();

        var reservaPublicId = await context.Reservas.AsNoTracking()
            .Where(r => r.Id == 1).Select(r => r.PublicId).FirstAsync();

        // Con cobros pero SOLO reservas.cancel (falta reservas.cancel_with_payment) -> 403, no toca nada.
        var service = BuildReservaServiceWithAuthz(context, "vendedor-1", Permissions.ReservasCancel);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.AnnulWithPaymentsToCreditAsync(reservaPublicId.ToString(), ValidReason, "vendedor-1", "Vendedor Uno"));

        var reserva = await context.Reservas.AsNoTracking().FirstAsync(r => r.Id == 1);
        Assert.Equal(EstadoReserva.Confirmed, reserva.Status);
        Assert.Empty(await context.ClientCreditEntries.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task AnnulWithCredit_PaymentsToCredit_WithReinforcedPermission_Succeeds()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedFirmReservaAsync(context, EstadoReserva.Confirmed, arsSale: 100m);
        AddLivePayment(context, 100m, "ARS");
        await context.SaveChangesAsync();

        var reservaPublicId = await context.Reservas.AsNoTracking()
            .Where(r => r.Id == 1).Select(r => r.PublicId).FirstAsync();

        // Con cobros y reservas.cancel + reservas.cancel_with_payment -> pasa: anula y genera saldo a favor.
        var service = BuildReservaServiceWithAuthz(
            context, "colab-1", Permissions.ReservasCancel, Permissions.ReservasCancelWithPayment);
        var dto = await service.AnnulWithPaymentsToCreditAsync(
            reservaPublicId.ToString(), ValidReason, "colab-1", "Colaborador Uno");

        Assert.Equal(EstadoReserva.Cancelled, dto.Status);
        var credit = await context.ClientCreditEntries.AsNoTracking().SingleAsync(c => c.CustomerId == 1);
        Assert.Equal(100m, credit.RemainingBalance);
    }
}
