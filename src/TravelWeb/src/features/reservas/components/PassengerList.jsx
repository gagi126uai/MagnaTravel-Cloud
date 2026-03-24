import React from 'react';
import { Plus, User, Trash2, Edit2 } from "lucide-react";
import { getPublicId } from "../../../lib/publicIds";

export function PassengerList({ passengers, onAddPassenger, onEditPassenger, onDeletePassenger }) {
    return (
        <div>
            <div className="flex flex-col sm:flex-row justify-between items-start sm:items-center gap-4 mb-4">
                <h3 className="text-lg font-medium text-gray-900 dark:text-white">Pasajeros del Viaje</h3>
                <button
                    onClick={onAddPassenger}
                    className="w-full sm:w-auto flex items-center justify-center gap-2 bg-indigo-600 text-white px-4 py-2 rounded-lg hover:bg-indigo-700 transition-colors shadow-sm"
                >
                    <Plus className="w-4 h-4" /> Agregar Pasajero
                </button>
            </div>

            {passengers?.length === 0 ? (
                <div className="text-center py-12 bg-gray-50 dark:bg-slate-800 rounded-lg border border-dashed border-gray-300 dark:border-slate-700">
                    <User className="w-12 h-12 text-gray-300 dark:text-slate-600 mx-auto mb-3" />
                    <p className="text-gray-500 dark:text-slate-400">No hay pasajeros registrados.</p>
                </div>
            ) : (
                <div className="bg-white dark:bg-slate-900 rounded-xl border border-slate-200 dark:border-slate-800 shadow-sm overflow-hidden">
                    <div className="overflow-x-auto">
                        <table className="min-w-full text-left border-collapse">
                            <thead>
                                <tr className="border-b border-slate-100 dark:border-slate-800 bg-slate-50/50 dark:bg-slate-800/30">
                                    <th className="px-4 py-3 text-xs uppercase text-slate-400 font-bold">Pasajero</th>
                                    <th className="px-4 py-3 text-xs uppercase text-slate-400 font-bold">Documento</th>
                                    <th className="px-4 py-3 text-xs uppercase text-slate-400 font-bold hidden sm:table-cell">Contacto</th>
                                    <th className="px-4 py-3 text-xs uppercase text-slate-400 font-bold text-right">Acciones</th>
                                </tr>
                            </thead>
                            <tbody>
                                {passengers?.map((pax) => (
                                    <tr key={getPublicId(pax)} className="group border-b border-slate-50 dark:border-slate-800/50 hover:bg-slate-50/20 dark:hover:bg-slate-800/10 transition-colors">
                                        <td className="px-4 py-3 whitespace-nowrap">
                                            <div className="flex items-center gap-3">
                                                <div className="w-9 h-9 rounded-full bg-indigo-50 dark:bg-indigo-900/30 flex items-center justify-center text-indigo-600 dark:text-indigo-400 font-bold text-xs shadow-sm border border-indigo-100 dark:border-indigo-800/50">
                                                    {pax.fullName?.[0] || 'P'}
                                                </div>
                                                <div>
                                                    <div className="text-sm font-semibold text-slate-900 dark:text-white uppercase">
                                                        {pax.fullName}
                                                    </div>
                                                    <div className="text-[10px] text-slate-500 flex items-center gap-1">
                                                        {pax.birthDate && <span>{new Date(pax.birthDate).toLocaleDateString('es-AR')}</span>}
                                                        {pax.gender && <span>• {pax.gender}</span>}
                                                    </div>
                                                </div>
                                            </div>
                                        </td>
                                        <td className="px-4 py-3 whitespace-nowrap">
                                            <div className="text-sm font-medium text-slate-700 dark:text-slate-300">
                                                {pax.documentNumber || '---'}
                                            </div>
                                            <div className="text-[10px] text-slate-500 uppercase">{pax.documentType || 'DNI'}</div>
                                        </td>
                                        <td className="px-4 py-3 whitespace-nowrap hidden sm:table-cell">
                                            {pax.phone && <div className="text-xs text-slate-600 dark:text-slate-400">{pax.phone}</div>}
                                            {pax.email && <div className="text-[10px] text-slate-500">{pax.email}</div>}
                                            {!pax.phone && !pax.email && <span className="text-slate-300 italic text-xs">Sin contacto</span>}
                                        </td>
                                        <td className="px-4 py-3 whitespace-nowrap text-right">
                                            <div className="flex justify-end gap-1">
                                                <button onClick={() => onEditPassenger(pax)} className="p-2 text-indigo-600 hover:bg-indigo-50 dark:hover:bg-indigo-900/40 rounded-lg transition-colors" title="Editar">
                                                    <Edit2 className="w-4 h-4" />
                                                </button>
                                                <button onClick={() => onDeletePassenger(getPublicId(pax))} className="p-2 text-rose-600 hover:bg-rose-50 dark:hover:bg-rose-900/40 rounded-lg transition-colors" title="Eliminar">
                                                    <Trash2 className="w-4 h-4" />
                                                </button>
                                            </div>
                                        </td>
                                    </tr>
                                ))}
                            </tbody>
                        </table>
                    </div>
                </div>
            )}
        </div>
    );
}
