import { useNavigate, Link } from "react-router-dom";
import { AlertCircle, DollarSign } from "lucide-react";
import { Button } from "../../../components/ui/button";
import { formatCurrency } from "../../../lib/utils";
import { agruparCobrosPendientesPorReserva } from "../lib/pendingCollectionsGrouping";

/**
 * Tarjeta "Cobros pendientes" del dashboard (spec dashboard 2026-08-18,
 * sección 1.3, columna TRABAJO). El botón "Cobrar" navega a la ficha de la
 * reserva — MISMO destino que ya usan el resto de las pantallas de cobranza
 * (`CollectionsTab.jsx`, `EstadoCuentaClienteTab.jsx`): no existe un deep-link
 * a una solapa puntual de la ficha todavía, así que no se inventa uno acá
 * (P-5, nada de modal nuevo tampoco).
 */
export function PendingCollectionsCard({ reservasPendientes }) {
  const navigate = useNavigate();
  const grupos = agruparCobrosPendientesPorReserva(reservasPendientes);

  return (
    <div className="rounded-[14px] border border-slate-200 bg-white shadow-sm dark:border-slate-800 dark:bg-slate-900">
      <div className="flex items-center gap-2 border-b border-slate-100 px-5 py-4 dark:border-slate-800">
        <AlertCircle className="h-4 w-4 text-slate-400" aria-hidden="true" />
        <h2 className="text-sm font-bold text-slate-900 dark:text-white">Cobros pendientes</h2>
      </div>

      {grupos.length === 0 ? (
        <p className="px-5 py-8 text-center text-sm text-slate-400">No hay cobros pendientes.</p>
      ) : (
        <div className="divide-y divide-slate-100 dark:divide-slate-800">
          {grupos.map((grupo) => (
            <div key={grupo.publicId} className="flex items-center justify-between gap-3 px-5 py-3">
              <div className="min-w-0">
                <p className="truncate text-sm font-semibold text-slate-900 dark:text-white">{grupo.name}</p>
                <p className="text-xs text-slate-400">{grupo.numeroReserva}</p>
              </div>
              <div className="flex items-center gap-3">
                <div className="flex flex-col items-end">
                  {grupo.lineas.map((linea) => (
                    <span key={linea.currency} className="text-sm font-bold text-red-700">
                      {formatCurrency(linea.amount, linea.currency)}
                    </span>
                  ))}
                </div>
                <Button
                  type="button"
                  variant="outline"
                  size="sm"
                  onClick={() => navigate(`/reservas/${grupo.publicId}`)}
                >
                  <DollarSign className="h-4 w-4" aria-hidden="true" />
                  Cobrar
                </Button>
              </div>
            </div>
          ))}
        </div>
      )}

      <div className="border-t border-slate-100 px-5 py-3 text-right dark:border-slate-800">
        <Link to="/payments" className="text-[13px] font-semibold text-primary hover:underline">
          Ir a Cobranzas →
        </Link>
      </div>
    </div>
  );
}
