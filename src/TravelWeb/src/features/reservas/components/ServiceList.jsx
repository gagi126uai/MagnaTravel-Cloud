/**
 * Lista de servicios contratados de una reserva.
 *
 * Muestra los servicios en dos layouts:
 *   - Desktop: tabla con columnas (Tipo, Descripción, Fecha/Estancia, Estado, Costo, Precio Venta, [Avisos], Acciones)
 *   - Mobile: tarjetas apiladas con la información clave
 *
 * Columna "Avisos" (UpcomingStartPill):
 *   Gateada por la prop isServiceDeadlineAlertsEnabled (flag EnableServiceDeadlineAlerts del backend).
 *   Decisión del dueño: catálogo OFF + avisos ON → la columna APARECE igual.
 *   Son dos flags independientes y la columna solo depende del flag de avisos.
 *
 * El gate de costo (quién ve el costo neto) cambia según el flag catálogo:
 *   - Flag OFF: isAdmin() (comportamiento original)
 *   - Flag ON:  hasPermission("cobranzas.see_cost") (admin sigue pasando porque admin tiene todo)
 *
 * Papelera borrar vs cancelar (decisión #9 guia UX 2026-06-08):
 *   - Servicio NO confirmado por el operador → "¿Borrar?" → desaparece de la reserva.
 *   - Servicio YA confirmado por el operador → "¿Cancelar?" → queda tachado (con motivo opcional).
 *   La decisión la toma el sistema solo según esServicioResuelto(svc).
 */

import React, { useCallback, useRef, useState } from 'react';
import { AlertTriangle, Plus, Plane, Hotel, Car, Package, ShieldCheck, Edit2, Trash2, CheckCircle2, Clock, X, Loader2 } from "lucide-react";
import { isAdmin, hasPermission } from "../../../auth";
import { api } from "../../../api";
import { showError, showSuccess } from "../../../alerts";
import { getApiErrorMessage } from "../../../lib/errors";
import {
    SERVICE_RECORD_KIND,
    getReservationServicePublicId
} from "../lib/reservationServiceModel";
import { UpcomingStartPill, estaEnVentana } from "./UpcomingStartPill";
import { CostConfirmCell, CostConfirmCellMobile } from "./CostConfirmCell";

/**
 * Determina si un servicio esta "resuelto" para el ciclo ADR-020.
 * Definicion por tipo (guia UX 2026-06-08):
 *   - Aereo: ticket EMITIDO (workflowStatus === "Emitido" o status === "TK"/"KL")
 *   - Hotel / Paquete: confirmado por el operador (workflowStatus === "Confirmado" o status HK/KK)
 *   - Asistencia: voucher emitido (workflowStatus === "Emitido")
 *   - Traslado: confirmado o marcado "no requiere confirmacion" (workflowStatus "Confirmado" | "NoConfirmation")
 */
function esServicioResuelto(svc) {
    const status = svc.workflowStatus || svc.status || '';
    const estadosResueltos = ['Confirmado', 'Emitido', 'HK', 'TK', 'KK', 'KL', 'NoConfirmation'];
    return estadosResueltos.includes(status);
}

/**
 * Texto de la pelotita de estado para un servicio que todavia NO esta resuelto.
 * Indica que falta para que ese servicio quede listo (decision 4 de UX).
 */
function textoFaltante(svc) {
    if (svc.recordKind === SERVICE_RECORD_KIND.FLIGHT) return "Falta emitir";
    if (svc.recordKind === SERVICE_RECORD_KIND.TRANSFER) return "Sin confirmar";
    if (svc.recordKind === SERVICE_RECORD_KIND.ASSISTANCE) return "Falta voucher";
    return "Pendiente";
}

/**
 * Determina si un servicio esta "confirmado por el operador" para la lógica borrar-vs-cancelar.
 *
 * Usa los mismos estados que esServicioResuelto porque un servicio resuelto implica
 * que el operador ya confirmó el compromiso (emitió ticket, confirmó hotel, etc.).
 * Decisión #9 guia UX: si está confirmado → cancelar (queda tachado); si no → borrar.
 */
export function esServicioConfirmadoPorOperador(svc) {
    return esServicioResuelto(svc);
}

/**
 * Formatea una fecha para mostrar en la tabla. Devuelve '-' si el valor
 * no existe o no es una fecha válida (evita que aparezca "Invalid Date").
 *
 * Se usa en la columna Fecha/Estancia para fechas de inicio Y fin,
 * porque Paquete puede no tener fecha de fin informada por el operador.
 */
function formatFechaSegura(valor) {
    if (!valor) return '-';
    const fecha = new Date(valor);
    // getTime() devuelve NaN si la fecha no es válida
    if (Number.isNaN(fecha.getTime())) return '-';
    return fecha.toLocaleDateString('es-AR');
}

/**
 * Modal de confirmación para borrar o cancelar un servicio.
 *
 * Decisión #9 (guia UX 2026-06-08):
 * - Servicio NO confirmado: texto "¿Borrar?" → llama onBorrar.
 * - Servicio CONFIRMADO: texto "¿Cancelar?" + campo motivo opcional → llama onCancelar.
 *
 * Props:
 * - service: objeto del servicio
 * - onBorrar: () => void — callback cuando el usuario confirma borrar
 * - onCancelar: (motivo: string|null) => void — callback cuando confirma cancelar
 * - onClose: () => void
 */
