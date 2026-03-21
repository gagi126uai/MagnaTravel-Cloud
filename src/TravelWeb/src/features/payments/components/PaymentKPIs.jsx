const currency = new Intl.NumberFormat("es-AR", {
  style: "currency",
  currency: "ARS",
  minimumFractionDigits: 2,
});

export function PaymentKPIs({ stats }) {
  const items = [
    { label: "Cuentas por cobrar", value: stats.accountsReceivable },
    { label: "Pendiente AFIP elegible", value: stats.afipEligiblePending },
    { label: "Ingresos del mes", value: stats.cashInThisMonth },
    { label: "Egresos del mes", value: stats.cashOutThisMonth },
    { label: "Facturado AFIP (mes)", value: stats.totalInvoicedMonth },
    { label: "Caja neta del mes", value: stats.netCashThisMonth },
  ];

  return (
    <div className="grid grid-cols-1 sm:grid-cols-2 xl:grid-cols-3 gap-4">
      {items.map((item) => (
        <div
          key={item.label}
          className="rounded-2xl border border-slate-200 dark:border-slate-800 bg-white dark:bg-slate-900 px-5 py-4 shadow-sm"
        >
          <div className="text-xs uppercase tracking-wider font-semibold text-slate-400 mb-2">{item.label}</div>
          <div className="text-2xl font-light text-slate-900 dark:text-white">{currency.format(item.value || 0)}</div>
        </div>
      ))}
    </div>
  );
}
