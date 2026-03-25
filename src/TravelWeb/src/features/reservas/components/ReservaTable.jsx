import React from "react";
import { User, Users, Archive, MessageCircle, DollarSign } from "lucide-react";
import { Button } from "../../../components/ui/button";
import { ReservaStatusBadge } from "./ReservaStatusBadge";
import { formatCurrency, formatDate } from "../../../lib/utils";
import { getPublicId } from "../../../lib/publicIds";

export function ReservaTable({ reservas, onRowClick, onArchive }) {
  return (
    <div className="overflow-hidden rounded-xl border border-slate-200 bg-white shadow-sm dark:border-slate-800 dark:bg-slate-900/50">
      <div className="overflow-x-auto">
        <table className="w-full border-collapse text-left">
          <thead>
            <tr className="border-b border-slate-100 bg-slate-50/50 dark:border-slate-800 dark:bg-slate-800/30">
              <th className="px-4 py-3 text-[11px] font-semibold uppercase tracking-wider text-slate-500 dark:text-slate-400">Reserva</th>
              <th className="px-4 py-3 text-[11px] font-semibold uppercase tracking-wider text-slate-500 dark:text-slate-400">Cliente / Pasajeros</th>
              <th className="px-4 py-3 text-[11px] font-semibold uppercase tracking-wider text-slate-500 dark:text-slate-400">Estado</th>
              <th className="px-4 py-3 text-right text-[11px] font-semibold uppercase tracking-wider text-slate-500 dark:text-slate-400">Finanzas</th>
              <th className="px-4 py-3 text-center text-[11px] font-semibold uppercase tracking-wider text-slate-500 dark:text-slate-400">Acciones</th>
            </tr>
          </thead>
          <tbody className="divide-y divide-slate-100 dark:divide-slate-800">
            {reservas.map((reserva) => (
              <tr
                key={getPublicId(reserva)}
                onClick={() => onRowClick(getPublicId(reserva))}
                className="cursor-pointer transition-all hover:bg-slate-50/80 dark:hover:bg-slate-800/40"
              >
                <td className="px-4 py-4">
                  <div className="flex flex-col">
                    <span className="text-sm font-bold text-slate-900 transition-colors hover:text-indigo-600 dark:text-white dark:hover:text-indigo-400">
                      #{reserva.numeroReserva}
                    </span>
                    <span className="mt-0.5 line-clamp-1 text-xs text-slate-500 dark:text-slate-400">
                      {reserva.name}
                    </span>
                    {reserva.startDate && (
                      <span className="mt-1 flex items-center gap-1 text-[10px] font-medium text-indigo-500 dark:text-indigo-400">
                        <span className="h-1 w-1 rounded-full bg-indigo-400" />
                        Viaja: {formatDate(reserva.startDate)}
                      </span>
                    )}
                  </div>
                </td>
                <td className="px-4 py-4">
                  <div className="flex flex-col gap-1.5">
                    <div className="flex items-center gap-2 text-sm text-slate-700 dark:text-slate-300">
                      <User className="h-3.5 w-3.5 text-slate-400" />
                      <span className="max-w-[180px] truncate font-medium">{reserva.customerName}</span>
                    </div>
                    <div className="flex items-center gap-2 text-[10px] text-slate-500 dark:text-slate-400">
                      <Users className="h-3.5 w-3.5" />
                      <span>{reserva.passengerCount || 0} pax</span>
                    </div>
                  </div>
                </td>
                <td className="px-4 py-4">
                  <ReservaStatusBadge status={reserva.status} />
                </td>
                <td className="px-4 py-4 text-right">
                  <div className="flex flex-col items-end gap-1">
                    <span className="text-sm font-bold text-slate-900 dark:text-white">
                      {formatCurrency(reserva.totalSale)}
                    </span>
                    {reserva.balance > 0 ? (
                      <div className="flex items-center gap-1 rounded bg-rose-50 px-1.5 py-0.5 text-[10px] font-semibold text-rose-600 dark:bg-rose-900/20 dark:text-rose-400">
                        <DollarSign className="h-2.5 w-2.5" />
                        Debe: {formatCurrency(reserva.balance)}
                      </div>
                    ) : (
                      <span className="rounded bg-emerald-50 px-1.5 py-0.5 text-[10px] font-semibold text-emerald-600 dark:bg-emerald-900/20 dark:text-emerald-400">
                        Saldado
                      </span>
                    )}
                  </div>
                </td>
                <td
                  className="px-4 py-4"
                  onClick={(event) => event.stopPropagation()}
                >
                  <div className="flex items-center justify-center gap-1">
                    <Button
                      variant="ghost"
                      size="icon"
                      className="h-8 w-8 text-slate-400 hover:bg-indigo-50 hover:text-indigo-600 dark:hover:bg-indigo-900/20"
                      onClick={() => onRowClick(getPublicId(reserva))}
                      title="Ver detalles"
                    >
                      <MessageCircle className="h-4 w-4" />
                    </Button>
                    <Button
                      variant="ghost"
                      size="icon"
                      className="h-8 w-8 text-slate-400 hover:bg-rose-50 hover:text-rose-600 dark:hover:bg-rose-900/20"
                      onClick={() => onArchive(getPublicId(reserva))}
                      title="Archivar"
                    >
                      <Archive className="h-4 w-4" />
                    </Button>
                  </div>
                </td>
              </tr>
            ))}
            {reservas.length === 0 && (
              <tr>
                <td colSpan="5" className="px-4 py-12 text-center">
                  <div className="flex flex-col items-center justify-center text-slate-400 dark:text-slate-600">
                    <Archive className="mb-3 h-12 w-12 opacity-20" />
                    <p className="text-sm font-medium">No se encontraron reservas</p>
                    <p className="text-xs">Intenta ajustar los filtros de busqueda</p>
                  </div>
                </td>
              </tr>
            )}
          </tbody>
        </table>
      </div>
    </div>
  );
}
