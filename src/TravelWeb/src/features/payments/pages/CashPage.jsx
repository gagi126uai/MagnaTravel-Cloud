import PaymentsCashPage from "./PaymentsCashPage";

export default function CashPage() {
  return (
    <div className="max-w-7xl mx-auto p-4 sm:p-8 space-y-8 pb-20">
      <div className="space-y-2 pb-6 border-b border-slate-100 dark:border-slate-800/50">
        <h1 className="text-3xl font-light tracking-tight text-slate-900 dark:text-white">
          Caja
        </h1>
        <p className="text-sm text-slate-500 dark:text-slate-400">
          Ingresos, egresos y ajustes manuales. Esta vista muestra solo movimientos reales de dinero.
        </p>
      </div>

      <PaymentsCashPage />
    </div>
  );
}
