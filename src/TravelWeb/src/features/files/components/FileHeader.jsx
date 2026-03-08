import React from 'react';
import { ArrowLeft, Trash2, Archive } from "lucide-react";

export function FileHeader({ file, onBack, onStatusChange, onDelete, onArchive }) {
    return (
        <div className="mb-8 flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4">
            <div>
                <button
                    onClick={onBack}
                    className="flex items-center text-gray-500 hover:text-gray-700 dark:text-slate-400 dark:hover:text-slate-200 mb-2 transition-colors"
                >
                    <ArrowLeft className="w-4 h-4 mr-1" /> Volver a Lista
                </button>
                <div className="flex items-center gap-3">
                    <h1 className="text-3xl font-bold text-gray-900 dark:text-white">Reserva #{file.fileNumber}</h1>
                    <span className={`px-3 py-1 rounded-full text-sm font-medium 
                ${file.status === 'Presupuesto' ? 'bg-gray-100 text-gray-800 dark:bg-gray-800 dark:text-gray-200' :
                            file.status === 'Reservado' ? 'bg-blue-100 text-blue-800 dark:bg-blue-900 dark:text-blue-200' :
                                file.status === 'Operativo' ? 'bg-purple-100 text-purple-800 dark:bg-purple-900 dark:text-purple-200' :
                                    file.status === 'Cerrado' ? 'bg-green-100 text-green-800 dark:bg-green-900 dark:text-green-200' : 'bg-red-100 text-red-800 dark:bg-red-900 dark:text-red-200'}`}>
                        {file.status}
                    </span>
                </div>
                <p className="text-xl text-gray-900 dark:text-white mt-1 font-semibold">{file.customerName}</p>
                <p className="text-lg text-gray-600 dark:text-slate-400">{file.name}</p>
            </div>

            <div className="flex flex-wrap gap-2">
                {/* STATUS ACTIONS */}
                {file.status === 'Presupuesto' && (
                    <button onClick={() => onStatusChange('Reservado')} className="btn btn-primary bg-blue-600 hover:bg-blue-700 text-white px-4 py-2 rounded shadow">
                        Confirmar Reserva
                    </button>
                )}
                {file.status === 'Reservado' && (
                    <>
                        <button onClick={() => onStatusChange('Operativo')} className="btn btn-secondary bg-purple-600 hover:bg-purple-700 text-white px-4 py-2 rounded shadow">
                            Pasar a Operativo
                        </button>
                        <button onClick={() => { if (confirm("¿Volver a Presupuesto?")) onStatusChange('Presupuesto'); }} className="btn bg-amber-100 text-amber-800 hover:bg-amber-200 px-4 py-2 rounded shadow ml-2">
                            Deshacer Reserva
                        </button>
                    </>
                )}
                {file.status === 'Operativo' && (
                    <button onClick={() => onStatusChange('Cerrado')} className="btn btn-success bg-green-600 hover:bg-green-700 text-white px-4 py-2 rounded shadow">
                        Cerrar Reserva
                    </button>
                )}

                {/* ADMIN ACTIONS */}
                <div className="ml-2 pl-2 border-l border-gray-300 dark:border-slate-700 flex gap-2">
                    {file.status === 'Presupuesto' && (
                        <button onClick={onDelete} className="btn bg-red-100 text-red-700 hover:bg-red-200 px-3 py-2 rounded" title="Eliminar Reserva">
                            <Trash2 className="w-5 h-5" />
                        </button>
                    )}
                    <button onClick={onArchive} className="btn bg-gray-100 text-gray-600 hover:bg-gray-200 dark:bg-slate-800 dark:text-slate-400 dark:hover:bg-slate-700 px-3 py-2 rounded" title="Archivar">
                        <Archive className="w-5 h-5" />
                    </button>
                </div>
            </div>
        </div >
    );
}
