import React from 'react';
import { ArrowLeft, Trash2, Archive, AlertTriangle, Undo2, Calendar, Pencil } from "lucide-react";
import { getReservaArchiveBlockReason } from "../archiveRules";
import { getStatusConfig, translateStatus } from "./ReservaStatusBadge";
import { ReservaStatusChips } from "./ReservaStatusChips";

function formatTripDate(value) {
    if (!value) return null;
    try {
        const d = new Date(value);
        if (isNaN(d.getTime())) return null;
        return d.toLocaleDateString("es-AR", { day: "2-digit", month: "2-digit", year: "numeric" });
    } catch {
        return null;
    }
}

export function ReservaHeader({ reserva, onBack, onStatusChange, onDelete, onArchive, onRevert, onEditDates }) {
    const isArchived = reserva.status === 'Archived';
    const canDelete = (reserva.status === 'Budget' || reserva.status === 'Confirmed');
    const archiveBlockReason = getReservaArchiveBlockReason(reserva);
    const canArchive = !archiveBlockReason;
    // Estados desde los que se puede revertir hacia atras (con autorizacion si no es admin).
    const canRevert = ['Confirmed', 'Traveling', 'Closed'].includes(reserva.status);
    // Las fechas se pueden editar en estados activos (no archivada/cancelada).
    const canEditDates = !isArchived && reserva.status !== 'Cancelled';
    const startLabel = formatTripDate(reserva.startDate);
    const endLabel = formatTripDate(reserva.endDate);

    // El boton "Cerrar reserva" solo se muestra cuando el viaje ya termino.
    // Disabled si quedo saldo pendiente (regla unificada con el job auto).
    const today = new Date();
    today.setHours(0, 0, 0, 0);
    const endHasPast = reserva.endDate ? new Date(reserva.endDate) < today : false;
    const canClose = endHasPast && reserva.balance <= 0;
    const closeTooltip = !endHasPast
        ? "El viaje todavia no termino"
        : reserva.balance > 0
            ? "No se puede cerrar con saldo pendiente"
            : "Cerrar reserva";

    const statusCfg = getStatusConfig(reserva.status);

    return (
        <div className="mb-8 flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4">
            <div>
                <button
                    onClick={onBack}
                    className="flex items-center text-slate-500 hover:text-slate-700 dark:text-slate-400 dark:hover:text-slate-200 mb-2 transition-colors font-medium text-sm"
                >
                    <ArrowLeft className="w-4 h-4 mr-1.5" /> Volver a Lista
                </button>
                <div className="flex items-center gap-3 flex-wrap">
                    <h1 className="text-3xl font-extrabold text-slate-900 dark:text-white tracking-tight">
                        Reserva <span className="text-indigo-600 dark:text-indigo-400">#{reserva.numeroReserva}</span>
                    </h1>
                    <span className={`px-3 py-1 rounded-full text-xs font-bold uppercase tracking-wider border ${statusCfg.color}`}>
                        {translateStatus(reserva.status)}
                    </span>
                    <ReservaStatusChips reserva={reserva} />
                </div>
                <p className="text-xl text-slate-900 dark:text-white mt-2 font-bold flex items-center gap-2">
                    {reserva.customerName}
                </p>
                {reserva.name && reserva.name !== `Reserva ${reserva.numeroReserva}` && (
                    <p className="text-lg text-slate-500 dark:text-slate-400 font-medium italic">{reserva.name}</p>
                )}

                {/* Fechas del viaje. Visibles en cualquier estado; editables si la
                    reserva no esta archivada/cancelada. Si todavia no hay fechas
                    cargadas mostramos un CTA para que el operador las complete. */}
                <div className="mt-3 flex items-center gap-3 flex-wrap">
                    <div className="inline-flex items-center gap-2 rounded-xl border border-slate-200 bg-white px-3 py-1.5 text-sm dark:border-slate-800 dark:bg-slate-900">
                        <Calendar className="w-4 h-4 text-indigo-500" />
                        <span className="font-medium text-slate-500 dark:text-slate-400">Salida:</span>
                        <span className={startLabel ? "font-bold text-slate-900 dark:text-white" : "italic text-slate-400"}>
                            {startLabel || "sin cargar"}
                        </span>
                        <span className="text-slate-300 dark:text-slate-700">·</span>
                        <span className="font-medium text-slate-500 dark:text-slate-400">Regreso:</span>
                        <span className={endLabel ? "font-bold text-slate-900 dark:text-white" : "italic text-slate-400"}>
                            {endLabel || "sin cargar"}
                        </span>
                    </div>
                    {canEditDates && onEditDates && (
                        <button
                            onClick={onEditDates}
                            type="button"
                            className="inline-flex items-center gap-1.5 rounded-lg border border-slate-200 bg-white px-2.5 py-1.5 text-xs font-semibold text-slate-700 hover:bg-slate-50 hover:border-slate-300 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-200 dark:hover:bg-slate-800"
                            title="Editar fechas del viaje"
                        >
                            <Pencil className="w-3.5 h-3.5" />
                            Editar fechas
                        </button>
                    )}
                </div>
            </div>

            {isArchived ? (
                <div className="flex items-center gap-2 px-4 py-3 bg-slate-100 dark:bg-slate-800 border border-slate-200 dark:border-slate-700 rounded-xl">
                    <AlertTriangle className="w-4 h-4 text-slate-500" />
                    <span className="text-sm font-medium text-slate-600 dark:text-slate-400">Solo lectura — Reserva archivada</span>
                </div>
            ) : (
                <div className="flex flex-wrap gap-3">
                    {/* STATUS ACTIONS */}
                    {reserva.status === 'Budget' && (
                        <button onClick={() => onStatusChange('Confirmed')} className="bg-indigo-600 hover:bg-indigo-700 text-white px-5 py-2.5 rounded-xl font-bold text-sm shadow-lg shadow-indigo-200 dark:shadow-none transition-all active:scale-95">
                            Confirmar Reserva
                        </button>
                    )}
                    {reserva.status === 'Confirmed' && (
                        <button
                            onClick={() => onStatusChange('Traveling')}
                            className="bg-emerald-600 hover:bg-emerald-700 text-white px-5 py-2.5 rounded-xl font-bold text-sm shadow-lg shadow-emerald-200 dark:shadow-none transition-all active:scale-95"
                            title="Marcar como en viaje (la transicion automatica ocurre cuando llega la fecha de salida)"
                        >
                            Marcar en viaje
                        </button>
                    )}
                    {reserva.status === 'Traveling' && endHasPast && (
                        <button
                            onClick={() => onStatusChange('Closed')}
                            disabled={!canClose}
                            className={`px-5 py-2.5 rounded-xl font-bold text-sm shadow-lg transition-all active:scale-95 ${canClose ? 'bg-slate-900 dark:bg-white dark:text-slate-900 text-white' : 'bg-slate-300 dark:bg-slate-700 text-slate-500 cursor-not-allowed shadow-none'}`}
                            title={closeTooltip}
                        >
                            Cerrar reserva
                        </button>
                    )}

                    {/* ADMIN ACTIONS */}
                    <div className="flex gap-2 ml-2 pl-4 border-l border-slate-200 dark:border-slate-800">
                        {canRevert && onRevert && (
                            <button onClick={onRevert} className="p-2.5 bg-amber-50 text-amber-700 hover:bg-amber-100 dark:bg-amber-900/20 dark:text-amber-300 rounded-xl transition-colors" title="Revertir estado (requiere autorizacion si no sos admin)">
                                <Undo2 className="w-5 h-5" />
                            </button>
                        )}
                        {canDelete && (
                            <button onClick={onDelete} className="p-2.5 bg-rose-50 text-rose-600 hover:bg-rose-100 dark:bg-rose-900/20 dark:text-rose-400 rounded-xl transition-colors" title="Eliminar Reserva">
                                <Trash2 className="w-5 h-5" />
                            </button>
                        )}
                        <button
                            onClick={canArchive ? onArchive : undefined}
                            disabled={!canArchive}
                            className={`p-2.5 rounded-xl transition-colors ${canArchive ? 'bg-slate-100 text-slate-600 hover:bg-slate-200 dark:bg-slate-800 dark:text-slate-400 dark:hover:bg-slate-700' : 'bg-slate-50 text-slate-300 dark:bg-slate-900 dark:text-slate-700 cursor-not-allowed'}`}
                            title={archiveBlockReason || "Archivar"}
                        >
                            <Archive className="w-5 h-5" />
                        </button>
                    </div>
                </div>
            )}
        </div>
    );
}
