import { useState, useMemo } from "react";
import { X, Users, AlertTriangle, Loader2 } from "lucide-react";
import { api } from "../../../api";
import { showError, showSuccess } from "../../../alerts";
import { getApiErrorMessage } from "../../../lib/errors";

const DOC_TYPES = [
    { value: "DNI", label: "DNI" },
    { value: "Pasaporte", label: "Pasaporte" },
    { value: "CUIT", label: "CUIT" },
    { value: "CUIL", label: "CUIL" },
];

function buildSlots(reserva) {
    const slots = [];
    const adults = reserva.adultCount || 0;
    const children = reserva.childCount || 0;
    const infants = reserva.infantCount || 0;

    for (let i = 0; i < adults; i++) slots.push({ kind: "Adulto", index: i + 1 });
    for (let i = 0; i < children; i++) slots.push({ kind: "Menor", index: i + 1 });
    for (let i = 0; i < infants; i++) slots.push({ kind: "Infante", index: i + 1 });
    return slots;
}

export function ConfirmReservaModal({ reserva, readiness, onClose, onConfirmed }) {
    const slots = useMemo(() => buildSlots(reserva), [reserva]);
    const alreadyLoadedCount = readiness?.currentPassengerCount ?? 0;
    const slotsToFill = slots.slice(alreadyLoadedCount);

    const [forms, setForms] = useState(() =>
        slotsToFill.map(() => ({ fullName: "", documentType: "DNI", documentNumber: "" }))
    );
    const [submitting, setSubmitting] = useState(false);

    const allValid = forms.every(f =>
        f.fullName.trim().length >= 3 &&
        (f.documentNumber || "").trim().length > 0
    );

    const updateForm = (i, patch) => {
        setForms(prev => prev.map((f, idx) => idx === i ? { ...f, ...patch } : f));
    };

    const handleSubmit = async () => {
        if (!allValid && slotsToFill.length > 0) {
            showError("Completa nombre y documento de cada pasajero antes de confirmar.");
            return;
        }
        setSubmitting(true);
        try {
            // 1) Crear los pasajeros faltantes uno por uno
            for (let i = 0; i < forms.length; i++) {
                const form = forms[i];
                await api.post(`/reservas/${reserva.publicId}/passengers`, {
                    fullName: form.fullName.trim(),
                    documentType: form.documentType,
                    documentNumber: form.documentNumber.trim(),
                    birthDate: null,
                    nationality: null,
                    phone: null,
                    email: null,
                    gender: null,
                    notes: null,
                });
            }
            // 2) Disparar la transicion
            await api.put(`/reservas/${reserva.publicId}/status`, { status: "Reservado" });
            showSuccess("Reserva confirmada");
            onConfirmed();
        } catch (error) {
            showError(getApiErrorMessage(error, "No se pudo confirmar la reserva."));
        } finally {
            setSubmitting(false);
        }
    };

    const handleConfirmDirect = async () => {
        // No hay pasajeros faltantes, pasamos directo
        setSubmitting(true);
        try {
            await api.put(`/reservas/${reserva.publicId}/status`, { status: "Reservado" });
            showSuccess("Reserva confirmada");
            onConfirmed();
        } catch (error) {
            showError(getApiErrorMessage(error, "No se pudo confirmar la reserva."));
        } finally {
            setSubmitting(false);
        }
    };

    const blockingNonPax = (readiness?.blockingReasons || []).filter(r => !r.toLowerCase().includes("pasajero"));

    return (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 backdrop-blur-sm p-4 animate-in fade-in duration-200">
            <div className="w-full max-w-2xl rounded-2xl border bg-card shadow-2xl max-h-[90vh] overflow-y-auto">
                <div className="px-6 py-4 border-b bg-slate-50/50 dark:bg-slate-900/50 flex items-center justify-between">
                    <div className="flex items-center gap-2">
                        <Users className="h-5 w-5 text-indigo-600" />
                        <div>
                            <h3 className="text-lg font-bold text-slate-900 dark:text-white">Confirmar reserva</h3>
                            <p className="text-xs text-muted-foreground">{reserva.numeroReserva} - {reserva.customerName}</p>
                        </div>
                    </div>
                    <button onClick={onClose} className="text-slate-400 hover:text-slate-600 transition-colors">
                        <X className="h-5 w-5" />
                    </button>
                </div>

                <div className="p-6 space-y-4">
                    {blockingNonPax.length > 0 && (
                        <div className="rounded-lg border border-rose-200 bg-rose-50 p-4 text-sm text-rose-800 dark:bg-rose-950/30 dark:border-rose-800 dark:text-rose-200">
                            <div className="flex items-start gap-2">
                                <AlertTriangle className="h-4 w-4 flex-shrink-0 mt-0.5" />
                                <div>
                                    <strong className="font-bold">No se puede confirmar todavia:</strong>
                                    <ul className="list-disc list-inside mt-1 space-y-1">
                                        {blockingNonPax.map((r, i) => <li key={i}>{r}</li>)}
                                    </ul>
                                </div>
                            </div>
                        </div>
                    )}

                    {slotsToFill.length === 0 ? (
                        <div className="rounded-lg border border-emerald-200 bg-emerald-50 p-4 text-sm text-emerald-800 dark:bg-emerald-950/30 dark:border-emerald-800 dark:text-emerald-200">
                            Todos los pasajeros ya estan cargados. Confirmando se cambia el estado a Reservado.
                        </div>
                    ) : (
                        <>
                            <div className="rounded-lg border border-amber-200 bg-amber-50 p-3 text-sm text-amber-900 dark:bg-amber-950/30 dark:border-amber-800 dark:text-amber-200">
                                Carga los <strong>{slotsToFill.length}</strong> pasajero(s) faltantes con nombre y documento.
                                Una vez confirmada la reserva, ya no podras editar las cantidades de pasajeros.
                            </div>

                            <div className="space-y-3">
                                {slotsToFill.map((slot, i) => (
                                    <div key={`${slot.kind}-${slot.index}`} className="rounded-xl border border-slate-200 bg-white p-3 dark:border-slate-700 dark:bg-slate-900">
                                        <div className="text-xs font-bold uppercase text-indigo-600 dark:text-indigo-400 mb-2">
                                            {slot.kind} {slot.index}
                                        </div>
                                        <div className="grid grid-cols-12 gap-2">
                                            <input
                                                type="text"
                                                placeholder="Nombre y apellido"
                                                value={forms[i].fullName}
                                                onChange={(e) => updateForm(i, { fullName: e.target.value })}
                                                className="col-span-12 sm:col-span-6 rounded-md border border-slate-200 bg-white px-3 py-2 text-sm dark:border-slate-700 dark:bg-slate-800"
                                            />
                                            <select
                                                value={forms[i].documentType}
                                                onChange={(e) => updateForm(i, { documentType: e.target.value })}
                                                className="col-span-4 sm:col-span-2 rounded-md border border-slate-200 bg-white px-2 py-2 text-sm dark:border-slate-700 dark:bg-slate-800"
                                            >
                                                {DOC_TYPES.map(d => <option key={d.value} value={d.value}>{d.label}</option>)}
                                            </select>
                                            <input
                                                type="text"
                                                placeholder="N de documento"
                                                value={forms[i].documentNumber}
                                                onChange={(e) => updateForm(i, { documentNumber: e.target.value })}
                                                className="col-span-8 sm:col-span-4 rounded-md border border-slate-200 bg-white px-3 py-2 text-sm dark:border-slate-700 dark:bg-slate-800"
                                            />
                                        </div>
                                    </div>
                                ))}
                            </div>
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
                        onClick={slotsToFill.length === 0 ? handleConfirmDirect : handleSubmit}
                        disabled={submitting || blockingNonPax.length > 0 || (slotsToFill.length > 0 && !allValid)}
                        className="px-4 py-2 rounded-lg text-sm font-bold text-white bg-indigo-600 hover:bg-indigo-700 transition-colors disabled:opacity-50 flex items-center gap-2"
                    >
                        {submitting ? <Loader2 className="h-4 w-4 animate-spin" /> : null}
                        {slotsToFill.length === 0 ? "Confirmar reserva" : `Cargar y confirmar (${slotsToFill.length})`}
                    </button>
                </div>
            </div>
        </div>
    );
}
