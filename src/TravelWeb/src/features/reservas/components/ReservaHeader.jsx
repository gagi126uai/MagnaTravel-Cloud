import React from 'react';
import { ArrowLeft, Trash2, Archive, AlertTriangle } from "lucide-react";

export function ReservaHeader({ reserva, onBack, onStatusChange, onDelete, onArchive }) {
    const isArchived = reserva.status === 'Archived';
    const canDelete = (reserva.status === 'Presupuesto' || reserva.status === 'Reservado');
    const canArchive = (reserva.status === 'Operativo' || reserva.status === 'Cerrado') && reserva.balance <= 0;

    return (
        <div className="mb-8 flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4">
            <div>
                <button
                    onClick={onBack}
                    className="flex items-center text-slate-500 hover:text-slate-700 dark:text-slate-400 dark:hover:text-slate-200 mb-2 transition-colors font-medium text-sm"
                >
                    <ArrowLeft className="w-4 h-4 mr-1.5" /> Volver a Lista
                </button>
                <div className="flex items-center gap-3">
                    <h1 className="text-3xl font-extrabold text-slate-900 dark:text-white tracking-tight">
                        Reserva <span className="text-indigo-600 dark:text-indigo-400">#{reserva.numeroReserva}</span>
                    </h1>
                    <span className={`px-3 py-1 rounded-full text-xs font-bold uppercase tracking-wider border
                ${reserva.status === 'Presupuesto' ? 'bg-blue-50 text-blue-700 border-blue-200 dark:bg-blue-900/30 dark:text-blue-300 dark:border-blue-800' :
                            reserva.status === 'Reservado' ? 'bg-amber-50 text-amber-700 border-amber-200 dark:bg-amber-900/30 dark:text-amber-300 dark:border-amber-800' :
                                reserva.status === 'Operativo' ? 'bg-emerald-50 text-emerald-700 border-emerald-200 dark:bg-emerald-900/30 dark:text-emerald-300 dark:border-emerald-800' :
                                    reserva.status === 'Archived' ? 'bg-slate-100 text-slate-500 border-slate-300 dark:bg-slate-800 dark:text-slate-400 dark:border-slate-700' :
                                        reserva.status === 'Cerrado' ? 'bg-slate-100 text-slate-600 border-slate-200 dark:bg-slate-800 dark:text-slate-400 dark:border-slate-700' : 'bg-rose-50 text-rose-700 border-rose-200 dark:bg-rose-900/30 dark:text-rose-300 dark:border-rose-800'}`}>
                        {reserva.status === 'Archived' ? 'Archivada' : reserva.status}
                    </span>
                </div>
                <p className="text-xl text-slate-900 dark:text-white mt-2 font-bold flex items-center gap-2">
                    {reserva.customerName}
                </p>
                <p className="text-lg text-slate-500 dark:text-slate-400 font-medium italic">{reserva.name}</p>
            </div>

            {isArchived ? (
                <div className="flex items-center gap-2 px-4 py-3 bg-slate-100 dark:bg-slate-800 border border-slate-200 dark:border-slate-700 rounded-xl">
                    <AlertTriangle className="w-4 h-4 text-slate-500" />
                    <span className="text-sm font-medium text-slate-600 dark:text-slate-400">Solo lectura — Reserva archivada</span>
                </div>
            ) : (
                <div className="flex flex-wrap gap-3">
                    {/* STATUS ACTIONS */}
                    {reserva.status === 'Presupuesto' && (
                        <button onClick={() => onStatusChange('Reservado')} className="bg-indigo-600 hover:bg-indigo-700 text-white px-5 py-2.5 rounded-xl font-bold text-sm shadow-lg shadow-indigo-200 dark:shadow-none transition-all active:scale-95">
                            Confirmar Reserva
                        </button>
                    )}
                    {reserva.status === 'Reservado' && (
                        <div className="flex gap-2">
                            <button onClick={() => onStatusChange('Operativo')} className="bg-emerald-600 hover:bg-emerald-700 text-white px-5 py-2.5 rounded-xl font-bold text-sm shadow-lg shadow-emerald-200 dark:shadow-none transition-all active:scale-95">
                                Pasar a Operativo
                            </button>
                            <button onClick={() => { if (confirm("¿Volver a Presupuesto?")) onStatusChange('Presupuesto'); }} className="bg-amber-100 text-amber-800 hover:bg-amber-200 dark:bg-amber-900/40 dark:text-amber-300 px-5 py-2.5 rounded-xl font-bold text-sm transition-all active:scale-95">
                                Deshacer Reserva
                            </button>
                        </div>
                    )}
                    {reserva.status === 'Operativo' && (
                        <button
                            onClick={() => onStatusChange('Cerrado')}
                            disabled={reserva.balance > 0}
                            className={`px-5 py-2.5 rounded-xl font-bold text-sm shadow-lg transition-all active:scale-95 ${reserva.balance <= 0 ? 'bg-slate-900 dark:bg-white dark:text-slate-900 text-white' : 'bg-slate-300 dark:bg-slate-700 text-slate-500 cursor-not-allowed shadow-none'}`}
                            title={reserva.balance > 0 ? "No se puede cerrar una reserva con saldo pendiente" : "Finalizar Reserva"}
                        >
                            Finalizar Reserva
                        </button>
                    )}

                    {/* ADMIN ACTIONS */}
                    <div className="flex gap-2 ml-2 pl-4 border-l border-slate-200 dark:border-slate-800">
                        {canDelete && (
                            <button onClick={onDelete} className="p-2.5 bg-rose-50 text-rose-600 hover:bg-rose-100 dark:bg-rose-900/20 dark:text-rose-400 rounded-xl transition-colors" title="Eliminar Reserva">
                                <Trash2 className="w-5 h-5" />
                            </button>
                        )}
                        <button
                            onClick={canArchive ? onArchive : undefined}
                            disabled={!canArchive}
                            className={`p-2.5 rounded-xl transition-colors ${canArchive ? 'bg-slate-100 text-slate-600 hover:bg-slate-200 dark:bg-slate-800 dark:text-slate-400 dark:hover:bg-slate-700' : 'bg-slate-50 text-slate-300 dark:bg-slate-900 dark:text-slate-700 cursor-not-allowed'}`}
                            title={canArchive ? "Archivar" : "Solo se pueden archivar reservas operativas/cerradas sin deuda"}
                        >
                            <Archive className="w-5 h-5" />
                        </button>
                    </div>
                </div>
            )}
        </div>
    );
}
