import { useEffect, useMemo, useRef, useState } from "react";
import { Users, Plus, X, Check } from "lucide-react";
import { api } from "../../../api";
import { showError, showSuccess } from "../../../alerts";
import { getApiErrorMessage } from "../../../lib/errors";

const SERVICE_TYPE_LABELS = {
    Hotel: "Hotel",
    Transfer: "Transfer",
    Package: "Paquete",
    Flight: "Vuelo",
    Generic: "Servicio",
};

/**
 * Lista de servicios asignables a partir de la reserva.
 * Devuelve [{ serviceType, publicId, label, sublabel, expectedPax }]
 */
function buildServiceList(reserva) {
    const list = [];
    (reserva.hotelBookings || []).forEach((h) => {
        list.push({
            serviceType: "Hotel",
            publicId: h.publicId,
            label: h.hotelName || "Hotel",
            sublabel: [h.city, h.roomType, h.rooms ? `${h.rooms} hab` : null].filter(Boolean).join(" • "),
            expectedPax: (h.adults || 0) + (h.children || 0),
        });
    });
    (reserva.transferBookings || []).forEach((t) => {
        list.push({
            serviceType: "Transfer",
            publicId: t.publicId,
            label: `Transfer ${t.vehicleType || ""}`.trim(),
            sublabel: [t.pickupLocation, t.dropoffLocation].filter(Boolean).join(" → "),
            expectedPax: t.passengers || 0,
        });
    });
    (reserva.packageBookings || []).forEach((p) => {
        list.push({
            serviceType: "Package",
            publicId: p.publicId,
            label: p.packageName || "Paquete",
            sublabel: p.destination || "",
            expectedPax: (p.adults || 0) + (p.children || 0),
        });
    });
    (reserva.flightSegments || []).forEach((f) => {
        list.push({
            serviceType: "Flight",
            publicId: f.publicId,
            label: `${f.airlineCode || ""} ${f.flightNumber || ""}`.trim() || "Vuelo",
            sublabel: [f.origin, f.destination].filter(Boolean).join(" → "),
            expectedPax: 0, // no se sabe sin tabla de pax-flight
        });
    });
    return list;
}

