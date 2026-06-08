import React from 'react';

/**
 * Mapeo canonico de estados de Reserva al label en espanol y al color del badge.
 *
 * Los keys son los strings persistidos en la BD (en ingles, alineados con EstadoReserva.cs).
 *
 * Ciclo nuevo (ADR-020, ciclo unico sin flags):
 *   Quotation → Budget → InManagement → Confirmed → Traveling → Closed
 *   Lost: cotizacion/presupuesto que no prospero (queda en historial)
 *   ToSettle: apartado para liquidar con el operador (desvio opcional post-viaje)
 *   Cancelled: cancelacion con proceso fiscal
 *
 * "Sold" (Vendida) YA NO EXISTE desde ADR-020. Si llega del backend como legacy,
 * el fallback lo muestra igual pero sin color ni icono especial.
 */
export const statusConfig = {
    // Cotizacion: primer paso del ciclo. Borrador interno del vendedor.
    // Color gris claro — indica "todavia nada", borrador.
    Quotation: {
        label: 'Cotizacion',
        color: 'bg-slate-100 text-slate-500 border-slate-200 dark:bg-slate-800/60 dark:text-slate-400 dark:border-slate-700',
        icon: '📝',
    },
    // Presupuesto: documento armado que el cliente recibe y evalua.
    // Color azul claro — sigue siendo "en curso", pero ya mas formal.
    Budget: {
        label: 'Presupuesto',
        color: 'bg-blue-50 text-blue-700 border-blue-200 dark:bg-blue-900/20 dark:text-blue-300 dark:border-blue-800',
        icon: '📋',
    },
    // En gestion: el cliente acepto; se solicitan servicios a los operadores.
    // Color celeste/cian — "en movimiento", diferente del azul del presupuesto.
    InManagement: {
        label: 'En gestion',
        color: 'bg-cyan-50 text-cyan-700 border-cyan-200 dark:bg-cyan-900/20 dark:text-cyan-300 dark:border-cyan-800',
        icon: '⚙️',
    },
    // Confirmada: todos los servicios resueltos. Se activa AUTOMATICAMENTE.
    // Color ambar/naranja — "lista pero en espera del viaje".
    Confirmed: {
        label: 'Confirmada',
        color: 'bg-amber-50 text-amber-700 border-amber-200 dark:bg-amber-900/20 dark:text-amber-300 dark:border-amber-800',
        icon: '🔒',
    },
    // En viaje: el cliente esta viajando.
    Traveling: {
        label: 'En viaje',
        color: 'bg-emerald-50 text-emerald-700 border-emerald-200 dark:bg-emerald-900/20 dark:text-emerald-300 dark:border-emerald-800',
        icon: '✈️',
    },
    // A liquidar: desvio opcional post-viaje para cerrar cuentas con el operador.
    ToSettle: {
        label: 'A liquidar',
        color: 'bg-violet-50 text-violet-700 border-violet-200 dark:bg-violet-900/20 dark:text-violet-300 dark:border-violet-800',
        icon: '📊',
    },
    // Finalizada: reserva cerrada, ciclo completo.
    Closed: {
        label: 'Finalizada',
        color: 'bg-slate-100 text-slate-600 border-slate-200 dark:bg-slate-800 dark:text-slate-400 dark:border-slate-700',
        icon: '✅',
    },
    // Perdido: cotizacion o presupuesto que el cliente no compro. Queda en historial.
    // Decisión #10 (guia UX 2026-06-08): gris oscuro + tachado visual — indica "no prospero".
    Lost: {
        label: 'Perdido',
        color: 'bg-slate-300 text-slate-600 border-slate-400 line-through dark:bg-slate-700 dark:text-slate-400 dark:border-slate-600',
        icon: '❌',
    },
    // Cancelada: proceso de cancelacion con implicancias fiscales.
    Cancelled: {
        label: 'Cancelada',
        color: 'bg-rose-50 text-rose-700 border-rose-200 dark:bg-rose-900/20 dark:text-rose-300 dark:border-rose-800',
        icon: '🚫',
    },
    // Archivada: solo lectura, fuera del ciclo activo.
    Archived: {
        label: 'Archivada',
        color: 'bg-slate-100 text-slate-500 border-slate-300 dark:bg-slate-800 dark:text-slate-400 dark:border-slate-700',
        icon: '📦',
    },
};

/**
 * Estados que tienen el candado activo (confirmada o posterior).
 * Cuando una reserva esta en uno de estos estados, editar cualquier dato
 * requiere autorizacion explicita (ADR-020 F4).
 */
export const LOCKED_STATUSES = new Set(['Confirmed', 'Traveling', 'ToSettle', 'Closed']);

/** Devuelve true si el status tiene candado activo. */
export function isStatusLocked(status) {
    return LOCKED_STATUSES.has(status);
}

/** Devuelve el label en espanol para mostrar en la UI. Si el status no existe, devuelve el string crudo. */
export function translateStatus(status) {
    return statusConfig[status]?.label ?? status ?? '';
}

/** Devuelve la config completa (label + color + icon) para un status, con fallback a Budget. */
export function getStatusConfig(status) {
    return statusConfig[status] ?? statusConfig.Budget;
}

/**
 * Badge de estado de la reserva.
 * El color y el label se leen del statusConfig canonico.
 */
export function ReservaStatusBadge({ status }) {
    const cfg = getStatusConfig(status);
    const label = statusConfig[status]?.label ?? status;
    return (
        <span className={`px-2.5 py-0.5 rounded-full text-xs font-medium border ${cfg.color}`}>
            {label}
        </span>
    );
}
