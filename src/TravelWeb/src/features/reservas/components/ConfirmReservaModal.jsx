/**
 * Modal para cargar pasajeros antes de avanzar una reserva de estado.
 *
 * ADR-020 (ciclo unico): se usa cuando el cliente acepta el presupuesto
 * (Budget → InManagement) y hay pasajeros faltantes que cargar.
 *
 * Si no hay pasajeros faltantes, la transicion es directa (sin modal).
 * El padre (ReservaDetailPage) decide cuando abrir este modal basandose
 * en la respuesta de GET /reservas/{id}/transition-readiness.
 *
 * Props:
 * - targetStatus: el estado destino (tipicamente "InManagement")
 * - readiness: objeto devuelto por GET /reservas/{id}/transition-readiness
 *
 * Reglas de negocio (Gaston 2026-06-08):
 * - NUNCA se puede avanzar con 0 pasajeros: adultos+menores+infantes >= 1.
 * - Si la carga de un pasajero falla, se muestra el error real y NO se intenta
 *   el cambio de estado (no encadenar PUT /status si el POST fallo).
 * - Tras cargar cada pasajero exitosamente, se refresca el estado en el padre
 *   para que el front quede sincronizado (hoy queda desincronizado).
 * - La composicion declarada (adultos/menores/infantes) se persiste antes de
 *   exigir los nominales, para que backend y front coincidan.
 */

import { useState, useMemo } from "react";
import { X, Users, AlertTriangle, Loader2, Pencil } from "lucide-react";
import { api } from "../../../api";
import { showError, showSuccess } from "../../../alerts";
import { getApiErrorMessage } from "../../../lib/errors";

const DOC_TYPES = [
    { value: "DNI", label: "DNI" },
    { value: "Pasaporte", label: "Pasaporte" },
    { value: "CUIT", label: "CUIT" },
    { value: "CUIL", label: "CUIL" },
];

function buildSlots(adults, children, infants) {
    const slots = [];
    for (let i = 0; i < adults; i++) slots.push({ kind: "Adulto", index: i + 1 });
    for (let i = 0; i < children; i++) slots.push({ kind: "Menor", index: i + 1 });
    for (let i = 0; i < infants; i++) slots.push({ kind: "Infante", index: i + 1 });
    return slots;
}

