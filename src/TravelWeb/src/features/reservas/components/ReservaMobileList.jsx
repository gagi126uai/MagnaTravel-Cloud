import React from "react";
import { User, Calendar, AlertCircle, CheckCircle2, FolderOpen } from "lucide-react";
import { ReservaStatusBadge } from "./ReservaStatusBadge";
import { formatCurrency, formatDate } from "../../../lib/utils";
import { getPublicId } from "../../../lib/publicIds";

export function ReservaMobileList({ reservas, onRowClick }) {
  if (reservas.length === 0) {
    return (
      <div className="rounded-xl border border-dashed border-slate-200 bg-slate-50 py-12 text-center dark:border-slate-800 dark:bg-slate-900">
        <p className="text-sm text-muted-foreground">No se encontraron reservas.</p>
      </div>
    );
  }

  return (
    <div className="space-y-3">
      {reservas.map((reserva) => {
        const hasPendingBalance = reserva.balance > 0;
        const isPaid = reserva.totalSale > 0 && reserva.balance <= 0;

        return (
          <div
            key={getPublicId(reserva)}
            onClick={() => onRowClick(getPublicId(reserva))}
            className="rounded-xl border border-slate-200 bg-white p-4 shadow-sm transition-transform active:scale-[0.98] dark:border-slate-800 dark:bg-slate-900"
          >
            <div className="mb-3 flex items-start justify-between">
              <div className="flex items-center gap-3">
                <div
                  className={`flex h-10 w-10 shrink-0 items-center justify-center rounded-full ${
                    hasPendingBalance
                      ? "bg-rose-50 text-rose-600 dark:bg-rose-900/20 dark:text-rose-400"
                      : isPaid
                        ? "bg-emerald-50 text-emerald-600 dark:bg-emerald-900/20 dark:text-emerald-400"
                        : "bg-indigo-50 text-indigo-600 dark:bg-indigo-900/20 dark:text-indigo-400"
                  }`}
                >
                  {hasPendingBalance ? (
                    <AlertCircle className="h-5 w-5" />
                  ) : isPaid ? (
                    <CheckCircle2 className="h-5 w-5" />
                  ) : (
                    <FolderOpen className="h-5 w-5" />
                  )}
                </div>
                <div>
                  <div className="leading-tight font-semibold text-slate-900 dark:text-white">{reserva.name}</div>
                  <div className="mt-1 flex items-center gap-2">
                    <span className="font-mono text-xs text-slate-500">#{reserva.numeroReserva}</span>
                  </div>
                </div>
              </div>
              <ReservaStatusBadge status={reserva.status} />
            </div>

            <div className="mb-3 grid grid-cols-2 gap-x-4 gap-y-2 text-sm">
              <div className="flex items-center gap-2 font-medium text-slate-600 dark:text-slate-400">
                <User className="h-3.5 w-3.5 opacity-70" />
                <span className="truncate">{reserva.customerName || "Sin asignar"}</span>
              </div>
              <div>
                {reserva.startDate ? (
                  <div className="flex items-center gap-1.5 text-xs font-medium">
                    <Calendar className="h-3.5 w-3.5 text-indigo-500 opacity-60" />
                    {formatDate(reserva.startDate)}
                  </div>
                ) : (
                  <span className="text-xs text-slate-400">-</span>
                )}
              </div>
            </div>

            <div className="flex items-center justify-between border-t border-slate-100 pt-3 dark:border-slate-800">
              <div className="text-xs text-slate-500">
                Venta: <span className="font-medium text-slate-900 dark:text-slate-200">{formatCurrency(reserva.totalSale)}</span>
              </div>
              <span className={`text-sm font-bold ${hasPendingBalance ? "text-rose-600 dark:text-rose-400" : "text-emerald-600 dark:text-emerald-400"}`}>
                {hasPendingBalance ? `Saldo: ${formatCurrency(reserva.balance)}` : "Pagado"}
              </span>
            </div>
          </div>
        );
      })}
    </div>
  );
}
