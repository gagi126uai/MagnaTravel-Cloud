import { useEffect, useState } from "react";
import { X, AlertTriangle, Loader2, Undo2 } from "lucide-react";
import { api } from "../../../api";
import { showError, showSuccess } from "../../../alerts";
import { getApiErrorMessage } from "../../../lib/errors";

const TARGET_LABELS = {
    Reservado: "Reservado",
    Presupuesto: "Presupuesto",
    Operativo: "Operativo",
};

/**
 * Modal para revertir el status de una Reserva.
 * - Si actor es Admin: muestra solo la confirmacion + reason opcional.
 * - Si NO es Admin: pide supervisor + reason obligatorios.
 * - Si la API devolvio HardBlockers, los muestra en rojo y deshabilita el submit.
 */
export function RevertStatusModal({ reserva, onClose, onReverted }) {
    const [options, setOptions] = useState(null);
    const [loadingOptions, setLoadingOptions] = useState(true);
    const [targetStatus, setTargetStatus] = useState("");
    const [supervisorId, setSupervisorId] = useState("");
    const [reason, setReason] = useState("");
    const [submitting, setSubmitting] = useState(false);

    useEffect(() => {
        let cancelled = false;
        (async () => {
            setLoadingOptions(true);
            try {
                const data = await api.get(`/reservas/${reserva.publicId}/revert-options`);
                if (!cancelled) {
                    setOptions(data);
                    if (data?.allowedTargets?.length === 1) setTargetStatus(data.allowedTargets[0]);
                }
            } catch (error) {
                if (!cancelled) showError(getApiErrorMessage(error, "No se pudieron cargar las opciones de reversion."));
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
    const canSubmit = !!targetStatus && !hardBlocked && (isAdmin || (supervisorId && reasonOk));

    const handleSubmit = async () => {
        setSubmitting(true);
        try {
            await api.post(`/reservas/${reserva.publicId}/revert-status`, {
                targetStatus,
                authorizedBySuperiorUserId: supervisorId || null,
                reason: reason.trim() || null,
            });
            showSuccess(`Reserva revertida a ${targetStatus}`);
            onReverted();
        } catch (error) {
            showError(getApiErrorMessage(error, "No se pudo revertir la reserva."));
        } finally {
            setSubmitting(false);
        }
    };

    return (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 backdrop-blur-sm p-4 animate-in fade-in duration-200">
            <div className="w-full max-w-lg rounded-2xl border bg-card shadow-2xl max-h-[90vh] overflow-y-auto">
                <div className="px-6 py-4 border-b bg-slate-50/50 dark:bg-slate-900/50 flex items-center justify-between">
                    <div className="flex items-center gap-2">
                        <Undo2 className="h-5 w-5 text-amber-600" />
                        <div>
                            <h3 className="text-lg font-bold text-slate-900 dark:text-white">Revertir estado de la reserva</h3>
                            <p className="text-xs text-muted-foreground">{reserva.numeroReserva} - actualmente {reserva.status}</p>
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
                                <div className="rounded-lg border border-rose-200 bg-rose-50 p-4 text-sm text-rose-800 dark:bg-rose-950/30 dark:border-rose-800 dark:text-rose-200">
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
                                    <div>
                                        <label className="text-xs font-bold uppercase text-slate-500 mb-1 block">Volver a</label>
                                        <select
                                            value={targetStatus}
                                            onChange={(e) => setTargetStatus(e.target.value)}
                                            className="w-full rounded-md border border-slate-200 bg-white px-3 py-2 text-sm dark:border-slate-700 dark:bg-slate-800"
                                        >
                                            <option value="">— Seleccionar —</option>
                                            {options.allowedTargets.map((t) => (
                                                <option key={t} value={t}>{TARGET_LABELS[t] || t}</option>
                                            ))}
                                        </select>
                                    </div>

                                    {requiresAuth && (
                                        <div className="rounded-lg border border-amber-200 bg-amber-50 p-3 text-xs text-amber-900 dark:bg-amber-950/30 dark:border-amber-800 dark:text-amber-200">
                                            No sos admin. Necesitas autorizacion de un supervisor para revertir el estado.
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
                                            Motivo {requiresAuth ? <span className="text-rose-500">* (mín. 10 caracteres)</span> : <span className="text-slate-400">(opcional)</span>}
                                        </label>
                                        <textarea
                                            value={reason}
                                            onChange={(e) => setReason(e.target.value)}
                                            rows={3}
                                            placeholder={requiresAuth ? "Motivo de la reversion..." : "Motivo (opcional)..."}
                                            className="w-full rounded-md border border-slate-200 bg-white px-3 py-2 text-sm dark:border-slate-700 dark:bg-slate-800"
                                        />
                                        {requiresAuth && reason.length > 0 && reason.trim().length < 10 && (
                                            <div className="mt-1 text-xs text-rose-600">El motivo debe tener al menos 10 caracteres.</div>
                                        )}
                                    </div>
                                </>
                            )}
                        </>
                    )}
                </div>

                <div className="px-6 py-4 border-t bg-slate-50/50 dark:bg-slate-900/50 flex justify-end gap-3">
                    <button
                        type="button"
                        onClick={onClose}
                        disabled={submitting}
                        className="px-4 py-2 rounded-lg text-sm font-bold text-slate-600 hover:bg-slate-100 dark:text-slate-300 dark:hover:bg-slate-800 transition-colors disabled:opacity-50"
                    >
                        Cancelar
                    </button>
                    <button
                        type="button"
                        onClick={handleSubmit}
                        disabled={submitting || !canSubmit}
                        className="px-4 py-2 rounded-lg text-sm font-bold text-white bg-amber-600 hover:bg-amber-700 transition-colors disabled:opacity-50 flex items-center gap-2"
                    >
                        {submitting ? <Loader2 className="h-4 w-4 animate-spin" /> : <Undo2 className="h-4 w-4" />}
                        Revertir
                    </button>
                </div>
            </div>
        </div>
    );
}
