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
    /// en estado activo (InManagement / Confirmed / Traveling / ToSettle). Es plata de VENTA (no costo):
    /// NO se enmascara.
    /// </summary>
    Task<List<FinanceCurrencyAmount>> GetAccountsReceivableByCurrencyAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Cuentas por PAGAR por moneda: suma de <c>SupplierBalanceByCurrency.Balance &gt; 0</c> (deuda con
    /// proveedores). Es plata de COSTO: el caller decide si la enmascara segun <c>cobranzas.see_cost</c>.
    /// </summary>
    Task<List<FinanceCurrencyAmount>> GetAccountsPayableByCurrencyAsync(CancellationToken cancellationToken);
}

/// <summary>
/// ADR-022 §4.7: monto por moneda neutral de capa (no acoplado a los DTOs de tesoreria ni de reportes).
/// Cada consumidor lo proyecta a su propio DTO (CurrencyAmountDto / CurrencyAmount).
/// </summary>
public record FinanceCurrencyAmount(string Currency, decimal Amount);
