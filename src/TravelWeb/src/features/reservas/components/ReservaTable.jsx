import React from "react";
import { User, Users, Archive } from "lucide-react";
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
import { getReservaSaleLines, getReservaFinanzasChips, FINANZAS_CHIP_TONE_CLASSES } from "../lib/reservaMoneyDisplay";

/**
 * Tabla del listado de Reservas (versión de escritorio). Tanda 1 rediseño
 * (2026-08-04, plan B4): el renglón chico bajo el número de reserva pasa a
 * mostrar el DESTINO en vez del nombre autogenerado ("Reserva F-2026-…"), la
 * columna Finanzas separa cada moneda en su propia línea (P-3⭐) y la única
 * acción por fila es "Archivar" con la palabra al lado del ícono y, si está
 * bloqueada, el motivo del motor escrito debajo (P-9/P-10/P-13⭐) — se elimina
 * el botón de chat, que solo repetía lo que ya hace un clic en la fila.
 *
 * `emptyState`: nodo opcional que reemplaza el cartel por default cuando no hay
 * filas. ReservasPage arma un mensaje distinto según el motivo (mes sin datos,
 * búsqueda sin resultados) porque solo esa pantalla tiene los datos del filtro
 * activo (mes, período) para escribir un mensaje útil.
 */
export function ReservaTable({ reservas, onRowClick, onArchive, emptyState }) {
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
          emptyState ? (
            <tr>
              <td colSpan={6} className="p-0">
                {emptyState}
              </td>
            </tr>
          ) : (
            <DataGridEmptyState
              colSpan={6}
              icon={Archive}
              title="No se encontraron reservas"
              description="Probá cambiando los filtros."
            />
          )
        ) : (
          reservas.map((reserva) => {
            const archiveBlockReason = getReservaArchiveBlockReason(reserva);
            const canArchive = !archiveBlockReason;
            const ventaLineas = getReservaSaleLines(reserva);
            const chips = getReservaFinanzasChips(reserva);

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
                    {reserva.destino ? (
                      <span className="mt-0.5 line-clamp-1 text-xs text-slate-500 dark:text-slate-400">
                        {reserva.destino}
                      </span>
                    ) : null}
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
                  <ReservaStatusBadge status={reserva.status} mostrarCandado />
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
                    <div className="flex flex-col items-end">
                      {ventaLineas.map((linea, index) => {
                        const enCero = Number(linea.amount) === 0;
                        return (
                          <span
                            key={linea.currency}
                            className={
                              index === 0
                                ? `text-sm font-bold ${enCero ? "text-slate-300 dark:text-slate-700" : "text-slate-900 dark:text-white"}`
                                : `text-xs font-semibold ${enCero ? "text-slate-300 dark:text-slate-700" : "text-slate-500 dark:text-slate-400"}`
                            }
                          >
                            {formatCurrency(linea.amount, linea.currency)}
                          </span>
                        );
                      })}
                    </div>
                    <div className="flex flex-col items-end gap-1">
                      {chips.map((chip, index) => (
                        <span key={index} className={FINANZAS_CHIP_TONE_CLASSES[chip.tone]}>
                          {chip.text}
                        </span>
                      ))}
                    </div>
                  </div>
                </DataGridCell>
                <DataGridActionCell
                  align="center"
                  onClick={(event) => event.stopPropagation()}
                >
                  <div className="flex flex-col items-center gap-1">
                    <Button
                      variant="outline"
                      size="sm"
                      disabled={!canArchive}
                      onClick={() => onArchive(reserva)}
                      className={`h-7 gap-1.5 px-2.5 text-xs font-semibold ${
                        canArchive
                          ? "text-slate-600 hover:border-amber-300 hover:bg-amber-50 hover:text-amber-700 dark:text-slate-300 dark:hover:bg-amber-900/20"
                          : "text-slate-400 dark:text-slate-600"
                      }`}
                    >
                      <Archive className="h-3.5 w-3.5" />
                      Archivar
                    </Button>
                    {archiveBlockReason ? (
                      // P-9/P-13⭐: el motivo va escrito debajo del botón, tal cual lo
                      // manda el motor — nunca escondido en un tooltip.
                      <span className="max-w-[130px] text-center text-[10px] leading-tight text-slate-400 dark:text-slate-500">
                        {archiveBlockReason}
                      </span>
                    ) : null}
                  </div>
                </DataGridActionCell>
              </DataGridRow>
            );
          })
        )}
      </DataGridBody>
    </DataGrid>
  );
}
