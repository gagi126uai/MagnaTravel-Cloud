/**
 * Panel EN LÍNEA para anular una reserva.
 *
 * ADR-035 (2026-06-19): la accion deja de ser un modal flotante y pasa a un panel inline
 * igual que RegistrarCobroInline y EmitirFacturaInline.
 *
 * ADR-036 (2026-06-21): el panel pasa a llamarse "Anular reserva" (en vez de "Cancelar").
 * En este producto "Cancelar" = saldar una deuda; "Anular" = deshacer el viaje.
 * El estado interno del backend sigue siendo "Cancelled", pero el usuario ve "Anular/Anulada".
 *
 * guia-ux-gaston.md 2026-06-25 — 3 casos distintos según `cancellationCase` del DTO:
 *   - DirectCancel (VERDE):      sin factura, sin cobros → baja directa.
 *   - PaymentsToCredit (CELESTE): sin factura, CON cobros → la plata pasa a saldo a favor.
 *   - CreditNote (ÁMBAR):        con factura CAE vivo → anulación formal con Nota de Crédito.
 *
 * Props:
 *   - reserva: objeto de la reserva (necesita publicId, numeroReserva, customerName,
 *              cancellationCase, cancellationCreditByCurrency, requiresInvoiceAnnulmentToCancel).
 *   - onCancelado: callback luego de confirmar exitosamente (nombre legacy mantenido).
 *   - onCerrar: callback cuando el usuario cierra el panel sin anular.
 */

import { useState, useEffect } from "react";
import { AlertTriangle, Loader2, Ban, X } from "lucide-react";
import { api } from "../../../api";
import { cancellationsApi } from "../api/cancellationsApi";
import { showSuccess, showError } from "../../../alerts";
import { formatCurrency } from "../../../lib/utils";
import {
    buildPenaltyClassificationPayload,
    buildSnapshotData,
} from "../lib/penaltyPayload";
import {
    TEXTO_BANNER_DIRECT_CANCEL,
    TEXTO_BANNER_SALDO_FAVOR_INICIO,
    TEXTO_BANNER_SALDO_FAVOR_ANTE_NEGRITA,
    TEXTO_BANNER_SALDO_FAVOR_NEGRITA,
    TEXTO_BANNER_SALDO_FAVOR_POST_NEGRITA,
    TEXTO_BANNER_CREDIT_NOTE,
    MENSAJE_EXITO_DIRECT_CANCEL,
    MENSAJE_EXITO_PAYMENTS_TO_CREDIT,
    MENSAJE_EXITO_CREDIT_NOTE,
} from "./cancelarReservaCopy";

// ─── Funciones de lógica pura (replicadas en cancelarReservaInline.test.mjs) ──

/**
 * Determina el caso de anulación a partir del discriminador del backend.
 * Cuando el campo `cancellationCase` no viene (DTO viejo en cache), cae al
 * comportamiento legacy usando `requiresInvoiceAnnulmentToCancel`.
 *
 * El caso "PaymentsToCredit" NO puede inferirse desde el booleano legacy solo
 * (requeriría saber si hay cobros), así que el fallback ignora ese caso y
 * queda en DirectCancel o CreditNote — igual que el comportamiento anterior.
 *
 * @param {object} reserva - DTO de la reserva
 * @returns {string} - "DirectCancel" | "PaymentsToCredit" | "CreditNote" | "NotApplicable" | "PreSale"
 */
function determinarCasoAnulacion(reserva) {
    if (reserva?.cancellationCase) {
        return reserva.cancellationCase;
    }
    // FALLBACK: DTO sin cancellationCase (versión vieja en cache o sin actualizar).
    // CreditNote si tiene factura CAE vivo, DirectCancel en cualquier otro caso.
    return reserva?.requiresInvoiceAnnulmentToCancel === true ? "CreditNote" : "DirectCancel";
}

