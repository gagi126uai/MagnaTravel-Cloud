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
 * El cartel de color (verde / ámbar) informa si la anulacion NECESITA emitir una nota de
 * crédito en AFIP/ARCA, usando el campo `requiresInvoiceAnnulmentToCancel` del DTO.
 *
 * Props:
 *   - reserva: objeto de la reserva (necesita publicId, numeroReserva, customerName,
 *              requiresInvoiceAnnulmentToCancel).
 *   - onCancelado: callback luego de confirmar exitosamente (nombre legacy mantenido por compatibilidad).
 *   - onCerrar: callback cuando el usuario cierra el panel sin anular.
 */

import { useState, useEffect } from "react";
import { AlertTriangle, Loader2, Ban, X } from "lucide-react";
import { api } from "../../../api";
import { cancellationsApi } from "../api/cancellationsApi";
import { showSuccess, showError } from "../../../alerts";
import { getApiErrorMessage } from "../../../lib/errors";
import {
    buildPenaltyClassificationPayload,
    buildSnapshotData,
} from "../lib/penaltyPayload";

export function CancelarReservaInline({ reserva, onCancelado, onCerrar }) {
    const [reason, setReason] = useState("");

    // `processing` cubre las 2 llamadas internas (draft + confirm) como un único loading.
    const [processing, setProcessing] = useState(false);

    // Mensaje de conflicto 409 (invariante de negocio). Se muestra en un banner dentro del
    // panel para que el usuario lo vea sin que desaparezca el formulario con lo cargado.
    const [conflictMessage, setConflictMessage] = useState(null);

    // Settings de AFIP/ARCA necesarios para construir el snapshot fiscal del confirm.
    // Se precargan al montar el panel para tenerlos listos cuando el usuario confirma.
    const [afipSettings, setAfipSettings] = useState(null);

    // Resetea el estado al montar (o si el panel se reutiliza).
    // useEffect con []: corre una sola vez al montar el componente.
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

        // PASO 1: crear el borrador (draft). Si falla acá no seguimos.
        let draft;
        try {
            draft = await cancellationsApi.draft(reserva.publicId, trimmedReason);
        } catch (error) {
            if (error?.status === 409) {
                const errorPayload = error?.payload;

                // INV-152: la reserva tiene servicios de más de un operador.
                if (errorPayload?.invariantCode === "INV-152") {
                    setConflictMessage(
                        "Esta reserva tiene servicios de más de un operador. Por ahora la cancelación de reservas con varios operadores no está disponible desde acá. Gestionala manualmente o pedile ayuda a un administrador."
                    );
                } else {
                    setConflictMessage(
                        getApiErrorMessage(error, "No se pudo iniciar la cancelación. Recargá la página y volvé a intentar.")
                    );
                }
            } else {
                showError(getApiErrorMessage(error, "No se pudo iniciar la cancelación."));
            }
            setProcessing(false);
            return;
        }

        // PASO 2: confirmar la cancelación. Emite la nota de crédito en AFIP/ARCA (async).
        //
        // Clasificación de penalidad: siempre "operator_pass_through" (int 0).
        // La agencia NO emite ningún cargo propio ni nota de débito.
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
            // ADR-036: el mensaje visible dice "anulada" (no "cancelada").
            showSuccess("Reserva anulada. La nota de crédito se está generando.", "Anulación confirmada");
            onCancelado();
        } catch (error) {
            if (error?.status === 409) {
                const errorPayload = error?.payload;
                const code = errorPayload?.code || "";

                let humanMessage;
                if (code === "CONCURRENT_EDIT") {
                    humanMessage = "Otro usuario modificó esta cancelación al mismo tiempo. Recargá la página y volvé a intentar.";
                } else {
                    humanMessage = getApiErrorMessage(error, "No se pudo confirmar la cancelación. Volvé a intentar.");
                }

                setConflictMessage(humanMessage);
            } else {
                showError(getApiErrorMessage(error, "No se pudo confirmar la cancelación."));
            }

            setProcessing(false);
        }
    };

    // ─── Render ───────────────────────────────────────────────────────────────

    const reasonTrimmed = reason.trim();
    const charsLeft = 1000 - reason.length;
    const tooShort = reason.length > 0 && reasonTrimmed.length < 10;
    const canSubmit = !processing && reasonTrimmed.length >= 10;

    // `requiresInvoiceAnnulmentToCancel` viene del DTO (ADR-035, campo boolean).
    // true  → reserva tiene factura con CAE vivo; al cancelar se emite NC en AFIP.
    // false → no hay factura emitida; se cancela sin nota de crédito.
    const requiereAnulacion = reserva?.requiresInvoiceAnnulmentToCancel === true;

    return (
        <div
            className="rounded-xl border-2 border-rose-200 bg-rose-50/40 dark:border-rose-900/40 dark:bg-rose-950/10 p-5 space-y-4"
            data-testid="cancelar-reserva-inline"
        >
            {/* ── Cabecera del panel ── */}
            <div className="flex items-center justify-between">
                <div className="flex items-center gap-2">
                    <Ban className="w-4 h-4 text-rose-600" aria-hidden="true" />
                    {/* ADR-036: "Anular reserva" en vez de "Cancelar reserva" */}
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

            {/* ── Cartel según haya o no factura emitida (ADR-035, 2026-06-19) ──
                Verde: no hay factura; se cancela directo.
                Ámbar: hay factura con CAE; la cancelación emite NC en AFIP/ARCA.

                Decisión de UX (guia-ux-gaston.md sección ADR-035):
                "Si la reserva NO tiene factura emitida → cartel verde. Si SÍ tiene → cartel ámbar." */}
            {!requiereAnulacion ? (
                <div
                    className="flex items-start gap-2 rounded-lg border border-green-200 bg-green-50 p-3.5 text-xs text-green-800 dark:bg-green-950/30 dark:border-green-800 dark:text-green-200"
                    data-testid="cancelar-banner-sin-factura"
                >
                    <span>Esta reserva no tiene factura emitida, se cancela directo, sin nota de crédito.</span>
                </div>
            ) : (
                <div
                    className="flex items-start gap-2 rounded-lg border border-amber-200 bg-amber-50 p-3.5 text-xs text-amber-800 dark:bg-amber-950/30 dark:border-amber-800 dark:text-amber-200"
                    data-testid="cancelar-banner-con-factura"
                >
                    <AlertTriangle className="h-4 w-4 flex-shrink-0 mt-0.5" aria-hidden="true" />
                    <span>
                        Esta reserva tiene factura emitida, al cancelar se emite la nota de crédito en AFIP/ARCA para anularla.
                    </span>
                </div>
            )}

            {/* ── Error de conflicto 409 ── */}
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
                    {/* ADR-036: "Anular reserva" en vez de "Cancelar reserva" */}
                    {processing ? "Anulando..." : "Anular reserva"}
                </button>
            </div>
        </div>
    );
}