function ModalBorrarVsCancelar({ service, onBorrar, onCancelar, onClose }) {
    const estaConfirmado = esServicioConfirmadoPorOperador(service);
    const [motivo, setMotivo] = useState('');
    const [loading, setLoading] = useState(false);
    const motivoInputRef = useRef(null);

    const handleCerrarConEscape = (e) => {
        if (e.key === 'Escape') onClose();
    };

    const handleConfirmar = async () => {
        setLoading(true);
        try {
            if (estaConfirmado) {
                await onCancelar(motivo.trim() || null);
            } else {
                await onBorrar();
            }
        } finally {
            setLoading(false);
        }
    };

    return (
        <div
            className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 backdrop-blur-sm p-4 animate-in fade-in duration-200"
            role="dialog"
            aria-modal="true"
            aria-label={estaConfirmado ? 'Cancelar servicio' : 'Borrar servicio'}
            onKeyDown={handleCerrarConEscape}
        >
            <div className="w-full max-w-sm rounded-2xl border bg-white shadow-2xl dark:bg-slate-900 dark:border-slate-800">
                {/* Header */}
                <div className="flex items-center justify-between border-b border-slate-100 px-6 py-4 dark:border-slate-800">
                    <div className="flex items-center gap-2">
                        <Trash2 className="h-4 w-4 text-slate-500" />
                        <h3 className="font-bold text-slate-900 dark:text-white">
                            {estaConfirmado ? 'Cancelar servicio' : 'Borrar servicio'}
                        </h3>
                    </div>
                    <button
                        type="button"
                        onClick={onClose}
                        aria-label="Cerrar"
                        className="text-slate-400 hover:text-slate-600 transition-colors dark:hover:text-slate-200"
                    >
                        <X className="h-5 w-5" />
                    </button>
                </div>

                <div className="p-6 space-y-4">
                    {estaConfirmado ? (
                        // Servicio ya confirmado por el operador: NO se puede borrar, solo cancelar.
                        // Quedará tachado en la lista con quién/cuándo.
                        <>
                            <p className="text-sm text-slate-600 dark:text-slate-300">
                                <span className="font-bold">Este servicio ya está confirmado.</span>{' '}
                                Al cancelarlo, queda tachado en la reserva (no desaparece) — hubo un compromiso real con el operador y puede haber penalidad o saldo a devolver al cliente.
                            </p>
                            <div>
                                <label
                                    htmlFor="motivo-cancelacion-servicio"
                                    className="mb-1 block text-xs font-bold uppercase tracking-wider text-slate-500 dark:text-slate-400"
                                >
                                    Motivo (opcional)
                                </label>
                                <textarea
                                    id="motivo-cancelacion-servicio"
                                    ref={motivoInputRef}
                                    value={motivo}
                                    onChange={(e) => setMotivo(e.target.value)}
                                    placeholder="¿Por qué se cancela este servicio?"
                                    rows={2}
                                    className="w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm text-slate-900 placeholder:text-slate-400 focus:border-indigo-500 focus:outline-none dark:border-slate-700 dark:bg-slate-800 dark:text-white"
                                />
                            </div>
                        </>
                    ) : (
                        // Servicio no confirmado: se puede borrar directo (era un borrador).
                        <p className="text-sm text-slate-600 dark:text-slate-300">
                            <span className="font-bold">¿Borrar este servicio?</span>{' '}
                            El operador todavía no lo confirmó, así que desaparece de la reserva sin ningún trámite adicional.
                        </p>
                    )}
                </div>

                <div className="flex justify-end gap-3 border-t border-slate-100 px-6 py-4 dark:border-slate-800">
                    <button
                        type="button"
                        onClick={onClose}
                        disabled={loading}
                        className="rounded-lg px-4 py-2 text-sm font-bold text-slate-600 hover:bg-slate-100 disabled:opacity-50 transition-colors dark:text-slate-300 dark:hover:bg-slate-800"
                    >
                        Volver
                    </button>
                    <button
                        type="button"
                        onClick={handleConfirmar}
                        disabled={loading}
                        data-testid={estaConfirmado ? 'btn-confirm-cancel-service' : 'btn-confirm-delete-service'}
                        className={`flex items-center gap-2 rounded-lg px-4 py-2 text-sm font-bold text-white transition-colors disabled:opacity-50 ${
                            estaConfirmado
                                ? 'bg-amber-600 hover:bg-amber-700'
                                : 'bg-rose-600 hover:bg-rose-700'
                        }`}
                    >
                        {loading && <Loader2 className="h-4 w-4 animate-spin" />}
                        {estaConfirmado ? 'Cancelar servicio' : 'Sí, borrar'}
                    </button>
                </div>
            </div>
        </div>
    );
}

/**
 * Boton para marcar un vuelo como emitido.
 * Decision 3 (guia UX 2026-06-08): boton en la misma fila, texto "Marcar emitido".
 * Endpoint: POST /api/reservas/{reservaId}/flights/{id}/mark-issued
 */
function BotonMarcarEmitido({ reservaId, servicePublicId, onResuelto }) {
    const [loading, setLoading] = useState(false);

    const handleClick = async (e) => {
        e.stopPropagation();
        setLoading(true);
        try {
            await api.post(`/reservas/${reservaId}/flights/${servicePublicId}/mark-issued`);
            showSuccess("Vuelo marcado como emitido");
            if (onResuelto) onResuelto();
        } catch (error) {
            showError(getApiErrorMessage(error, "No se pudo marcar el vuelo como emitido."));
        } finally {
            setLoading(false);
        }
    };

    return (
        <button
            type="button"
            onClick={handleClick}
            disabled={loading}
            data-testid="btn-mark-issued"
            className="inline-flex items-center gap-1 rounded-lg border border-emerald-200 bg-emerald-50 px-2 py-1 text-[10px] font-bold text-emerald-700 transition-colors hover:bg-emerald-100 disabled:opacity-50 dark:border-emerald-800 dark:bg-emerald-950/30 dark:text-emerald-300"
        >
            <CheckCircle2 className="h-3 w-3" />
            {loading ? "..." : "Marcar emitido"}
        </button>
    );
}

