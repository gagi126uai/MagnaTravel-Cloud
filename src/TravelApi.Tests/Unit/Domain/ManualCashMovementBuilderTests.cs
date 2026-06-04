using TravelApi.Domain.Entities;
using TravelApi.Domain.Helpers;
using Xunit;

namespace TravelApi.Tests.Unit.Domain;

/// <summary>
/// FC1.2.0 v3 (MR-V2-01, 2026-05-17): tests del helper estatico
/// <see cref="ManualCashMovementBuilder"/>. No requiere fixture ni BD: el
/// builder construye POCOs y se evalua en memoria.
/// </summary>
public class ManualCashMovementBuilderTests
{
    private static OperatorRefundReceived BuildValidRefund()
    {
        return new OperatorRefundReceived
        {
            Id = 1,
            PublicId = Guid.NewGuid(),
            SupplierId = 10,
            Supplier = new Supplier { Id = 10, Name = "Operador Test SA" },
            ReceivedAt = new DateTime(2026, 1, 15, 10, 30, 0, DateTimeKind.Utc),
            ReceivedAmount = 50_000m,
            Method = "Transfer",
            Reference = "TX-12345",
            Currency = "ARS",
            ExchangeRateAtReceipt = 1m,
            ReceivedByUserId = "user-cashier",
            ReceivedByUserName = "Cashier Test",
        };
    }

    private static ClientCreditEntry BuildValidEntry()
    {
        return new ClientCreditEntry
        {
            Id = 100,
            PublicId = Guid.NewGuid(),
            CustomerId = 5,
            OperatorRefundAllocationId = 200,
            BookingCancellationId = 300,
            BookingCancellation = new BookingCancellation
            {
                Id = 300,
                ReservaId = 999,
                CustomerId = 5,
                SupplierId = 10,
                OriginatingInvoiceId = 1,
                DraftedByUserId = "user-vendor",
            },
            CreditedAmount = 20_000m,
            RemainingBalance = 20_000m,
        };
    }

    // ============== BuildIncomeForRefund ==============

    [Fact]
    public void BuildIncomeForRefund_con_datos_validos_arma_movimiento_income()
    {
        var refund = BuildValidRefund();

        var movement = ManualCashMovementBuilder.BuildIncomeForRefund(refund, "user-cashier");

        Assert.Equal(CashMovementDirections.Income, movement.Direction);
        Assert.Equal(50_000m, movement.Amount);
        Assert.Equal(refund.ReceivedAt, movement.OccurredAt);
        Assert.Equal("Transfer", movement.Method);
        Assert.Equal("OperatorRefund", movement.Category);
        Assert.Contains("Operador Test SA", movement.Description);
        Assert.Contains(refund.PublicId.ToString(), movement.Description);
        Assert.Equal("TX-12345", movement.Reference);
        Assert.Equal("user-cashier", movement.CreatedBy);
        Assert.Equal(refund.SupplierId, movement.RelatedSupplierId);

        // MR-V2-05: N:M, trazabilidad por OperatorRefundReceived (navigation property).
        // Despues del fix del bug FC1.2.2 (2026-05-18, FK violation con Id=0), el builder
        // setea la navigation property en vez del FK escalar. EF resuelve el FK al persistir
        // (SaveChanges en orden topologico). En este test Unit, sin BD, el FK escalar
        // OperatorRefundReceivedId queda null, pero la navigation apunta al refund correcto.
        Assert.Null(movement.RelatedReservaId);
        Assert.Same(refund, movement.OperatorRefundReceived);
        Assert.Null(movement.OperatorRefundReceivedId);
        Assert.Null(movement.ClientCreditWithdrawalId);
    }

    [Fact]
    public void BuildIncomeForRefund_redondea_el_monto_a_dos_decimales()
    {
        var refund = BuildValidRefund();
        refund.ReceivedAmount = 100.005m;

        var movement = ManualCashMovementBuilder.BuildIncomeForRefund(refund, "user-cashier");

        // AwayFromZero: 0.005 -> 0.01.
        Assert.Equal(100.01m, movement.Amount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-50)]
    [InlineData(-0.01)]
    public void BuildIncomeForRefund_con_amount_invalido_lanza_invalid_operation(decimal amount)
    {
        var refund = BuildValidRefund();
        refund.ReceivedAmount = amount;

        Assert.Throws<InvalidOperationException>(
            () => ManualCashMovementBuilder.BuildIncomeForRefund(refund, "user-cashier"));
    }

