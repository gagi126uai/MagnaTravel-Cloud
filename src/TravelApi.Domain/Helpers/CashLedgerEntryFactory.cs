using TravelApi.Domain.Entities;

namespace TravelApi.Domain.Helpers;

/// <summary>
/// ADR-022 §4.4: punto UNICO de construccion de un <see cref="CashLedgerEntry"/> a partir de cada
/// origen. Espejo de <see cref="ManualCashMovementBuilder"/>: funciones puras, <c>static</c>, que solo
/// arman el POCO. NO hacen <c>Add</c> ni <c>SaveChangesAsync</c> — eso lo hace el caller dentro de la
/// misma transaccion que crea el hecho economico.
///
/// <para><b>Por que un solo lugar</b>: el mapeo origen -&gt; asiento (direction, moneda real, source type,
/// FK seteada) vive aca y no se duplica en los 4 services (Payment, Supplier, Treasury, cancelacion).
/// Asi la regla "el asiento lleva la moneda REAL de caja, nunca la imputada" se prueba una sola vez.</para>
///
/// <para><b>Patron de FK por navigation (no por Id escalar)</b>: igual que <see cref="ManualCashMovementBuilder"/>,
/// cuando el origen acaba de hacer <c>Add()</c> y todavia tiene <c>Id == 0</c> (no se persistio), seteamos
/// la NAVIGATION property y EF resuelve la FK al guardar en orden topologico. Por eso cada factory recibe
/// la entidad de origen completa, no su Id.</para>
/// </summary>
public static class CashLedgerEntryFactory
{
    /// <summary>
    /// Asiento de un COBRO a cliente (origen <see cref="Payment"/>, <c>Income</c>).
    ///
    /// <para><b>Moneda (RK-3)</b>: usa <see cref="Payment.Currency"/> y <see cref="Payment.Amount"/> —
    /// la caja REAL —, NUNCA <c>ImputedAmount</c>/<c>ImputedCurrency</c>. Un cobro cruzado entra a caja
    /// en la moneda que efectivamente entro; el equivalente imputado es asunto del saldo, no de la caja.</para>
    /// </summary>
    public static CashLedgerEntry ForPayment(Payment payment, string? actorUserId, string? actorUserName)
    {
        if (payment is null) throw new ArgumentNullException(nameof(payment));
        if (payment.Amount <= 0m)
            throw new InvalidOperationException("Payment.Amount debe ser > 0 para asentar en el Libro de Caja.");

        return new CashLedgerEntry
        {
            Direction = CashMovementDirections.Income,
            Amount = ReservationEconomicPolicy.RoundCurrency(payment.Amount),
            Currency = Monedas.Normalizar(payment.Currency),
            Method = string.IsNullOrWhiteSpace(payment.Method) ? "Transfer" : payment.Method,
            OccurredAt = payment.PaidAt,
            SourceType = CashLedgerSourceTypes.CustomerPayment,
            // Navigation property (no el Id escalar): el Payment puede tener Id=0 si se acaba de Add().
            Payment = payment,
            ReservaId = payment.ReservaId,
            CreatedByUserId = actorUserId,
            CreatedByUserName = actorUserName,
            CreatedAt = DateTime.UtcNow,
        };
    }

    /// <summary>
    /// Asiento de un PAGO a proveedor (origen <see cref="SupplierPayment"/>, <c>Expense</c>).
    /// Moneda = <see cref="SupplierPayment.Currency"/> (caja real, no imputada).
    /// </summary>
    public static CashLedgerEntry ForSupplierPayment(SupplierPayment payment, string? actorUserId, string? actorUserName)
    {
        if (payment is null) throw new ArgumentNullException(nameof(payment));
        if (payment.Amount <= 0m)
            throw new InvalidOperationException("SupplierPayment.Amount debe ser > 0 para asentar en el Libro de Caja.");

        return new CashLedgerEntry
        {
            Direction = CashMovementDirections.Expense,
            Amount = ReservationEconomicPolicy.RoundCurrency(payment.Amount),
            Currency = Monedas.Normalizar(payment.Currency),
            Method = string.IsNullOrWhiteSpace(payment.Method) ? "Transfer" : payment.Method,
            OccurredAt = payment.PaidAt,
            SourceType = CashLedgerSourceTypes.SupplierPayment,
            SupplierPayment = payment,
            ReservaId = payment.ReservaId,
            SupplierId = payment.SupplierId,
            CreatedByUserId = actorUserId,
            CreatedByUserName = actorUserName,
            CreatedAt = DateTime.UtcNow,
        };
    }

