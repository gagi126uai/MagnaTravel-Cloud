using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Moq;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-022 Tanda 3 (capas 4, 7, 8): tesoreria LEE del Libro de Caja (con reversas netendo), enmascarado
/// see_cost NUEVO sobre egresos a proveedor, fuente UNICA AR/AP (dashboard == tesoreria) y cuenta corriente
/// del cliente por moneda + bolsillo de saldo a favor.
///
/// <para><b>Nota InMemory</b>: el provider InMemory no aplica CHECK constraints ni el indice unico parcial.
/// Estos tests verifican el COMPORTAMIENTO de lectura (que numero da el resumen, que se enmascara, que el
/// dashboard y la tesoreria coinciden). La validacion del schema contra Postgres es de la tanda de integracion.</para>
/// </summary>
public class Adr022Tanda3Tests
{
    private static void AddApprovedInvoice(AppDbContext context, int reservaId, decimal amount, string currency = "ARS")
        => context.Invoices.Add(new Invoice
        {
            ReservaId = reservaId,
            TipoComprobante = 11,
            PuntoDeVenta = 1,
            NumeroComprobante = reservaId,
            Resultado = "A",
            ImporteTotal = amount,
            MonId = currency == "USD" ? "DOL" : "PES",
            IssuedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        });

    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    // ====================================================================================
    // Harness de permisos (mismo patron que Adr022Tanda2Tests / SupplierService).
    // ====================================================================================

