using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Application.Mappings;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Reservations;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-023 TANDA T2: el historial de "Cobranza y Facturacion" (GetHistoryAsync) lee la PLATA del LIBRO DE
/// CAJA (CashLedgerEntry), la misma fuente que la pantalla de Caja, y las facturas/NC de Invoices.
///
/// <para>Cubre los invariantes INV-T2-1..7 y el fix de RestorePaymentAsync (INV-T2-5). Para el masking se usa
/// el mismo harness que Adr022Tanda2Tests: un caller con cobranzas.see_cost ve los montos; sin el permiso,
/// CostMasking es fail-closed y oculta los egresos de costo.</para>
///
/// <para><b>Nota InMemory</b>: el provider InMemory no aplica CHECK ni el indice unico parcial del libro. Estos
/// tests verifican el COMPORTAMIENTO de la proyeccion (que filas salen, con que signo/moneda/estado y que se
/// enmascara), sembrando asientos directamente o pasando por el flujo real de cobro/anulacion/restore.</para>
/// </summary>
public class Adr023HistoryFromLedgerTests
{
    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    private static readonly IMapper Mapper =
        new MapperConfiguration(c => c.AddProfile<MappingProfile>()).CreateMapper();

    private static IOperationalFinanceSettingsService Settings()
    {
        var mock = new Mock<IOperationalFinanceSettingsService>();
        mock.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings());
        return mock.Object;
    }

    private static IHttpContextAccessor HttpContextFor(string userId, bool isAdmin = false)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };
        var identity = new ClaimsIdentity(claims, "Test", ClaimTypes.Name, ClaimTypes.Role);
        if (isAdmin) identity.AddClaim(new Claim(ClaimTypes.Role, "Admin"));
        return new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
    }

    private static IUserPermissionResolver Resolver(string userId, params string[] permissions)
    {
        var mock = new Mock<IUserPermissionResolver>();
        IReadOnlySet<string> set = new HashSet<string>(permissions);
        mock.Setup(r => r.GetPermissionsAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(set);
        return mock.Object;
    }

    /// <summary>
    /// PaymentService con un caller que SI ve costos (cobranzas.see_cost + view_all). Sin owner-scope:
    /// ve todo el libro. Es el harness por defecto (la mayoria de los tests no prueban masking ni scope).
    /// </summary>
    private static PaymentService BuildServiceSeeAll(AppDbContext context, string userId = "tester")
    {
        var accessor = HttpContextFor(userId);
        var resolver = Resolver(userId, Permissions.CobranzasSeeCost, Permissions.CobranzasViewAll);
        return new PaymentService(
            context,
            new EntityReferenceResolver(context),
            Mapper,
            Settings(),
            NullLogger<PaymentService>.Instance,
            resolver,
            accessor);
    }

    /// <summary>PaymentService con un caller que NO tiene see_cost (pero si view_all): los egresos de costo se enmascaran.</summary>
    private static PaymentService BuildServiceNoSeeCost(AppDbContext context, string userId = "tester")
    {
        var accessor = HttpContextFor(userId);
        var resolver = Resolver(userId, Permissions.CobranzasViewAll); // view_all pero NO see_cost
        return new PaymentService(
            context,
            new EntityReferenceResolver(context),
            Mapper,
            Settings(),
            NullLogger<PaymentService>.Instance,
            resolver,
            accessor);
    }

    /// <summary>PaymentService con un caller OWNER-SCOPE (ni Admin ni view_all): solo ve la plata de sus reservas.</summary>
    private static PaymentService BuildServiceOwnerScope(AppDbContext context, string userId)
    {
        var accessor = HttpContextFor(userId);
        var resolver = Resolver(userId, Permissions.CobranzasSeeCost); // ve costos pero NO view_all -> filtra mine
        return new PaymentService(
            context,
            new EntityReferenceResolver(context),
            Mapper,
            Settings(),
            NullLogger<PaymentService>.Instance,
            resolver,
            accessor);
    }

    private static Reserva NewReserva(int id, string? responsibleUserId = null) => new()
    {
        Id = id,
        NumeroReserva = $"F-2026-{id:D4}",
        Name = $"Reserva {id}",
        Status = EstadoReserva.Confirmed,
        ResponsibleUserId = responsibleUserId,
    };

    private static FinanceHistoryQuery AllQuery() => new() { Page = 1, PageSize = 100 };

    // ====================================================================================
    // T2.2 — cobro con moneda real
    // ====================================================================================

    [Fact]
    public async Task History_CustomerPayment_ShowsRealCurrency_ArsAndPositiveSign()
    {
        await using var context = CreateContext();
        var reserva = NewReserva(1);
        context.Reservas.Add(reserva);
        var payment = new Payment { Id = 10, ReservaId = 1, Amount = 500m, Currency = Monedas.ARS, Method = "Transfer", PaidAt = DateTime.UtcNow };
        context.Payments.Add(payment);
        context.CashLedgerEntries.Add(new CashLedgerEntry
        {
            Direction = CashMovementDirections.Income, Amount = 500m, Currency = Monedas.ARS, Method = "Transfer",
            OccurredAt = DateTime.UtcNow, SourceType = CashLedgerSourceTypes.CustomerPayment, PaymentId = 10, ReservaId = 1,
        });
        await context.SaveChangesAsync();

        var page = await BuildServiceSeeAll(context).GetHistoryAsync(AllQuery(), CancellationToken.None);

        var row = Assert.Single(page.Items.Where(i => i.EntityType == "payment"));
        Assert.Equal(500m, row.Amount); // ingreso positivo
        Assert.Equal(Monedas.ARS, row.Currency);
        Assert.Equal("Cobranza", row.Kind);
        Assert.False(row.IsReversal);
        Assert.Equal(payment.PublicId, row.PublicId); // PublicId del origen, no del asiento
    }

    [Fact]
    public async Task History_CustomerPaymentInUsd_ReportedAsUsd_NotArs()
    {
        // INV-T2-6: un cobro en USD nunca se reporta como ARS.
        await using var context = CreateContext();
        context.Reservas.Add(NewReserva(1));
        context.Payments.Add(new Payment { Id = 11, ReservaId = 1, Amount = 200m, Currency = Monedas.USD, Method = "Cash", PaidAt = DateTime.UtcNow });
        context.CashLedgerEntries.Add(new CashLedgerEntry
        {
            Direction = CashMovementDirections.Income, Amount = 200m, Currency = Monedas.USD, Method = "Cash",
            OccurredAt = DateTime.UtcNow, SourceType = CashLedgerSourceTypes.CustomerPayment, PaymentId = 11, ReservaId = 1,
        });
        await context.SaveChangesAsync();

        var page = await BuildServiceSeeAll(context).GetHistoryAsync(AllQuery(), CancellationToken.None);

        var row = Assert.Single(page.Items.Where(i => i.EntityType == "payment"));
        Assert.Equal(Monedas.USD, row.Currency);
        Assert.Equal(200m, row.Amount);
    }

    // ====================================================================================
    // INV-T2-2 — un cobro anulado: dos filas neteando 0, con la anulacion VISIBLE
    // ====================================================================================

    [Fact]
    public async Task History_ReversedPayment_ShowsBothRows_NetZero_WithReversalVisible()
    {
        await using var context = CreateContext();
        var now = DateTime.UtcNow;
        context.Reservas.Add(NewReserva(1));
        context.Payments.Add(new Payment { Id = 12, ReservaId = 1, Amount = 300m, Currency = Monedas.ARS, Method = "Transfer", PaidAt = now });
        // Original revertido + su reversa (Direction invertida). El libro conserva las dos filas.
        context.CashLedgerEntries.AddRange(
            new CashLedgerEntry
            {
                Direction = CashMovementDirections.Income, Amount = 300m, Currency = Monedas.ARS, Method = "Transfer",
                OccurredAt = now, SourceType = CashLedgerSourceTypes.CustomerPayment, PaymentId = 12, ReservaId = 1, IsReversed = true,
            },
            new CashLedgerEntry
            {
                Direction = CashMovementDirections.Expense, Amount = 300m, Currency = Monedas.ARS, Method = "Transfer",
                OccurredAt = now, SourceType = CashLedgerSourceTypes.CustomerPayment, PaymentId = 12, ReservaId = 1, IsReversal = true,
            });
        await context.SaveChangesAsync();

        var page = await BuildServiceSeeAll(context).GetHistoryAsync(AllQuery(), CancellationToken.None);

        var moneyRows = page.Items.Where(i => i.EntityType == "payment").ToList();
        Assert.Equal(2, moneyRows.Count);
        Assert.Equal(0m, moneyRows.Sum(r => r.Amount)); // +300 y -300 netean 0
        Assert.Contains(moneyRows, r => r.IsReversal && r.Kind == "Anulacion"); // la anulacion se VE
        Assert.Contains(moneyRows, r => !r.IsReversal); // el cobro original tambien sigue
    }

    // ====================================================================================
    // INV-T2-3 — el puente de sobrepago NO aparece (no tiene asiento)
    // ====================================================================================

    [Fact]
    public async Task History_OverpaymentBridge_DoesNotAppear()
    {
        // El puente (AffectsCash=false) nunca genero asiento de caja. Al leer del libro, no aparece por
        // construccion. Sembramos el Payment puente SIN asiento de libro; no debe surgir ninguna fila.
        await using var context = CreateContext();
        context.Reservas.Add(NewReserva(1));
        context.Payments.Add(new Payment
        {
            Id = 13, ReservaId = 1, Amount = -50m, Currency = Monedas.ARS,
            Method = OverpaymentCreditCleanup.BridgeMethod, AffectsCash = false, OriginalPaymentId = 99, PaidAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync(); // a proposito: NINGUN CashLedgerEntry

        var page = await BuildServiceSeeAll(context).GetHistoryAsync(AllQuery(), CancellationToken.None);

        Assert.Empty(page.Items); // ni el puente ni nada
    }

    // ====================================================================================
    // T2.2 — pagos a proveedor visibles (para view_all)
    // ====================================================================================

    [Fact]
    public async Task History_SupplierPayment_AppearsForViewAll()
    {
        await using var context = CreateContext();
        var reserva = NewReserva(1);
        var supplier = new Supplier { Id = 5, Name = "Operador X", CurrentBalance = 0m };
        context.Reservas.Add(reserva);
        context.Suppliers.Add(supplier);
        var supplierPayment = new SupplierPayment { Id = 20, SupplierId = 5, ReservaId = 1, Amount = 400m, Currency = Monedas.ARS, Method = "Transfer", PaidAt = DateTime.UtcNow };
        context.SupplierPayments.Add(supplierPayment);
        context.CashLedgerEntries.Add(new CashLedgerEntry
        {
            Direction = CashMovementDirections.Expense, Amount = 400m, Currency = Monedas.ARS, Method = "Transfer",
            OccurredAt = DateTime.UtcNow, SourceType = CashLedgerSourceTypes.SupplierPayment, SupplierPaymentId = 20, ReservaId = 1, SupplierId = 5,
        });
        await context.SaveChangesAsync();

        var page = await BuildServiceSeeAll(context).GetHistoryAsync(AllQuery(), CancellationToken.None);

        var row = Assert.Single(page.Items.Where(i => i.LedgerSourceType == CashLedgerSourceTypes.SupplierPayment));
        Assert.Equal(-400m, row.Amount); // egreso negativo
        Assert.Equal("Pago a proveedor", row.Kind);
        Assert.False(row.AmountMasked); // con see_cost no se enmascara
        Assert.Equal(supplierPayment.PublicId, row.PublicId);
    }

    // ====================================================================================
    // INV-T2-4 — masking see_cost sobre LedgerSourceType CRUDO (SupplierPayment Y OperatorRefund)
    // ====================================================================================

    [Fact]
    public async Task History_WithoutSeeCost_MasksSupplierPaymentAndOperatorRefund_NotCustomerPayment()
    {
        await using var context = CreateContext();
        context.Reservas.Add(NewReserva(1));
        context.Suppliers.Add(new Supplier { Id = 5, Name = "Operador X", CurrentBalance = 0m });
        context.Payments.Add(new Payment { Id = 30, ReservaId = 1, Amount = 100m, Currency = Monedas.ARS, Method = "Cash", PaidAt = DateTime.UtcNow });
        context.SupplierPayments.Add(new SupplierPayment { Id = 31, SupplierId = 5, ReservaId = 1, Amount = 200m, Currency = Monedas.ARS, Method = "Transfer", PaidAt = DateTime.UtcNow });
        // OperatorRefund: nace de un ManualCashMovement; el SourceType del asiento es OperatorRefund.
        context.ManualCashMovements.Add(new ManualCashMovement { Id = 32, Direction = CashMovementDirections.Income, Amount = 150m, Method = "Transfer", OccurredAt = DateTime.UtcNow, Description = "Refund operador", Category = "Refund", RelatedReservaId = 1 });
        context.CashLedgerEntries.AddRange(
            new CashLedgerEntry { Direction = CashMovementDirections.Income, Amount = 100m, Currency = Monedas.ARS, Method = "Cash", OccurredAt = DateTime.UtcNow, SourceType = CashLedgerSourceTypes.CustomerPayment, PaymentId = 30, ReservaId = 1 },
            new CashLedgerEntry { Direction = CashMovementDirections.Expense, Amount = 200m, Currency = Monedas.ARS, Method = "Transfer", OccurredAt = DateTime.UtcNow, SourceType = CashLedgerSourceTypes.SupplierPayment, SupplierPaymentId = 31, ReservaId = 1, SupplierId = 5 },
            new CashLedgerEntry { Direction = CashMovementDirections.Income, Amount = 150m, Currency = Monedas.ARS, Method = "Transfer", OccurredAt = DateTime.UtcNow, SourceType = CashLedgerSourceTypes.OperatorRefund, ManualCashMovementId = 32, ReservaId = 1 });
        await context.SaveChangesAsync();

        var page = await BuildServiceNoSeeCost(context).GetHistoryAsync(AllQuery(), CancellationToken.None);

        var supplier = Assert.Single(page.Items.Where(i => i.LedgerSourceType == CashLedgerSourceTypes.SupplierPayment));
        Assert.True(supplier.AmountMasked);
        Assert.Equal(0m, supplier.Amount);

        var refund = Assert.Single(page.Items.Where(i => i.LedgerSourceType == CashLedgerSourceTypes.OperatorRefund));
        Assert.True(refund.AmountMasked); // decidido sobre el LedgerSourceType crudo, aunque MovementSourceType viaje colapsado
        Assert.Equal(0m, refund.Amount);
        Assert.Equal("ManualAdjustment", refund.MovementSourceType); // colapsado para el front, pero el crudo permitio enmascarar

        var customer = Assert.Single(page.Items.Where(i => i.LedgerSourceType == CashLedgerSourceTypes.CustomerPayment));
        Assert.False(customer.AmountMasked); // el cobro de cliente NO se enmascara
        Assert.Equal(100m, customer.Amount);
    }

    // ====================================================================================
    // T2.3 — facturas desde Invoices: rechazada visible, NC visible
    // ====================================================================================

    [Fact]
    public async Task History_RejectedInvoice_AppearsWithRejectedState()
    {
        await using var context = CreateContext();
        context.Reservas.Add(NewReserva(1));
        context.Invoices.Add(new Invoice
        {
            Id = 40, ReservaId = 1, TipoComprobante = 6, NumeroComprobante = 1001,
            ImporteTotal = 1000m, Resultado = "R", CreatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        var page = await BuildServiceSeeAll(context).GetHistoryAsync(AllQuery(), CancellationToken.None);

        var row = Assert.Single(page.Items.Where(i => i.EntityType == "invoice"));
        Assert.Equal("Comprobante rechazado", row.Kind); // nunca pasa por aprobada
        Assert.Contains("Rechazada por ARCA", row.Title);
        Assert.Equal("R", row.InvoiceResultado);
    }

    [Fact]
    public async Task History_CreditNote_AppearsFromInvoices()
    {
        await using var context = CreateContext();
        context.Reservas.Add(NewReserva(1));
        context.Invoices.Add(new Invoice
        {
            Id = 41, ReservaId = 1, TipoComprobante = 8, NumeroComprobante = 2001,
            ImporteTotal = 500m, Resultado = "A", CreatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        var page = await BuildServiceSeeAll(context).GetHistoryAsync(AllQuery(), CancellationToken.None);

        var row = Assert.Single(page.Items.Where(i => i.EntityType == "invoice"));
        Assert.Equal("Nota de credito", row.Kind);
        Assert.Equal("Nota de Credito B", row.Title);
    }

    [Fact]
    public async Task History_AnnulledInvoice_AppearsMarkedAnnulled()
    {
        await using var context = CreateContext();
        context.Reservas.Add(NewReserva(1));
        context.Invoices.Add(new Invoice
        {
            Id = 42, ReservaId = 1, TipoComprobante = 6, NumeroComprobante = 3001,
            ImporteTotal = 800m, Resultado = "A", AnnulmentStatus = AnnulmentStatus.Succeeded, CreatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        var page = await BuildServiceSeeAll(context).GetHistoryAsync(AllQuery(), CancellationToken.None);

        var row = Assert.Single(page.Items.Where(i => i.EntityType == "invoice"));
        Assert.Equal("Comprobante anulado", row.Kind);
        Assert.Contains("Anulada", row.Title);
    }

    // ====================================================================================
    // T2.3 — el Payment tecnico CreditNoteReversal YA NO aparece como fila "Reversion"
    // ====================================================================================

    [Fact]
    public async Task History_CreditNoteReversalPayment_DoesNotAppearAsReversionRow()
    {
        // El Payment con EntryType=CreditNoteReversal (AffectsCash=false) no tiene asiento de libro.
        // La NC que representa ya aparece por Invoices, asi que NO debe haber fila "Reversion".
        await using var context = CreateContext();
        context.Reservas.Add(NewReserva(1));
        context.Payments.Add(new Payment
        {
            Id = 50, ReservaId = 1, Amount = 700m, Currency = Monedas.ARS,
            EntryType = PaymentEntryTypes.CreditNoteReversal, AffectsCash = false, PaidAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync(); // sin asiento de libro

        var page = await BuildServiceSeeAll(context).GetHistoryAsync(AllQuery(), CancellationToken.None);

        Assert.Empty(page.Items); // ninguna fila "Reversion"
        Assert.DoesNotContain(page.Items, i => i.Kind == "Reversion");
    }

    // ====================================================================================
    // INV-T2-7 — owner-scope vs view_all
    // ====================================================================================

    [Fact]
    public async Task History_OwnerScope_IsSubsetOfViewAll()
    {
        await using var context = CreateContext();
        var now = DateTime.UtcNow;
        // Reserva propia del vendedor + reserva ajena + asiento manual sin reserva.
        var mine = NewReserva(1, responsibleUserId: "vendedor");
        var others = NewReserva(2, responsibleUserId: "otro");
        context.Reservas.AddRange(mine, others);
        context.Payments.AddRange(
            new Payment { Id = 60, ReservaId = 1, Amount = 100m, Currency = Monedas.ARS, Method = "Cash", PaidAt = now },
            new Payment { Id = 61, ReservaId = 2, Amount = 200m, Currency = Monedas.ARS, Method = "Cash", PaidAt = now });
        context.ManualCashMovements.Add(new ManualCashMovement { Id = 62, Direction = CashMovementDirections.Expense, Amount = 50m, Method = "Cash", OccurredAt = now, Description = "Ajuste sin reserva", Category = "Gastos" });
        context.CashLedgerEntries.AddRange(
            new CashLedgerEntry { Direction = CashMovementDirections.Income, Amount = 100m, Currency = Monedas.ARS, Method = "Cash", OccurredAt = now, SourceType = CashLedgerSourceTypes.CustomerPayment, PaymentId = 60, ReservaId = 1 },
            new CashLedgerEntry { Direction = CashMovementDirections.Income, Amount = 200m, Currency = Monedas.ARS, Method = "Cash", OccurredAt = now, SourceType = CashLedgerSourceTypes.CustomerPayment, PaymentId = 61, ReservaId = 2 },
            new CashLedgerEntry { Direction = CashMovementDirections.Expense, Amount = 50m, Currency = Monedas.ARS, Method = "Cash", OccurredAt = now, SourceType = CashLedgerSourceTypes.ManualAdjustment, ManualCashMovementId = 62 });
        await context.SaveChangesAsync();

        var viewAll = await BuildServiceSeeAll(context).GetHistoryAsync(AllQuery(), CancellationToken.None);
        var owner = await BuildServiceOwnerScope(context, "vendedor").GetHistoryAsync(AllQuery(), CancellationToken.None);

        // view_all ve las 3 filas de plata (2 cobros + 1 ajuste sin reserva).
        Assert.Equal(3, viewAll.Items.Count(i => i.EntityType != "invoice"));
        // owner-scope ve SOLO su reserva (1 fila). El ajuste sin reserva y la reserva ajena quedan fuera.
        var ownerMoney = owner.Items.Where(i => i.EntityType != "invoice").ToList();
        Assert.Single(ownerMoney);
        Assert.Equal(mine.PublicId, ownerMoney[0].ReservaPublicId);
        // subconjunto: cada PublicId del owner esta en view_all.
        var viewAllIds = viewAll.Items.Select(i => i.PublicId).ToHashSet();
        Assert.All(ownerMoney, r => Assert.Contains(r.PublicId, viewAllIds));
    }

    // ====================================================================================
    // INV-T2-5 — RestorePaymentAsync re-asienta un asiento vivo, neto +Amount, idempotente
    // ====================================================================================

    [Fact]
    public async Task RestorePayment_ReassentsLiveLedgerEntry_NetIsPositiveAmount_AndIsIdempotent()
    {
        await using var context = CreateContext();
        var reserva = new Reserva
        {
            Id = 1, NumeroReserva = "F-2026-0001", Name = "Reserva", Status = EstadoReserva.Confirmed,
            TotalSale = 1000m, TotalCost = 0m, Balance = 1000m, TotalPaid = 0m,
        };
        context.Reservas.Add(reserva);
        context.Servicios.Add(new ServicioReserva
        {
            Id = 1, ReservaId = 1, ServiceType = "Hotel", ProductType = "Hotel", Description = "Sustento",
            ConfirmationNumber = "OK", Status = "Confirmado", DepartureDate = DateTime.UtcNow.AddDays(15),
            SalePrice = 1000m, NetCost = 0m, Commission = 1000m, CreatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        var service = BuildServiceSeeAll(context);

        // 1) Cobro real (crea asiento vivo).
        var dto = await service.CreatePaymentAsync(new CreatePaymentRequest
        {
            ReservaId = reserva.PublicId.ToString(), Amount = 300m, Method = "Transfer", Reference = "TX-1",
        }, CancellationToken.None);
        var paymentId = await context.Payments.Where(p => p.PublicId == dto.PublicId).Select(p => p.Id).SingleAsync();

        // 2) Anular el cobro (soft-delete + reversa en el libro): neto 0.
        await service.DeletePaymentAsync(dto.PublicId.ToString(), CancellationToken.None);

        // 3) Restaurar: debe re-asentar un asiento vivo nuevo -> neto +300. Se usa el overload por id interno
        //    (el que usa el controller para deshacer una eliminacion): el publicId no resuelve un pago
        //    soft-deleted porque el query filter lo excluye, mientras que RestorePaymentAsync(int) usa
        //    IgnoreQueryFilters. Es el camino real de "deshacer".
        await service.RestorePaymentAsync(paymentId, CancellationToken.None);

        var entries = await context.CashLedgerEntries.Where(e => e.PaymentId == paymentId).ToListAsync();
        // Exactamente UN asiento vivo (no reversa, no revertido).
        var live = entries.Where(e => !e.IsReversal && !e.IsReversed).ToList();
        Assert.Single(live);
        // Neto del libro para ese pago = +Amount.
        var net = entries.Sum(e => e.Direction == CashMovementDirections.Expense ? -e.Amount : e.Amount);
        Assert.Equal(300m, net);

        // 4) Idempotencia del re-asentado: si por algun motivo restore se ejecuta cuando YA existe un asiento
        //    vivo para el pago (p.ej. un doble-disparo), el guard hasLive impide crear un segundo asiento. Lo
        //    simulamos volviendo a marcar el pago como eliminado SIN tocar el libro (el asiento vivo sigue),
        //    y restaurando otra vez: la cuenta de asientos vivos debe seguir en 1, no 2.
        var restored = await context.Payments.IgnoreQueryFilters().SingleAsync(p => p.Id == paymentId);
        Assert.False(restored.IsDeleted);
        restored.IsDeleted = true;
        await context.SaveChangesAsync();
        await service.RestorePaymentAsync(paymentId, CancellationToken.None);
        var liveAfter = await context.CashLedgerEntries.CountAsync(e => e.PaymentId == paymentId && !e.IsReversal && !e.IsReversed);
        Assert.Equal(1, liveAfter); // el guard no duplico el asiento
    }

    [Fact]
    public async Task RestorePayment_AppearsInHistory_AsTwoOriginalRowsAndOneReversal_NetPositive()
    {
        // El historial completo del ciclo cobro -> anular -> restaurar: 3 filas de plata (cobro original,
        // su anulacion, y el re-cobro) que netean +Amount. La anulacion sigue visible (INV-T2-2/T2-5).
        await using var context = CreateContext();
        var reserva = new Reserva
        {
            Id = 1, NumeroReserva = "F-2026-0001", Name = "Reserva", Status = EstadoReserva.Confirmed,
            TotalSale = 1000m, TotalCost = 0m, Balance = 1000m, TotalPaid = 0m,
        };
        context.Reservas.Add(reserva);
        context.Servicios.Add(new ServicioReserva
        {
            Id = 1, ReservaId = 1, ServiceType = "Hotel", ProductType = "Hotel", Description = "Sustento",
            ConfirmationNumber = "OK", Status = "Confirmado", DepartureDate = DateTime.UtcNow.AddDays(15),
            SalePrice = 1000m, NetCost = 0m, Commission = 1000m, CreatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        var service = BuildServiceSeeAll(context);
        var dto = await service.CreatePaymentAsync(new CreatePaymentRequest
        {
            ReservaId = reserva.PublicId.ToString(), Amount = 250m, Method = "Transfer",
        }, CancellationToken.None);
        var paymentId = await context.Payments.Where(p => p.PublicId == dto.PublicId).Select(p => p.Id).SingleAsync();
        await service.DeletePaymentAsync(dto.PublicId.ToString(), CancellationToken.None);
        await service.RestorePaymentAsync(paymentId, CancellationToken.None); // overload por id interno (camino real de "deshacer")

        var page = await service.GetHistoryAsync(AllQuery(), CancellationToken.None);
        var moneyRows = page.Items.Where(i => i.EntityType == "payment").ToList();
        Assert.Equal(3, moneyRows.Count); // cobro + anulacion + re-cobro
        Assert.Single(moneyRows.Where(r => r.IsReversal));
        Assert.Equal(250m, moneyRows.Sum(r => r.Amount)); // neto +250
    }
}
