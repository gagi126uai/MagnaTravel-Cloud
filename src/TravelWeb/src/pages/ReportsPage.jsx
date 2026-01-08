import { useEffect, useState } from "react";
import { apiRequest } from "../api";
import { showError } from "../alerts";

export default function ReportsPage() {
  const [summary, setSummary] = useState(null);

  useEffect(() => {
    apiRequest("/api/reports/summary")
      .then(setSummary)
      .catch(() => {
        showError("No se pudieron cargar los reportes.");
      });
  }, []);

  return (
    <div className="space-y-6">
      <div>
        <h2 className="text-2xl font-semibold">Reportes</h2>
        <p className="mt-1 text-sm text-slate-500 dark:text-slate-400">
          Controla ingresos, pagos y saldos pendientes.
        </p>
      </div>

      {summary && (
        <div className="grid gap-4 md:grid-cols-2">
          <div className="rounded-2xl border border-indigo-200 bg-indigo-50 p-5 shadow-sm dark:border-indigo-500/30 dark:bg-indigo-500/10">
            <p className="text-xs uppercase tracking-[0.2em] text-indigo-500">Ingresos totales</p>
            <p className="mt-2 text-2xl font-semibold">${summary.totalRevenue.toFixed(2)}</p>
          </div>
          <div className="rounded-2xl border border-slate-200 bg-white p-5 shadow-sm dark:border-slate-800 dark:bg-slate-900/60">
            <p className="text-xs uppercase tracking-[0.2em] text-slate-400">Saldo pendiente</p>
            <p className="mt-2 text-2xl font-semibold">
              ${summary.outstandingBalance.toFixed(2)}
            </p>
          </div>
          <div className="rounded-2xl border border-slate-200 bg-white p-5 shadow-sm dark:border-slate-800 dark:bg-slate-900/60">
            <p className="text-xs uppercase tracking-[0.2em] text-slate-400">Reservas totales</p>
            <p className="mt-2 text-2xl font-semibold">{summary.totalReservations}</p>
          </div>
          <div className="rounded-2xl border border-slate-200 bg-white p-5 shadow-sm dark:border-slate-800 dark:bg-slate-900/60">
            <p className="text-xs uppercase tracking-[0.2em] text-slate-400">Pagos registrados</p>
            <p className="mt-2 text-2xl font-semibold">{summary.totalPayments}</p>
          </div>
        </div>
      )}
    </div>
  );
}