    private static IHttpContextAccessor BuildHttpContextAccessor(string userId, bool isAdmin = false)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };
        var identity = new ClaimsIdentity(claims, "Test");
        if (isAdmin)
            identity.AddClaim(new Claim(ClaimTypes.Role, "Admin"));
        return new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
    }

    private static IUserPermissionResolver BuildResolver(string userId, params string[] permissions)
    {
        var mock = new Mock<IUserPermissionResolver>();
        IReadOnlySet<string> set = new HashSet<string>(permissions);
        mock.Setup(r => r.GetPermissionsAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(set);
        return mock.Object;
    }

    /// <summary>Tesoreria con un caller que SI ve costos (tiene cobranzas.see_cost).</summary>
    private static TreasuryService BuildTreasuryCanSeeCost(AppDbContext context)
    {
        const string userId = "see-cost-user";
        return new TreasuryService(
            context, null!, financePositionService: null,
            httpContextAccessor: BuildHttpContextAccessor(userId),
            permissionResolver: BuildResolver(userId, Permissions.CobranzasSeeCost));
    }

    /// <summary>Tesoreria con un caller que NO ve costos (sin el permiso) -> egresos de proveedor enmascarados.</summary>
    private static TreasuryService BuildTreasuryNoCost(AppDbContext context)
    {
        const string userId = "no-cost-user";
        return new TreasuryService(
            context, null!, financePositionService: null,
            httpContextAccessor: BuildHttpContextAccessor(userId),
            permissionResolver: BuildResolver(userId /* sin permisos */));
    }

    // ====================================================================================
    // Seeds de asientos del libro
    // ====================================================================================

    private static CashLedgerEntry Ledger(
        string direction, decimal amount, string sourceType, string currency = "ARS",
        bool isReversal = false, bool isReversed = false, DateTime? occurredAt = null,
        int? supplierPaymentId = null, int? paymentId = null, int? manualId = null) => new()
    {
        Direction = direction,
        Amount = amount,
        Currency = currency,
        Method = "Transfer",
        OccurredAt = occurredAt ?? DateTime.UtcNow,
        SourceType = sourceType,
        IsReversal = isReversal,
        IsReversed = isReversed,
        SupplierPaymentId = supplierPaymentId,
        PaymentId = paymentId,
        ManualCashMovementId = manualId,
    };

    // ====================================================================================
    // Capa 4 — tesoreria lee del libro (con reversas)
    // ====================================================================================

    [Fact]
    public async Task CashSummary_ReversedPayment_NetsToZero()
    {
        await using var context = CreateContext();
        var now = DateTime.UtcNow;

        // Cobro +100 que YA fue revertido (IsReversed=true) + su reversa (IsReversal=true, Direction invertida).
        // El libro conserva las dos filas; el arqueo del mes debe dar neto 0 (no CashIn 100 / CashOut 100).
        context.CashLedgerEntries.AddRange(
            Ledger(CashMovementDirections.Income, 100m, CashLedgerSourceTypes.CustomerPayment, isReversed: true, occurredAt: now),
            Ledger(CashMovementDirections.Expense, 100m, CashLedgerSourceTypes.CustomerPayment, isReversal: true, occurredAt: now));
        await context.SaveChangesAsync();

        var summary = await BuildTreasuryCanSeeCost(context).GetCashSummaryAsync(cancellationToken: CancellationToken.None);

        Assert.Equal(0m, summary.CashInThisMonth);
        Assert.Equal(0m, summary.CashOutThisMonth);
        Assert.Equal(0m, summary.NetCashThisMonth);
        Assert.Empty(summary.CashByCurrency); // ambas monedas netearon a 0 -> sin lineas
    }

    [Fact]
    public async Task CashSummary_EditedPayment_ReflectsNewAmount()
    {
        await using var context = CreateContext();
        var now = DateTime.UtcNow;

        // Edicion de monto: viejo +100 (IsReversed) + reversa -100 + nuevo +150. Neto = 150 (no 50 ni 350).
        context.CashLedgerEntries.AddRange(
            Ledger(CashMovementDirections.Income, 100m, CashLedgerSourceTypes.CustomerPayment, isReversed: true, occurredAt: now),
            Ledger(CashMovementDirections.Expense, 100m, CashLedgerSourceTypes.CustomerPayment, isReversal: true, occurredAt: now),
            Ledger(CashMovementDirections.Income, 150m, CashLedgerSourceTypes.CustomerPayment, occurredAt: now));
        await context.SaveChangesAsync();

        var summary = await BuildTreasuryCanSeeCost(context).GetCashSummaryAsync(cancellationToken: CancellationToken.None);

        Assert.Equal(150m, summary.CashInThisMonth);
        Assert.Equal(0m, summary.CashOutThisMonth);
    }

    [Fact]
    public async Task CashByCurrency_SeparatesIncomeAndExpensePerCurrency()
    {
        await using var context = CreateContext();
        var now = DateTime.UtcNow;

        // Cobro ARS 500, cobro USD 50, pago a proveedor ARS 120.
        context.CashLedgerEntries.AddRange(
            Ledger(CashMovementDirections.Income, 500m, CashLedgerSourceTypes.CustomerPayment, currency: "ARS", occurredAt: now),
            Ledger(CashMovementDirections.Income, 50m, CashLedgerSourceTypes.CustomerPayment, currency: "USD", occurredAt: now),
            Ledger(CashMovementDirections.Expense, 120m, CashLedgerSourceTypes.SupplierPayment, currency: "ARS", occurredAt: now));
        await context.SaveChangesAsync();

        var summary = await BuildTreasuryCanSeeCost(context).GetSummaryAsync(CancellationToken.None);

        Assert.Equal(500m, summary.CashInByCurrency.Single(x => x.Currency == "ARS").Amount);
        Assert.Equal(50m, summary.CashInByCurrency.Single(x => x.Currency == "USD").Amount);
        Assert.Equal(120m, summary.CashOutByCurrency.Single(x => x.Currency == "ARS").Amount);
    }

    [Fact]
    public async Task Movements_ReversalAppearsAsRowWithInvertedSign()
    {
        await using var context = CreateContext();
        var now = DateTime.UtcNow;

        var payment = new Payment { Id = 1, Amount = 100m, Currency = "ARS", PaidAt = now, Status = "Paid", AffectsCash = true, Method = "Transfer", Notes = "Cobro X" };
        context.Payments.Add(payment);
        context.CashLedgerEntries.AddRange(
            Ledger(CashMovementDirections.Income, 100m, CashLedgerSourceTypes.CustomerPayment, isReversed: true, occurredAt: now, paymentId: 1),
            Ledger(CashMovementDirections.Expense, 100m, CashLedgerSourceTypes.CustomerPayment, isReversal: true, occurredAt: now, paymentId: 1));
        await context.SaveChangesAsync();

        var page = await BuildTreasuryCanSeeCost(context).GetMovementsAsync(
            new TreasuryMovementsQuery { Direction = "all", SourceType = "all" }, CancellationToken.None);

        // Q4: el libro muestra la anulacion como UNA fila propia con signo invertido (transparencia).
        Assert.Equal(2, page.Items.Count);
        Assert.Single(page.Items, m => m.Direction == CashMovementDirections.Income && m.Amount == 100m);
        Assert.Single(page.Items, m => m.Direction == CashMovementDirections.Expense && m.Amount == 100m);
        // Ambas apuntan al MISMO origen (Payment.PublicId), para que el front sepa que cobro se anulo.
        Assert.All(page.Items, m => Assert.Equal(payment.PublicId, m.SourcePublicId));
    }

    // ====================================================================================
    // Capa 4 — enmascarado see_cost (B3, trabajo NUEVO)
    // ====================================================================================

    [Fact]
    public async Task Movements_WithoutSeeCost_MasksSupplierPaymentAmounts()
    {
        await using var context = CreateContext();
        var now = DateTime.UtcNow;

        var supplier = new Supplier { Id = 1, Name = "Operador" };
        var supplierPayment = new SupplierPayment { Id = 1, SupplierId = 1, Amount = 120m, Currency = "ARS", PaidAt = now, Method = "Transfer" };
        context.Suppliers.Add(supplier);
        context.SupplierPayments.Add(supplierPayment);
        context.CashLedgerEntries.AddRange(
            Ledger(CashMovementDirections.Income, 500m, CashLedgerSourceTypes.CustomerPayment, occurredAt: now),
            Ledger(CashMovementDirections.Expense, 120m, CashLedgerSourceTypes.SupplierPayment, occurredAt: now, supplierPaymentId: 1));
        await context.SaveChangesAsync();

        var page = await BuildTreasuryNoCost(context).GetMovementsAsync(
            new TreasuryMovementsQuery { Direction = "all", SourceType = "all" }, CancellationToken.None);

        var supplierRow = page.Items.Single(m => m.SourceType == "SupplierPayment");
        var customerRow = page.Items.Single(m => m.SourceType == "CustomerPayment");
        Assert.Equal(0m, supplierRow.Amount);   // egreso a proveedor = costo -> enmascarado a 0
        Assert.Equal(500m, customerRow.Amount);  // cobro de cliente = venta -> visible
    }

    [Fact]
    public async Task Movements_WithSeeCost_ShowsSupplierPaymentAmounts()
    {
        await using var context = CreateContext();
        var now = DateTime.UtcNow;

        var supplier = new Supplier { Id = 1, Name = "Operador" };
        context.Suppliers.Add(supplier);
        context.SupplierPayments.Add(new SupplierPayment { Id = 1, SupplierId = 1, Amount = 120m, Currency = "ARS", PaidAt = now, Method = "Transfer" });
        context.CashLedgerEntries.Add(
            Ledger(CashMovementDirections.Expense, 120m, CashLedgerSourceTypes.SupplierPayment, occurredAt: now, supplierPaymentId: 1));
        await context.SaveChangesAsync();

        var page = await BuildTreasuryCanSeeCost(context).GetMovementsAsync(
            new TreasuryMovementsQuery { Direction = "all", SourceType = "all" }, CancellationToken.None);

        Assert.Equal(120m, page.Items.Single(m => m.SourceType == "SupplierPayment").Amount);
    }

    [Fact]
    public async Task Movements_WithoutSeeCost_MasksOperatorRefundAmounts_ButNotManualNorClientRefund()
    {
        // fix S2(a): el monto que el operador DEVUELVE es informacion de costo (RK-9) -> sin see_cost se
        // enmascara, aunque el front lo vea colapsado como "ManualAdjustment". En cambio, un ajuste manual
        // genuino (gasto propio) y la devolucion FISICA al cliente NO son costo y quedan visibles.
        await using var context = CreateContext();
        var now = DateTime.UtcNow;

        context.CashLedgerEntries.AddRange(
            Ledger(CashMovementDirections.Income, 99m, CashLedgerSourceTypes.OperatorRefund, occurredAt: now),
            Ledger(CashMovementDirections.Expense, 40m, CashLedgerSourceTypes.ManualAdjustment, occurredAt: now),
            Ledger(CashMovementDirections.Expense, 25m, CashLedgerSourceTypes.ClientCreditWithdrawal, occurredAt: now));
        await context.SaveChangesAsync();

        var page = await BuildTreasuryNoCost(context).GetMovementsAsync(
            new TreasuryMovementsQuery { Direction = "all", SourceType = "all" }, CancellationToken.None);

        // Las tres salen como "ManualAdjustment" en el contrato del front; se distinguen por LedgerSourceType.
        var refundRow = page.Items.Single(m => m.LedgerSourceType == CashLedgerSourceTypes.OperatorRefund);
        var manualRow = page.Items.Single(m => m.LedgerSourceType == CashLedgerSourceTypes.ManualAdjustment);
        var clientRefundRow = page.Items.Single(m => m.LedgerSourceType == CashLedgerSourceTypes.ClientCreditWithdrawal);

        Assert.Equal(0m, refundRow.Amount);      // refund de operador = costo -> enmascarado
        Assert.Equal(40m, manualRow.Amount);      // gasto manual genuino -> visible
        Assert.Equal(25m, clientRefundRow.Amount); // devolucion fisica al cliente -> visible (no es costo)
    }

    [Fact]
    public async Task Movements_WithSeeCost_ShowsOperatorRefundAmounts()
    {
        await using var context = CreateContext();
        var now = DateTime.UtcNow;

        context.CashLedgerEntries.Add(
            Ledger(CashMovementDirections.Income, 99m, CashLedgerSourceTypes.OperatorRefund, occurredAt: now));
        await context.SaveChangesAsync();

        var page = await BuildTreasuryCanSeeCost(context).GetMovementsAsync(
            new TreasuryMovementsQuery { Direction = "all", SourceType = "all" }, CancellationToken.None);

        Assert.Equal(99m, page.Items.Single(m => m.LedgerSourceType == CashLedgerSourceTypes.OperatorRefund).Amount);
    }

    [Fact]
    public async Task CashSummary_WithoutSeeCost_MasksCashOut_KeepsCashIn()
    {
        // fix S2(b): la SALIDA de caja (pagos a proveedor + devoluciones de operador) es costo -> sin see_cost
        // se enmascara (escalar 0, por-moneda vacio/0), igual que AccountsPayable. La ENTRADA (cobros) queda.
        await using var context = CreateContext();
        var now = DateTime.UtcNow;

        context.CashLedgerEntries.AddRange(
            Ledger(CashMovementDirections.Income, 500m, CashLedgerSourceTypes.CustomerPayment, currency: "ARS", occurredAt: now),
            Ledger(CashMovementDirections.Expense, 120m, CashLedgerSourceTypes.SupplierPayment, currency: "ARS", occurredAt: now));
        await context.SaveChangesAsync();

        var hidden = await BuildTreasuryNoCost(context).GetCashSummaryAsync(cancellationToken: CancellationToken.None);
        var shown = await BuildTreasuryCanSeeCost(context).GetCashSummaryAsync(cancellationToken: CancellationToken.None);

        // Sin see_cost: la entrada se ve, la salida no; el neto se reporta = entrada (no se filtra por resta).
        Assert.Equal(500m, hidden.CashInThisMonth);
        Assert.Equal(0m, hidden.CashOutThisMonth);
        Assert.Equal(500m, hidden.NetCashThisMonth);
        Assert.Equal(0m, hidden.CashByCurrency.Single(x => x.Currency == "ARS").CashOutThisMonth);
        Assert.Equal(500m, hidden.CashByCurrency.Single(x => x.Currency == "ARS").CashInThisMonth);

        // Con see_cost: la salida se ve.
        Assert.Equal(120m, shown.CashOutThisMonth);
        Assert.Equal(380m, shown.NetCashThisMonth);
    }

    [Fact]
    public async Task Summary_WithoutSeeCost_MasksCashOutByCurrency_KeepsCashInByCurrency()
    {
        // fix S2(b): mismo enmascarado en el endpoint /summary (CashOutThisMonth + CashOutByCurrency).
        await using var context = CreateContext();
        var now = DateTime.UtcNow;

        context.CashLedgerEntries.AddRange(
            Ledger(CashMovementDirections.Income, 500m, CashLedgerSourceTypes.CustomerPayment, currency: "ARS", occurredAt: now),
            Ledger(CashMovementDirections.Expense, 120m, CashLedgerSourceTypes.SupplierPayment, currency: "ARS", occurredAt: now));
        await context.SaveChangesAsync();

        var hidden = await BuildTreasuryNoCost(context).GetSummaryAsync(CancellationToken.None);

        Assert.Equal(500m, hidden.CashInByCurrency.Single(x => x.Currency == "ARS").Amount); // entrada visible
        Assert.Empty(hidden.CashOutByCurrency);  // salida = costo -> lista vacia (fail-closed)
        Assert.Equal(0m, hidden.CashOutThisMonth);
    }

    [Fact]
    public async Task Summary_WithoutSeeCost_HidesAccountsPayable()
    {
        await using var context = CreateContext();
        context.SupplierBalanceByCurrency.Add(new SupplierBalanceByCurrency { SupplierId = 1, Currency = "ARS", Balance = 300m, ConfirmedPurchases = 300m });
        await context.SaveChangesAsync();

        var hidden = await BuildTreasuryNoCost(context).GetSummaryAsync(CancellationToken.None);
        var shown = await BuildTreasuryCanSeeCost(context).GetSummaryAsync(CancellationToken.None);

        Assert.Empty(hidden.AccountsPayableByCurrency);                       // AP es costo -> sin see_cost no se ve
        Assert.Equal(300m, shown.AccountsPayableByCurrency.Single().Amount);  // con see_cost si
    }

    // ====================================================================================
    // Capa 7 — fuente unica AR/AP (dashboard == tesoreria)
    // ====================================================================================

    [Fact]
    public async Task FinancePosition_DashboardAndTreasury_SameNumbers()
    {
        await using var context = CreateContext();

        // Reservas firmes con saldo por moneda + deuda a proveedor por moneda. ADR-036 (2026-06-21): Traveling
        // ya no es firme cobrable; usamos Closed (firme con deuda, ADR-033) para la segunda moneda.
        context.Reservas.AddRange(
            new Reserva { Id = 1, NumeroReserva = "F-1", Name = "R1", Status = EstadoReserva.Confirmed, Balance = 500m },
            new Reserva { Id = 2, NumeroReserva = "F-2", Name = "R2", Status = EstadoReserva.Closed, Balance = 300m });
        context.ReservaMoneyByCurrency.AddRange(
            new ReservaMoneyByCurrency { ReservaId = 1, Currency = "ARS", Balance = 500m, ConfirmedSale = 500m },
            new ReservaMoneyByCurrency { ReservaId = 2, Currency = "USD", Balance = 300m, ConfirmedSale = 300m });
        context.SupplierBalanceByCurrency.AddRange(
            new SupplierBalanceByCurrency { SupplierId = 1, Currency = "ARS", Balance = 200m, ConfirmedPurchases = 200m },
            new SupplierBalanceByCurrency { SupplierId = 1, Currency = "USD", Balance = 100m, ConfirmedPurchases = 100m });
        await context.SaveChangesAsync();

        // Misma fuente unica para los dos consumidores.
        var finance = new FinancePositionService(context);
        var ar = await finance.GetAccountsReceivableByCurrencyAsync(CancellationToken.None);
        var ap = await finance.GetAccountsPayableByCurrencyAsync(CancellationToken.None);

        var treasury = await BuildTreasuryCanSeeCost(context).GetSummaryAsync(CancellationToken.None);

        // AR de tesoreria == AR de la fuente unica.
        Assert.Equal(ar.Single(x => x.Currency == "ARS").Amount, treasury.AccountsReceivableByCurrency.Single(x => x.Currency == "ARS").Amount);
        Assert.Equal(ar.Single(x => x.Currency == "USD").Amount, treasury.AccountsReceivableByCurrency.Single(x => x.Currency == "USD").Amount);
        // AP de tesoreria == AP de la fuente unica.
        Assert.Equal(ap.Single(x => x.Currency == "ARS").Amount, treasury.AccountsPayableByCurrency.Single(x => x.Currency == "ARS").Amount);
        Assert.Equal(ap.Single(x => x.Currency == "USD").Amount, treasury.AccountsPayableByCurrency.Single(x => x.Currency == "USD").Amount);

        Assert.Equal(500m, ar.Single(x => x.Currency == "ARS").Amount);
        Assert.Equal(300m, ar.Single(x => x.Currency == "USD").Amount);
        Assert.Equal(200m, ap.Single(x => x.Currency == "ARS").Amount);
        Assert.Equal(100m, ap.Single(x => x.Currency == "USD").Amount);
    }

    [Fact]
    public async Task FinancePosition_AR_ExcludesNonActiveStatuses()
    {
        await using var context = CreateContext();

        // Confirmed cuenta; Quotation y Cancelled NO (no hay saldo exigible).
        context.Reservas.AddRange(
            new Reserva { Id = 1, NumeroReserva = "F-1", Name = "R1", Status = EstadoReserva.Confirmed, Balance = 500m },
            new Reserva { Id = 2, NumeroReserva = "F-2", Name = "R2", Status = EstadoReserva.Quotation, Balance = 999m },
            new Reserva { Id = 3, NumeroReserva = "F-3", Name = "R3", Status = EstadoReserva.Cancelled, Balance = 999m });
        context.ReservaMoneyByCurrency.AddRange(
            new ReservaMoneyByCurrency { ReservaId = 1, Currency = "ARS", Balance = 500m, ConfirmedSale = 500m },
            new ReservaMoneyByCurrency { ReservaId = 2, Currency = "ARS", Balance = 999m, ConfirmedSale = 999m },
            new ReservaMoneyByCurrency { ReservaId = 3, Currency = "ARS", Balance = 999m, ConfirmedSale = 999m });
        await context.SaveChangesAsync();

        var ar = await new FinancePositionService(context).GetAccountsReceivableByCurrencyAsync(CancellationToken.None);

        Assert.Equal(500m, ar.Single().Amount); // solo la Confirmed
    }

    // ====================================================================================
    // Capa 8 — cuenta corriente del cliente por moneda + bolsillo de saldo a favor
    // ====================================================================================

    [Fact]
    public async Task CustomerOverview_ReceivableByCurrency_DerivesFromChildTable()
    {
        await using var context = CreateContext();

        var customer = new Customer { Id = 1, FullName = "Ana Gomez" };
        context.Customers.Add(customer);
        // ADR-036 (2026-06-21): Traveling ya no es firme cobrable; usamos Closed (firme con deuda) para la
        // segunda moneda y conservar la cobertura multimoneda de la cuenta del cliente.
        context.Reservas.AddRange(
            new Reserva { Id = 1, NumeroReserva = "F-1", Name = "R1", PayerId = 1, Status = EstadoReserva.Confirmed, Balance = 500m },
            new Reserva { Id = 2, NumeroReserva = "F-2", Name = "R2", PayerId = 1, Status = EstadoReserva.Closed, Balance = 100m });
        context.ReservaMoneyByCurrency.AddRange(
            new ReservaMoneyByCurrency { ReservaId = 1, Currency = "ARS", Balance = 500m, ConfirmedSale = 500m },
            new ReservaMoneyByCurrency { ReservaId = 2, Currency = "USD", Balance = 100m, ConfirmedSale = 100m });
        AddApprovedInvoice(context, 1, 500m);
        AddApprovedInvoice(context, 2, 100m, "USD");
        await context.SaveChangesAsync();

        var overview = await new CustomerService(context, new FinancePositionService(context)).GetCustomerAccountOverviewAsync(1, CancellationToken.None);

        Assert.Equal(500m, overview.Summary.ReceivableByCurrency.Single(x => x.Currency == "ARS").Amount);
        Assert.Equal(100m, overview.Summary.ReceivableByCurrency.Single(x => x.Currency == "USD").Amount);
    }

    [Fact]
    public async Task CustomerOverview_CreditByCurrency_SumsCancellationAndOverpayment()
    {
        await using var context = CreateContext();

        var customer = new Customer { Id = 1, FullName = "Ana Gomez" };
        context.Customers.Add(customer);

        // Credito de CANCELACION (ARS 200) + credito de SOBREPAGO (ARS 50) + credito USD (40) -> el bolsillo
        // suma POR MONEDA cualquier origen. Un credito ya consumido (Remaining 0) NO cuenta.
        context.ClientCreditEntries.AddRange(
            new ClientCreditEntry { Id = 1, CustomerId = 1, Currency = "ARS", CreditedAmount = 200m, RemainingBalance = 200m, BookingCancellationId = 99 },
            new ClientCreditEntry { Id = 2, CustomerId = 1, Currency = "ARS", CreditedAmount = 50m, RemainingBalance = 50m, SourcePaymentId = 7 },
            new ClientCreditEntry { Id = 3, CustomerId = 1, Currency = "USD", CreditedAmount = 40m, RemainingBalance = 40m, SourcePaymentId = 8 },
            new ClientCreditEntry { Id = 4, CustomerId = 1, Currency = "ARS", CreditedAmount = 70m, RemainingBalance = 0m, BookingCancellationId = 100, IsFullyConsumed = true });
        await context.SaveChangesAsync();

        var overview = await new CustomerService(context, new FinancePositionService(context)).GetCustomerAccountOverviewAsync(1, CancellationToken.None);

        Assert.Equal(250m, overview.Summary.CreditBalanceByCurrency.Single(x => x.Currency == "ARS").Amount); // 200 + 50
        Assert.Equal(40m, overview.Summary.CreditBalanceByCurrency.Single(x => x.Currency == "USD").Amount);
    }

    [Fact]
    public async Task CustomerOverview_NoCredits_EmptyPocket_ScalarUnchanged()
    {
        await using var context = CreateContext();

        var customer = new Customer { Id = 1, FullName = "Ana Gomez" };
        context.Customers.Add(customer);
        context.Reservas.Add(new Reserva { Id = 1, NumeroReserva = "F-1", Name = "R1", PayerId = 1, Status = EstadoReserva.Confirmed, Balance = 300m });
        context.ReservaMoneyByCurrency.Add(new ReservaMoneyByCurrency { ReservaId = 1, Currency = "ARS", Balance = 300m, ConfirmedSale = 300m });
        AddApprovedInvoice(context, 1, 300m);
        await context.SaveChangesAsync();

        var overview = await new CustomerService(context, new FinancePositionService(context)).GetCustomerAccountOverviewAsync(1, CancellationToken.None);

        // Sin creditos: el bolsillo viene vacio y el escalar de compat (semaforo) no cambia.
        Assert.Empty(overview.Summary.CreditBalanceByCurrency);
        Assert.Equal(300m, overview.Customer.CurrentBalance);
        Assert.Equal(300m, overview.Summary.ReceivableByCurrency.Single().Amount);
    }
}
