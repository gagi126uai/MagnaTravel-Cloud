import React from 'react';

/**
 * Indicadores de ESTADO DE PAGO de la reserva. Son chips complementarios, NO el estado operativo.
 *
 * Feedback 2026-06-19 (cambio 6): diferenciamos "estado operativo" (badge grande)
 * de "estado de pago" (estos chips más chicos con prefijo "Pago:" en gris).
 *
 * ADR-036 (2026-06-21): se agrega el chip "Debe — no viaja" (rojo) para reservas Confirmadas
 * con saldo pendiente del cliente. Es el aviso de que el cliente no puede viajar todavía.
 * Solo muestra plata del CLIENTE (no costo ni deuda al operador).
 *
 * NOTA PARCIAL — ventana de aviso (pendiente backend):
 * La config "Alertas por reservas próximas con deuda" (enableUpcomingUnpaidReservationNotifications
 * + upcomingUnpaidReservationAlertDays) existe en /settings/operational-finance, pero ESA config
 * no está expuesta en el OperationalFlagsContext ni en el ReservaDto.
 * Por eso el chip "Debe — no viaja" HOY se muestra para TODA reserva Confirmed con saldo pendiente,
 * sin filtro de ventana de días. Para aplicar el filtro, el backend debe exponer un campo
 * (ej. isWithinUnpaidAlertWindow: bool) en el ReservaDto — reportado al equipo de backend.
 *
 * Valores posibles:
 * - "Pagada" (verde): reserva Confirmed con isFullyPaid = true.
 * - "Debe — no viaja" (rojo): reserva Confirmed con isFullyPaid = false (ADR-036).
 * - "En curso" (verde pulse): el viaje está pasando ahora mismo (Traveling).
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
            // ADR-036: chip rojo "Debe — no viaja" en vez del chip ambar de "Saldo pendiente".
            // El cliente tiene deuda y no puede pasar a En viaje hasta saldar.
            // Prefijo "Pago:" en gris para que no parezca un segundo estado operativo (regla ADR-035 A-quinque).
            chips.push({
                key: 'debe-no-viaja',
                label: 'Debe — no viaja',
                className: 'bg-rose-100 text-rose-700 border-rose-200 dark:bg-rose-900/30 dark:text-rose-300 dark:border-rose-800',
                title: 'El cliente tiene saldo pendiente. No puede viajar hasta que pague el total.',
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
