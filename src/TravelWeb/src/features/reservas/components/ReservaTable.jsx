import React from "react";
import { User, Users, Archive, MessageCircle, DollarSign } from "lucide-react";
import { Button } from "../../../components/ui/button";
import {
  DataGrid,
  DataGridActionCell,
  DataGridBody,
  DataGridCell,
  DataGridEmptyState,
  DataGridHeader,
  DataGridHeaderCell,
  DataGridHeaderRow,
  DataGridRow,
} from "../../../components/ui/DataGrid";
import { ReservaStatusBadge } from "./ReservaStatusBadge";
import { formatCurrency, formatDate } from "../../../lib/utils";
import { getPublicId } from "../../../lib/publicIds";
import { getReservaArchiveBlockReason } from "../archiveRules";

export function ReservaTable({ reservas, onRowClick, onArchive }) {
  return (
    <DataGrid minWidth="920px">
      <DataGridHeader>
        <DataGridHeaderRow>
          <DataGridHeaderCell>Reserva</DataGridHeaderCell>
          <DataGridHeaderCell>Cliente / pasajeros</DataGridHeaderCell>
          <DataGridHeaderCell>Estado</DataGridHeaderCell>
          <DataGridHeaderCell>Creada</DataGridHeaderCell>
          <DataGridHeaderCell align="right">Finanzas</DataGridHeaderCell>
          <DataGridHeaderCell align="center">Acciones</DataGridHeaderCell>
        </DataGridHeaderRow>
      </DataGridHeader>
      <DataGridBody>
        {reservas.length === 0 ? (
          <DataGridEmptyState
            colSpan={6}
            icon={Archive}
            title="No se encontraron reservas"
            description="Intenta ajustar los filtros de busqueda."
          />
        ) : (
          reservas.map((reserva) => {
            const archiveBlockReason = getReservaArchiveBlockReason(reserva);
            const canArchive = !archiveBlockReason;

            return (
              <DataGridRow
                key={getPublicId(reserva)}
                clickable
                onClick={() => onRowClick(getPublicId(reserva))}
              >
                <DataGridCell>
                  <div className="flex flex-col">
                    <span className="text-sm font-bold text-slate-900 transition-colors hover:text-indigo-600 dark:text-white dark:hover:text-indigo-400">
                      #{reserva.numeroReserva}
                    </span>
                    <span className="mt-0.5 line-clamp-1 text-xs text-slate-500 dark:text-slate-400">{reserva.name}</span>
                    {reserva.startDate ? (
                      <span className="mt-1 flex items-center gap-1 text-[10px] font-medium text-indigo-500 dark:text-indigo-400">
                        <span className="h-1 w-1 rounded-full bg-indigo-400" />
                        Viaja: {formatDate(reserva.startDate)}
                      </span>
                    ) : null}
                  </div>
                </DataGridCell>
                <DataGridCell>
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
                </DataGridCell>
                <DataGridCell>
                  <ReservaStatusBadge status={reserva.status} />
                </DataGridCell>
                <DataGridCell>
                  <div className="flex flex-col">
                    <span className="text-xs text-slate-600 dark:text-slate-300">
                      {reserva.createdAt ? formatDate(reserva.createdAt) : "-"}
                    </span>
                    {reserva.responsibleUserName ? (
                      <span className="mt-0.5 text-[10px] text-slate-400 dark:text-slate-500">
                        {reserva.responsibleUserName}
                      </span>
                    ) : null}
                  </div>
                </DataGridCell>
                <DataGridCell align="right">
                  <div className="flex flex-col items-end gap-1">
                    <span className="text-sm font-bold text-slate-900 dark:text-white">{formatCurrency(reserva.totalSale)}</span>
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
                </DataGridCell>
                <DataGridActionCell
                  align="center"
                  onClick={(event) => event.stopPropagation()}
                >
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
                    disabled={!canArchive}
                    className={`h-8 w-8 ${
                      canArchive
                        ? "text-slate-400 hover:bg-amber-50 hover:text-amber-600 dark:hover:bg-amber-900/20"
                        : "text-slate-300 dark:text-slate-700"
                    }`}
                    onClick={() => onArchive(reserva)}
                    title={archiveBlockReason || "Archivar"}
                  >
                    <Archive className="h-4 w-4" />
                  </Button>
                </DataGridActionCell>
              </DataGridRow>
            );
          })
        )}
      </DataGridBody>
    </DataGrid>
  );
}
