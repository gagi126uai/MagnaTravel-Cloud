using TravelApi.Domain.Entities;

namespace TravelApi.Domain.Reservations;

/// <summary>
/// ADR-021 §15.4 (multimoneda, eje proveedor, 2026-06-08): calculador PURO de la deuda de la agencia
/// HACIA un proveedor/operador, SEPARADA por moneda. Es el espejo de <see cref="ReservaMoneyCalculator"/>
/// del lado cliente: en vez de "cuanto nos debe el cliente" calcula "cuanto le debemos al operador".
///
/// <para>La deuda de cada moneda = (compras CONFIRMADAS en esa moneda) - (pagos imputados a esa moneda).
/// Las compras vienen en la moneda del servicio; los pagos en la moneda a la que se IMPUTAN (el
/// equivalente convertido si el pago cruzo moneda). El saldo a favor de una moneda (sobrepago al
/// operador) NO compensa la deuda de otra.</para>
///
/// <para><b>Regla de oro (regresion)</b>: un proveedor 100% en una sola moneda (caso legacy ARS) da
/// exactamente la misma deuda escalar que la cuenta vieja (compras - pagos, sin separar). Funcion pura:
/// recibe los montos ya materializados, no toca EF ni base de datos.</para>
/// </summary>
public static class SupplierDebtCalculator
{
    // ADR-022 §4.10 (fix #4, 2026-06-11): UNICA fuente de los estados de Reserva en los que un servicio
    // "cuenta" como vivo para la cuenta corriente del proveedor. Antes esta lista estaba DUPLICADA en
    // SupplierService.ValidReservationStatuses y SupplierDebtPersister.ValidReservationStatuses: si una de
    // las dos cambiaba, el numero de deuda salia distinto segun el camino (servicio del proveedor vs
    // persister generico) y mentia en silencio. Centralizada aca, ambos consumen el mismo array.
    // ADR-020: InManagement (En gestion) reemplazo al viejo Sold. La deuda real ademas filtra por servicio
    // confirmado (CountsForSupplierDebtByType); este conjunto solo define que RESERVAS son "vivas" para el
    // proveedor. ADR-036 (2026-06-21, prepago puro): se quito ToSettle (estado eliminado). Traveling SIGUE
    // contando: aunque el cliente ya quedo saldado para viajar, la deuda CON EL OPERADOR puede seguir viva
    // (el casillero "pagado al operador" por servicio es trabajo aparte; ver ADR-036). Closed se mantiene.
    public static readonly string[] ValidReservationStatuses =
    {
        EstadoReserva.InManagement,
        EstadoReserva.Confirmed,
        EstadoReserva.Traveling,
        EstadoReserva.Closed
    };

    /// <summary>
    /// (2026-06-26) Regla unica: ¿los servicios de un proveedor generan Cuenta por Pagar (deuda de compra) segun
    /// su modelo de facturacion al cliente?
    ///
    /// <list type="bullet">
    /// <item><see cref="SupplierInvoicingMode.TotalToCustomer"/> (reseller): la agencia COMPRA al operador y le
    ///   revende al cliente -> SI hay deuda de compra (el costo confirmado es Cuenta por Pagar). Comportamiento
    ///   historico.</item>
    /// <item><see cref="SupplierInvoicingMode.CommissionOnly"/> (intermediacion): el operador le factura DIRECTO
    ///   al cliente final; la agencia NO compra el servicio, solo cobra su comision -> NO hay deuda de compra con
    ///   ese operador por ese servicio. Antes de este fix la deuda nacia ciega del NetCost ignorando el modo, e
    ///   inflaba las Cuentas por Pagar con plata que la agencia nunca le debe al operador.</item>
    /// </list>
    ///
    /// <para><b>Por que vive en el dominio</b>: es la MISMA regla que tienen que aplicar el persister de la deuda
    /// (escalar + tabla hija) y todas las lecturas de la cuenta del proveedor en <c>SupplierService</c>. Tenerla
    /// en un solo lugar evita que una rama la aplique y otra no (la deuda mentiria distinto segun el camino).
    /// Solo afecta el lado COMPRA (ConfirmedPurchases): los pagos ya registrados al operador se siguen contando
    /// (un CommissionOnly no deberia tener pagos, pero si los hubiera, quedan como saldo a favor, no se ocultan).</para>
    /// </summary>
    public static bool SupplierGeneratesPurchaseDebt(SupplierInvoicingMode invoicingMode)
        => invoicingMode == SupplierInvoicingMode.TotalToCustomer;

