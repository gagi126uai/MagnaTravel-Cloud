import { useEffect, useState } from "react";
import { apiRequest } from "../api";

export default function ReportsPage() {
  const [summary, setSummary] = useState(null);
  const [error, setError] = useState("");

  useEffect(() => {
    apiRequest("/api/reports/summary")
      .then(setSummary)
      .catch(() => setError("No se pudieron cargar los reportes."));
  }, []);

  return (
    <div>
      <h2 className="text-2xl font-semibold">Reportes</h2>
      <p className="mt-1 text-sm text-slate-400">
        Controla ingresos, pagos y saldos pendientes.
      </p>

      {error && <p className="mt-4 text-sm text-rose-400">{error}</p>}

      {summary && (
        <div className="mt-6 grid gap-4 md:grid-cols-2">
          <div className="rounded-xl border border-slate-800 bg-slate-900 p-4">
            <p className="text-xs text-slate-400">Ingresos totales</p>
            <p className="text-2xl font-semibold">${summary.totalRevenue.toFixed(2)}</p>
          </div>
          <div className="rounded-xl border border-slate-800 bg-slate-900 p-4">
            <p className="text-xs text-slate-400">Saldo pendiente</p>
            <p className="text-2xl font-semibold">
              ${summary.outstandingBalance.toFixed(2)}
            </p>
          </div>
          <div className="rounded-xl border border-slate-800 bg-slate-900 p-4">
            <p className="text-xs text-slate-400">Reservas totales</p>
            <p className="text-2xl font-semibold">{summary.totalReservations}</p>
          </div>
          <div className="rounded-xl border border-slate-800 bg-slate-900 p-4">
            <p className="text-xs text-slate-400">Pagos registrados</p>
            <p className="text-2xl font-semibold">{summary.totalPayments}</p>
          </div>
        </div>
      )}
    </div>
  );
}
