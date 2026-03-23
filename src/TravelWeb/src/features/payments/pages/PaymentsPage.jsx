import { Outlet } from "react-router-dom";
import { FinanceSubnav } from "../components/FinanceSubnav";

export default function PaymentsPage() {
  return (
    <div className="max-w-7xl mx-auto p-4 sm:p-8 space-y-8 pb-20">
      <div className="space-y-4 pb-6 border-b border-slate-100 dark:border-slate-800/50">
        <div>
          <h1 className="text-3xl font-light tracking-tight text-slate-900 dark:text-white mb-1">
            Cobranzas, Caja y Facturacion
          </h1>
          <p className="text-sm text-slate-500 dark:text-slate-400">
            Separa seguimiento de cobros, movimientos reales de caja y estado fiscal AFIP.
          </p>
        </div>

        <FinanceSubnav />
      </div>

      <Outlet />
    </div>
  );
}
