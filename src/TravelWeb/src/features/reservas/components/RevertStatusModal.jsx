import { useEffect, useState } from "react";
import { X, AlertTriangle, Loader2, Undo2 } from "lucide-react";
import { api } from "../../../api";
import { showConfirm, showError, showSuccess } from "../../../alerts";
import { getApiErrorMessage } from "../../../lib/errors";
import { translateStatus } from "./ReservaStatusBadge";
import { isReservaAnulada } from "../moneyStatus";
import { construirConfirmacionDeshacerAnulacion } from "../lib/revertAnuladaConfirmLogic";
import { debeMostrarCartelPorRechazoDeRevert } from "../lib/revertRejectionPresentation";
import { CartelEmergente, CARTEL_EMERGENTE_VARIANTES } from "../../../components/CartelEmergente";
import { Button } from "../../../components/ui/button";

/**
 * Modal para revertir el status de una Reserva.
 *
 * Props básicas (uso normal):
 * - Si actor es Admin: muestra solo la confirmacion + reason opcional.
 * - Si NO es Admin: pide supervisor + reason obligatorios.
 * - Si la API devolvio HardBlockers, los muestra en rojo y deshabilita el submit.
 *
 * Props adicionales para el flujo "Reabrir para facturar" (ADR-036):
 * - forceReason: cuando es true, el motivo es obligatorio TAMBIÉN para admin (mín. 10 caracteres).
 *   Justificación: reabrir una reserva Finalizada es sensible (vuelve al circuito fiscal);
 *   siempre queda registrado el motivo para auditoria.
 * - lockedTarget: cuando viene con valor, pre-selecciona ese target y oculta el selector.
 *   ADR-036: ya no se pasa "ToSettle" porque ese estado fue eliminado. Sin lockedTarget,
 *   el modal muestra todas las opciones del backend (auto-selecciona si solo hay una).
 */