export function ConfirmReservaModal({ reserva, readiness, onClose, onConfirmed, targetStatus = "InManagement" }) {
    const detectedAdults = readiness?.expectedAdults ?? 0;
    const detectedChildren = readiness?.expectedChildren ?? 0;
    const detectedInfants = readiness?.expectedInfants ?? 0;
    const isAmbiguous = readiness?.ambiguousComposition ?? false;
    const alreadyLoadedCount = readiness?.currentPassengerCount ?? 0;

    const [editingComp, setEditingComp] = useState(false);
    const [adults, setAdults] = useState(detectedAdults);
    const [children, setChildren] = useState(detectedChildren);
    const [infants, setInfants] = useState(detectedInfants);

    const slots = useMemo(() => buildSlots(adults, children, infants), [adults, children, infants]);
    const slotsToFill = slots.slice(alreadyLoadedCount);

    const [forms, setForms] = useState(() =>
        Array.from({ length: 100 }, () => ({ fullName: "", documentType: "DNI", documentNumber: "" }))
    );
    const [submitting, setSubmitting] = useState(false);

    // Regla Gaston 2026-06-08: NUNCA avanzar con 0 pasajeros.
    // La suma de adultos+menores+infantes debe ser al menos 1.
    const totalPasajeros = adults + children + infants;
    const tieneCeroPasajeros = totalPasajeros === 0;

    const allValid = slotsToFill.every((_, i) =>
        forms[i].fullName.trim().length >= 3 &&
        (forms[i].documentNumber || "").trim().length > 0
    );

    const updateForm = (i, patch) => {
        setForms(prev => prev.map((f, idx) => idx === i ? { ...f, ...patch } : f));
    };

    const handleSubmit = async () => {
        // Guard: 0 pasajeros nunca debe pasar (el botón ya debería estar deshabilitado,
        // pero re-verificamos por si acaso).
        if (tieneCeroPasajeros) {
            showError("Tiene que haber al menos 1 pasajero antes de continuar.");
            return;
        }
        if (slotsToFill.length > 0 && !allValid) {
            showError("Completa nombre y documento de cada pasajero antes de continuar.");
            return;
        }
        setSubmitting(true);
        try {
            // PASO 0) Persistir la composición declarada (adultos/menores/infantes) ANTES
            //         de hacer cualquier otra cosa. El backend exige que la composición
            //         esté guardada explícitamente antes de aceptar pasajeros nominales.
            //
            //         Este PATCH solo está habilitado en estados Cotización/Presupuesto
            //         (Quotation/Budget). Estamos en ese punto exactamente: el usuario
            //         está aprobando el presupuesto y todavía no avanzamos el estado.
            //
            //         Si falla (por ejemplo, el backend rechaza 0 pasajeros o hay un
            //         error de red), cortamos aquí y NO cargamos nominales ni avanzamos.
            await api.patch(`/reservas/${reserva.publicId}/passenger-counts`, {
                adultCount: adults,
                childCount: children,
                infantCount: infants,
            });

            // PASO 1) Crear los pasajeros faltantes uno a uno.
            //         Si alguno falla, cortamos acá y NO intentamos el cambio de estado.
            //         Mostramos el error real para que el usuario sepa qué pasó.
            for (let i = 0; i < slotsToFill.length; i++) {
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

            // PASO 2) Disparar la transicion al estado destino.
            //         Solo llegamos acá si la composición se persistió Y todos los
            //         pasajeros se cargaron exitosamente.
            await api.put(`/reservas/${reserva.publicId}/status`, { status: targetStatus });
            showSuccess("Reserva en gestion");

            // PASO 3) Avisamos al padre que confirmo; el padre recarga la reserva
            //         para que el front quede sincronizado con el estado real del backend.
            onConfirmed();
        } catch (error) {
            // Mostramos el mensaje real del backend para que el usuario entienda que paso.
            // No encadenamos los pasos siguientes si cualquier paso fallo.
            showError(getApiErrorMessage(error, "No se pudo avanzar la reserva. Revisá los datos e intentá de nuevo."));
        } finally {
            setSubmitting(false);
        }
    };

    const blockingNonPax = (readiness?.blockingReasons || []).filter(r => !r.toLowerCase().includes("pasajero"));
    const numInputClass = "w-16 rounded-md border border-slate-200 bg-white px-2 py-1.5 text-center text-base font-bold text-slate-900 focus:border-indigo-500 focus:outline-none dark:border-slate-700 dark:bg-slate-800 dark:text-white";

    // El boton de continuar se deshabilita si:
    // - hay bloqueos no relacionados a pasajeros
    // - quedan formularios de pasajero sin completar
    // - el total de pasajeros declarados es 0 (regla Gaston 2026-06-08)
    const botonDeshabilitado =
        submitting ||
        blockingNonPax.length > 0 ||
        (slotsToFill.length > 0 && !allValid) ||
        tieneCeroPasajeros;

    return (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 backdrop-blur-sm p-4 animate-in fade-in duration-200">
            <div className="w-full max-w-2xl rounded-2xl border bg-card shadow-2xl max-h-[90vh] overflow-y-auto">
                <div className="px-6 py-4 border-b bg-slate-50/50 dark:bg-slate-900/50 flex items-center justify-between">
                    <div className="flex items-center gap-2">
                        <Users className="h-5 w-5 text-indigo-600" />
                        <div>
                            <h3 className="text-lg font-bold text-slate-900 dark:text-white">
                                Pasar a En gestion
                            </h3>
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
                                    <strong className="font-bold">No se puede avanzar todavia:</strong>
                                    <ul className="list-disc list-inside mt-1 space-y-1">
                                        {blockingNonPax.map((r, i) => <li key={i}>{r}</li>)}
                                    </ul>
                                </div>
                            </div>
                        </div>
                    )}

                    {/* Seccion de composicion de pasajeros: SIEMPRE visible porque
                        si el backend no detecto composicion el usuario debe definirla.
                        Ademas, totalPasajeros=0 necesita que el usuario corrija. */}
                    <div className="rounded-lg border border-slate-200 bg-slate-50/50 p-3 text-sm dark:border-slate-700 dark:bg-slate-800/30">
                        <div className="flex flex-wrap items-center justify-between gap-2">
                            <div>
                                <div className="text-[11px] font-bold uppercase tracking-wider text-slate-500">Composicion de pasajeros</div>
                                {!editingComp ? (
                                    <div className="mt-1 text-slate-700 dark:text-slate-200">
                                        <strong>{adults}</strong> adultos · <strong>{children}</strong> menores · <strong>{infants}</strong> infantes
                                        <span className="ml-2 text-xs text-slate-500">({totalPasajeros} pasajeros)</span>
                                    </div>
                                ) : (
                                    <div className="mt-1 flex items-center gap-3">
                                        <label className="flex items-center gap-1 text-xs">
                                            <span className="text-slate-500">Adultos</span>
                                            <input type="number" min="0" value={adults} onChange={(e) => setAdults(Math.max(0, parseInt(e.target.value, 10) || 0))} className={numInputClass} />
                                        </label>
                                        <label className="flex items-center gap-1 text-xs">
                                            <span className="text-slate-500">Menores</span>
                                            <input type="number" min="0" value={children} onChange={(e) => setChildren(Math.max(0, parseInt(e.target.value, 10) || 0))} className={numInputClass} />
                                        </label>
                                        <label className="flex items-center gap-1 text-xs">
                                            <span className="text-slate-500">Infantes</span>
                                            <input type="number" min="0" value={infants} onChange={(e) => setInfants(Math.max(0, parseInt(e.target.value, 10) || 0))} className={numInputClass} />
                                        </label>
                                    </div>
                                )}
                            </div>
                            <button
                                type="button"
                                onClick={() => setEditingComp(v => !v)}
                                className="inline-flex items-center gap-1 rounded-md border border-slate-200 px-2 py-1 text-xs font-bold text-slate-600 hover:border-indigo-400 hover:text-indigo-600 dark:border-slate-700 dark:text-slate-300"
                            >
                                <Pencil className="h-3 w-3" />
                                {editingComp ? "Listo" : "Ajustar"}
                            </button>
                        </div>

                        {isAmbiguous && (
                            <div className="mt-2 flex items-start gap-2 rounded border border-amber-200 bg-amber-50 px-2 py-1.5 text-xs text-amber-800 dark:border-amber-800 dark:bg-amber-950/30 dark:text-amber-200">
                                <AlertTriangle className="h-3.5 w-3.5 flex-shrink-0 mt-0.5" />
                                <span>Algunos servicios declaran composicion distinta entre si. Verifica antes de continuar — podes ajustar arriba.</span>
                            </div>
                        )}

                        {/* Aviso de 0 pasajeros: regla de negocio Gaston 2026-06-08.
                            Una reserva nunca puede avanzar con 0 pasajeros. */}
                        {tieneCeroPasajeros && (
                            <div className="mt-2 flex items-start gap-2 rounded border border-rose-200 bg-rose-50 px-2 py-1.5 text-xs text-rose-800 dark:border-rose-800 dark:bg-rose-950/30 dark:text-rose-200">
                                <AlertTriangle className="h-3.5 w-3.5 flex-shrink-0 mt-0.5" />
                                <span>Tiene que haber al menos 1 pasajero. Ajustá la composicion antes de continuar.</span>
                            </div>
                        )}
                    </div>

                    {slotsToFill.length === 0 && !tieneCeroPasajeros ? (
                        <div className="rounded-lg border border-emerald-200 bg-emerald-50 p-4 text-sm text-emerald-800 dark:bg-emerald-950/30 dark:border-emerald-800 dark:text-emerald-200">
                            Todos los pasajeros ya estan cargados. Al continuar la reserva pasa a En gestion.
                        </div>
                    ) : !tieneCeroPasajeros && (
                        <>
                            <div className="rounded-lg border border-amber-200 bg-amber-50 p-3 text-sm text-amber-900 dark:bg-amber-950/30 dark:border-amber-800 dark:text-amber-200">
                                Carga los <strong>{slotsToFill.length}</strong> pasajero(s) faltantes con nombre y documento.
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
                        onClick={handleSubmit}
                        disabled={botonDeshabilitado}
                        data-testid="btn-confirm-advance-status"
                        className="px-4 py-2 rounded-lg text-sm font-bold text-white bg-cyan-600 hover:bg-cyan-700 transition-colors disabled:opacity-50 flex items-center gap-2"
                    >
                        {submitting ? <Loader2 className="h-4 w-4 animate-spin" /> : null}
                        {slotsToFill.length === 0 ? "Pasar a En gestion" : `Cargar pasajeros y pasar (${slotsToFill.length})`}
                    </button>
                </div>
            </div>
        </div>
    );
}