/**
 * Boton para marcar un traslado como "no requiere confirmacion".
 * Decision 3 (guia UX 2026-06-08): boton en la misma fila, texto "No requiere confirmacion".
 * Endpoint: POST /api/reservas/{reservaId}/transfers/{id}/no-confirmation
 */
function BotonNoRequiereConfirmacion({ reservaId, servicePublicId, onResuelto }) {
    const [loading, setLoading] = useState(false);

    const handleClick = async (e) => {
        e.stopPropagation();
        setLoading(true);
        try {
            await api.post(`/reservas/${reservaId}/transfers/${servicePublicId}/no-confirmation`);
            showSuccess("Traslado marcado como que no requiere confirmacion");
            if (onResuelto) onResuelto();
        } catch (error) {
            showError(getApiErrorMessage(error, "No se pudo registrar el traslado."));
        } finally {
            setLoading(false);
        }
    };

    return (
        <button
            type="button"
            onClick={handleClick}
            disabled={loading}
            data-testid="btn-no-confirmation"
            className="inline-flex items-center gap-1 rounded-lg border border-slate-200 bg-slate-50 px-2 py-1 text-[10px] font-bold text-slate-600 transition-colors hover:bg-slate-100 disabled:opacity-50 dark:border-slate-700 dark:bg-slate-800 dark:text-slate-300"
        >
            <Clock className="h-3 w-3" />
            {loading ? "..." : "No requiere confirmacion"}
        </button>
    );
}

/**
 * Resumen de resolucion de servicios para la vista "En gestion" (decision 4 de UX).
 * Muestra "X de Y servicios resueltos" con pelotita verde (resuelto) / amarilla (pendiente) por fila.
 * Solo aparece cuando la reserva esta en estado InManagement.
 */
function ResumenServiciosResueltos({ services, reservaStatus }) {
    if (reservaStatus !== 'InManagement') return null;

    // No contar servicios cancelados
    const activos = services.filter(s => (s.workflowStatus || '') !== 'Cancelado');
    if (activos.length === 0) return null;

    const resueltos = activos.filter(esServicioResuelto).length;

    return (
        <div
            className="mb-4 flex items-center gap-3 rounded-xl border border-cyan-200 bg-cyan-50 px-4 py-2.5 text-sm dark:border-cyan-900/40 dark:bg-cyan-950/20"
            data-testid="resumen-servicios-resueltos"
            data-resolved={resueltos}
            data-total={activos.length}
        >
            <span className="font-bold text-cyan-800 dark:text-cyan-200">
                {resueltos} de {activos.length} {activos.length === 1 ? 'servicio resuelto' : 'servicios resueltos'}
            </span>
            {resueltos === activos.length ? (
                <span className="text-xs text-cyan-600 dark:text-cyan-400">
                    Todos listos — la reserva se va a confirmar automaticamente.
                </span>
            ) : (
                <span className="text-xs text-cyan-600 dark:text-cyan-400">
                    Cuando se resuelvan todos, la reserva se confirma sola.
                </span>
            )}
        </div>
    );
}

/**
 * Icono representativo del tipo de servicio en la lista.
 * Cada tipo tiene su color canonico para diferenciarlos visualmente de un vistazo.
 */
function ServiceIcon({ service, className = "w-4 h-4 mr-2" }) {
    if (service.recordKind === SERVICE_RECORD_KIND.FLIGHT) {
        return <Plane className={`${className} text-sky-500`} />;
    }
    if (service.recordKind === SERVICE_RECORD_KIND.HOTEL) {
        return <Hotel className={`${className} text-amber-500`} />;
    }
    if (service.recordKind === SERVICE_RECORD_KIND.TRANSFER) {
        return <Car className={`${className} text-emerald-500`} />;
    }
    // Asistencia / seguro de viaje: escudo de proteccion en azul (color "confianza")
    if (service.recordKind === SERVICE_RECORD_KIND.ASSISTANCE) {
        return <ShieldCheck className={`${className} text-blue-500`} />;
    }

    return <Package className={`${className} text-violet-500`} />;
}


/**
 * Props:
 *   services                        — lista de servicios normalizados
 *   serviceCollectionErrors         — objeto { tipoKey: mensajeError } para mostrar errores de carga
 *   onAddService                    — callback para agregar un servicio nuevo
 *   onEditService                   — callback(service) para editar un servicio existente
 *   onDeleteService                 — callback(service) para eliminar un servicio (NO confirmado por operador)
 *   onCancelService                 — callback(service, motivo|null) para cancelar un servicio confirmado por operador
 *   reservaId                       — publicId de la reserva (necesario para los endpoints confirm-cost y resolver)
 *   reservaStatus                   — status actual de la reserva (para mostrar resumen InManagement y pelotitas)
 *   isCatalogFindOrCreateEnabled    — flag catálogo: cuando es false, el render es IDÉNTICO al original
 *   isServiceDeadlineAlertsEnabled  — flag avisos: cuando es true, muestra columna "Avisos" (UpcomingStartPill).
 *                                     Es INDEPENDIENTE del flag de catálogo (decisión del dueño).
 *   windowDays                      — int|null, días de ventana para las pills (upcomingStartsWindowDays del contexto)
 *   onServiceConfirmed              — callback(servicioActualizado) cuando confirm-cost tiene éxito
 *   onServiceResolved               — callback() cuando un servicio se resuelve (marcar emitido / no requiere confirmacion)
 *                                     El padre recarga la reserva para reflejar el nuevo estado.
 */
