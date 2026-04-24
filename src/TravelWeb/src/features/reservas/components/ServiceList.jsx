import React from 'react';
import { AlertTriangle, Plus, Plane, Hotel, Car, Package, Edit2, Trash2, ShieldCheck } from "lucide-react";
import { isAdmin } from "../../../auth";
import {
    SERVICE_RECORD_KIND,
    getReservationServicePublicId
} from "../lib/reservationServiceModel";

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

    return <Package className={`${className} text-violet-500`} />;
}

export function ServiceList({ services, serviceCollectionErrors = {}, onAddService, onEditService, onDeleteService }) {
    const admin = isAdmin();
    const collectionErrorMessages = Object.values(serviceCollectionErrors).filter(Boolean);

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
                                    <th className="pb-3 text-xs uppercase text-slate-400 font-medium">Fecha</th>
                                    <th className="pb-3 text-xs uppercase text-slate-400 font-medium">Estado</th>
                                    {admin && <th className="pb-3 text-xs uppercase text-slate-400 font-medium text-right pr-4">Neto Cto</th>}
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
                                                </div>
                                            </td>
                                            <td className="py-4 align-middle whitespace-nowrap text-sm text-gray-600 dark:text-slate-400">
                                                {svc.date ? new Date(svc.date).toLocaleDateString('es-AR') : '-'}
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
                                            {admin && (
                                                <td className="py-4 align-middle text-right text-xs text-slate-500 font-mono pr-4">
                                                    ${netCost.toLocaleString()}
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
                                    <div className="flex justify-between items-end">
                                        <div className="text-[11px] text-slate-500 flex flex-col gap-0.5">
                                            <span>{svc.date ? new Date(svc.date).toLocaleDateString('es-AR') : '-'}</span>
                                            {admin && <span className="text-[9px] opacity-70">Neto: ${netCost.toLocaleString()}</span>}
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
