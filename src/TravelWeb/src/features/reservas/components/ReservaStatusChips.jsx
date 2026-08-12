import React from 'react';
import { getMoneyStatus } from "../moneyStatus";

/**
 * Chips complementarios de la reserva: tres ejes independientes + corrección opcional.
 *
 * Fix (2026-08-04, pedido del dueño viendo PROD — maqueta firmada línea 1412 "Pago: … · Factura: …"):
 * el Eje Pago ahora se muestra SIEMPRE, con el estado que corresponda (Pagada / Sin
 * movimientos / A favor / Debe / Debe — no viaja / Vencida con deuda / Multa por
 * anulación pendiente / Saldo a favor, en anuladas). Antes el chip directamente
 * DESAPARECÍA para varios de esos estados ("un rótulo, un solo eje" — regla de Gastón
 * 2026-06-22) y el renglón quedaba mostrando solo "Factura:", que es justo lo que el
 * dueño vio en PROD y no le gustó. La ÚNICA excepción que se mantiene a propósito es
 * una anulada con plata "Inconsistente"/"MultaEnRevision" (dato roto o sin comprobante
 * fiscal firme): ahí se sigue sin afirmar un monto o una dirección de plata concreta
 * — decisión FIRMADA del dueño 2026-07-04 — pero ahora en vez de dejar la fila muda se
 * muestra un chip neutro "Sin novedades" (ver el último `else` de más abajo).
 *   - Eje Pago:    ver getMoneyStatus (moneyStatus.js) para la lista completa de kinds.
 *   - Eje Viaje:   Vencida con deuda  ← SOLO este caso; "En viaje" lo dice el badge grande.
 *                  (se repite también en el Eje Pago desde el fix de arriba, redundancia
 *                  chica y a propósito para que "Pago:" nunca quede en blanco)
 *   - Eje Factura: Sin facturar / Facturada en parte / Facturada total / Facturada y devuelta (ADR-048 T3)
 *
 * Tanda 6 (2026-07-05): el Eje Pago YA NO decide mirando collectionStatus/balance acá —
 * delega en getMoneyStatus (../moneyStatus.js), la fuente ÚNICA de esta categorización
 * en toda la app (ver ese archivo para la lista completa de reglas).
 *
 * "Vencida con deuda" (Eje Viaje) sigue leyendo reserva.hasOverdueDebt directamente:
 * es un eje aparte y no estaba duplicado en otro lugar antes de este fix.
 * "En viaje" NO se chip-ea — el badge grande "EN VIAJE" ya lo dice, repetirlo agrega ruido.
 *
 * Eje Factura: siempre visible (ADR-037). Lee reserva.invoicingStatus.
 *
 * Chip "En corrección" (2026-06-22): tratamiento secundario (ámbar/gris chico).
 *   Aparece cuando reserva.isUnderCorrection === true.
 *   Indica que la reserva fue sacada de viaje por corrección y está congelada para
 *   el pase automático; no compite con el badge de estado operativo grande.
 *
 * Flags que provee el backend en ReservaDto (leídos por getMoneyStatus, no acá):
 *   collectionStatus, hasOverdueDebt, isWithinUnpaidAlertWindow, cancelledMoneyContext.
 * Flags propios de este componente: invoicingStatus, isUnderCorrection.
 *
 * Feedback 2026-06-19 (cambio 6): chips más chicos para no competir con el badge de estado.
 */

// Molde único de chip (Lavado de cara, 2026-08-11, estándar visual B.5: "24 px de alto,
// redondo completo, 11 px mayúsculas, borde 1 px del mismo tono"). Los 4 chips de esta
// cabecera (Pago, Viaje, Factura, En corrección) comparten esta forma — solo cambia el
// color (className de cada estado, que SIGUE informando algo: verde=plata entró,
// rojo=freno, ámbar=pide algo — P-20, "un color, un significado". Este lavado de cara
// es de PIEL, no de semántica: no se tocan los colores por estado, solo la forma).
const CLASE_CHIP_ESTANDAR = "inline-flex h-6 items-center rounded-full border px-2.5 text-[11px] font-bold uppercase tracking-wider";

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
    // ADR-048 T3/T4 (2026-07-17, spec Punto 2, P1=B FIRMADA): la reserva SÍ tuvo factura, pero
    // una Nota de Crédito la devolvió entera (neto quedó en ~0). Gris pizarra + tilde ✓ (misma
    // familia neutra que "Sin facturar", pero la tilde comunica "ciclo cerrado" en vez de
    // "todavía nada" — nunca hay que mostrar "Sin facturar" acá, sería la mentira que esto corrige).
    FullyReturned: {
        label: '✓ Facturada y devuelta',
        className: 'bg-slate-100 text-slate-600 border-slate-200 dark:bg-slate-800 dark:text-slate-300 dark:border-slate-700',
        title: 'Se facturó y después se devolvió con una nota de crédito. No queda saldo facturado.',
    },
};