    [Fact]
    public void BuildIncomeForRefund_con_supplier_null_lanza_invalid_operation()
    {
        var refund = BuildValidRefund();
        refund.Supplier = null!;

        var ex = Assert.Throws<InvalidOperationException>(
            () => ManualCashMovementBuilder.BuildIncomeForRefund(refund, "user-cashier"));
        Assert.Contains("Supplier", ex.Message);
        Assert.Contains("Include", ex.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildIncomeForRefund_con_method_vacio_lanza_invalid_operation(string? method)
    {
        var refund = BuildValidRefund();
        refund.Method = method!;

        Assert.Throws<InvalidOperationException>(
            () => ManualCashMovementBuilder.BuildIncomeForRefund(refund, "user-cashier"));
    }

    [Fact]
    public void BuildIncomeForRefund_con_refund_null_lanza_argument_null()
    {
        Assert.Throws<ArgumentNullException>(
            () => ManualCashMovementBuilder.BuildIncomeForRefund(null!, "user-cashier"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildIncomeForRefund_con_user_vacio_lanza_argument(string? user)
    {
        var refund = BuildValidRefund();

        Assert.Throws<ArgumentException>(
            () => ManualCashMovementBuilder.BuildIncomeForRefund(refund, user!));
    }

    // ============== BuildExpenseForWithdrawal ==============

    [Fact]
    public void BuildExpenseForWithdrawal_con_physical_cash_arma_expense()
    {
        var entry = BuildValidEntry();
        var withdrawal = new ClientCreditWithdrawal
        {
            Id = 50,
            PublicId = Guid.NewGuid(),
            ClientCreditEntryId = entry.Id,
            Amount = 10_000m,
            Kind = WithdrawalKind.PhysicalCash,
            ExecutedByUserId = "user-cashier",
            ExecutedByUserName = "Cashier",
            ExecutedAt = new DateTime(2026, 2, 1, 11, 0, 0, DateTimeKind.Utc),
        };

        var movement = ManualCashMovementBuilder.BuildExpenseForWithdrawal(withdrawal, entry, "user-cashier");

        Assert.Equal(CashMovementDirections.Expense, movement.Direction);
        Assert.Equal(10_000m, movement.Amount);
        Assert.Equal(withdrawal.ExecutedAt, movement.OccurredAt);
        Assert.Equal("Cash", movement.Method);
        Assert.Equal("ClientCreditWithdrawal", movement.Category);
        Assert.Contains(entry.PublicId.ToString(), movement.Description);
        Assert.Contains("PhysicalCash", movement.Description);
        Assert.Equal("user-cashier", movement.CreatedBy);
        Assert.Equal(entry.BookingCancellation!.ReservaId, movement.RelatedReservaId);
        // Bug fix FC1.2.3 (27506d9, 2026-05-18): el builder setea la NAVIGATION
        // (ClientCreditWithdrawal) y deja el FK escalar null, porque en T3 el
        // withdrawal todavia no tiene Id (== 0 hasta SaveChanges). EF resuelve la FK
        // al persistir en orden topologico. En este Unit sin BD el escalar queda null.
        Assert.Same(withdrawal, movement.ClientCreditWithdrawal);
        Assert.Null(movement.ClientCreditWithdrawalId);
        Assert.Null(movement.OperatorRefundReceivedId);
        Assert.Null(movement.RelatedSupplierId);
    }

    [Fact]
    public void BuildExpenseForWithdrawal_con_transfer_arma_expense_transfer()
    {
        var entry = BuildValidEntry();
        var withdrawal = new ClientCreditWithdrawal
        {
            Id = 51,
            PublicId = Guid.NewGuid(),
            ClientCreditEntryId = entry.Id,
            Amount = 5_000m,
            Kind = WithdrawalKind.Transfer,
            ExecutedByUserId = "user-cashier",
            ExecutedByUserName = "Cashier",
        };

        var movement = ManualCashMovementBuilder.BuildExpenseForWithdrawal(withdrawal, entry, "user-cashier");

        Assert.Equal(CashMovementDirections.Expense, movement.Direction);
        Assert.Equal("Transfer", movement.Method);
        Assert.Equal("ClientCreditWithdrawal", movement.Category);
    }

    [Fact]
    public void BuildExpenseForWithdrawal_con_reversed_to_operator_arma_income()
    {
        // ReversedToOperator es caso especial: el cliente devuelve dinero
        // ya recibido. Vuelve a caja como Income, no como Expense.
        var entry = BuildValidEntry();
        var withdrawal = new ClientCreditWithdrawal
        {
            Id = 52,
            PublicId = Guid.NewGuid(),
            ClientCreditEntryId = entry.Id,
            Amount = 5_000m,
            Kind = WithdrawalKind.ReversedToOperator,
            ExecutedByUserId = "user-cashier",
            ExecutedByUserName = "Cashier",
        };

        var movement = ManualCashMovementBuilder.BuildExpenseForWithdrawal(withdrawal, entry, "user-cashier");

        Assert.Equal(CashMovementDirections.Income, movement.Direction);
        Assert.Equal("ClientCreditReversal", movement.Category);
    }

    [Fact]
    public void BuildExpenseForWithdrawal_con_kept_as_credit_lanza_invalid_operation()
    {
        var entry = BuildValidEntry();
        var withdrawal = new ClientCreditWithdrawal
        {
            Id = 53,
            ClientCreditEntryId = entry.Id,
            Amount = 1m,  // El builder valida amount > 0 antes que el kind.
            Kind = WithdrawalKind.KeptAsCredit,
            ExecutedByUserId = "user",
            ExecutedByUserName = "User",
        };

        var ex = Assert.Throws<InvalidOperationException>(
            () => ManualCashMovementBuilder.BuildExpenseForWithdrawal(withdrawal, entry, "user"));
        Assert.Contains("KeptAsCredit", ex.Message);
    }

    [Fact]
    public void BuildExpenseForWithdrawal_con_applied_to_new_booking_lanza_not_implemented()
    {
        var entry = BuildValidEntry();
        var withdrawal = new ClientCreditWithdrawal
        {
            Id = 54,
            ClientCreditEntryId = entry.Id,
            Amount = 1_000m,
            Kind = WithdrawalKind.AppliedToNewBooking,
            ExecutedByUserId = "user",
            ExecutedByUserName = "User",
        };

        Assert.Throws<NotImplementedException>(
            () => ManualCashMovementBuilder.BuildExpenseForWithdrawal(withdrawal, entry, "user"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    public void BuildExpenseForWithdrawal_con_amount_invalido_lanza_invalid_operation(decimal amount)
    {
        var entry = BuildValidEntry();
        var withdrawal = new ClientCreditWithdrawal
        {
            Id = 55,
            ClientCreditEntryId = entry.Id,
            Amount = amount,
            Kind = WithdrawalKind.PhysicalCash,
            ExecutedByUserId = "user",
            ExecutedByUserName = "User",
        };

        Assert.Throws<InvalidOperationException>(
            () => ManualCashMovementBuilder.BuildExpenseForWithdrawal(withdrawal, entry, "user"));
    }

    [Fact]
    public void BuildExpenseForWithdrawal_con_entry_sin_bc_navigation_no_falla_pero_reserva_es_null()
    {
        // Si el caller no hace Include(entry.BookingCancellation), el helper
        // degrada en silencio y deja RelatedReservaId = null. La trazabilidad
        // sigue viva via ClientCreditWithdrawalId.
        var entry = BuildValidEntry();
        entry.BookingCancellation = null!;

        var withdrawal = new ClientCreditWithdrawal
        {
            Id = 56,
            ClientCreditEntryId = entry.Id,
            Amount = 1_000m,
            Kind = WithdrawalKind.PhysicalCash,
            ExecutedByUserId = "user",
            ExecutedByUserName = "User",
        };

        var movement = ManualCashMovementBuilder.BuildExpenseForWithdrawal(withdrawal, entry, "user");
        Assert.Null(movement.RelatedReservaId);
        // Trazabilidad por la NAVIGATION (ver nota del fix 27506d9 mas arriba): el FK
        // escalar queda null en Unit; EF lo resuelve al persistir.
        Assert.Same(withdrawal, movement.ClientCreditWithdrawal);
        Assert.Null(movement.ClientCreditWithdrawalId);
    }

    [Fact]
    public void BuildExpenseForWithdrawal_con_withdrawal_null_lanza_argument_null()
    {
        var entry = BuildValidEntry();
        Assert.Throws<ArgumentNullException>(
            () => ManualCashMovementBuilder.BuildExpenseForWithdrawal(null!, entry, "user"));
    }

    [Fact]
    public void BuildExpenseForWithdrawal_con_entry_null_lanza_argument_null()
    {
        var withdrawal = new ClientCreditWithdrawal
        {
            Amount = 1m,
            Kind = WithdrawalKind.PhysicalCash,
            ExecutedByUserId = "u",
            ExecutedByUserName = "u",
        };
        Assert.Throws<ArgumentNullException>(
            () => ManualCashMovementBuilder.BuildExpenseForWithdrawal(withdrawal, null!, "user"));
    }

    // ============== BuildExpenseForWithdrawal: methodOverride (MR-02) ==============

    [Fact]
    public void BuildExpenseForWithdrawal_con_methodOverride_usa_el_valor_pasado()
    {
        // Escenario: el cashier retira por cheque (no es ni Cash ni Transfer
        // "puro"). El controller pasa methodOverride="Cheque" al builder para
        // que el Libro de Caja muestre el detalle real, no el default.
        var entry = BuildValidEntry();
        var withdrawal = new ClientCreditWithdrawal
        {
            Id = 60,
            PublicId = Guid.NewGuid(),
            ClientCreditEntryId = entry.Id,
            Amount = 3_000m,
            Kind = WithdrawalKind.PhysicalCash, // El kind dice "Cash" como default...
            ExecutedByUserId = "user-cashier",
            ExecutedByUserName = "Cashier",
        };

        // ...pero el override gana.
        var movement = ManualCashMovementBuilder.BuildExpenseForWithdrawal(
            withdrawal, entry, "user-cashier", methodOverride: "Cheque");

        Assert.Equal("Cheque", movement.Method);
        // El resto del movimiento no se debe afectar.
        Assert.Equal(CashMovementDirections.Expense, movement.Direction);
        Assert.Equal(3_000m, movement.Amount);
    }

    [Fact]
    public void BuildExpenseForWithdrawal_sin_methodOverride_usa_default_por_kind()
    {
        // Confirma que el comportamiento default sigue intacto cuando el caller
        // no pasa el override (compatibilidad hacia atras).
        var entry = BuildValidEntry();
        var physicalCash = new ClientCreditWithdrawal
        {
            Id = 61,
            ClientCreditEntryId = entry.Id,
            Amount = 1m,
            Kind = WithdrawalKind.PhysicalCash,
            ExecutedByUserId = "u",
            ExecutedByUserName = "u",
        };
        var transfer = new ClientCreditWithdrawal
        {
            Id = 62,
            ClientCreditEntryId = entry.Id,
            Amount = 1m,
            Kind = WithdrawalKind.Transfer,
            ExecutedByUserId = "u",
            ExecutedByUserName = "u",
        };

        var movCash = ManualCashMovementBuilder.BuildExpenseForWithdrawal(physicalCash, entry, "u");
        var movTransfer = ManualCashMovementBuilder.BuildExpenseForWithdrawal(transfer, entry, "u");

        Assert.Equal("Cash", movCash.Method);
        Assert.Equal("Transfer", movTransfer.Method);
    }

    [Theory]
    [InlineData(null, "Cash")]      // null -> default por kind PhysicalCash
    [InlineData("", "Cash")]        // vacio -> default (defensa contra DTO mal armado)
    [InlineData("   ", "Cash")]     // whitespace -> default
    [InlineData("MercadoPago", "MercadoPago")] // valor real del usuario
    public void BuildExpenseForWithdrawal_methodOverride_vacio_o_whitespace_cae_al_default(
        string? methodOverride, string expectedMethod)
    {
        var entry = BuildValidEntry();
        var withdrawal = new ClientCreditWithdrawal
        {
            Id = 63,
            ClientCreditEntryId = entry.Id,
            Amount = 1m,
            Kind = WithdrawalKind.PhysicalCash,
            ExecutedByUserId = "u",
            ExecutedByUserName = "u",
        };

        var movement = ManualCashMovementBuilder.BuildExpenseForWithdrawal(
            withdrawal, entry, "u", methodOverride: methodOverride);

        Assert.Equal(expectedMethod, movement.Method);
    }
}
