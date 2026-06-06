/**
 * Lista de servicios contratados de una reserva.
 *
 * Muestra los servicios en dos layouts:
 *   - Desktop: tabla con columnas (Tipo, Descripción, Fecha/Estancia, Estado, Costo, Precio Venta, [Avisos], Acciones)
 *   - Mobile: tarjetas apiladas con la información clave
 *
 * Feature gateada por flag EnableCatalogFindOrCreate:
 *   - Flag OFF: render IDÉNTICO al anterior (ni una clase distinta, ni el gate de permisos cambia)
 *   - Flag ON: agrega columna "Avisos" (deadline), pill violeta "creado en venta",
 *              pill ámbar "A confirmar" y botón "Confirmar costo" (este último solo para cobranzas.see_cost)
 *
 * El gate de costo (quién ve el costo neto) cambia según el flag:
 *   - Flag OFF: isAdmin() (comportamiento original)
 *   - Flag ON:  hasPermission("cobranzas.see_cost") (admin sigue pasando porque admin tiene todo)
 */

import React, { useCallback } from 'react';
import { AlertTriangle, Plus, Plane, Hotel, Car, Package, ShieldCheck, Edit2, Trash2 } from "lucide-react";
import { isAdmin, hasPermission } from "../../../auth";
import {
    SERVICE_RECORD_KIND,
    getReservationServicePublicId
} from "../lib/reservationServiceModel";
import { DeadlinePill } from "./DeadlinePill";
import { CostConfirmCell, CostConfirmCellMobile } from "./CostConfirmCell";

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
 * Pill violeta que indica que el producto fue creado durante la venta
 * (es un producto nuevo en el catálogo, no uno preexistente).
 *
 * Solo visible con flag ON. La ven todos (no requiere permiso especial).
 * La condición: productCreatedInSale === true en el DTO del servicio.
 */
function PillCreadoEnVenta({ service }) {
    if (!service.productCreatedInSale) return null;

    // Texto varía por tipo: "Asistencia creada en venta" (femenino) vs el resto "creado en venta"
    const textosPorTipo = {
        [SERVICE_RECORD_KIND.HOTEL]: "Hotel creado en venta",
        [SERVICE_RECORD_KIND.FLIGHT]: "Aéreo creado en venta",
        [SERVICE_RECORD_KIND.TRANSFER]: "Traslado creado en venta",
        [SERVICE_RECORD_KIND.PACKAGE]: "Paquete creado en venta",
        [SERVICE_RECORD_KIND.ASSISTANCE]: "Asistencia creada en venta",
    };

    const texto = textosPorTipo[service.recordKind];
    if (!texto) return null;

    return (
        <span
            className="text-[11px] font-semibold px-2 py-0.5 rounded-full bg-violet-200 text-violet-700 dark:bg-violet-900/30 dark:text-violet-300"
            data-testid="pill-created-in-sale"
        >
            {texto}
        </span>
    );
}

/**
 * Props:
 *   services                    — lista de servicios normalizados
 *   serviceCollectionErrors     — objeto { tipoKey: mensajeError } para mostrar errores de carga
 *   onAddService                — callback para agregar un servicio nuevo
 *   onEditService               — callback(service) para editar un servicio existente
 *   onDeleteService             — callback(service) para eliminar un servicio
 *   reservaId                   — publicId de la reserva (necesario para los endpoints confirm-cost)
 *   isCatalogFindOrCreateEnabled — flag del servidor; cuando es false, el render es IDÉNTICO al original
 *   onServiceConfirmed          — callback(servicioActualizado) cuando confirm-cost tiene éxito;
 *                                  el padre actualiza el estado de la reserva con el DTO devuelto
 */
