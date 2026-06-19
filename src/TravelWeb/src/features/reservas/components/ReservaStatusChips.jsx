import React from 'react';

/**
 * Indicadores de ESTADO DE PAGO de la reserva. Son chips complementarios, NO el estado operativo.
 *
 * Feedback 2026-06-19 (cambio 6): diferenciamos visualmente "estado operativo de la reserva"
 * (el badge grande: Presupuesto, En gestión, Confirmada, etc.) de "estado de pago"
 * (estos chips: Pagada, Saldo pendiente, Vencida con deuda).
 * Para eso:
 *   - Los chips son más pequeños (text-[10px] en vez de text-xs)
 *   - Llevan el prefijo "Pago:" en gris claro para que el usuario entienda que es un eje diferente
 *   - No se mezclan visualmente con el badge de estado operativo
 *
 * Valores posibles:
 * - "Pagada" (verde): reserva Confirmed con Balance == 0.
 * - "Saldo pendiente" (amarillo): reserva Confirmed con Balance > 0.
 * - "En curso" (verde pulse): el viaje está pasando ahora mismo.
 * - "Vencida con deuda" (rojo pulse): el viaje terminó pero queda saldo pendiente.
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
        // Solo mostrar "En curso" si NO está vencida con deuda (en ese caso
        // mostramos la alerta más fuerte).
        chips.push({
            key: 'in-progress',
            label: '• En curso',
            className: 'bg-emerald-600 text-white border-emerald-700 animate-pulse',
            title: 'El cliente esta viajando ahora.',
        });
    }

    if (chips.length === 0) return null;

    return (
        // Contenedor con label "Pago:" para que quede claro que estos chips son del eje de cobro,
        // no del estado operativo de la reserva. Así no parece "dos estados a la vez".
        <span className="inline-flex items-center gap-1.5" data-testid="reserva-payment-chips">
            <span className="text-[10px] font-semibold text-slate-400 dark:text-slate-500 uppercase tracking-wider">
                Pago:
            </span>
            {chips.map((chip) => (
                <span
                    key={chip.key}
                    data-testid={`chip-pago-${chip.key}`}
                    className={`px-2 py-0.5 rounded-full text-[10px] font-bold uppercase tracking-wider border ${chip.className}`}
                    title={chip.title}
                >
                    {chip.label}
                </span>
            ))}
        </span>
    );
}
