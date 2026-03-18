import React from 'react';
import { FilePlus, User, AlertCircle, ArrowRight, CheckCircle2, Receipt } from "lucide-react";

export function InvoicingTab({ reservas, onInvoice }) {
    const filtered = reservas.filter(r => r.pendingBilling > 0);

    return (
        <div className="space-y-6">
            {/* Desktop View */}
            <div className="hidden md:block overflow-hidden rounded-xl border border-slate-200 dark:border-slate-800 bg-white dark:bg-slate-900 shadow-sm">
                <table className="w-full text-left border-collapse">
                    <thead>
                        <tr className="bg-slate-50 dark:bg-slate-800/50 border-b border-slate-200 dark:border-slate-800">
                            <th className="px-6 py-4 text-[11px] uppercase tracking-wider text-slate-500 font-semibold text-center w-16">#</th>
                            <th className="px-6 py-4 text-[11px] uppercase tracking-wider text-slate-500 font-semibold">Reserva / Cliente</th>
                            <th className="px-6 py-4 text-[11px] uppercase tracking-wider text-slate-500 font-semibold text-right">Dinero Ingresado</th>
                            <th className="px-6 py-4 text-[11px] uppercase tracking-wider text-slate-500 font-semibold text-right">Ya Facturado</th>
                            <th className="px-6 py-4 text-[11px] uppercase tracking-wider text-slate-500 font-semibold text-right">A Facturar AFIP</th>
                            <th className="px-6 py-4 text-[11px] uppercase tracking-wider text-slate-500 font-semibold text-right pr-8">Acción</th>
                        </tr>
                    </thead>
                    <tbody className="divide-y divide-slate-100 dark:divide-slate-800">
                        {filtered.map((reserva, idx) => (
                            <tr key={reserva.id} className="group hover:bg-slate-50 dark:hover:bg-slate-800/40 transition-colors">
                                <td className="px-6 py-4 text-center text-xs text-slate-400 font-mono">{(idx + 1).toString().padStart(2, '0')}</td>
                                <td className="px-6 py-4">
                                    <div className="flex flex-col">
                                        <a href={`/reservas/${reserva.id}`} className="font-bold text-slate-900 dark:text-white hover:text-indigo-600 dark:hover:text-indigo-400 transition-colors uppercase tracking-tight">
                                            {reserva.numeroReserva}
                                        </a>
                                        <span className="text-sm text-slate-500 dark:text-slate-400 flex items-center gap-1.5 mt-0.5">
                                            <User className="w-3 h-3 opacity-40" />
                                            {reserva.payer?.fullName || reserva.customerName || "Sin Cliente"}
                                        </span>
                                    </div>
                                </td>
                                <td className="px-6 py-4 text-right text-sm font-medium text-slate-600 dark:text-slate-400">
                                    ${reserva.computedPaid?.toLocaleString('es-AR', { minimumFractionDigits: 2 })}
                                </td>
                                <td className="px-6 py-4 text-right text-sm font-medium text-slate-400">
                                    ${reserva.computedInvoiced?.toLocaleString('es-AR', { minimumFractionDigits: 2 })}
                                </td>
                                <td className="px-6 py-4 text-right">
                                    <div className="flex flex-col items-end">
                                        <div className="text-base font-bold text-indigo-600 dark:text-indigo-400 flex items-center gap-2">
                                            <AlertCircle className="w-3.5 h-3.5" />
                                            ${reserva.pendingBilling?.toLocaleString('es-AR', { minimumFractionDigits: 2 })}
                                        </div>
                                        <span className="text-[10px] uppercase font-bold text-slate-400 tracking-widest mt-0.5">Pendiente AFIP</span>
                                    </div>
                                </td>
                                <td className="px-6 py-4 text-right pr-8">
                                    <button
                                        onClick={() => onInvoice(reserva)}
                                        className="inline-flex items-center gap-2 px-4 py-2 rounded-lg bg-indigo-600 text-white text-sm font-medium hover:bg-indigo-700 transition-colors shadow-sm"
                                    >
                                        <Receipt className="w-4 h-4" />
                                        Emitir Factura
                                    </button>
                                </td>
                            </tr>
                        ))}
                    </tbody>
                </table>
            </div>

            {/* Mobile View */}
            <div className="md:hidden space-y-4">
                {filtered.map(reserva => (
                    <div key={reserva.id} className="bg-white dark:bg-slate-900 rounded-2xl p-5 border border-slate-200 dark:border-slate-800 shadow-sm">
                        <div className="flex justify-between items-start mb-4">
                            <div>
                                <div className="text-[10px] font-bold uppercase tracking-widest text-slate-400 mb-1">{reserva.numeroReserva}</div>
                                <h3 className="font-bold text-slate-900 dark:text-white line-clamp-1">
                                    {reserva.payer?.fullName || reserva.customerName || "Sin Cliente"}
                                </h3>
                            </div>
                            <div className="bg-amber-50 dark:bg-amber-900/20 text-amber-600 dark:text-amber-400 p-2 rounded-xl">
                                <AlertCircle className="w-5 h-5" />
                            </div>
                        </div>

                        <div className="space-y-3 py-3 border-t border-slate-50 dark:border-slate-800/50 mb-4">
                            <div className="flex justify-between items-center text-sm">
                                <span className="text-slate-500">Cobrado:</span>
                                <span className="font-semibold text-slate-900 dark:text-white">${reserva.computedPaid?.toLocaleString()}</span>
                            </div>
                            <div className="flex justify-between items-center text-sm">
                                <span className="text-slate-500">Ya Facturado:</span>
                                <span className="font-semibold text-slate-400">${reserva.computedInvoiced?.toLocaleString()}</span>
                            </div>
                            <div className="flex justify-between items-center pt-2 border-t border-slate-50 dark:border-slate-800/50">
                                <span className="font-bold text-slate-900 dark:text-white">A Facturar:</span>
                                <span className="text-lg font-black text-indigo-600 dark:text-indigo-400">${reserva.pendingBilling?.toLocaleString()}</span>
                            </div>
                        </div>

                        <button
                            onClick={() => onInvoice(reserva)}
                            className="w-full flex items-center justify-center gap-3 py-3.5 rounded-xl bg-indigo-600 text-white font-medium hover:bg-indigo-700 transition-colors shadow-sm"
                        >
                            <FilePlus className="w-5 h-5" />
                            Emitir Comprobante
                            <ArrowRight className="w-4 h-4 ml-auto opacity-50" />
                        </button>
                    </div>
                ))}
            </div>

            {filtered.length === 0 && (
                <div className="py-20 text-center bg-slate-50/50 dark:bg-slate-800/20 rounded-3xl border-2 border-dashed border-slate-200 dark:border-slate-800">
                    <div className="w-16 h-16 bg-white dark:bg-slate-900 rounded-full flex items-center justify-center mx-auto mb-4 shadow-sm border border-slate-100 dark:border-slate-800">
                        <CheckCircle2 className="w-8 h-8 text-emerald-400" />
                    </div>
                    <h3 className="text-lg font-bold text-slate-900 dark:text-white mb-1">Facturación al día</h3>
                    <p className="text-slate-500 dark:text-slate-400 text-sm">Todo el dinero ingresado cuenta con su comprobante fiscal correspondiente.</p>
                </div>
            )}
        </div>
    );
}