export function ReservaStatusChips({ reserva }) {
    if (!reserva) return null;

    // ── Eje PAGO ──────────────────────────────────────────────────────────────────
    // Fix C2 (Tanda 6, saneamiento 2026-07-05): esta rama YA NO decide sola leyendo
    // collectionStatus/isWithinUnpaidAlertWindow — delega en getMoneyStatus (moneyStatus.js),
    // que es la MISMA función que usan ReservaSummaryStrip/ReservaTable/CustomerAccountPage.
    // Acá solo se traduce el "kind" devuelto a la forma visual del chip (className/title).
    //
    // Se muestra chip SOLO para los casos que ya mostraba (comportamiento sin cambios en
    // reservas vivas): Pagada / Sin movimientos / Debe — no viaja. "SaldoAFavor" sigue sin
    // chip acá (se muestra en otro lado, ver EstadoCuentaResumen). Los kinds "debe" y
    // "vencidaConDeuda" tampoco generan chip de Pago (ese último lo cubre el eje Viaje, abajo).
    //
    // NUEVO (Tanda 6): en una reserva ANULADA se agregan dos chips propios —
    // "Saldo a favor" (verde) y "Multa por anulación pendiente de cobro" (ámbar) — para que
    // la ficha nunca insinúe una "deuda" genérica sobre un viaje que ya quedó sin efecto.
    let chipPago = null;
    const moneyStatus = getMoneyStatus(reserva);

    if (moneyStatus.kind === 'sinMovimientos') {
        // Sin movimientos: la reserva existe pero no hay cargos ni cobros todavía.
        // Gris neutro: no alarma ni confirma nada.
        // El key/data-testid "sin-cobros" se mantiene para no romper selectores de QA.
        chipPago = {
            key: 'sin-cobros',
            label: moneyStatus.label,
            className: 'bg-slate-100 text-slate-500 border-slate-200 dark:bg-slate-800 dark:text-slate-400 dark:border-slate-700',
            title: 'Todavía no hay movimientos de plata registrados para esta reserva.',
        };
    } else if (moneyStatus.kind === 'pagada') {
        chipPago = {
            key: 'paid',
            label: moneyStatus.label,
            className: 'bg-emerald-50 text-emerald-700 border-emerald-200 dark:bg-emerald-900/30 dark:text-emerald-300 dark:border-emerald-800',
            title: 'El cliente no debe nada.',
        };
    } else if (moneyStatus.kind === 'debeNoViaja') {
        // ADR-036/037: chip rojo "Debe — no viaja", SOLO dentro de la ventana de aviso
        // y SOLO en Confirmed (si ya pasó a Traveling, el cliente pagó — invariante del sistema).
        chipPago = {
            key: 'debe-no-viaja',
            label: moneyStatus.label,
            className: 'bg-rose-100 text-rose-700 border-rose-200 dark:bg-rose-900/30 dark:text-rose-300 dark:border-rose-800',
            title: 'El cliente tiene saldo pendiente. No puede viajar hasta que pague el total.',
        };
    } else if (moneyStatus.kind === 'saldoAFavorAnulada') {
        chipPago = {
            key: 'saldo-a-favor-anulada',
            label: moneyStatus.label,
            className: 'bg-emerald-50 text-emerald-700 border-emerald-200 dark:bg-emerald-900/30 dark:text-emerald-300 dark:border-emerald-800',
            title: 'Quedó plata del cliente sin devolver ni aplicar a otra reserva.',
        };
    } else if (moneyStatus.kind === 'multaPorCobrar') {
        chipPago = {
            key: 'multa-por-cobrar',
            label: moneyStatus.label,
            className: 'bg-amber-100 text-amber-700 border-amber-200 dark:bg-amber-900/30 dark:text-amber-300 dark:border-amber-800',
            title: 'La multa por anulación tiene una Nota de Débito viva y todavía no se cobró.',
        };
    } else if (moneyStatus.kind === 'saldoAFavor') {
        // Fix (2026-08-04, pedido del dueño viendo PROD): "A favor" (reserva VIVA con saldo
        // a favor del cliente, collectionStatus=SaldoAFavor) no tenía chip acá — el renglón
        // "Pago:" desaparecía entero. No confundir con 'saldoAFavorAnulada' de arriba (misma
        // idea, pero en una reserva anulada): son ejes de negocio distintos, con su propio kind.
        chipPago = {
            key: 'saldo-a-favor',
            label: moneyStatus.label,
            className: 'bg-emerald-50 text-emerald-700 border-emerald-200 dark:bg-emerald-900/30 dark:text-emerald-300 dark:border-emerald-800',
            title: 'Quedó plata del cliente a favor, para usar en otra reserva o devolver.',
        };
    } else if (moneyStatus.kind === 'debe' || moneyStatus.kind === 'vencidaConDeuda') {
        // Fix (2026-08-04): 'debe' (deuda cobrable genérica) y 'vencidaConDeuda' tampoco
        // tenían chip de Pago — quedaba en blanco. 'vencidaConDeuda' además se repite en el
        // eje Viaje (más abajo): es una redundancia chica y a propósito, porque el dueño pidió
        // que el renglón "Pago:" nunca desaparezca, pase lo que pase con el estado de la plata.
        chipPago = {
            key: moneyStatus.kind === 'vencidaConDeuda' ? 'vencida-con-deuda' : 'debe',
            label: moneyStatus.label,
            className: 'bg-rose-100 text-rose-700 border-rose-200 dark:bg-rose-900/30 dark:text-rose-300 dark:border-rose-800',
            title: 'El cliente tiene saldo pendiente de pago.',
        };
    } else {
        // Fix (2026-08-04): fallback final para que "Pago:" NUNCA desaparezca — cubre el
        // kind "none" (anulada con cancelledMoneyContext "Inconsistente"/"MultaEnRevision", o
        // genuinamente saldada en cero). A propósito NO inventamos un monto ni una dirección
        // (a favor/debe): esos dos casos son justamente los que el dueño decidió (2026-07-04)
        // que NUNCA se le muestran al vendedor como si fueran plata real — esa regla sigue de
        // pie, esto solo evita que la fila quede muda en vez de mostrar un texto neutro.
        // "Sin movimientos": mismo vocabulario que ya usa el listado (Tanda 1, firmado) para
        // este caso exacto — anulada con plata en revisión no promete cobro ni devolución.
        chipPago = {
            key: 'sin-novedades',
            label: 'Sin movimientos',
            className: 'bg-slate-100 text-slate-500 border-slate-200 dark:bg-slate-800 dark:text-slate-400 dark:border-slate-700',
            title: 'No hay nada pendiente de cobro ni de devolución para mostrar.',
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

            {/* Eje Pago: SIEMPRE visible (fix 2026-08-04) — chipPago nunca queda null,
                el último `else` de arriba le pone un texto neutro a cualquier caso que
                antes no tenía chip propio. El `chipPago &&` queda como red defensiva. */}
            {chipPago && (
                <span className="inline-flex items-center gap-1.5" data-testid="reserva-payment-chips">
                    <span className="text-[10px] font-semibold text-slate-400 dark:text-slate-500 uppercase tracking-wider">
                        Pago:
                    </span>
                    <span
                        data-testid={`chip-pago-${chipPago.key}`}
                        className={`${CLASE_CHIP_ESTANDAR} ${chipPago.className}`}
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
                        className={`${CLASE_CHIP_ESTANDAR} ${chipViaje.className}`}
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
                    className={`${CLASE_CHIP_ESTANDAR} ${invoicing.className}`}
                    title={invoicing.title}
                >
                    {invoicing.label}
                </span>
            </span>

            {/* Chip "En corrección": tratamiento secundario, no compite con el badge grande.
                Solo aparece cuando isUnderCorrection=true — la reserva fue sacada de viaje
                por corrección y está congelada para el pase automático hasta que se corrija
                la fecha del servicio (spec UX 2026-06-22). */}
            {reserva.isUnderCorrection && (
                <span
                    data-testid="chip-en-correccion"
                    className={`${CLASE_CHIP_ESTANDAR} bg-amber-50 text-amber-700 border-amber-200 dark:bg-amber-900/30 dark:text-amber-300 dark:border-amber-800`}
                    title="Pendiente revisar fechas — congelada para el pase automático a viaje"
                >
                    En corrección
                </span>
            )}

        </span>
    );
}
