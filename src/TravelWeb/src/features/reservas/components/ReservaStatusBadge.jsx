import React from 'react';

/**
 * Mapeo canonico de estados de Reserva. Los keys son los strings persistidos
 * en BD (en ingles, alineados con el enum EstadoReserva del backend); el label
 * es lo que se muestra al usuario en espanol.
 *
 * Si llega un status desconocido (ej. legacy "Operativo" antes de migrar)
 * se usa el fallback de Budget para no romper la UI; en ese caso se renderiza
 * el string crudo como label.
 */
export const statusConfig = {
    Budget: {
        label: 'Presupuesto',
        color: 'bg-blue-50 text-blue-700 border-blue-200 dark:bg-blue-900/20 dark:text-blue-300 dark:border-blue-800',
        icon: '📋',
    },
    Confirmed: {
        label: 'Confirmada',
        color: 'bg-amber-50 text-amber-700 border-amber-200 dark:bg-amber-900/20 dark:text-amber-300 dark:border-amber-800',
        icon: '📌',
    },
    Traveling: {
        label: 'En viaje',
        color: 'bg-emerald-50 text-emerald-700 border-emerald-200 dark:bg-emerald-900/20 dark:text-emerald-300 dark:border-emerald-800',
        icon: '✈️',
    },
    Closed: {
        label: 'Finalizada',
        color: 'bg-slate-100 text-slate-600 border-slate-200 dark:bg-slate-800 dark:text-slate-400 dark:border-slate-700',
        icon: '✅',
    },
    Cancelled: {
        label: 'Cancelada',
        color: 'bg-rose-50 text-rose-700 border-rose-200 dark:bg-rose-900/20 dark:text-rose-300 dark:border-rose-800',
        icon: '❌',
    },
    Archived: {
        label: 'Archivada',
        color: 'bg-slate-100 text-slate-500 border-slate-300 dark:bg-slate-800 dark:text-slate-400 dark:border-slate-700',
        icon: '📦',
    },
};

/** Devuelve el label en espanol para mostrar; si el status no existe en el config devuelve el string crudo. */
export function translateStatus(status) {
    return statusConfig[status]?.label ?? status ?? '';
}

/** Devuelve la config completa (label + color + icon) para un status, con fallback a Budget. */
export function getStatusConfig(status) {
    return statusConfig[status] ?? statusConfig.Budget;
}

export function ReservaStatusBadge({ status }) {
    const cfg = getStatusConfig(status);
    const label = statusConfig[status]?.label ?? status;
    return (
        <span className={`px-2.5 py-0.5 rounded-full text-xs font-medium border ${cfg.color}`}>
            {label}
        </span>
    );
}
