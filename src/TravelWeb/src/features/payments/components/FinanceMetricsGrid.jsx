/**
 * Grilla de métricas financieras — usada en Caja (PaymentsCashPage) y otras pantallas de pagos.
 *
 * Multimoneda (2026-06-11): cada tarjeta puede mostrar DOS cifras, una por moneda
 * (pesos arriba con cartelito $, dólares abajo con cartelito US$).
 * Para activarlo: pasar `valuesByCurrency: [{currency, value}]` en el item en vez de `value`.
 * Regla ③: si el item solo tiene `value` (mono-moneda), la tarjeta se ve IGUAL que antes.
 *
 * F4-7 (2026-06-26): soporte para `testId` por item (data-testid en la tarjeta) y para
 * `creditApplicationsByCurrency` — línea chica debajo del número grande que muestra el monto
 * de saldo a favor aplicado de otra reserva. Solo aparece si el backend lo expone con monto > 0.
 *
 * @param {Array<ItemMetrics>} items
 * @param {string} columns - Clase Tailwind para el grid (default "md:grid-cols-3")
 *
 * ItemMetrics shape:
 *   { label, value?, valuesByCurrency?, isCount?, help?, testId?, creditApplicationsByCurrency? }
 *   - value: número único (mono-moneda o conteos)
 *   - valuesByCurrency: [{currency:"ARS"|"USD", value:number}] — activa el modo bi-moneda
 *   - isCount: si true, muestra el valor como entero sin formatear como moneda
 *   - testId: data-testid de la tarjeta entera (para tests y QA)
 *   - creditApplicationsByCurrency: [{currency, value}] — línea chica "aplicados de saldo a favor"
 *     Solo se renderiza si hay al menos una entrada con value > 0.
 */
import { CurrencyBadge } from "../../../components/ui/CurrencyBadge";
import { formatCurrency } from "../lib/financeUtils";

export function FinanceMetricsGrid({ items, columns = "md:grid-cols-3" }) {
  return (
    <div className={`grid grid-cols-1 gap-4 ${columns}`}>
      {items.map((item) => (
        <div
          key={item.label}
          data-testid={item.testId}
          className="rounded-2xl border border-slate-200 dark:border-slate-800 bg-white dark:bg-slate-900 px-5 py-4 shadow-sm"
        >
          <div className="text-xs uppercase tracking-wider font-semibold text-slate-400 mb-2">
            {item.label}
          </div>

          {item.valuesByCurrency && item.valuesByCurrency.length > 0 ? (
            // Modo multimoneda: dos líneas, una por moneda (pesos arriba, dólares abajo)
            <div className="space-y-1.5">
              {item.valuesByCurrency.map((pm) => (
                <div key={pm.currency} className="flex items-center gap-1.5">
                  <CurrencyBadge currency={pm.currency} size="sm" />
                  <span className="text-xl font-light text-slate-900 dark:text-white">
                    {formatCurrency(pm.value, pm.currency)}
                  </span>
                </div>
              ))}
            </div>
          ) : (
            // Modo mono-moneda o conteo: una sola cifra (comportamiento idéntico al original)
            <div className="text-2xl font-light text-slate-900 dark:text-white">
              {item.isCount ? Number(item.value || 0) : formatCurrency(item.value)}
            </div>
          )}

          {/* F4-7: línea chica de saldo a favor aplicado este mes.
              Solo aparece si el backend manda `creditApplicationsByCurrency` con monto > 0.
              Si el backend no lo expone todavía, este bloque no se renderiza nunca.
              Texto exacto de la spec: "+ $ X aplicados de saldo a favor". */}
          {Array.isArray(item.creditApplicationsByCurrency) &&
            item.creditApplicationsByCurrency.some((pm) => pm.value > 0) && (
            <div
              data-testid="kpi-cobrado-mes-saldo-favor"
              className="mt-1.5 space-y-0.5"
            >
              {item.creditApplicationsByCurrency
                .filter((pm) => pm.value > 0)
                .map((pm) => (
                  <div
                    key={pm.currency}
                    className="text-xs text-slate-400 dark:text-slate-500"
                  >
                    + {formatCurrency(pm.value, pm.currency)} aplicados de saldo a favor
                  </div>
                ))}
            </div>
          )}

          {item.help && (
            <div className="mt-2 text-xs text-slate-500 dark:text-slate-400">{item.help}</div>
          )}
        </div>
      ))}
    </div>
  );
}
