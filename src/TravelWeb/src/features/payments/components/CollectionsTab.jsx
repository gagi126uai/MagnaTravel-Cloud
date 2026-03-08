import React from 'react';
import { DollarSign, User, FileText, ArrowRight, Wallet } from "lucide-react";

export function CollectionsTab({ files, onPay }) {
    const filtered = files.filter(f => f.pendingCollection > 0);

    return (
        <div className="space-y-6">
            {/* Desktop View */}
            <div className="hidden md:block overflow-hidden rounded-xl border border-slate-200 dark:border-slate-800 bg-white dark:bg-slate-900 shadow-sm">
                <table className="w-full text-left border-collapse">
                    <thead>
                        <tr className="bg-slate-50 dark:bg-slate-800/50 border-b border-slate-200 dark:border-slate-800">
                            <th className="px-6 py-4 text-[11px] uppercase tracking-wider text-slate-500 font-semibold text-center w-16">#</th>
                            <th className="px-6 py-4 text-[11px] uppercase tracking-wider text-slate-500 font-semibold">Reserva / Cliente</th>
                            <th className="px-6 py-4 text-[11px] uppercase tracking-wider text-slate-500 font-semibold text-right">Venta Total</th>
                            <th className="px-6 py-4 text-[11px] uppercase tracking-wider text-slate-500 font-semibold text-right">Cobrado</th>
                            <th className="px-6 py-4 text-[11px] uppercase tracking-wider text-slate-500 font-semibold text-right">Pendiente</th>
                            <th className="px-6 py-4 text-[11px] uppercase tracking-wider text-slate-500 font-semibold text-right pr-8">Acción</th>
                        </tr>
                    </thead>
                    <tbody className="divide-y divide-slate-100 dark:divide-slate-800">
                        {filtered.map((file, idx) => (
                            <tr key={file.id} className="group hover:bg-slate-50 dark:hover:bg-slate-800/40 transition-colors">
                                <td className="px-6 py-4 text-center text-xs text-slate-400 font-mono">{(idx + 1).toString().padStart(2, '0')}</td>
                                <td className="px-6 py-4">
                                    <div className="flex flex-col">
                                        <a href={`/files/${file.id}`} className="font-bold text-slate-900 dark:text-white hover:text-indigo-600 dark:hover:text-indigo-400 transition-colors flex items-center gap-2">
                                            <FileText className="w-3.5 h-3.5 opacity-40" />
                                            {file.fileNumber}
                                        </a>
                                        <span className="text-sm text-slate-500 dark:text-slate-400 flex items-center gap-1.5 mt-0.5">
                                            <User className="w-3 h-3 opacity-40" />
                                            {file.payer?.fullName || file.customerName || "Consumidor Final"}
                                        </span>
                                    </div>
                                </td>
                                <td className="px-6 py-4 text-right text-sm font-medium text-slate-600 dark:text-slate-400">
                                    ${file.totalSaleAmount?.toLocaleString('es-AR', { minimumFractionDigits: 2 })}
                                </td>
                                <td className="px-6 py-4 text-right text-sm font-medium text-emerald-600 dark:text-emerald-400">
                                    ${file.computedPaid?.toLocaleString('es-AR', { minimumFractionDigits: 2 })}
                                </td>
                                <td className="px-6 py-4 text-right">
                                    <div className="text-base font-bold text-rose-600 dark:text-rose-400">
                                        ${file.pendingCollection?.toLocaleString('es-AR', { minimumFractionDigits: 2 })}
                                    </div>
                                </td>
                                <td className="px-6 py-4 text-right pr-8">
                                    <button
                                        onClick={() => onPay(file)}
                                        className="inline-flex items-center gap-2 px-4 py-2 rounded-lg bg-slate-900 dark:bg-white text-white dark:text-slate-900 text-sm font-medium hover:bg-slate-800 transition-colors shadow-sm"
                                    >
                                        <DollarSign className="w-4 h-4" />
                                        Cobrar
                                    </button>
                                </td>
                            </tr>
                        ))}
                    </tbody>
                </table>
            </div>

            {/* Mobile View */}
            <div className="md:hidden space-y-4">
                {filtered.map(file => (
                    <div key={file.id} className="bg-white dark:bg-slate-900 rounded-2xl p-5 border border-slate-200 dark:border-slate-800 shadow-sm">
                        <div className="flex justify-between items-start mb-4">
                            <div>
                                <span className="text-[10px] font-bold uppercase tracking-widest text-indigo-600 dark:text-indigo-400 bg-indigo-50 dark:bg-indigo-900/30 px-2 py-0.5 rounded-full mb-2 inline-block">
                                    {file.fileNumber}
                                </span>
                                <h3 className="font-bold text-slate-900 dark:text-white line-clamp-1">
                                    {file.payer?.fullName || file.customerName || "Sin Cliente"}
                                </h3>
                            </div>
                            <div className="text-right">
                                <div className="text-[10px] text-slate-400 uppercase font-bold tracking-tight">Pendiente</div>
                                <div className="text-lg font-black text-rose-600 dark:text-rose-400">
                                    ${file.pendingCollection?.toLocaleString('es-AR')}
                                </div>
                            </div>
                        </div>

                        <div className="grid grid-cols-2 gap-4 py-3 border-y border-slate-50 dark:border-slate-800/50 mb-4">
                            <div>
                                <div className="text-[10px] text-slate-400 uppercase font-bold tracking-tight">Total Venta</div>
                                <div className="text-sm font-semibold text-slate-700 dark:text-slate-200">${file.totalSaleAmount?.toLocaleString()}</div>
                            </div>
                            <div>
                                <div className="text-[10px] text-slate-400 uppercase font-bold tracking-tight">Cobrado</div>
                                <div className="text-sm font-semibold text-emerald-600">${file.computedPaid?.toLocaleString()}</div>
                            </div>
                        </div>

                        <button
                            onClick={() => onPay(file)}
                            className="w-full flex items-center justify-center gap-3 py-3.5 rounded-xl bg-slate-900 dark:bg-white text-white dark:text-slate-900 font-medium hover:bg-slate-800 transition-colors shadow-sm"
                        >
                            <Wallet className="w-5 h-5" />
                            Registrar Cobro
                            <ArrowRight className="w-4 h-4 ml-auto opacity-50" />
                        </button>
                    </div>
                ))}
            </div>

            {filtered.length === 0 && (
                <div className="py-20 text-center bg-slate-50/50 dark:bg-slate-800/20 rounded-3xl border-2 border-dashed border-slate-200 dark:border-slate-800">
                    <div className="w-16 h-16 bg-white dark:bg-slate-900 rounded-full flex items-center justify-center mx-auto mb-4 shadow-sm border border-slate-100 dark:border-slate-800">
                        <Wallet className="w-8 h-8 text-slate-300" />
                    </div>
                    <h3 className="text-lg font-bold text-slate-900 dark:text-white mb-1">Todo al día</h3>
                    <p className="text-slate-500 dark:text-slate-400 text-sm">No hay reservas con deuda comercial pendiente.</p>
                </div>
            )}
        </div>
    );
}
