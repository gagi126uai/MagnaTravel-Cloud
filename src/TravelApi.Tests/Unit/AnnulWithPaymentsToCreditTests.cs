using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
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

    private ReservaService BuildReservaService(AppDbContext context, IAuditService? auditService = null)
        => new(context, _mapper, _settingsServiceMock.Object, BuildUserManager(), NullLogger<ReservaService>.Instance,
            auditService: auditService);

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
            reservaPublicId.ToString(), actorUserId: "u1", actorUserName: "User One");

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
            reservaPublicId.ToString(), actorUserId: "u1", actorUserName: "User One");

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
                reservaPublicId.ToString(), actorUserId: "u1", actorUserName: "User One"));

        // No se creo credito ni puente, y la reserva sigue Confirmed (no se anulo).
        Assert.Empty(await context.ClientCreditEntries.AsNoTracking().ToListAsync());
        Assert.Empty(await context.Payments.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.Method == CancellationToClientCreditConverter.BridgeMethod).ToListAsync());
        var reserva = await context.Reservas.AsNoTracking().FirstAsync(r => r.Id == 1);
        Assert.Equal(EstadoReserva.Confirmed, reserva.Status);
    }

    // ============================ Test 3b — sin cobros: rechaza (es baja simple) ============================

    [Fact]
    public async Task AnnulWithCredit_WithoutPayments_Rejects()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedFirmReservaAsync(context, EstadoReserva.Confirmed, arsSale: 100m);
        // sin cobros

        var reservaPublicId = await context.Reservas.AsNoTracking()
            .Where(r => r.Id == 1).Select(r => r.PublicId).FirstAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BuildReservaService(context).AnnulWithPaymentsToCreditAsync(
                reservaPublicId.ToString(), actorUserId: "u1", actorUserName: "User One"));

        Assert.Empty(await context.ClientCreditEntries.AsNoTracking().ToListAsync());
        var reserva = await context.Reservas.AsNoTracking().FirstAsync(r => r.Id == 1);
        Assert.Equal(EstadoReserva.Confirmed, reserva.Status);
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
                reservaPublicId.ToString(), actorUserId: "u1", actorUserName: "User One"));

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
            reservaPublicId.ToString(), actorUserId: "u1", actorUserName: "User One");

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
        await service.AnnulWithPaymentsToCreditAsync(reservaPublicId.ToString(), "u1", "User One");

        // Segunda invocacion (simula doble clic / retry de la ExecutionStrategy): debe ser NO-OP, no lanzar
        // (la reserva ya esta Cancelled) y NO duplicar el credito ni el puente.
        var dto = await service.AnnulWithPaymentsToCreditAsync(reservaPublicId.ToString(), "u1", "User One");
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
                reservaPublicId.ToString(), "u1", "User One"));

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
            reservaPublicId.ToString(), "u1", "User One");

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
        // ...y NO expone costo del operador ni otros datos sensibles.
        Assert.DoesNotContain("cost", staged.Details, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("netcost", staged.Details, StringComparison.OrdinalIgnoreCase);
    }
}