export function PassengerAssignmentsPanel({ reserva, isBudget }) {
    const [assignments, setAssignments] = useState([]);
    const [loading, setLoading] = useState(false);
    const [openServicePublicId, setOpenServicePublicId] = useState(null);
    const popoverRef = useRef(null);

    const services = useMemo(() => buildServiceList(reserva), [reserva]);
    const passengers = reserva.passengers || [];
    const reservaPublicId = reserva.publicId;

    const reload = async () => {
        if (!reservaPublicId) return;
        setLoading(true);
        try {
            const data = await api.get(`/reservas/${reservaPublicId}/assignments`);
            setAssignments(Array.isArray(data) ? data : []);
        } catch (error) {
            showError(getApiErrorMessage(error, "No se pudieron cargar las asignaciones."));
        } finally {
            setLoading(false);
        }
    };

    useEffect(() => {
        if (isBudget) return;
        reload();
        // Reload cuando cambia el publicId o el conteo de pasajeros (cargar/borrar pax desde otra parte)
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [reservaPublicId, isBudget, passengers.length]);

    // Cerrar popover al click afuera
    useEffect(() => {
        if (!openServicePublicId) return;
        const onDoc = (e) => {
            if (popoverRef.current && !popoverRef.current.contains(e.target)) {
                setOpenServicePublicId(null);
            }
        };
        document.addEventListener("mousedown", onDoc);
        return () => document.removeEventListener("mousedown", onDoc);
    }, [openServicePublicId]);

    const handleAssign = async (service, passenger) => {
        try {
            await api.post(`/reservas/${reservaPublicId}/assignments`, {
                passengerPublicIdOrLegacyId: passenger.publicId,
                serviceType: service.serviceType,
                servicePublicIdOrLegacyId: service.publicId,
                roomNumber: null,
                seatNumber: null,
                notes: null,
            });
            await reload();
        } catch (error) {
            showError(getApiErrorMessage(error, "No se pudo asignar el pasajero."));
        }
    };

    const handleUnassign = async (assignment) => {
        try {
            await api.delete(`/reservas/assignments/${assignment.publicId}`);
            showSuccess("Asignación eliminada");
            await reload();
        } catch (error) {
            showError(getApiErrorMessage(error, "No se pudo quitar la asignación."));
        }
    };

    if (isBudget) {
        return (
            <div className="rounded-xl border border-dashed border-slate-300 bg-slate-50/50 p-6 text-center text-sm text-slate-500 dark:border-slate-700 dark:bg-slate-800/30 dark:text-slate-400">
                <Users className="mx-auto mb-2 h-5 w-5 opacity-50" />
                Confirmá la reserva (pasala de Presupuesto a Reservado) para asignar pasajeros a los servicios.
            </div>
        );
    }

    if (services.length === 0) {
        return null; // No hay servicios → no hay nada que asignar
    }

    if (passengers.length === 0) {
        return (
            <div className="rounded-xl border border-dashed border-slate-300 bg-slate-50/50 p-6 text-center text-sm text-slate-500 dark:border-slate-700 dark:bg-slate-800/30 dark:text-slate-400">
                <Users className="mx-auto mb-2 h-5 w-5 opacity-50" />
                Cargá pasajeros nominales en la pestaña Pasajeros para poder asignarlos a los servicios.
            </div>
        );
    }

    return (
        <div className="rounded-xl border border-slate-200 bg-white shadow-sm dark:border-slate-800 dark:bg-slate-900/50">
            <div className="flex items-center justify-between border-b border-slate-100 bg-slate-50/40 px-5 py-3 dark:border-slate-800 dark:bg-slate-800/20">
                <div className="flex items-center gap-2">
                    <Users className="h-4 w-4 text-indigo-500" />
                    <h4 className="text-sm font-bold text-slate-900 dark:text-white">Asignación de pasajeros a servicios</h4>
                </div>
                {loading && <span className="text-[10px] text-slate-400">Actualizando…</span>}
            </div>

            <div className="divide-y divide-slate-100 dark:divide-slate-800">
                {services.map((service) => {
                    const serviceAssignments = assignments.filter(
                        (a) => a.serviceType === service.serviceType && a.servicePublicId === service.publicId
                    );
                    const assignedPaxIds = new Set(serviceAssignments.map((a) => a.passengerPublicId));
                    const unassigned = passengers.filter((p) => !assignedPaxIds.has(p.publicId));
                    const isOpen = openServicePublicId === service.publicId;

                    return (
                        <div key={`${service.serviceType}-${service.publicId}`} className="flex flex-wrap items-start gap-3 px-5 py-3">
                            <div className="min-w-[180px] flex-1">
                                <div className="text-xs font-bold uppercase tracking-wider text-slate-500 dark:text-slate-400">
                                    {SERVICE_TYPE_LABELS[service.serviceType] || service.serviceType}
                                </div>
                                <div className="text-sm font-semibold text-slate-900 dark:text-white">{service.label}</div>
                                {service.sublabel && (
                                    <div className="text-xs text-slate-500 dark:text-slate-400">{service.sublabel}</div>
                                )}
                                {service.expectedPax > 0 && (
                                    <div className={`mt-1 text-[10px] font-bold ${serviceAssignments.length > service.expectedPax ? "text-rose-600" : "text-slate-500"}`}>
                                        {serviceAssignments.length} / {service.expectedPax} pax
                                    </div>
                                )}
                            </div>

                            <div className="flex flex-1 flex-wrap items-center gap-1.5">
                                {serviceAssignments.length === 0 && (
                                    <span className="text-xs italic text-slate-400">Sin asignaciones</span>
                                )}
                                {serviceAssignments.map((a) => (
                                    <span key={a.publicId} className="inline-flex items-center gap-1 rounded-full bg-indigo-50 px-2.5 py-1 text-xs font-medium text-indigo-700 dark:bg-indigo-900/30 dark:text-indigo-300">
                                        {a.passengerFullName}
                                        <button
                                            type="button"
                                            onClick={() => handleUnassign(a)}
                                            className="ml-0.5 rounded-full p-0.5 text-indigo-400 hover:bg-indigo-100 hover:text-indigo-700 dark:hover:bg-indigo-900/50"
                                            title="Quitar asignación"
                                        >
                                            <X className="h-3 w-3" />
                                        </button>
                                    </span>
                                ))}

                                <div className="relative">
                                    <button
                                        type="button"
                                        onClick={() => setOpenServicePublicId(isOpen ? null : service.publicId)}
                                        disabled={unassigned.length === 0}
                                        className="inline-flex items-center gap-1 rounded-full border border-dashed border-slate-300 px-2 py-1 text-xs font-bold text-slate-500 hover:border-indigo-400 hover:text-indigo-600 disabled:cursor-not-allowed disabled:opacity-40 dark:border-slate-600 dark:text-slate-400"
                                        title={unassigned.length === 0 ? "Todos los pasajeros ya están asignados" : "Asignar pasajero"}
                                    >
                                        <Plus className="h-3 w-3" />
                                        Asignar
                                    </button>
                                    {isOpen && (
                                        <div ref={popoverRef} className="absolute right-0 z-20 mt-1 w-64 overflow-hidden rounded-lg border border-slate-200 bg-white shadow-lg dark:border-slate-700 dark:bg-slate-900">
                                            <div className="border-b border-slate-100 bg-slate-50 px-3 py-2 text-[10px] font-bold uppercase text-slate-500 dark:border-slate-800 dark:bg-slate-800 dark:text-slate-400">
                                                Pasajeros disponibles
                                            </div>
                                            <div className="max-h-56 overflow-y-auto">
                                                {unassigned.map((p) => (
                                                    <button
                                                        key={p.publicId}
                                                        type="button"
                                                        onClick={async () => {
                                                            await handleAssign(service, p);
                                                            setOpenServicePublicId(null);
                                                        }}
                                                        className="flex w-full items-center gap-2 px-3 py-2 text-left text-sm hover:bg-indigo-50 dark:hover:bg-indigo-900/20"
                                                    >
                                                        <Check className="h-3.5 w-3.5 text-emerald-500 opacity-0" />
                                                        <span className="flex-1 truncate text-slate-700 dark:text-slate-200">{p.fullName}</span>
                                                        {p.documentNumber && (
                                                            <span className="text-[10px] text-slate-400">{p.documentNumber}</span>
                                                        )}
                                                    </button>
                                                ))}
                                            </div>
                                        </div>
                                    )}
                                </div>
                            </div>
                        </div>
                    );
                })}
            </div>
        </div>
    );
}
