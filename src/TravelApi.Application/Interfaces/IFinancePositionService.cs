namespace TravelApi.Application.Interfaces;

/// <summary>
/// ADR-022 §4.7 (T4): fuente UNICA de la posicion financiera por moneda. Antes el dashboard
/// (ReportService) y la tesoreria (TreasuryService) armaban las cuentas por cobrar (AR) y por pagar (AP)
/// con queries distintas -> podian no cerrar. Este servicio centraliza esas dos queries para que ambos
/// consumidores muestren EXACTAMENTE el mismo numero.
///
/// <para><b>Por que un servicio compartido y no un helper estatico</b>: las dos consultas corren contra
/// las tablas hijas materializadas (ReservaMoneyByCurrency / SupplierBalanceByCurrency) via EF; necesitan
/// el <c>AppDbContext</c> del scope. Una interfaz inyectable es testeable, no introduce ciclo (ambos
/// services ya dependen del DbContext, no entre si) y deja una sola definicion de "que es AR" y "que es AP".</para>
///
/// <para><b>Moneda SIEMPRE separada</b>: cada metodo devuelve una lista con una linea por moneda. NUNCA se
/// consolida ni se convierte ARS+USD en un solo total (limite contable de ADR-021).</para>
/// </summary>
public interface IFinancePositionService
{
    /// <summary>
    /// Cuentas por COBRAR por moneda: suma de <c>ReservaMoneyByCurrency.Balance &gt; 0</c> de las reservas
    /// en estado firme con deuda (InManagement / Confirmed / Closed; ADR-036 quito Traveling y ToSettle). Es
    /// plata de VENTA (no costo): NO se enmascara.
    /// </summary>
    Task<List<FinanceCurrencyAmount>> GetAccountsReceivableByCurrencyAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Cuentas por PAGAR por moneda: suma de <c>SupplierBalanceByCurrency.Balance &gt; 0</c> (deuda con
    /// proveedores). Es plata de COSTO: el caller decide si la enmascara segun <c>cobranzas.see_cost</c>.
    /// </summary>
    Task<List<FinanceCurrencyAmount>> GetAccountsPayableByCurrencyAsync(CancellationToken cancellationToken);

    /// <summary>
    /// ADR-023 T1: saldo a COBRAR del cliente POR MONEDA (deuda exigible), derivado de
    /// ReservaMoneyByCurrency de sus reservas en firme. Misma definicion canonica de "en firme"
    /// (InManagement / Confirmed / Closed; ADR-036 quito Traveling y ToSettle) que el AR global. NUNCA mezcla monedas.
    /// </summary>
    Task<List<FinanceCurrencyAmount>> GetCustomerReceivableByCurrencyAsync(int customerId, CancellationToken cancellationToken);

    /// <summary>
    /// ADR-023 T1: escalar de compat (suma cross-moneda) del saldo a cobrar de UN cliente. Solo para los
    /// campos escalares que el front actual todavia lee (CurrentBalance / TotalBalance). NUNCA se usa para
    /// decidir nada por moneda. Con todo en ARS coincide con la unica linea ARS.
    /// </summary>
    Task<decimal> GetCustomerReceivableScalarAsync(int customerId, CancellationToken cancellationToken);

    /// <summary>
    /// ADR-023 T1: saldo a cobrar escalar de TODOS los clientes activos de una sola pasada (para el
    /// ordenamiento de la lista y el enriquecimiento de la grilla). Devuelve <c>Customer.PublicId -&gt; escalar</c>.
    /// Se agrupa por PublicId (no por Id interno) porque el DTO de la lista expone PublicId, no Id.
    /// </summary>
    Task<Dictionary<Guid, decimal>> GetReceivableScalarByCustomerPublicIdAsync(CancellationToken cancellationToken);

    /// <summary>
    /// ADR-023 T1: indica si un estado de reserva cuenta como deuda exigible (en firme). Expuesto para que
    /// los consumidores (CustomerService overview, ReportService) usen el MISMO predicado canonico sin
    /// re-declarar la lista de estados. Evita que existan dos definiciones de "que es saldo a cobrar".
    /// </summary>
    bool IsInFirmReceivableStatus(string status);
}

/// <summary>
/// ADR-022 §4.7: monto por moneda neutral de capa (no acoplado a los DTOs de tesoreria ni de reportes).
/// Cada consumidor lo proyecta a su propio DTO (CurrencyAmountDto / CurrencyAmount).
/// </summary>
public record FinanceCurrencyAmount(string Currency, decimal Amount);
