import React from 'react';

/**
 * Indicadores de PLATA/PROCESO de la reserva (cobro + facturación). Son chips complementarios,
 * NO el estado operativo (ese es el badge grande).
 *
 * Feedback 2026-06-19 (cambio 6): diferenciamos "estado operativo" (badge grande)
 * de los ejes de plata (estos chips más chicos con prefijo en gris).
 *
 * ADR-036 (2026-06-21): chip "Debe — no viaja" (rojo) para reservas Confirmadas con saldo
 * pendiente del cliente: aviso de que el cliente no puede viajar todavía. Solo plata del
 * CLIENTE (no costo ni deuda al operador).
 *
 * ADR-037 (2026-06-21):
 * - Se agrega el carril de FACTURACIÓN como chip propio con prefijo "Factura:" en gris,
 *   separado del eje de cobro ("Pago:"). Se muestra SIEMPRE (en todos los estados, incluso
 *   pre-venta dirá "Sin facturar"). Lee `reserva.invoicingStatus` (NotInvoiced /
 *   PartiallyInvoiced / FullyInvoiced), carril derivado del backend.
 * - El aviso "Debe — no viaja" ahora respeta la ventana de aviso: se muestra SOLO cuando
 *   `reserva.isWithinUnpaidAlertWindow === true` (el backend ya lo calcula contra StartDate y
 *   la config existente upcomingUnpaidReservationAlertDays). Fuera de la ventana no se muestra.
 *
 * Valores posibles (eje de cobro):
 * - "Pagada" (verde): reserva Confirmed con isFullyPaid = true.
 * - "Debe — no viaja" (rojo): reserva Confirmed con isFullyPaid = false Y dentro de la ventana de aviso.
 * - "En curso" (verde pulse): el viaje está pasando ahora mismo (Traveling).
 * - "Vencida con deuda" (rojo pulse): el viaje terminó pero queda saldo pendiente.
 *
 * Los flags `isFullyPaid`, `hasOverdueDebt`, `isInProgress`, `isWithinUnpaidAlertWindow` y el
 * carril `invoicingStatus` los provee el backend en ReservaDto/ReservaListDto.
 */
const INVOICING_CHIP = {
    NotInvoiced: {
        label: 'Sin facturar',
        className: 'bg-slate-100 text-slate-600 border-slate-200 dark:bg-slate-800 dark:text-slate-300 dark:border-slate-700',
        title: 'Todavía no se emitió la factura de venta de esta reserva.',
    },
    PartiallyInvoiced: {
        label: 'Facturada en parte',
        className: 'bg-amber-50 text-amber-700 border-amber-200 dark:bg-amber-900/30 dark:text-amber-300 dark:border-amber-800',
        title: 'Se facturó una parte de la venta. Queda saldo sin facturar.',
    },
    FullyInvoiced: {
        label: 'Facturada total',
        className: 'bg-emerald-50 text-emerald-700 border-emerald-200 dark:bg-emerald-900/30 dark:text-emerald-300 dark:border-emerald-800',
        title: 'La venta está facturada en su totalidad.',
    },
};

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
        } else if (reserva.isWithinUnpaidAlertWindow) {
            // ADR-036/037: chip rojo "Debe — no viaja", SOLO dentro de la ventana de aviso.
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

    // ADR-037: carril de facturación, se muestra SIEMPRE.
    const invoicing = INVOICING_CHIP[reserva.invoicingStatus] || INVOICING_CHIP.NotInvoiced;

    return (
        <span className="inline-flex items-center gap-2" data-testid="reserva-money-chips">
            {/* Eje de cobro ("Pago:") — solo cuando hay algo que decir. */}
            {chips.length > 0 && (
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
            )}
            {/* Eje de facturación ("Factura:") — siempre visible. */}
            <span className="inline-flex items-center gap-1.5" data-testid="reserva-invoicing-chip">
                <span className="text-[10px] font-semibold text-slate-400 dark:text-slate-500 uppercase tracking-wider">
                    Factura:
                </span>
                <span
                    data-testid={`chip-factura-${reserva.invoicingStatus || 'NotInvoiced'}`}
                    className={`px-2 py-0.5 rounded-full text-[10px] font-bold uppercase tracking-wider border ${invoicing.className}`}
                    title={invoicing.title}
                >
                    {invoicing.label}
                </span>
            </span>
        </span>
    );
}