export function ServiceList({
    services,
    serviceCollectionErrors = {},
    onAddService,
    onEditService,
    onDeleteService,
    onCancelService,
    reservaId,
    reservaStatus,
    isCatalogFindOrCreateEnabled = false,
    isServiceDeadlineAlertsEnabled = false,
    windowDays = null,
    onServiceConfirmed,
    onServiceResolved,
}) {
    // Gate de costo: con flag OFF se usa isAdmin() (comportamiento original).
    // Con flag ON se usa hasPermission("cobranzas.see_cost") — admin pasa igual (bypass en hasPermission).
    const mostrarCosto = isCatalogFindOrCreateEnabled
        ? hasPermission("cobranzas.see_cost")
        : isAdmin();

    // Solo quien ve costos Y tiene flag ON puede interactuar con confirm-cost.
    const puedeConfirmarCosto = isCatalogFindOrCreateEnabled && mostrarCosto;

    const collectionErrorMessages = Object.values(serviceCollectionErrors).filter(Boolean);

    // Estado del modal de borrar vs cancelar (decisión #9 guia UX 2026-06-08).
    // El modal se abre al presionar la papelera de cualquier servicio.
    const [modalBorrarCancelar, setModalBorrarCancelar] = useState(null);

    /**
     * Maneja el clic en la papelera de un servicio.
     * Decide solo si mostrar el modal de "¿Borrar?" o "¿Cancelar?" según si el servicio
     * está confirmado por el operador (esServicioResuelto).
     * Decisión #9 (guia UX 2026-06-08).
     */
    const handleTrashClick = useCallback((svc) => {
        setModalBorrarCancelar(svc);
    }, []);

    const handleModalBorrar = useCallback(async () => {
        if (!modalBorrarCancelar) return;
        if (onDeleteService) {
            await onDeleteService(modalBorrarCancelar);
        }
        setModalBorrarCancelar(null);
    }, [modalBorrarCancelar, onDeleteService]);

    const handleModalCancelar = useCallback(async (motivo) => {
        if (!modalBorrarCancelar) return;
        if (onCancelService) {
            await onCancelService(modalBorrarCancelar, motivo);
        }
        setModalBorrarCancelar(null);
    }, [modalBorrarCancelar, onCancelService]);

    /**
     * Fábrica de callbacks confirm-cost por servicio.
     * El DTO que devuelve el backend NO tiene recordKind (lo agrega el frontend al normalizar).
     * Por eso necesitamos pasarle el recordKind original del servicio al padre para que
     * pueda hacer el upsert en la colección correcta del snapshot de la reserva.
     */
    const crearCallbackConfirmado = useCallback((recordKind) => {
        return (servicioActualizado) => {
            if (onServiceConfirmed) {
                // Pasamos el recordKind junto al DTO para que el padre sepa en qué colección insertar
                onServiceConfirmed(servicioActualizado, recordKind);
            }
        };
    }, [onServiceConfirmed]);

    return (
        <div>
            {/* Modal de borrar vs cancelar: se abre al presionar la papelera de cualquier servicio.
                El sistema decide solo el texto ("¿Borrar?" vs "¿Cancelar?") según si el operador confirmó. */}
            {modalBorrarCancelar && (
                <ModalBorrarVsCancelar
                    service={modalBorrarCancelar}
                    onBorrar={handleModalBorrar}
                    onCancelar={handleModalCancelar}
                    onClose={() => setModalBorrarCancelar(null)}
                />
            )}

            <div className="flex flex-col sm:flex-row justify-between items-start sm:items-center gap-4 mb-4">
                <h3 className="text-lg font-medium text-gray-900 dark:text-white">Servicios Contratados</h3>
                <button
                    onClick={onAddService}
                    className="w-full sm:w-auto flex items-center justify-center gap-2 bg-blue-600 text-white px-4 py-2 rounded-lg hover:bg-blue-700 transition-colors shadow-sm"
                >
                    <Plus className="w-4 h-4" /> Agregar Servicio
                </button>
            </div>

            {/* Resumen "X de Y servicios resueltos": solo aparece en estado InManagement (decision 4 de UX) */}
            <ResumenServiciosResueltos services={services} reservaStatus={reservaStatus} />

            {collectionErrorMessages.length > 0 ? (
                <div className="mb-4 rounded-xl border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-800 dark:border-amber-900/40 dark:bg-amber-950/20 dark:text-amber-300">
                    <div className="flex items-start gap-3">
                        <AlertTriangle className="mt-0.5 h-4 w-4 flex-shrink-0" />
                        <div className="space-y-1">
                            <div className="font-semibold">La reserva sigue visible, pero una o mas listas de servicios no se pudieron refrescar.</div>
                            {collectionErrorMessages.map((message) => (
                                <div key={message}>{message}</div>
                            ))}
                        </div>
                    </div>
                </div>
            ) : null}

            {services.length === 0 ? (
                <div className="text-center py-12 bg-gray-50 dark:bg-slate-800 rounded-lg border border-dashed border-gray-300 dark:border-slate-700">
                    <Plane className="w-12 h-12 text-gray-300 dark:text-slate-600 mx-auto mb-3" />
                    <p className="text-gray-500 dark:text-slate-400">No hay servicios cargados en este file.</p>
                </div>
            ) : (
                <>
                    {/* Desktop Table View */}
                    <div className="hidden md:block overflow-hidden">
                        <table className="min-w-full text-left border-collapse">
                            <thead>
                                <tr className="border-b border-slate-100 dark:border-slate-800">
                                    <th className="pb-3 text-xs uppercase text-slate-400 font-medium">Tipo</th>
                                    <th className="pb-3 text-xs uppercase text-slate-400 font-medium">Descripción</th>
                                    <th className="pb-3 text-xs uppercase text-slate-400 font-medium">Fecha / Estancia</th>
                                    <th className="pb-3 text-xs uppercase text-slate-400 font-medium">Estado</th>
                                    {mostrarCosto && <th className="pb-3 text-xs uppercase text-slate-400 font-medium text-right pr-4">Costo Neto</th>}
                                    <th className="pb-3 text-xs uppercase text-slate-400 font-medium text-right pr-4">Precio Venta</th>
                                    {/* Columna Avisos: aparece cuando enableServiceDeadlineAlerts está ON.
                                        Es independiente del flag de catálogo (decisión del dueño). */}
                                    {isServiceDeadlineAlertsEnabled && (
                                        <th className="pb-3 text-xs uppercase text-slate-400 font-medium pr-4">Avisos</th>
                                    )}
                                    <th className="pb-3 text-xs uppercase text-slate-400 font-medium text-right pr-4">Acciones</th>
                                </tr>
                            </thead>
                            <tbody>
                                {services.map((svc) => {
                                    const netCost = svc.netCost || 0;
                                    const isGeneric = svc.recordKind === SERVICE_RECORD_KIND.GENERIC;
                                    const displayType = svc.displayType || svc._type || 'Servicio';
                                    const serviceKey = `${svc.recordKind || displayType}-${getReservationServicePublicId(svc)}`;

                                    return (
                                        <tr key={serviceKey} className="group border-b border-slate-50 dark:border-slate-800/50 hover:bg-slate-50/50 dark:hover:bg-slate-800/20 transition-colors">
                                            <td className="py-4 align-middle whitespace-nowrap pr-4">
                                                <div className="flex items-center">
                                                    <ServiceIcon service={svc} />
                                                    <span className="text-xs font-bold uppercase tracking-wider text-slate-500 dark:text-slate-400">{displayType}</span>
                                                    {isGeneric && (
                                                        <span className="ml-2 text-[9px] font-bold uppercase tracking-wider rounded-full bg-slate-100 dark:bg-slate-800 text-slate-500 px-2 py-0.5">
                                                            Generico
                                                        </span>
                                                    )}
                                                </div>
                                            </td>
                                            <td className="py-4 align-middle">
                                                <div className="text-sm font-semibold text-slate-900 dark:text-white line-clamp-1">{svc.name}</div>
                                                <div className="flex flex-wrap gap-2 mt-1">
                                                    {/* FIX 4: pill "creado en venta" eliminada (no aporta al usuario) */}
                                                    {svc.pnr && (
                                                        <span className="text-[10px] bg-slate-100 dark:bg-slate-800 text-slate-600 dark:text-slate-400 px-1.5 py-0.5 rounded font-mono font-bold">PNR: {svc.pnr}</span>
                                                    )}
                                                    {svc.confirmationNumber && (
                                                        <span className="text-[10px] bg-emerald-50 dark:bg-emerald-900/20 text-emerald-700 dark:text-emerald-400 px-1.5 py-0.5 rounded flex items-center gap-1 font-medium">
                                                            <ShieldCheck className="w-3 h-3" /> {svc.confirmationNumber}
                                                        </span>
                                                    )}
                                                    {svc.recordKind === SERVICE_RECORD_KIND.FLIGHT && svc.origin && (
                                                        <span className="text-[10px] text-slate-400">{svc.origin} ➔ {svc.destination}</span>
                                                    )}
                                                    {svc.recordKind === SERVICE_RECORD_KIND.TRANSFER && svc.pickupLocation && (
                                                        <span className="text-[10px] text-slate-400">{svc.pickupLocation} ➔ {svc.dropoffLocation}</span>
                                                    )}
                                                    {/* Datos clave de la asistencia: poliza, plan y zona */}
                                                    {svc.recordKind === SERVICE_RECORD_KIND.ASSISTANCE && svc.policyNumber && (
                                                        <span className="text-[10px] bg-blue-50 dark:bg-blue-900/20 text-blue-700 dark:text-blue-400 px-1.5 py-0.5 rounded font-mono font-bold">Poliza: {svc.policyNumber}</span>
                                                    )}
                                                    {svc.recordKind === SERVICE_RECORD_KIND.ASSISTANCE && svc.planType && (
                                                        <span className="text-[10px] bg-slate-100 dark:bg-slate-800 text-slate-600 dark:text-slate-400 px-1.5 py-0.5 rounded">{svc.planType}</span>
                                                    )}
                                                    {svc.recordKind === SERVICE_RECORD_KIND.ASSISTANCE && svc.coverageZone && (
                                                        <span className="text-[10px] text-slate-400">{svc.coverageZone}</span>
                                                    )}
                                                </div>
                                            </td>
                                            <td className="py-4 align-middle whitespace-nowrap text-xs text-slate-600 dark:text-slate-400">
                                                {svc.recordKind === SERVICE_RECORD_KIND.HOTEL || svc.recordKind === SERVICE_RECORD_KIND.PACKAGE ? (
                                                    <div className="flex flex-col">
                                                        <span>{formatFechaSegura(svc.date || svc.startDate || svc.checkIn)}</span>
                                                        {/* FIX: solo mostrar "al ..." si hay fecha de fin válida.
                                                            Paquete puede no tener endDate si el operador no la informa. */}
                                                        {formatFechaSegura(svc.endDate || svc.checkOut) !== '-' && (
                                                            <span className="text-[10px] opacity-60">al {formatFechaSegura(svc.endDate || svc.checkOut)}</span>
                                                        )}
                                                    </div>
                                                ) : svc.recordKind === SERVICE_RECORD_KIND.ASSISTANCE ? (
                                                    // Asistencia: muestra vigencia desde/hasta (fechas date-only)
                                                    <div className="flex flex-col">
                                                        <span>{formatFechaSegura(svc.validFrom)}</span>
                                                        {formatFechaSegura(svc.validTo) !== '-' && (
                                                            <span className="text-[10px] opacity-60">al {formatFechaSegura(svc.validTo)}</span>
                                                        )}
                                                    </div>
                                                ) : (
                                                    formatFechaSegura(svc.date)
                                                )}
                                            </td>
                                            <td className="py-4 align-middle whitespace-nowrap">
                                                <div className="flex flex-col gap-1.5">
                                                    <span className={`px-2 py-0.5 rounded text-[10px] font-semibold uppercase tracking-wider ${
                                                        svc.workflowStatus === 'Confirmado' ? 'bg-green-100 text-green-700 dark:bg-green-900/30 dark:text-green-400' :
                                                        svc.workflowStatus === 'Cancelado' ? 'bg-rose-100 text-rose-700 dark:bg-rose-900/30 dark:text-rose-400' :
                                                        'bg-amber-100 text-amber-700 dark:bg-amber-900/30 dark:text-amber-400'
                                                    }`}>
                                                        {svc.workflowStatus || 'Solicitado'}
                                                    </span>
                                                    {/* Pelotita ADR-020 (decision 4): solo en estado InManagement.
                                                        Verde = resuelto; Amarilla con texto = que falta resolver. */}
                                                    {reservaStatus === 'InManagement' && (
                                                        esServicioResuelto(svc) ? (
                                                            <span className="inline-flex items-center gap-1 text-[10px] font-semibold text-emerald-600 dark:text-emerald-400">
                                                                <span className="h-2 w-2 rounded-full bg-emerald-500 flex-shrink-0" aria-hidden="true" />
                                                                Resuelto
                                                            </span>
                                                        ) : (
                                                            <span className="inline-flex items-center gap-1 text-[10px] font-semibold text-amber-600 dark:text-amber-400">
                                                                <span className="h-2 w-2 rounded-full bg-amber-400 flex-shrink-0" aria-hidden="true" />
                                                                {textoFaltante(svc)}
                                                            </span>
                                                        )
                                                    )}
                                                </div>
                                            </td>

                                            {/* Celda de costo: dos ramas completamente separadas para garantizar
                                                que flag OFF = markup IDÉNTICO al de HEAD (sin diferencia de clase ni de elemento).
                                                - Rama flag ON + see_cost + tipo específico: td con align-top y CostConfirmCell
                                                - Rama genérica/flag OFF: td EXACTAMENTE igual al td original de HEAD */}
                                            {mostrarCosto && puedeConfirmarCosto && !isGeneric ? (
                                                // Flag ON + see_cost + tipo específico: celda con pill y botón de confirmación
                                                <td className="py-4 align-top text-right pr-4">
                                                    <CostConfirmCell
                                                        service={svc}
                                                        reservaId={reservaId}
                                                        onConfirmado={crearCallbackConfirmado(svc.recordKind)}
                                                    />
                                                </td>
                                            ) : mostrarCosto ? (
                                                // Flag OFF o genérico: td IDÉNTICO al HEAD (align-middle, sin span extra)
                                                <td className="py-4 align-middle text-right text-xs text-slate-500 font-mono pr-4">
                                                    ${netCost.toLocaleString(undefined, { minimumFractionDigits: 2 })}
                                                </td>
                                            ) : null}

                                            <td className="py-4 align-middle text-right text-xs font-bold text-slate-900 dark:text-white font-mono pr-4">
                                                ${(svc.salePrice || 0).toLocaleString(undefined, { minimumFractionDigits: 2 })}
                                            </td>

                                            {/* Columna Avisos: solo con flag enableServiceDeadlineAlerts ON.
                                                Usa UpcomingStartPill (fecha de inicio del servicio). */}
                                            {isServiceDeadlineAlertsEnabled && (
                                                <td className="py-4 align-middle pr-4 whitespace-nowrap">
                                                    <UpcomingStartPill service={svc} windowDays={windowDays} mostrarGuion={true} />
                                                </td>
                                            )}

                                            <td className="py-4 align-middle pr-4">
                                                <div className="flex flex-col items-end gap-1.5">
                                                    {/* Botones de resolver: decision 3 de UX (ADR-020).
                                                        Solo aparecen en InManagement Y cuando el servicio todavia no esta resuelto. */}
                                                    {reservaStatus === 'InManagement' && !esServicioResuelto(svc) && (
                                                        <>
                                                            {svc.recordKind === SERVICE_RECORD_KIND.FLIGHT && (
                                                                <BotonMarcarEmitido
                                                                    reservaId={reservaId}
                                                                    servicePublicId={getReservationServicePublicId(svc)}
                                                                    onResuelto={onServiceResolved}
                                                                />
                                                            )}
                                                            {svc.recordKind === SERVICE_RECORD_KIND.TRANSFER && (
                                                                <BotonNoRequiereConfirmacion
                                                                    reservaId={reservaId}
                                                                    servicePublicId={getReservationServicePublicId(svc)}
                                                                    onResuelto={onServiceResolved}
                                                                />
                                                            )}
                                                        </>
                                                    )}
                                                    {/* Desktop: icono + palabra siempre visible (spec UX 2026-06-08).
                                                        textoTacho es dinámico: "Cancelar" si el operador ya confirmó, "Borrar" si no.
                                                        aria-label y texto visible dicen lo mismo para coherencia con lectores de pantalla. */}
                                                    <div className="flex justify-end gap-1 transition-opacity">
                                                        <button
                                                            onClick={() => onEditService(svc)}
                                                            data-testid={`btn-edit-service-${getReservationServicePublicId(svc)}`}
                                                            aria-label="Editar servicio"
                                                            className="inline-flex items-center gap-1 p-1.5 text-blue-600 hover:bg-blue-50 dark:hover:bg-blue-900/20 rounded text-xs font-semibold"
                                                        >
                                                            <Edit2 className="w-4 h-4" />
                                                            Editar
                                                        </button>
                                                        {/* Papelera: abre el modal que decide borrar vs cancelar según decisión #9 */}
                                                        {(() => {
                                                            const textoTacho = esServicioConfirmadoPorOperador(svc) ? 'Cancelar' : 'Borrar';
                                                            return (
                                                                <button
                                                                    onClick={() => handleTrashClick(svc)}
                                                                    data-testid={`btn-delete-service-${getReservationServicePublicId(svc)}`}
                                                                    aria-label={`${textoTacho} servicio`}
                                                                    className="inline-flex items-center gap-1 p-1.5 text-red-600 hover:bg-red-50 dark:hover:bg-red-900/20 rounded text-xs font-semibold"
                                                                >
                                                                    <Trash2 className="w-4 h-4" />
                                                                    {textoTacho}
                                                                </button>
                                                            );
                                                        })()}
                                                    </div>
                                                </div>
                                            </td>
                                        </tr>
                                    );
                                })}
                            </tbody>
                        </table>
                    </div>

                    {/* Mobile View */}
                    <div className="md:hidden space-y-4">
                        {services.map((svc) => {
                            const netCost = svc.netCost || 0;
                            const isGeneric = svc.recordKind === SERVICE_RECORD_KIND.GENERIC;
                            const displayType = svc.displayType || svc._type || 'Servicio';
                            const serviceKey = `${svc.recordKind || displayType}-${getReservationServicePublicId(svc)}`;

                            // FIX 4: pill "creado en venta" eliminada. Solo queda la pill de próximo inicio.
                            // Sin pill no hay "—" en mobile (solo omitimos la línea entera).
                            // Pill de próximo inicio: solo con flag avisos ON, sin cancelado,
                            // Y con la fecha DENTRO de la ventana de alerta (no solo que exista).
                            // Si la fecha está fuera de ventana, UpcomingStartPill devuelve null en mobile
                            // → renderizar el div vacío produciría márgenes con nada adentro.
                            const tieneUpcomingPillMobile = isServiceDeadlineAlertsEnabled &&
                                svc.workflowStatus !== "Cancelado" &&
                                estaEnVentana(svc.date, windowDays);
                            const mostrarLineaPills = tieneUpcomingPillMobile;

                            return (
                                <div key={serviceKey} className="bg-white dark:bg-slate-900 p-4 rounded-xl border border-slate-100 dark:border-slate-800 shadow-sm">
                                    <div className="flex justify-between mb-2">
                                        <div className="flex items-center gap-2">
                                            <ServiceIcon service={svc} className="w-4 h-4" />
                                            <span className="text-xs font-bold text-slate-400 uppercase tracking-tighter">{displayType}</span>
                                            {isGeneric && (
                                                <span className="text-[9px] font-bold uppercase rounded-full bg-slate-100 dark:bg-slate-800 text-slate-500 px-2 py-0.5">
                                                    Generico
                                                </span>
                                            )}
                                        </div>
                                        <span className={`text-[10px] px-2 py-0.5 rounded font-bold uppercase tracking-wider ${
                                            svc.workflowStatus === 'Confirmado' ? 'bg-green-100 text-green-700 dark:bg-green-900/30' :
                                            svc.workflowStatus === 'Cancelado' ? 'bg-rose-100 text-rose-700 dark:bg-rose-900/30' :
                                            'bg-amber-100 text-amber-700 dark:bg-amber-900/30'
                                        }`}>
                                            {svc.workflowStatus || 'Solicitado'}
                                        </span>
                                    </div>
                                    <div className="font-medium text-slate-900 dark:text-white mb-1 line-clamp-1">{svc.name}</div>

                                    {/* Línea de pill de próximo inicio: solo si aplica */}
                                    {mostrarLineaPills && (
                                        <div className="flex flex-wrap gap-2 mt-1 mb-1">
                                            {tieneUpcomingPillMobile && (
                                                <UpcomingStartPill service={svc} windowDays={windowDays} mostrarGuion={false} />
                                            )}
                                        </div>
                                    )}

                                    <div className="flex justify-between items-end">
                                        <div className="text-[11px] text-slate-500 flex flex-col gap-0.5">
                                            <span>
                                                {svc.recordKind === SERVICE_RECORD_KIND.ASSISTANCE
                                                    ? (svc.validFrom ? `Vigencia: ${formatFechaSegura(svc.validFrom)}` : '-')
                                                    : formatFechaSegura(svc.date)}
                                                {/* FIX: solo agregar "al ..." si la fecha de fin es válida */}
                                                {(svc.recordKind === SERVICE_RECORD_KIND.HOTEL || svc.recordKind === SERVICE_RECORD_KIND.PACKAGE) &&
                                                    formatFechaSegura(svc.endDate || svc.checkOut) !== '-' &&
                                                    ` al ${formatFechaSegura(svc.endDate || svc.checkOut)}`}
                                                {svc.recordKind === SERVICE_RECORD_KIND.ASSISTANCE && svc.validTo &&
                                                    ` al ${formatFechaSegura(svc.validTo)}`}
                                            </span>
                                            <div className="flex gap-2 items-center mt-1">
                                                <span className="font-bold text-slate-900 dark:text-white">Venta: ${(svc.salePrice || 0).toLocaleString()}</span>
                                                {/* Costo en mobile: gateado igual que en desktop */}
                                                {mostrarCosto && (
                                                    puedeConfirmarCosto && !isGeneric ? (
                                                        // Con flag ON + see_cost: celda interactiva
                                                        <CostConfirmCellMobile
                                                            service={svc}
                                                            reservaId={reservaId}
                                                            onConfirmado={crearCallbackConfirmado(svc.recordKind)}
                                                        />
                                                    ) : (
                                                        // Sin flag o sin permiso: número simple
                                                        <span className="text-[9px] opacity-70">Costo: ${netCost.toLocaleString()}</span>
                                                    )
                                                )}
                                            </div>
                                            {/* Pelotita ADR-020 en mobile: solo en InManagement (decision 4) */}
                                            {reservaStatus === 'InManagement' && (
                                                esServicioResuelto(svc) ? (
                                                    <span className="inline-flex items-center gap-1 text-[10px] font-semibold text-emerald-600 dark:text-emerald-400 mt-1">
                                                        <span className="h-2 w-2 rounded-full bg-emerald-500 flex-shrink-0" aria-hidden="true" />
                                                        Resuelto
                                                    </span>
                                                ) : (
                                                    <span className="inline-flex items-center gap-1 text-[10px] font-semibold text-amber-600 dark:text-amber-400 mt-1">
                                                        <span className="h-2 w-2 rounded-full bg-amber-400 flex-shrink-0" aria-hidden="true" />
                                                        {textoFaltante(svc)}
                                                    </span>
                                                )
                                            )}
                                        </div>
                                        <div className="flex flex-col items-end gap-2">
                                            {/* Botones de resolver en mobile: decision 3 de UX */}
                                            {reservaStatus === 'InManagement' && !esServicioResuelto(svc) && (
                                                <>
                                                    {svc.recordKind === SERVICE_RECORD_KIND.FLIGHT && (
                                                        <BotonMarcarEmitido
                                                            reservaId={reservaId}
                                                            servicePublicId={getReservationServicePublicId(svc)}
                                                            onResuelto={onServiceResolved}
                                                        />
                                                    )}
                                                    {svc.recordKind === SERVICE_RECORD_KIND.TRANSFER && (
                                                        <BotonNoRequiereConfirmacion
                                                            reservaId={reservaId}
                                                            servicePublicId={getReservationServicePublicId(svc)}
                                                            onResuelto={onServiceResolved}
                                                        />
                                                    )}
                                                </>
                                            )}
                                            {/* Mobile: mismo patrón icono + palabra (spec UX 2026-06-08).
                                                textoTachoMobile sincronizado con la lógica desktop para no bifurcar. */}
                                            {(() => {
                                                const textoTachoMobile = esServicioConfirmadoPorOperador(svc) ? 'Cancelar' : 'Borrar';
                                                return (
                                                    <div className="flex items-center gap-2">
                                                        <button
                                                            onClick={() => onEditService(svc)}
                                                            data-testid={`btn-edit-service-mobile-${getReservationServicePublicId(svc)}`}
                                                            aria-label="Editar servicio"
                                                            className="inline-flex items-center gap-1 p-2 text-slate-500 rounded-lg bg-slate-50 dark:bg-slate-800 text-xs font-semibold"
                                                        >
                                                            <Edit2 className="w-3.5 h-3.5" />
                                                            Editar
                                                        </button>
                                                        {/* Papelera mobile: mismo modal borrar vs cancelar */}
                                                        <button
                                                            onClick={() => handleTrashClick(svc)}
                                                            data-testid={`btn-delete-service-mobile-${getReservationServicePublicId(svc)}`}
                                                            aria-label={`${textoTachoMobile} servicio`}
                                                            className="inline-flex items-center gap-1 p-2 text-red-500 rounded-lg bg-red-50 dark:bg-red-900/20 text-xs font-semibold"
                                                        >
                                                            <Trash2 className="w-3.5 h-3.5" />
                                                            {textoTachoMobile}
                                                        </button>
                                                    </div>
                                                );
                                            })()}
                                        </div>
                                    </div>
                                </div>
                            );
                        })}
                    </div>
                </>
            )}
        </div>
    );
}
