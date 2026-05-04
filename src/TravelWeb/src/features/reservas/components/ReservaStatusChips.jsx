import React from 'react';

/**
 * Chips derivados que complementan al badge de estado principal. No son estados
 * de la maquina — son indicadores visuales calculados sobre el estado +
 * propiedades financieras/temporales de la reserva.
 *
 * - "Pagada" (verde): reserva en Confirmed con Balance == 0. Le dice al
 *   operador "no hay nada por cobrar, esta lista para viajar".
 * - "Saldo pendiente" (amarillo): reserva en Confirmed con Balance > 0.
 * - "En curso" (verde solido pulse): reserva en Traveling cuyas fechas
 *   indican que el viaje esta pasando AHORA.
 * - "Vencida con deuda" (rojo pulse): reserva en Traveling cuyo viaje ya
 *   termino pero tiene saldo pendiente. No se cierra automaticamente; alerta
 *   fuerte para que el operador la cobre o la dé de baja.
 *
 * Los flags `isFullyPaid`, `hasOverdueDebt`, `isInProgress` los provee el backend
 * en ReservaDto/ReservaListDto.
 */
export function ReservaStatusChips({ reserva }) {
    if (!reserva) return null;

    const chips = [];

    if (reserva.status === 'Confirmed') {
        if (reserva.isFullyPaid) {
            chips.push({
                key: 'paid',
                label: 'Pagada',
                className: 'bg-emerald-50 text-emerald-700 border-emerald-200 dark:bg-emerald-900/30 dark:text-emerald-300 dark:border-emerald-800',
                title: 'El cliente no debe nada. Lista para viajar.',
            });
        } else {
            chips.push({
                key: 'unpaid',
                label: 'Saldo pendiente',
                className: 'bg-amber-50 text-amber-700 border-amber-200 dark:bg-amber-900/30 dark:text-amber-300 dark:border-amber-800',
                title: 'El cliente todavia debe parte de la reserva.',
            });
        }
    }

    if (reserva.hasOverdueDebt) {
        chips.push({
            key: 'overdue',
            label: 'Vencida con deuda',
            className: 'bg-rose-600 text-white border-rose-700 animate-pulse',
            title: 'El viaje ya termino pero quedo saldo pendiente. La reserva no se cerro automaticamente.',
        });
    } else if (reserva.isInProgress) {
        // Solo mostrar "En curso" si NO esta vencida con deuda (en ese caso
        // mostramos la alerta mas fuerte).
        chips.push({
            key: 'in-progress',
            label: '• En curso',
            className: 'bg-emerald-600 text-white border-emerald-700 animate-pulse',
            title: 'El cliente esta viajando ahora.',
        });
    }

    if (chips.length === 0) return null;

    return (
        <>
            {chips.map((chip) => (
                <span
                    key={chip.key}
                    className={`px-3 py-1 rounded-full text-xs font-bold uppercase tracking-wider border ${chip.className}`}
                    title={chip.title}
                >
                    {chip.label}
                </span>
            ))}
        </>
    );
}