    /// <summary>
    /// ADR-041 TANDA 5 (2026-06-27): deriva el vencimiento SUGERIDO de una compra/servicio con el operador.
    /// = <paramref name="serviceOrPurchaseDate"/> + el plazo por defecto del proveedor (en dias).
    ///
    /// <para>OPCIONAL: si el proveedor no tiene plazo (<paramref name="defaultPaymentTermDays"/> = null), no
    /// hay vencimiento sugerido y devuelve <c>null</c> (comportamiento actual). Seguimos prepago: este dato es
    /// solo INFORMATIVO (priorizar/avisar), NO bloquea nada. Funcion pura para poder testear la derivacion sin
    /// EF; la validacion de que el plazo no sea negativo se hace al persistir el proveedor.</para>
    /// </summary>
    public static DateTime? DeriveSuggestedDueDate(DateTime serviceOrPurchaseDate, int? defaultPaymentTermDays)
    {
        if (defaultPaymentTermDays is not int termDays)
        {
            return null;
        }

        return serviceOrPurchaseDate.AddDays(termDays);
    }

    /// <summary>
    /// Una compra confirmada que aporta a la deuda: su monto (NetCost) y su moneda (null = ARS).
    /// </summary>
    public readonly record struct ConfirmedPurchase(string? Currency, decimal NetCost);

    /// <summary>
    /// Un pago al proveedor: monto real de caja, su moneda real, y (si cruzo) la moneda imputada y el
    /// equivalente imputado. Para imputar la deuda se usa <c>ImputedAmount ?? Amount</c> sobre
    /// <c>ImputedCurrency ?? Currency</c> (idem eje cliente).
    /// </summary>
    public readonly record struct SupplierPaymentInput(
        decimal Amount, string? Currency, string? ImputedCurrency, decimal? ImputedAmount);

    /// <summary>
    /// Moneda a la que un pago IMPUTA su deuda: la imputada si el pago cruzo de moneda, si no su propia
    /// moneda (normalizada, null -> ARS). Es la primitiva UNICA de imputacion de pagos: la usan tanto el
    /// calculo de deuda (<see cref="Calculate"/>) como el extracto del proveedor
    /// (<c>SupplierAccountStatementBuilder</c>), para que la deuda materializada y el saldo de cierre del
    /// extracto NO puedan divergir (no hay dos formulas de imputacion escritas a mano).
    /// </summary>
    public static string ImputedCurrencyOf(SupplierPaymentInput payment)
        => Monedas.Normalizar(payment.ImputedCurrency ?? payment.Currency);

    /// <summary>
    /// Monto que un pago IMPUTA a la deuda: el equivalente imputado si cruzo de moneda, si no su monto de
    /// caja. Primitiva UNICA de imputacion de pagos (ver <see cref="ImputedCurrencyOf"/>).
    /// </summary>
    public static decimal ImputedAmountOf(SupplierPaymentInput payment)
        => payment.ImputedAmount ?? payment.Amount;

