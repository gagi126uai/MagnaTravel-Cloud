import React from 'react';

/**
 * Chips complementarios de la reserva: tres ejes independientes.
 *
 * Un rótulo = un solo eje. Regla de Gastón 2026-06-22 (refinamiento por review):
 *   - Eje Pago:    Pagada / Debe — no viaja
 *   - Eje Viaje:   Vencida con deuda  ← SOLO este caso; "En viaje" lo dice el badge grande.
 *   - Eje Factura: Sin facturar / Facturada en parte / Facturada total
 *
 * "Pagada" aparece cuando isFullyPaid es true en CUALQUIER estado.
 * "Debe — no viaja" aparece solo en Confirmed dentro de la ventana de aviso.
 * "Vencida con deuda" aparece cuando hasOverdueDebt === true (el viaje terminó con saldo).
 * "En viaje" NO se chip-ea — lo dice el badge grande; repetirlo genera ruido.
 *
 * Eje Factura: siempre visible (ADR-037). Lee reserva.invoicingStatus.
 *
 * Flags que provee el backend en ReservaDto:
 *   isFullyPaid, hasOverdueDebt, isWithinUnpaidAlertWindow, invoicingStatus.
 *
 * Feedback 2026-06-19 (cambio 6): chips más chicos para no competir con el badge de estado.
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

    // ── Eje PAGO ──────────────────────────────────────────────────────────────────
    // "Pagada": true cuando isFullyPaid sin importar el estado.
    // Antes estaba limitado a status === 'Confirmed'; ahora aplica a cualquier estado.
    // Ejemplo: una reserva Traveling pagada debe mostrar "Pago: Pagada", no nada.
    let chipPago = null;
    if (reserva.isFullyPaid) {
        chipPago = {
            key: 'paid',
            label: 'Pagada',
            className: 'bg-emerald-50 text-emerald-700 border-emerald-200 dark:bg-emerald-900/30 dark:text-emerald-300 dark:border-emerald-800',
            title: 'El cliente no debe nada.',
        };
    } else if (reserva.status === 'Confirmed' && reserva.isWithinUnpaidAlertWindow === true) {
        // ADR-036/037: chip rojo "Debe — no viaja", SOLO dentro de la ventana de aviso
        // y SOLO en Confirmed (si ya pasó a Traveling, el cliente pagó — invariante del sistema).
        chipPago = {
            key: 'debe-no-viaja',
            label: 'Debe — no viaja',
            className: 'bg-rose-100 text-rose-700 border-rose-200 dark:bg-rose-900/30 dark:text-rose-300 dark:border-rose-800',
            title: 'El cliente tiene saldo pendiente. No puede viajar hasta que pague el total.',
        };
    }

    // ── Eje VIAJE ─────────────────────────────────────────────────────────────────
    // Solo mostramos chip cuando hay una ANOMALÍA que el badge grande no comunica:
    // "Vencida con deuda" = el viaje terminó y quedó plata pendiente.
    // "En viaje" (isInProgress) NO se chip-ea — el badge grande "EN VIAJE" ya lo dice,
    // repetirlo agrega ruido sin información extra (refinamiento review 2026-06-22).
    let chipViaje = null;
    if (reserva.hasOverdueDebt) {
        chipViaje = {
            key: 'overdue',
            label: 'Vencida con deuda',
            className: 'bg-rose-600 text-white border-rose-700 animate-pulse',
            title: 'El viaje ya terminó pero quedó saldo pendiente. La reserva no se cerró automáticamente.',
        };
    }

    // ── Eje FACTURA ───────────────────────────────────────────────────────────────
    // Siempre visible (ADR-037). Valor por defecto: NotInvoiced.
    const invoicing = INVOICING_CHIP[reserva.invoicingStatus] || INVOICING_CHIP.NotInvoiced;

    return (
        <span className="inline-flex items-center gap-2 flex-wrap" data-testid="reserva-money-chips">

            {/* Eje Pago: solo cuando hay algo que decir (pagada o debe-no-viaja). */}
            {chipPago && (
                <span className="inline-flex items-center gap-1.5" data-testid="reserva-payment-chips">
                    <span className="text-[10px] font-semibold text-slate-400 dark:text-slate-500 uppercase tracking-wider">
                        Pago:
                    </span>
                    <span
                        data-testid={`chip-pago-${chipPago.key}`}
                        className={`px-2 py-0.5 rounded-full text-[10px] font-bold uppercase tracking-wider border ${chipPago.className}`}
                        title={chipPago.title}
                    >
                        {chipPago.label}
                    </span>
                </span>
            )}

            {/* Eje Viaje: solo cuando hay deuda vencida (anomalía que el badge no comunica). */}
            {chipViaje && (
                <span className="inline-flex items-center gap-1.5" data-testid="reserva-travel-chips">
                    <span className="text-[10px] font-semibold text-slate-400 dark:text-slate-500 uppercase tracking-wider">
                        Viaje:
                    </span>
                    <span
                        data-testid={`chip-viaje-${chipViaje.key}`}
                        className={`px-2 py-0.5 rounded-full text-[10px] font-bold uppercase tracking-wider border ${chipViaje.className}`}
                        title={chipViaje.title}
                    >
                        {chipViaje.label}
                    </span>
                </span>
            )}

            {/* Eje Factura: siempre visible (ADR-037). */}
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
