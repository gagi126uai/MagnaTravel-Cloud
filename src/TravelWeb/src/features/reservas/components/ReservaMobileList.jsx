import React from "react";
import { User, Calendar, AlertCircle, CheckCircle2, FolderOpen, Archive } from "lucide-react";
import { ListEmptyState } from "../../../components/ui/ListEmptyState";
import { MobileRecordCard, MobileRecordList } from "../../../components/ui/MobileRecordCard";
import { ReservaStatusBadge } from "./ReservaStatusBadge";
import { formatCurrency, formatDate } from "../../../lib/utils";
import { getPublicId } from "../../../lib/publicIds";
import { getReservaArchiveBlockReason } from "../archiveRules";
import { getReservaSaleLines, getReservaFinanzasChips, FINANZAS_CHIP_TONE_CLASSES } from "../lib/reservaMoneyDisplay";

/** Ícono/color del círculo de la izquierda, según el chip principal de Finanzas. */
const ACENTO_POR_TONO = {
  rojo: { icon: AlertCircle, className: "bg-rose-50 text-rose-600 dark:bg-rose-900/20 dark:text-rose-400" },
  verde: { icon: CheckCircle2, className: "bg-emerald-50 text-emerald-600 dark:bg-emerald-900/20 dark:text-emerald-400" },
  ambar: { icon: AlertCircle, className: "bg-amber-50 text-amber-600 dark:bg-amber-900/20 dark:text-amber-400" },
  gris: { icon: FolderOpen, className: "bg-indigo-50 text-indigo-600 dark:bg-indigo-900/20 dark:text-indigo-400" },
};

/**
 * Listado de Reservas en mobile (una tarjeta por reserva). Tanda 1 rediseño
 * (2026-08-04, plan B6): refleja lo mismo que la tabla de escritorio — plata por
 * moneda separada (P-3⭐), destino en vez del nombre autogenerado, y "Archivar"
 * con la palabra al lado del ícono y el motivo escrito debajo cuando está
 * bloqueado (P-9). Antes esta tarjeta no tenía acción de archivar.
 *
 * Fix B2 (review 11/08/2026): en un dispositivo TÁCTIL no hay hover — un globito
 * (`title`) ahí nunca se ve, así que en esta tarjeta el motivo sigue ESCRITO a la
 * vista, como texto debajo del botón (nunca en tooltip). Enmienda P-9 de la
 * constitución (11/08/2026): el globito en listados SOLO aplica a escritorio
 * (ReservaTable.jsx) — en táctil/mobile el criterio de 2026-08-04 sigue vigente.
 */
export function ReservaMobileList({ reservas, onRowClick, onArchive, emptyState }) {
  if (reservas.length === 0) {
    return (
      emptyState || (
        <ListEmptyState
          title="No se encontraron reservas"
          description="Probá cambiando los filtros."
          className="rounded-xl border border-dashed border-slate-200 bg-slate-50 dark:border-slate-800 dark:bg-slate-900"
        />
      )
    );
  }

  return (
    <MobileRecordList className="md:hidden">
      {reservas.map((reserva) => {
        const archiveBlockReason = getReservaArchiveBlockReason(reserva);
        const canArchive = !archiveBlockReason;
        const ventaLineas = getReservaSaleLines(reserva);
        const chips = getReservaFinanzasChips(reserva);
        const acento = ACENTO_POR_TONO[chips[0]?.tone] || ACENTO_POR_TONO.gris;
        const AcentoIcon = acento.icon;

        return (
          <MobileRecordCard
            key={getPublicId(reserva)}
            onClick={() => onRowClick(getPublicId(reserva))}
            accentSlot={
              <div className={`flex h-10 w-10 shrink-0 items-center justify-center rounded-full ${acento.className}`}>
                <AcentoIcon className="h-5 w-5" />
              </div>
            }
            statusSlot={<ReservaStatusBadge status={reserva.status} mostrarCandado />}
            title={`#${reserva.numeroReserva}`}
            subtitle={reserva.destino || null}
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
              <div className="flex flex-col items-start gap-0.5">
                {ventaLineas.map((linea, index) => (
                  <span
                    key={linea.currency}
                    className={index === 0 ? "text-xs text-slate-500" : "text-[11px] text-slate-400"}
                  >
                    {index === 0 ? "Vendido: " : ""}
                    <span className="font-medium text-slate-900 dark:text-slate-200">
                      {formatCurrency(linea.amount, linea.currency)}
                    </span>
                  </span>
                ))}
              </div>
            }
            footerActions={
              <div className="flex flex-col items-end gap-1.5">
                <div className="flex flex-col items-end gap-1">
                  {chips.map((chip, index) => (
                    <span key={index} className={FINANZAS_CHIP_TONE_CLASSES[chip.tone]}>
                      {chip.text}
                    </span>
                  ))}
                </div>
                <button
                  type="button"
                  disabled={!canArchive}
                  onClick={(event) => {
                    event.stopPropagation();
                    onArchive(reserva);
                  }}
                  className={`inline-flex items-center gap-1.5 rounded-md border px-2 py-1 text-[11px] font-semibold ${
                    canArchive
                      ? "border-slate-200 text-slate-600 hover:border-amber-300 hover:bg-amber-50 hover:text-amber-700 dark:border-slate-700 dark:text-slate-300 dark:hover:bg-amber-900/20"
                      : "border-slate-100 text-slate-400 dark:border-slate-800 dark:text-slate-600"
                  }`}
                >
                  <Archive className="h-3.5 w-3.5" />
                  Archivar
                </button>
                {/* Fix B2 (review 11/08/2026): táctil no tiene hover, así que acá el
                    motivo sigue escrito a la vista — nunca en tooltip (enmienda P-9). */}
                {archiveBlockReason ? (
                  <span className="max-w-[140px] text-right text-[10px] leading-tight text-slate-400 dark:text-slate-500">
                    {archiveBlockReason}
                  </span>
                ) : null}
              </div>
            }
          />
        );
      })}
    </MobileRecordList>
  );
}