    /// <summary>
    /// Calcula la deuda por moneda. Devuelve una linea por cada moneda presente en compras o pagos.
    /// </summary>
    public static IReadOnlyDictionary<string, SupplierDebtLine> Calculate(
        IEnumerable<ConfirmedPurchase> confirmedPurchases,
        IEnumerable<SupplierPaymentInput> payments)
    {
        ArgumentNullException.ThrowIfNull(confirmedPurchases);
        ArgumentNullException.ThrowIfNull(payments);

        var purchasesByCurrency = new Dictionary<string, decimal>(StringComparer.Ordinal);
        var paidByCurrency = new Dictionary<string, decimal>(StringComparer.Ordinal);

        foreach (var purchase in confirmedPurchases)
        {
            string currency = Monedas.Normalizar(purchase.Currency);
            purchasesByCurrency.TryGetValue(currency, out var current);
            purchasesByCurrency[currency] = current + purchase.NetCost;
        }

        foreach (var payment in payments)
        {
            // Imputa el equivalente a la moneda de la deuda (ImputedCurrency); si no cruzo, su propia
            // moneda y su Amount. Para el caso legacy (sin moneda ni imputacion) es ARS + Amount. Usamos
            // las primitivas publicas para que el extracto del proveedor impute EXACTAMENTE igual.
            string imputedCurrency = ImputedCurrencyOf(payment);
            decimal imputedAmount = ImputedAmountOf(payment);
            paidByCurrency.TryGetValue(imputedCurrency, out var current);
            paidByCurrency[imputedCurrency] = current + imputedAmount;
        }

        // Union de monedas presentes en compras o pagos.
        var currencies = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in purchasesByCurrency.Keys) currencies.Add(key);
        foreach (var key in paidByCurrency.Keys) currencies.Add(key);

        var lines = new Dictionary<string, SupplierDebtLine>(StringComparer.Ordinal);
        foreach (var currency in currencies)
        {
            purchasesByCurrency.TryGetValue(currency, out var confirmed);
            paidByCurrency.TryGetValue(currency, out var paid);
            lines[currency] = new SupplierDebtLine(currency, confirmed, paid);
        }
        return lines;
    }

    /// <summary>
    /// Deriva el escalar surrogate de <c>Supplier.CurrentBalance</c> (semaforo de "tiene deuda
    /// pendiente", no monto), espejo de <see cref="ReservaMoneySummary.Balance"/>:
    /// <list type="bullet">
    /// <item><b>Mono-moneda</b>: la deuda cruda de esa unica moneda (puede ser negativa = sobrepago)
    ///   -> identico a la cuenta legacy del proveedor, incluso el sobrepago.</item>
    /// <item><b>Multimoneda</b>: <c>sum(max(0, linea.Balance))</c> — el sobrepago de una moneda no
    ///   compensa la deuda de otra.</item>
    /// </list>
    /// </summary>
    public static decimal ToSurrogateBalance(IReadOnlyDictionary<string, SupplierDebtLine> porMoneda)
    {
        ArgumentNullException.ThrowIfNull(porMoneda);
        if (porMoneda.Count == 0) return 0m;

        if (porMoneda.Count == 1)
        {
            foreach (var line in porMoneda.Values)
                return line.Balance; // crudo (puede ser negativo) = identico a legacy mono-moneda
        }

        decimal surrogate = 0m;
        foreach (var line in porMoneda.Values)
        {
            if (line.Balance > 0m) surrogate += line.Balance;
        }
        return surrogate;
    }
}

/// <summary>
/// ADR-021 §15.3: deuda con un proveedor en UNA moneda. Value object inmutable. La proyeccion
/// persistida es la tabla hija <c>SupplierBalanceByCurrency</c>.
/// </summary>
public sealed class SupplierDebtLine
{
    public string Currency { get; }

    /// <summary>Compras confirmadas (NetCost que cuenta como deuda) en esta moneda.</summary>
    public decimal ConfirmedPurchases { get; }

    /// <summary>Pagado al proveedor imputado a esta moneda.</summary>
    public decimal TotalPaid { get; }

    /// <summary>Deuda de esta moneda = <see cref="ConfirmedPurchases"/> - <see cref="TotalPaid"/> (puede ser negativa).</summary>
    public decimal Balance { get; }

    public SupplierDebtLine(string currency, decimal confirmedPurchases, decimal totalPaid)
    {
        Currency = currency;
        ConfirmedPurchases = confirmedPurchases;
        TotalPaid = totalPaid;
        Balance = confirmedPurchases - totalPaid;
    }
}
