/**
 * Grilla de métricas financieras — usada en Caja (PaymentsCashPage) y otras pantallas de pagos.
 *
 * Multimoneda (2026-06-11): cada tarjeta puede mostrar DOS cifras, una por moneda
 * (pesos arriba con cartelito $, dólares abajo con cartelito US$).
 * Para activarlo: pasar `valuesByCurrency: [{currency, value}]` en el item en vez de `value`.
 * Regla ③: si el item solo tiene `value` (mono-moneda), la tarjeta se ve IGUAL que antes.
 *
 * @param {Array<ItemMetrics>} items
 * @param {string} columns - Clase Tailwind para el grid (default "md:grid-cols-3")
 *
 * ItemMetrics shape:
 *   { label, value?, valuesByCurrency?, isCount?, help? }
 *   - value: número único (mono-moneda o conteos)
 *   - valuesByCurrency: [{currency:"ARS"|"USD", value:number}] — activa el modo bi-moneda
 *   - isCount: si true, muestra el valor como entero sin formatear como moneda
 */
import { CurrencyBadge } from "../../../components/ui/CurrencyBadge";
import { formatCurrency } from "../lib/financeUtils";

export function FinanceMetricsGrid({ items, columns = "md:grid-cols-3" }) {
  return (
    <div className={`grid grid-cols-1 gap-4 ${columns}`}>
      {items.map((item) => (
        <div
          key={item.label}
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

          {item.help && (
            <div className="mt-2 text-xs text-slate-500 dark:text-slate-400">{item.help}</div>
          )}
        </div>
      ))}
    </div>
  );
}