export function RevertStatusModal({ reserva, onClose, onReverted, forceReason = false, lockedTarget = null }) {
    const [options, setOptions] = useState(null);
    const [loadingOptions, setLoadingOptions] = useState(true);
    // Si hay lockedTarget, pre-seleccionamos ese valor desde el inicio.
    const [targetStatus, setTargetStatus] = useState(lockedTarget ?? "");
    const [supervisorId, setSupervisorId] = useState("");
    const [reason, setReason] = useState("");
    const [submitting, setSubmitting] = useState(false);
    // Rechazo del motor al confirmar (P-13: se muestra TAL CUAL, nunca se reescribe). La
    // decisión toast-vs-Cartel emergente único la toma debeMostrarCartelPorRechazoDeRevert
    // (T-6: primero mira el `code` estructurado del 409, el largo del texto es solo un
    // fallback legacy — ver ese archivo para el detalle).
    const [rejectionMessage, setRejectionMessage] = useState(null);

    useEffect(() => {
        let cancelled = false;
        (async () => {
            setLoadingOptions(true);
            try {
                const data = await api.get(`/reservas/${reserva.publicId}/revert-options`);
                if (!cancelled) {
                    setOptions(data);
                    // Si hay lockedTarget, ese es el destino fijo y no lo pisamos con el del backend.
                    // Sin lockedTarget: auto-seleccionamos si solo hay una opción (comportamiento original).
                    if (!lockedTarget && data?.allowedTargets?.length === 1) {
                        setTargetStatus(data.allowedTargets[0]);
                    }
                }
            } catch (error) {
                if (!cancelled) showError(getApiErrorMessage(error, "No se pudieron cargar las opciones. Probá de nuevo."));
            } finally {
                if (!cancelled) setLoadingOptions(false);
            }
        })();
        return () => { cancelled = true; };
    }, [reserva.publicId]);

    const isAdmin = !!options?.actorIsAdmin;
    const requiresAuth = !!options?.requiresAuthorization;
    const hardBlocked = (options?.hardBlockers?.length ?? 0) > 0;
    const reasonOk = reason.trim().length >= 10;

    // Cuando forceReason=true, el motivo es obligatorio TAMBIÉN para admin.
    // Caso de uso: "Reabrir para facturar" siempre queda auditado (acción fiscal sensible).
    const reasonRequired = requiresAuth || forceReason;

    const canSubmit = !!targetStatus && !hardBlocked
        && (isAdmin || (supervisorId && reasonOk))   // gate de supervisor (no-admin)
        && (!forceReason || reasonOk);               // gate de motivo obligatorio (si forceReason)

    const handleSubmit = async () => {
        // ADR-050 (2026-07-24): si la reserva de ORIGEN está anulada, "Volver atrás" deshace
        // la anulación ENTERA (revive servicios, retira saldo a favor no usado, aborta el
        // reembolso del operador) — no es un simple cambio de estado. Confirmación EXTRA,
        // específica para este caso, ANTES de disparar el POST. Para reverts desde otros
        // estados el flujo sigue igual (el motivo obligatorio de siempre alcanza).
        if (isReservaAnulada(reserva)) {
            const confirmado = await showConfirm(construirConfirmacionDeshacerAnulacion());
            if (!confirmado) return;
        }

        setSubmitting(true);
        setRejectionMessage(null);
        try {
            await api.post(`/reservas/${reserva.publicId}/revert-status`, {
                targetStatus,
                authorizedBySuperiorUserId: supervisorId || null,
                reason: reason.trim() || null,
            });
            showSuccess(`Reserva revertida a ${translateStatus(targetStatus)}`);
            onReverted();
        } catch (error) {
            // P-13: el mensaje del motor se muestra TAL CUAL, nunca se reescribe ni se mapea
            // por texto. Los rechazos de ADR-050 (saldo a favor ya usado en otra reserva,
            // nota de débito de la multa ya emitida) llegan con `code: "UNDO_ANNULMENT_BLOCKED"`
            // en el 409 — el API client ya lo deja en error.code (ver src/api.js). Con ese code
            // la decisión toast-vs-cartel es SIEMPRE cartel, sin mirar el largo del texto
            // (dos de estos mensajes miden 78 y 79 caracteres, por debajo del umbral de
            // "mensaje largo" — ver revertRejectionPresentation.js para el detalle completo).
            const mensaje = getApiErrorMessage(error, "No se pudo revertir la reserva.");
            if (debeMostrarCartelPorRechazoDeRevert({ mensaje, code: error.code })) {
                setRejectionMessage(mensaje);
            } else {
                showError(mensaje);
            }
        } finally {
            setSubmitting(false);
        }
    };

    return (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 backdrop-blur-sm p-4 animate-in fade-in duration-200">
            <div className="w-full max-w-lg rounded-[14px] border bg-card shadow-2xl max-h-[90vh] overflow-y-auto">
                <div className="px-6 py-4 border-b bg-slate-50/50 dark:bg-slate-900/50 flex items-center justify-between">
                    <div className="flex items-center gap-2">
                        <Undo2 className="h-5 w-5 text-amber-600" />
                        <div>
                            {/* Cuando lockedTarget viene seteado, el titulo cambia al de la accion especifica.
                                Sin lockedTarget: titulo generico "Revertir estado".
                                ADR-036: ya no se usa lockedTarget="ToSettle" (ese estado fue eliminado). */}
                            <h3 className="text-lg font-bold text-slate-900 dark:text-white">
                                {lockedTarget ? "Reabrir para facturar" : "Revertir estado de la reserva"}
                            </h3>
                            <p className="text-xs text-muted-foreground">{reserva.numeroReserva} - actualmente {translateStatus(reserva.status)}</p>
                        </div>
                    </div>
                    <button onClick={onClose} className="text-slate-400 hover:text-slate-600 transition-colors">
                        <X className="h-5 w-5" />
                    </button>
                </div>

                <div className="p-6 space-y-4">
                    {loadingOptions ? (
                        <div className="flex items-center justify-center py-10 text-slate-500">
                            <Loader2 className="h-5 w-5 animate-spin mr-2" /> Cargando opciones...
                        </div>
                    ) : (
                        <>
                            {hardBlocked && (
                                <div className="rounded-[10px] border border-rose-200 bg-rose-50 p-4 text-sm text-rose-800 dark:bg-rose-950/30 dark:border-rose-800 dark:text-rose-200">
                                    <div className="flex items-start gap-2">
                                        <AlertTriangle className="h-4 w-4 flex-shrink-0 mt-0.5" />
                                        <div>
                                            <strong className="font-bold">Bloqueado:</strong>
                                            <ul className="list-disc list-inside mt-1 space-y-1">
                                                {options.hardBlockers.map((b, i) => <li key={i}>{b}</li>)}
                                            </ul>
                                        </div>
                                    </div>
                                </div>
                            )}

                            {!hardBlocked && options?.allowedTargets?.length === 0 && (
                                <div className="text-sm text-slate-500 italic">
                                    No hay reversiones disponibles desde el estado actual.
                                </div>
                            )}

                            {!hardBlocked && options?.allowedTargets?.length > 0 && (
                                <>
                                    {/* Selector de destino:
                                        - Con lockedTarget: el destino ya está fijado, ocultamos el select para simplificar.
                                        - Sin lockedTarget: select normal con todas las opciones. */}
                                    {lockedTarget ? (
                                        <div className="rounded-[10px] border border-slate-200 bg-slate-50 px-3 py-2 text-sm text-slate-700 dark:border-slate-700 dark:bg-slate-800 dark:text-slate-200">
                                            Destino: <strong>{translateStatus(lockedTarget)}</strong>
                                        </div>
                                    ) : (
                                        <div>
                                            <label className="text-xs font-bold uppercase text-slate-500 mb-1 block">Volver a</label>
                                            <select
                                                data-testid="revert-target-select"
                                                value={targetStatus}
                                                onChange={(e) => setTargetStatus(e.target.value)}
                                                className="w-full rounded-md border border-slate-200 bg-white px-3 py-2 text-sm dark:border-slate-700 dark:bg-slate-800"
                                            >
                                                <option value="">— Seleccionar —</option>
                                                {options.allowedTargets.map((t) => (
                                                    <option key={t} value={t}>{translateStatus(t)}</option>
                                                ))}
                                            </select>
                                        </div>
                                    )}

                                    {requiresAuth && (
                                        <div className="rounded-[10px] border border-amber-200 bg-amber-50 p-3 text-xs text-amber-900 dark:bg-amber-950/30 dark:border-amber-800 dark:text-amber-200">
                                            Para cambiar el estado necesitás que te autorice un supervisor.
                                        </div>
                                    )}

                                    {requiresAuth && (
                                        <div>
                                            <label className="text-xs font-bold uppercase text-slate-500 mb-1 block">
                                                Supervisor que autoriza <span className="text-rose-500">*</span>
                                            </label>
                                            <select
                                                value={supervisorId}
                                                onChange={(e) => setSupervisorId(e.target.value)}
                                                className="w-full rounded-md border border-slate-200 bg-white px-3 py-2 text-sm dark:border-slate-700 dark:bg-slate-800"
                                            >
                                                <option value="">— Seleccionar supervisor —</option>
                                                {(options?.supervisors ?? []).map((s) => (
                                                    <option key={s.userId} value={s.userId}>{s.fullName}</option>
                                                ))}
                                            </select>
                                            {(!options?.supervisors || options.supervisors.length === 0) && (
                                                <div className="mt-1 text-xs text-rose-600">
                                                    No hay supervisores disponibles para autorizar. Contactá a un admin.
                                                </div>
                                            )}
                                        </div>
                                    )}

                                    <div>
                                        <label className="text-xs font-bold uppercase text-slate-500 mb-1 block">
                                            {/* reasonRequired = requiresAuth (no-admin) OR forceReason (admin en accion sensible).
                                                En ambos casos el motivo es obligatorio con mínimo 10 caracteres. */}
                                            Motivo del cambio {reasonRequired
                                                ? <span className="text-rose-500">* (mín. 10 caracteres)</span>
                                                : null}
                                        </label>
                                        <textarea
                                            value={reason}
                                            onChange={(e) => setReason(e.target.value)}
                                            rows={3}
                                            placeholder={reasonRequired ? "Motivo del cambio de estado..." : "Motivo del cambio..."}
                                            className="w-full rounded-md border border-slate-200 bg-white px-3 py-2 text-sm dark:border-slate-700 dark:bg-slate-800"
                                        />
                                        {reasonRequired && reason.length > 0 && reason.trim().length < 10 && (
                                            <div className="mt-1 text-xs text-rose-600">El motivo debe tener al menos 10 caracteres.</div>
                                        )}
                                    </div>
                                </>
                            )}
                        </>
                    )}
                </div>

                <div className="px-6 py-4 border-t bg-slate-50/50 dark:bg-slate-900/50 flex justify-end gap-3">
                    <Button type="button" variant="ghost" onClick={onClose} disabled={submitting}>
                        Cancelar
                    </Button>
                    <Button
                        type="button"
                        onClick={handleSubmit}
                        disabled={submitting || !canSubmit}
                        className="gap-2"
                        data-testid="revert-submit-btn"
                    >
                        {submitting ? <Loader2 className="h-4 w-4 animate-spin" /> : <Undo2 className="h-4 w-4" />}
                        {/* ADR-036: "Reabrir" cuando hay lockedTarget (flujo Reabrir-para-facturar), "Revertir" para el generico */}
                        {lockedTarget ? "Reabrir" : "Revertir"}
                    </Button>
                </div>
            </div>

            {/* Rechazo del motor que merece Cartel emergente único (ej. ADR-050: saldo a favor
                ya usado en otra reserva, nota de débito de la multa ya emitida) — mensaje tal
                cual, nunca reescrito. Los rechazos que no llegan a este umbral siguen yendo
                por toast (ver debeMostrarCartelPorRechazoDeRevert más arriba). */}
            <CartelEmergente
                isOpen={Boolean(rejectionMessage)}
                variant={CARTEL_EMERGENTE_VARIANTES.BLOQUEO}
                message={rejectionMessage}
                onClose={() => setRejectionMessage(null)}
                dataTestId="revert-status-cartel-rechazo"
            />
        </div>
    );
}
