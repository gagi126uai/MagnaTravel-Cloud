import React from "react";
import { User, Calendar, AlertCircle, CheckCircle2, FolderOpen } from "lucide-react";
import { ListEmptyState } from "../../../components/ui/ListEmptyState";
import { MobileRecordCard, MobileRecordList } from "../../../components/ui/MobileRecordCard";
import { ReservaStatusBadge } from "./ReservaStatusBadge";
import { formatCurrency, formatDate } from "../../../lib/utils";
import { getPublicId } from "../../../lib/publicIds";

export function ReservaMobileList({ reservas, onRowClick }) {
  if (reservas.length === 0) {
    return (
      <ListEmptyState
        title="No se encontraron reservas"
        description="Intenta ajustar los filtros de busqueda."
        className="rounded-xl border border-dashed border-slate-200 bg-slate-50 dark:border-slate-800 dark:bg-slate-900"
      />
    );
  }

  return (
    <MobileRecordList className="md:hidden">
      {reservas.map((reserva) => {
        const hasPendingBalance = reserva.balance > 0;
        const isPaid = reserva.totalSale > 0 && reserva.balance <= 0;

        return (
          <MobileRecordCard
            key={getPublicId(reserva)}
            onClick={() => onRowClick(getPublicId(reserva))}
            accentSlot={
              <div
                className={`flex h-10 w-10 shrink-0 items-center justify-center rounded-full ${
                  hasPendingBalance
                    ? "bg-rose-50 text-rose-600 dark:bg-rose-900/20 dark:text-rose-400"
                    : isPaid
                      ? "bg-emerald-50 text-emerald-600 dark:bg-emerald-900/20 dark:text-emerald-400"
                      : "bg-indigo-50 text-indigo-600 dark:bg-indigo-900/20 dark:text-indigo-400"
                }`}
              >
                {hasPendingBalance ? <AlertCircle className="h-5 w-5" /> : isPaid ? <CheckCircle2 className="h-5 w-5" /> : <FolderOpen className="h-5 w-5" />}
              </div>
            }
            statusSlot={<ReservaStatusBadge status={reserva.status} />}
            title={reserva.name}
            subtitle={`#${reserva.numeroReserva}`}
            meta={
              <>
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
              </>
            }
            footer={
              <div className="text-xs text-slate-500">
                Venta: <span className="font-medium text-slate-900 dark:text-slate-200">{formatCurrency(reserva.totalSale)}</span>
              </div>
            }
            footerActions={
              <span className={`text-sm font-bold ${hasPendingBalance ? "text-rose-600 dark:text-rose-400" : "text-emerald-600 dark:text-emerald-400"}`}>
                {hasPendingBalance ? `Saldo: ${formatCurrency(reserva.balance)}` : "Pagado"}
              </span>
            }
          />
        );
      })}
    </MobileRecordList>
  );
}
