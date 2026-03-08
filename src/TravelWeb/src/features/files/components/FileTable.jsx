import React from 'react';
import { User, Users, Archive, MessageCircle, CreditCard, DollarSign } from "lucide-react";
import { Button } from "../../../components/ui/button";
import { FileStatusBadge } from "./FileStatusBadge";
import { formatCurrency, formatDate } from "../../../lib/utils";

export function FileTable({ files, onRowClick, onArchive }) {
    return (
        <div className="bg-white dark:bg-slate-900/50 rounded-xl border border-slate-200 dark:border-slate-800 overflow-hidden shadow-sm">
            <div className="overflow-x-auto">
                <table className="w-full text-left border-collapse">
                    <thead>
                        <tr className="border-b border-slate-100 dark:border-slate-800 bg-slate-50/50 dark:bg-slate-800/30">
                            <th className="px-4 py-3 text-[11px] font-semibold text-slate-500 dark:text-slate-400 uppercase tracking-wider">Reserva</th>
                            <th className="px-4 py-3 text-[11px] font-semibold text-slate-500 dark:text-slate-400 uppercase tracking-wider">Cliente / Pasajeros</th>
                            <th className="px-4 py-3 text-[11px] font-semibold text-slate-500 dark:text-slate-400 uppercase tracking-wider">Estado</th>
                            <th className="px-4 py-3 text-[11px] font-semibold text-slate-500 dark:text-slate-400 uppercase tracking-wider text-right">Finanzas</th>
                            <th className="px-4 py-3 text-[11px] font-semibold text-slate-500 dark:text-slate-400 uppercase tracking-wider text-center">Acciones</th>
                        </tr>
                    </thead>
                    <tbody className="divide-y divide-slate-100 dark:divide-slate-800">
                        {files.map((file) => (
                            <tr
                                key={file.id}
                                onClick={() => onRowClick(file.id)}
                                className="group hover:bg-slate-50/80 dark:hover:bg-slate-800/40 transition-all cursor-pointer"
                            >
                                <td className="px-4 py-4">
                                    <div className="flex flex-col">
                                        <span className="text-sm font-bold text-slate-900 dark:text-white group-hover:text-indigo-600 dark:group-hover:text-indigo-400 transition-colors">
                                            #{file.fileNumber}
                                        </span>
                                        <span className="text-xs text-slate-500 dark:text-slate-400 mt-0.5 line-clamp-1">
                                            {file.name}
                                        </span>
                                        {file.startDate && (
                                            <span className="text-[10px] font-medium text-indigo-500 dark:text-indigo-400 mt-1 flex items-center gap-1">
                                                <span className="w-1 h-1 rounded-full bg-indigo-400 animate-pulse"></span>
                                                Viaja: {formatDate(file.startDate)}
                                            </span>
                                        )}
                                    </div>
                                </td>
                                <td className="px-4 py-4">
                                    <div className="flex flex-col gap-1.5">
                                        <div className="flex items-center gap-2 text-sm text-slate-700 dark:text-slate-300">
                                            <User className="h-3.5 w-3.5 text-slate-400" />
                                            <span className="font-medium truncate max-w-[180px]">{file.customerName}</span>
                                        </div>
                                        <div className="flex items-center gap-2 text-[10px] text-slate-500 dark:text-slate-400">
                                            <Users className="h-3.5 w-3.5" />
                                            <span>{file.passengerCount || 0} pax</span>
                                            {file.destinations && (
                                                <span className="before:content-['•'] before:mx-1 truncate">{file.destinations}</span>
                                            )}
                                        </div>
                                    </div>
                                </td>
                                <td className="px-4 py-4">
                                    <FileStatusBadge status={file.status} />
                                </td>
                                <td className="px-4 py-4 text-right">
                                    <div className="flex flex-col items-end gap-1">
                                        <span className="text-sm font-bold text-slate-900 dark:text-white">
                                            {formatCurrency(file.totalSale)}
                                        </span>
                                        {file.balance > 0 ? (
                                            <div className="flex items-center gap-1 text-[10px] font-semibold text-rose-600 dark:text-rose-400 bg-rose-50 dark:bg-rose-900/20 px-1.5 py-0.5 rounded">
                                                <DollarSign className="h-2.5 w-2.5" />
                                                Debe: {formatCurrency(file.balance)}
                                            </div>
                                        ) : (
                                            <span className="text-[10px] font-semibold text-emerald-600 dark:text-emerald-400 bg-emerald-50 dark:bg-emerald-900/20 px-1.5 py-0.5 rounded">
                                                Saldado
                                            </span>
                                        )}
                                    </div>
                                </td>
                                <td className="px-4 py-4" onClick={(e) => e.stopPropagation()}>
                                    <div className="flex items-center justify-center gap-1 opacity-0 group-hover:opacity-100 transition-opacity">
                                        <Button
                                            variant="ghost"
                                            size="icon"
                                            className="h-8 w-8 text-slate-400 hover:text-indigo-600 hover:bg-indigo-50 dark:hover:bg-indigo-900/20"
                                            onClick={() => onRowClick(file.id)}
                                            title="Ver Detalles"
                                        >
                                            <MessageCircle className="h-4 w-4" />
                                        </Button>
                                        <Button
                                            variant="ghost"
                                            size="icon"
                                            className="h-8 w-8 text-slate-400 hover:text-rose-600 hover:bg-rose-50 dark:hover:bg-rose-900/20"
                                            onClick={() => onArchive(file.id)}
                                            title="Archivar"
                                        >
                                            <Archive className="h-4 w-4" />
                                        </Button>
                                    </div>
                                </td>
                            </tr>
                        ))}
                        {files.length === 0 && (
                            <tr>
                                <td colSpan="5" className="px-4 py-12 text-center">
                                    <div className="flex flex-col items-center justify-center text-slate-400 dark:text-slate-600">
                                        <Archive className="h-12 w-12 mb-3 opacity-20" />
                                        <p className="text-sm font-medium">No se encontraron reservas</p>
                                        <p className="text-xs">Intenta ajustar los filtros de búsqueda</p>
                                    </div>
                                </td>
                            </tr>
                        )}
                    </tbody>
                </table>
            </div>
        </div>
    );
}