    /// <summary>
    /// Asiento de un MOVIMIENTO MANUAL (gasto/ajuste o de cancelacion). UN solo asiento por
    /// <see cref="ManualCashMovement"/> (RK-1: no se asienta ademas el refund/withdrawal por separado).
    ///
    /// <para>El <see cref="CashLedgerEntry.SourceType"/> se DERIVA de los FKs del manual:</para>
    /// <list type="bullet">
    /// <item><c>OperatorRefundReceivedId</c> -&gt; <c>OperatorRefund</c> (ingreso del refund de operador).</item>
    /// <item><c>ClientCreditWithdrawalId</c> -&gt; <c>ClientCreditWithdrawal</c> (devolucion fisica al cliente).</item>
    /// <item>ninguno -&gt; <c>ManualAdjustment</c> (gasto/ajuste puro).</item>
    /// </list>
    ///
    /// <para><b>Moneda (B1, RK-3)</b>: el <see cref="ManualCashMovement.Currency"/> sirve SOLO para el
    /// ajuste puro. Para un movimiento de CANCELACION nace en ARS por default y NO refleja el hecho real,
    /// asi que la moneda del asiento la pone el caller desde el ORIGEN REAL (<c>OperatorRefundReceived.Currency</c>
    /// o <c>ClientCreditEntry.Currency</c>) via <paramref name="currencyOverride"/>. Si el caller no lo pasa
    /// (ajuste puro), se usa la del propio manual.</para>
    /// </summary>
    /// <param name="movement">El movimiento manual ya armado (puede tener Id=0 si recien se Add()-eo).</param>
    /// <param name="currencyOverride">
    /// Moneda REAL del hecho para movimientos de cancelacion. Si es null/vacio, se usa
    /// <see cref="ManualCashMovement.Currency"/> (caso del ajuste puro).
    /// </param>
    public static CashLedgerEntry ForManualMovement(
        ManualCashMovement movement,
        string? currencyOverride,
        string? actorUserId,
        string? actorUserName)
    {
        if (movement is null) throw new ArgumentNullException(nameof(movement));
        if (movement.Amount <= 0m)
            throw new InvalidOperationException("ManualCashMovement.Amount debe ser > 0 para asentar en el Libro de Caja.");

        // SourceType derivado de los FKs del manual (RK-1: una sola fila de caja por hecho).
        string sourceType;
        if (movement.OperatorRefundReceivedId != null || movement.OperatorRefundReceived != null)
        {
            sourceType = CashLedgerSourceTypes.OperatorRefund;
        }
        else if (movement.ClientCreditWithdrawalId != null || movement.ClientCreditWithdrawal != null)
        {
            sourceType = CashLedgerSourceTypes.ClientCreditWithdrawal;
        }
        else
        {
            sourceType = CashLedgerSourceTypes.ManualAdjustment;
        }

        var currency = string.IsNullOrWhiteSpace(currencyOverride)
            ? Monedas.Normalizar(movement.Currency)
            : Monedas.Normalizar(currencyOverride);

        return new CashLedgerEntry
        {
            Direction = movement.Direction,
            Amount = ReservationEconomicPolicy.RoundCurrency(movement.Amount),
            Currency = currency,
            Method = string.IsNullOrWhiteSpace(movement.Method) ? "Transfer" : movement.Method,
            OccurredAt = movement.OccurredAt,
            SourceType = sourceType,
            ManualCashMovement = movement,
            ReservaId = movement.RelatedReservaId,
            SupplierId = movement.RelatedSupplierId,
            CreatedByUserId = actorUserId,
            CreatedByUserName = actorUserName,
            CreatedAt = DateTime.UtcNow,
        };
    }

    /// <summary>
    /// ADR-022 §4.5: construye la REVERSA (contra-asiento) de un asiento vigente. El caller debe,
    /// en la MISMA transaccion y ANTES de insertar cualquier asiento nuevo, marcar el original
    /// <see cref="CashLedgerEntry.IsReversed"/>=true (para que salga del indice unico parcial) y luego
    /// hacer <c>Add</c> de esta reversa.
    ///
    /// <para>La reversa copia monto/moneda/origen del original, invierte la <see cref="CashLedgerEntry.Direction"/>,
    /// marca <see cref="CashLedgerEntry.IsReversal"/>=true y apunta <see cref="CashLedgerEntry.ReversedEntryId"/>
    /// al original. NO copia el FK de origen como vigente (sale del indice por <c>IsReversal=true</c>), pero SI lo
    /// conserva para la trazabilidad "que cobro/pago se anulo".</para>
    /// </summary>
    /// <param name="original">El asiento vigente que se anula. Debe tener Id real (ya persistido).</param>
    /// <param name="occurredAt">Fecha de la anulacion (normalmente "ahora").</param>
    public static CashLedgerEntry Reverse(
        CashLedgerEntry original,
        DateTime occurredAt,
        string? actorUserId,
        string? actorUserName)
    {
        if (original is null) throw new ArgumentNullException(nameof(original));
        if (original.IsReversal)
            throw new InvalidOperationException("No se puede revertir una reversa (un contra-asiento no se anula a su vez).");

        var invertedDirection = string.Equals(original.Direction, CashMovementDirections.Income, StringComparison.Ordinal)
            ? CashMovementDirections.Expense
            : CashMovementDirections.Income;

        return new CashLedgerEntry
        {
            Direction = invertedDirection,
            Amount = original.Amount,
            Currency = original.Currency,
            Method = original.Method,
            OccurredAt = occurredAt,
            SourceType = original.SourceType,
            IsReversal = true,
            ReversedEntryId = original.Id,
            // Conservar el MISMO FK de origen (trazabilidad). Sale del indice unico por IsReversal=true.
            PaymentId = original.PaymentId,
            SupplierPaymentId = original.SupplierPaymentId,
            OperatorRefundReceivedId = original.OperatorRefundReceivedId,
            ClientCreditWithdrawalId = original.ClientCreditWithdrawalId,
            ManualCashMovementId = original.ManualCashMovementId,
            ReservaId = original.ReservaId,
            SupplierId = original.SupplierId,
            CustomerId = original.CustomerId,
            CreatedByUserId = actorUserId,
            CreatedByUserName = actorUserName,
            CreatedAt = DateTime.UtcNow,
        };
    }
}