export function ServiceList({
    services,
    serviceCollectionErrors = {},
    onAddService,
    onEditService,
    onDeleteService,
    reservaId,
    isCatalogFindOrCreateEnabled = false,
    onServiceConfirmed,
}) {
    // Gate de costo: con flag OFF se usa isAdmin() (comportamiento original).
    // Con flag ON se usa hasPermission("cobranzas.see_cost") — admin pasa igual (bypass en hasPermission).
    const mostrarCosto = isCatalogFindOrCreateEnabled
        ? hasPermission("cobranzas.see_cost")
        : isAdmin();

    // Solo quien ve costos Y tiene flag ON puede interactuar con confirm-cost.
    const puedeConfirmarCosto = isCatalogFindOrCreateEnabled && mostrarCosto;

    const collectionErrorMessages = Object.values(serviceCollectionErrors).filter(Boolean);

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
            <div className="flex flex-col sm:flex-row justify-between items-start sm:items-center gap-4 mb-4">
                <h3 className="text-lg font-medium text-gray-900 dark:text-white">Servicios Contratados</h3>
                <button
                    onClick={onAddService}
                    className="w-full sm:w-auto flex items-center justify-center gap-2 bg-blue-600 text-white px-4 py-2 rounded-lg hover:bg-blue-700 transition-colors shadow-sm"
                >
                    <Plus className="w-4 h-4" /> Agregar Servicio
                </button>
            </div>

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
                                    {/* Columna Avisos: solo existe con flag ON. Con flag OFF ni el th se renderiza. */}
                                    {isCatalogFindOrCreateEnabled && (
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
                                                    {/* Pill violeta "creado en venta": solo con flag ON, la ven todos */}
                                                    {isCatalogFindOrCreateEnabled && (
                                                        <PillCreadoEnVenta service={svc} />
                                                    )}
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
                                                        <span>{new Date(svc.date || svc.startDate || svc.checkIn).toLocaleDateString('es-AR')}</span>
                                                        <span className="text-[10px] opacity-60">al {new Date(svc.endDate || svc.checkOut).toLocaleDateString('es-AR')}</span>
                                                    </div>
                                                ) : svc.recordKind === SERVICE_RECORD_KIND.ASSISTANCE ? (
                                                    // Asistencia: muestra vigencia desde/hasta (fechas date-only)
                                                    <div className="flex flex-col">
                                                        <span>{svc.validFrom ? new Date(svc.validFrom).toLocaleDateString('es-AR') : '-'}</span>
                                                        {svc.validTo && (
                                                            <span className="text-[10px] opacity-60">al {new Date(svc.validTo).toLocaleDateString('es-AR')}</span>
                                                        )}
                                                    </div>
                                                ) : (
                                                    svc.date ? new Date(svc.date).toLocaleDateString('es-AR') : '-'
                                                )}
                                            </td>
                                            <td className="py-4 align-middle whitespace-nowrap">
                                                <span className={`px-2 py-0.5 rounded text-[10px] font-semibold uppercase tracking-wider ${
                                                    svc.workflowStatus === 'Confirmado' ? 'bg-green-100 text-green-700 dark:bg-green-900/30 dark:text-green-400' :
                                                    svc.workflowStatus === 'Cancelado' ? 'bg-rose-100 text-rose-700 dark:bg-rose-900/30 dark:text-rose-400' :
                                                    'bg-amber-100 text-amber-700 dark:bg-amber-900/30 dark:text-amber-400'
                                                }`}>
                                                    {svc.workflowStatus || 'Solicitado'}
                                                </span>
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

                                            {/* Columna Avisos: solo con flag ON. Traslado/Asistencia/Genérico muestran "—" */}
                                            {isCatalogFindOrCreateEnabled && (
                                                <td className="py-4 align-middle pr-4 whitespace-nowrap">
                                                    <DeadlinePill service={svc} mostrarGuion={true} />
                                                </td>
                                            )}

                                            <td className="py-4 align-middle text-right pr-4">
                                                <div className="flex justify-end gap-1 transition-opacity">
                                                    <button onClick={() => onEditService(svc)} className="p-1.5 text-blue-600 hover:bg-blue-50 dark:hover:bg-blue-900/20 rounded">
                                                        <Edit2 className="w-4 h-4" />
                                                    </button>
                                                    <button onClick={() => onDeleteService(svc)} className="p-1.5 text-red-600 hover:bg-red-50 dark:hover:bg-red-900/20 rounded">
                                                        <Trash2 className="w-4 h-4" />
                                                    </button>
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

                            // Pills de avisos para mobile: violeta + deadline (si alguna aplica)
                            // Solo con flag ON; sin deadline no hay "—" en mobile (solo omitimos)
                            const tienePillCreadoEnVenta = isCatalogFindOrCreateEnabled && svc.productCreatedInSale;
                            // Decidimos si hay una deadline para mostrar en mobile (misma lógica que DeadlinePill)
                            const esHotelOPaquete = svc.recordKind === SERVICE_RECORD_KIND.HOTEL || svc.recordKind === SERVICE_RECORD_KIND.PACKAGE;
                            const tieneDeadlineHotelOPaquete = esHotelOPaquete && Boolean(svc.operatorPaymentDeadline);
                            const tieneDeadlineVuelo = svc.recordKind === SERVICE_RECORD_KIND.FLIGHT && Boolean(svc.ticketingDeadline);
                            const tieneDeadlineMobile = isCatalogFindOrCreateEnabled &&
                                svc.workflowStatus !== "Cancelado" &&
                                (tieneDeadlineHotelOPaquete || tieneDeadlineVuelo);
                            const mostrarLineaPills = tienePillCreadoEnVenta || tieneDeadlineMobile;

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

                                    {/* Línea de pills (violeta + deadline): solo si hay al menos una pill */}
                                    {mostrarLineaPills && (
                                        <div className="flex flex-wrap gap-2 mt-1 mb-1">
                                            {tienePillCreadoEnVenta && <PillCreadoEnVenta service={svc} />}
                                            {tieneDeadlineMobile && <DeadlinePill service={svc} mostrarGuion={false} />}
                                        </div>
                                    )}

                                    <div className="flex justify-between items-end">
                                        <div className="text-[11px] text-slate-500 flex flex-col gap-0.5">
                                            <span>
                                                {svc.recordKind === SERVICE_RECORD_KIND.ASSISTANCE
                                                    ? (svc.validFrom ? `Vigencia: ${new Date(svc.validFrom).toLocaleDateString('es-AR')}` : '-')
                                                    : (svc.date ? new Date(svc.date).toLocaleDateString('es-AR') : '-')}
                                                {(svc.recordKind === SERVICE_RECORD_KIND.HOTEL || svc.recordKind === SERVICE_RECORD_KIND.PACKAGE) &&
                                                    ` al ${new Date(svc.endDate || svc.checkOut).toLocaleDateString('es-AR')}`}
                                                {svc.recordKind === SERVICE_RECORD_KIND.ASSISTANCE && svc.validTo &&
                                                    ` al ${new Date(svc.validTo).toLocaleDateString('es-AR')}`}
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
                                        </div>
                                        <div className="flex items-center gap-2">
                                            <button onClick={() => onEditService(svc)} className="p-2 text-slate-400 rounded-lg bg-slate-50 dark:bg-slate-800"><Edit2 className="w-3.5 h-3.5" /></button>
                                            <button onClick={() => onDeleteService(svc)} className="p-2 text-red-400 rounded-lg bg-red-50 dark:bg-red-900/20"><Trash2 className="w-3.5 h-3.5" /></button>
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