/**
 * Formatea los montos de saldo a favor para el cartel celeste (caso PaymentsToCredit).
 * Usa el formateador de moneda del proyecto para ser consistente con el resto de la app.
 * Separa con " · " cuando hay más de una moneda (nunca suma ARS + USD: regla del contador).
 *
 * @param {Array<{currency: string, amount: number}>} creditByCurrency
 * @returns {string} - Ej: "$ 150.000,00 · US$200,00"
 */
function formatearMontosSaldoAFavor(creditByCurrency) {
    if (!creditByCurrency || creditByCurrency.length === 0) return "";
    return creditByCurrency
        .map((item) => formatCurrency(item.amount, item.currency))
        .join(" · ");
}

// ─── Componente ───────────────────────────────────────────────────────────────

export function CancelarReservaInline({ reserva, onCancelado, onCerrar }) {
    const [reason, setReason] = useState("");

    // `processing` cubre todas las llamadas internas como un único loading visible al usuario.
    const [processing, setProcessing] = useState(false);

    // Mensaje de conflicto (400/409 recuperable). Se muestra en un banner inline para que
    // el usuario lo vea sin que desaparezca el formulario con lo cargado.
    const [conflictMessage, setConflictMessage] = useState(null);

    // Settings de AFIP/ARCA necesarios para construir el snapshot fiscal del paso confirm.
    // Solo los usa el camino CreditNote; se precargan igual para tenerlos listos si hace falta.
    const [afipSettings, setAfipSettings] = useState(null);

    // useEffect con []: corre una sola vez al montar el componente para resetear el estado.
    useEffect(() => {
        setReason("");
        setProcessing(false);
        setConflictMessage(null);
        setAfipSettings(null);
    }, []);

    // Precarga los settings de AFIP/ARCA en segundo plano al abrir el panel.
    // Si falla, buildSnapshotData tiene fallbacks conservadores (Monotributo, Consumidor Final).
    useEffect(() => {
        let cancelado = false;

        (async () => {
            try {
                const data = await api.get("/afip/settings");
                if (!cancelado) setAfipSettings(data);
            } catch {
                // Fallback: buildSnapshotData usa valores conservadores si no hay settings.
            }
        })();

        return () => { cancelado = true; };
    }, []);

    // ─── Lógica principal ──────────────────────────────────────────────────────

    const handleCancelar = async () => {
        const trimmedReason = reason.trim();
        if (trimmedReason.length < 10) {
            showError("El motivo debe tener al menos 10 caracteres.");
            return;
        }

        setProcessing(true);
        setConflictMessage(null);

        const caso = determinarCasoAnulacion(reserva);

        // ── Casos SIN factura (DirectCancel y PaymentsToCredit) → endpoint annul-with-credit ─────────
        // Ambos van al MISMO endpoint dedicado (un solo POST con { reason }, sin el flujo de 2 pasos del draft):
        //   - DirectCancel:      sin cobros → baja directa, sin nota de crédito.
        //   - PaymentsToCredit:  con cobros → la plata cobrada queda como saldo a favor del cliente.
        // El backend decide internamente qué hacer según haya o no cobros; el front solo cambia el mensaje de
        // éxito. El front valida el motivo ≥10 chars arriba; el backend también valida server-side.
        if (caso === "DirectCancel" || caso === "PaymentsToCredit") {
            try {
                await cancellationsApi.annulWithCredit(reserva.publicId, trimmedReason);
                const mensajeExito = caso === "DirectCancel"
                    ? MENSAJE_EXITO_DIRECT_CANCEL
                    : MENSAJE_EXITO_PAYMENTS_TO_CREDIT;
                showSuccess(mensajeExito, "Anulación confirmada");
                onCancelado();
            } catch (error) {
                // NUNCA mostramos el texto crudo del backend (puede traer nombres internos).
                const code = error?.payload?.invariantCode || error?.payload?.code || "";
                if (error?.status === 400) {
                    // 400: el backend rechazó el motivo (< 10 chars). El front ya lo valida,
                    // pero el backend también controla server-side (regla de auditoría).
                    setConflictMessage("Revisá el motivo de la anulación (mínimo 10 caracteres).");
                } else if (error?.status === 403) {
                    showError("No tenés permiso para anular esta reserva.");
                } else if (error?.status === 404) {
                    showError("No encontramos la reserva. Recargá la página.");
                } else if (error?.status === 409 && code === "INV-100") {
                    setConflictMessage(
                        "Esta reserva tiene más de una factura emitida. La anulación de una reserva con varias facturas todavía no está disponible de forma automática; contactá a administración."
                    );
                } else if (error?.status === 409) {
                    setConflictMessage(
                        "No se pudo anular la reserva. Probá de nuevo; si el problema sigue, contactá a administración."
                    );
                } else {
                    showError("No se pudo anular la reserva. Probá de nuevo en unos segundos.");
                }
                setProcessing(false);
            }
            return;
        }

        // ── Caso CreditNote (con factura CAE viva) → flujo draft → confirm ──────────────
        // Este camino emite la Nota de Crédito en AFIP/ARCA (2 llamadas al backend). DirectCancel y
        // PaymentsToCredit ya se resolvieron arriba por el endpoint annul-with-credit; acá solo cae CreditNote
        // (y el fallback conservador de DTOs viejos sin cancellationCase con requiresInvoiceAnnulmentToCancel).

        // PASO 1: crear el borrador (draft). Si falla acá no seguimos.
        let draft;
        try {
            draft = await cancellationsApi.draft(reserva.publicId, trimmedReason);
        } catch (error) {
            // NUNCA mostramos el texto crudo del backend.
            const code = error?.payload?.invariantCode || error?.payload?.code || "";
            if (error?.status === 409 && code === "INV-100") {
                setConflictMessage(
                    "Esta reserva tiene más de una factura emitida. Por ahora no se puede anular toda la reserva de una vez: anulá cada factura desde la solapa Facturas, o contactá a administración."
                );
            } else if (error?.status === 409) {
                setConflictMessage(
                    "No se pudo iniciar la anulación. Probá de nuevo; si el problema sigue, contactá a administración."
                );
            } else {
                showError("No se pudo iniciar la anulación. Probá de nuevo en unos segundos.");
            }
            setProcessing(false);
            return;
        }

        // PASO 2: confirmar la cancelación. Emite la NC en AFIP/ARCA si hay factura (async).
        //
        // Clasificación de penalidad: siempre "operator_pass_through" (int 0).
        // La agencia NO emite ningún cargo propio ni nota de débito en este paso.
        // Solo se emite la nota de crédito. Es la opción más neutra para el mostrador.
        const penaltyClassification = buildPenaltyClassificationPayload(
            "operator_pass_through",
            null,
            null,
            null
        );

        const snapshotData = buildSnapshotData(afipSettings);

        const payload = {
            snapshotData,
            isAdminOverride: false,
            overrideReason: null,
            approvalRequestPublicId: null,
            ...penaltyClassification,
        };

        try {
            await cancellationsApi.confirm(draft.publicId, payload);

            // Solo llega acá el caso CreditNote (con NC en AFIP), más el fallback conservador de DTOs viejos.
            showSuccess(MENSAJE_EXITO_CREDIT_NOTE, "Anulación confirmada");
            onCancelado();
        } catch (error) {
            // NUNCA mostramos el texto crudo del backend.
            const code = error?.payload?.code || error?.payload?.invariantCode || "";
            if (error?.status === 409 && code === "CONCURRENT_EDIT") {
                setConflictMessage(
                    "Otro usuario modificó esta cancelación al mismo tiempo. Recargá la página y volvé a intentar."
                );
            } else if (error?.status === 409) {
                setConflictMessage(
                    "No se pudo confirmar la anulación. Probá de nuevo; si el problema sigue, contactá a administración."
                );
            } else {
                showError("No se pudo confirmar la anulación. Probá de nuevo en unos segundos.");
            }

            setProcessing(false);
        }
    };

    // ─── Render ───────────────────────────────────────────────────────────────

    const reasonTrimmed = reason.trim();
    const charsLeft = 1000 - reason.length;
    const tooShort = reason.length > 0 && reasonTrimmed.length < 10;
    const canSubmit = !processing && reasonTrimmed.length >= 10;

    // Determina el caso activo para mostrar el cartel correcto.
    const casoAnulacion = determinarCasoAnulacion(reserva);

    // Monto formateado para el cartel celeste (solo viene en el caso PaymentsToCredit).
    const montosFormateados = formatearMontosSaldoAFavor(reserva?.cancellationCreditByCurrency);

    return (
        <div
            className="rounded-xl border-2 border-rose-200 bg-rose-50/40 dark:border-rose-900/40 dark:bg-rose-950/10 p-5 space-y-4"
            data-testid="cancelar-reserva-inline"
        >
            {/* ── Cabecera del panel ── */}
            <div className="flex items-center justify-between">
                <div className="flex items-center gap-2">
                    <Ban className="w-4 h-4 text-rose-600" aria-hidden="true" />
                    <h4 className="text-sm font-bold text-slate-900 dark:text-white">Anular reserva</h4>
                    <span className="text-xs text-slate-500 dark:text-slate-400">
                        #{reserva.numeroReserva} — {reserva.customerName}
                    </span>
                </div>
                <button
                    type="button"
                    onClick={onCerrar}
                    disabled={processing}
                    className="text-slate-400 hover:text-slate-600 dark:hover:text-slate-200 rounded p-1 disabled:opacity-40"
                    aria-label="Cerrar sin anular la reserva"
                >
                    <X className="w-4 h-4" />
                </button>
            </div>

            {/* ── Cartel según el caso de anulación (guia-ux-gaston.md 2026-06-25) ──
                Caso 2 DirectCancel     → VERDE:   sin factura, sin cobros.
                Caso 3 PaymentsToCredit → CELESTE:  sin factura, con cobros (plata → saldo a favor).
                Caso 4 CreditNote       → ÁMBAR:   con factura CAE → emite NC en AFIP/ARCA.
                Cualquier otro caso     → ÁMBAR:   fallback conservador (no prometemos nada).
                Los textos viven en cancelarReservaCopy.js para que los tests puedan importarlos. */}
            {casoAnulacion === "DirectCancel" && (
                <div
                    className="flex items-start gap-2 rounded-lg border border-green-200 bg-green-50 p-3.5 text-xs text-green-800 dark:bg-green-950/30 dark:border-green-800 dark:text-green-200"
                    data-testid="cancelar-banner-sin-factura"
                >
                    <span>{TEXTO_BANNER_DIRECT_CANCEL}</span>
                </div>
            )}

            {casoAnulacion === "PaymentsToCredit" && (
                <div
                    className="flex items-start gap-2 rounded-lg border border-sky-200 bg-sky-50 p-3.5 text-xs text-sky-800 dark:bg-sky-950/30 dark:border-sky-800 dark:text-sky-200"
                    data-testid="cancelar-banner-saldo-favor"
                >
                    {/* Decisión UX (guia 2026-06-25): mostrar el monto cobrado por moneda
                        para que el agente sepa exactamente cuánto queda como saldo a favor.
                        Los montos nunca se suman entre monedas (regla del contador: ARS y USD siempre separados).
                        "SALDO A FAVOR" en negrita (presentacional; el texto vive en cancelarReservaCopy.js). */}
                    <span>
                        {TEXTO_BANNER_SALDO_FAVOR_INICIO}
                        {montosFormateados ? ` (${montosFormateados})` : ""}.{" "}
                        {TEXTO_BANNER_SALDO_FAVOR_ANTE_NEGRITA}
                        <strong>{TEXTO_BANNER_SALDO_FAVOR_NEGRITA}</strong>
                        {TEXTO_BANNER_SALDO_FAVOR_POST_NEGRITA}
                    </span>
                </div>
            )}

            {casoAnulacion !== "DirectCancel" && casoAnulacion !== "PaymentsToCredit" && (
                <div
                    className="flex items-start gap-2 rounded-lg border border-amber-200 bg-amber-50 p-3.5 text-xs text-amber-800 dark:bg-amber-950/30 dark:border-amber-800 dark:text-amber-200"
                    data-testid="cancelar-banner-con-factura"
                >
                    <AlertTriangle className="h-4 w-4 flex-shrink-0 mt-0.5" aria-hidden="true" />
                    <span>{TEXTO_BANNER_CREDIT_NOTE}</span>
                </div>
            )}

            {/* ── Error de conflicto (400/409 recuperable) ── */}
            {conflictMessage && (
                <div
                    role="alert"
                    className="rounded-lg border border-rose-200 bg-rose-50 p-4 text-sm text-rose-800 dark:bg-rose-950/30 dark:border-rose-800 dark:text-rose-200 flex items-start gap-2"
                    data-testid="cancelar-inline-conflict-msg"
                >
                    <AlertTriangle className="h-4 w-4 flex-shrink-0 mt-0.5" aria-hidden="true" />
                    <span>{conflictMessage}</span>
                </div>
            )}

            {/* ── Motivo obligatorio ── */}
            <div>
                <label
                    className="block text-xs font-bold uppercase tracking-wider text-slate-500 mb-1.5"
                    htmlFor="cancelar-inline-reason"
                >
                    {/* ADR-036: "de la anulación" en vez de "de la cancelación" */}
                    Motivo de la anulación <span className="text-rose-500" aria-hidden="true">*</span>
                </label>
                <textarea
                    id="cancelar-inline-reason"
                    value={reason}
                    onChange={(e) => setReason(e.target.value)}
                    rows={4}
                    maxLength={1000}
                    disabled={processing}
                    placeholder="Por ejemplo: el cliente cambió de planes por motivos personales..."
                    data-testid="cancelar-inline-reason-textarea"
                    aria-describedby="cancelar-inline-reason-hint"
                    className="w-full rounded-xl border border-slate-300 dark:border-slate-600 dark:bg-slate-800 dark:text-white px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-rose-400 disabled:opacity-50"
                />
                <div id="cancelar-inline-reason-hint" className="mt-1 flex justify-between text-xs text-slate-400">
                    {tooShort ? (
                        <span className="text-rose-600 font-semibold" role="alert">Mínimo 10 caracteres.</span>
                    ) : (
                        <span />
                    )}
                    <span>{charsLeft} restantes</span>
                </div>
            </div>

            {/* ── Acciones ── */}
            <div className="flex justify-end gap-3 pt-1">
                <button
                    type="button"
                    onClick={onCerrar}
                    disabled={processing}
                    className="rounded-lg border border-slate-200 bg-white px-4 py-2 text-sm font-medium text-slate-700 hover:bg-slate-50 dark:bg-slate-800 dark:text-slate-200 dark:border-slate-700 dark:hover:bg-slate-700 transition-colors disabled:opacity-50"
                >
                    Volver
                </button>
                <button
                    type="button"
                    onClick={handleCancelar}
                    disabled={!canSubmit}
                    data-testid="cancelar-inline-confirm-btn"
                    className="rounded-lg bg-rose-600 px-4 py-2 text-sm font-bold text-white hover:bg-rose-700 transition-colors disabled:opacity-50 flex items-center gap-2"
                >
                    {processing && <Loader2 className="h-4 w-4 animate-spin" aria-hidden="true" />}
                    {processing ? "Anulando..." : "Anular reserva"}
                </button>
            </div>
        </div>
    );
}
