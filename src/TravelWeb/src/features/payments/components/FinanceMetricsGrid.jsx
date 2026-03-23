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
          <div className="text-2xl font-light text-slate-900 dark:text-white">
            {item.isCount ? Number(item.value || 0) : formatCurrency(item.value)}
          </div>
          {item.help && (
            <div className="mt-2 text-xs text-slate-500 dark:text-slate-400">{item.help}</div>
          )}
        </div>
      ))}
    </div>
  );
}
