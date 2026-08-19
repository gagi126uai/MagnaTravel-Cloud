import { Link } from "react-router-dom";
import { Plane } from "lucide-react";
import { getPublicId } from "../../../lib/publicIds";
import { formatCurrency } from "../../../lib/utils";
import { FINANZAS_CHIP_TONE_CLASSES } from "../../reservas/lib/reservaMoneyDisplay";
import { agruparSalidasPorDia, armarChipDeudaSalida } from "../lib/upcomingTripsGrouping";

/**
 * Tarjeta "Salidas de los próximos 7 días" del dashboard (spec dashboard
 * 2026-08-18, sección 1.3, columna TRABAJO). El backend ya recorta la lista a
 * "mi cartera" cuando el usuario no tiene `reservas.view_all` — acá solo se
 * agrupa por día y se arma el chip de deuda de cada fila (R4, YA resuelto).
 */
export function UpcomingTripsCard({ proximosViajes }) {
  const grupos = agruparSalidasPorDia(proximosViajes);

  return (
    <div className="rounded-[14px] border border-slate-200 bg-white shadow-sm dark:border-slate-800 dark:bg-slate-900">
      <div className="flex items-center gap-2 border-b border-slate-100 px-5 py-4 dark:border-slate-800">
        <Plane className="h-4 w-4 text-slate-400" aria-hidden="true" />
        <h2 className="text-sm font-bold text-slate-900 dark:text-white">Salidas de los próximos 7 días</h2>
      </div>

      {grupos.length === 0 ? (
        <p className="px-5 py-8 text-center text-sm text-slate-400">No hay salidas esta semana.</p>
      ) : (
        <div className="divide-y divide-slate-100 dark:divide-slate-800">
          {grupos.map((grupo) => (
            <div key={grupo.clave}>
              <p className="px-5 pt-3 text-[11px] font-bold uppercase tracking-wide text-slate-400">
                {grupo.etiqueta}
              </p>
              {grupo.viajes.map((viaje) => (
                <FilaSalida key={getPublicId(viaje)} viaje={viaje} />
              ))}
            </div>
          ))}
        </div>
      )}

      <div className="border-t border-slate-100 px-5 py-3 text-right dark:border-slate-800">
        <Link to="/reservas" className="text-[13px] font-semibold text-primary hover:underline">
          Ver todas →
        </Link>
      </div>
    </div>
  );
}

function FilaSalida({ viaje }) {
  const chip = armarChipDeudaSalida(viaje.pendingBalances);

  return (
    <Link
      to={`/reservas/${getPublicId(viaje)}`}
      className="flex items-center justify-between gap-3 px-5 py-3 transition-colors hover:bg-slate-50 dark:hover:bg-slate-800/50"
    >
      <div className="min-w-0">
        <p className="truncate text-sm font-semibold text-slate-900 dark:text-white">{viaje.name}</p>
        <p className="text-xs text-slate-400">
          {viaje.numeroReserva}
          {typeof viaje.paxCount === "number" ? ` · ${viaje.paxCount} pax` : ""}
        </p>
      </div>

      {chip.tone === "success" ? (
        <span className={FINANZAS_CHIP_TONE_CLASSES.verde}>Saldada</span>
      ) : (
        <div className="flex flex-col items-end gap-1">
          {chip.lineas.map((linea) => (
            <span key={linea.currency} className={FINANZAS_CHIP_TONE_CLASSES.rojo}>
              Debe {formatCurrency(linea.amount, linea.currency)}
            </span>
          ))}
        </div>
      )}
    </Link>
  );
}
