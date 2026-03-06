import React from 'react';
import { Loader2, XCircle, Download, FileText, ArrowUpRight, History, Receipt, AlertCircle, RotateCw } from "lucide-react";

export function HistoryTab({ payments, invoices, onDownloadPdf, onAnnulInvoice, onRetryInvoice }) {
    const getInvoiceLabel = (type) => {
        switch (type) {
            case 1: return "Factura A";
            case 6: return "Factura B";
            case 11: return "Factura C";
            case 3: return "NC A";
            case 8: return "NC B";
            case 13: return "NC C";
            default: return `Comp (${type})`;
        }
    };

    return (
        <div className="animate-in fade-in slide-in-from-bottom-4 duration-700 grid grid-cols-1 lg:grid-cols-2 gap-8 mt-6">
            {/* MOVIMIENTOS DE CAJA (COBROS) */}
            <div className="bg-white dark:bg-slate-900/40 rounded-3xl border border-slate-200 dark:border-slate-800 p-6 shadow-sm">
                <div className="flex items-center justify-between mb-8">
                    <h3 className="text-lg font-black text-slate-900 dark:text-white flex items-center gap-3">
                        <div className="p-2 rounded-xl bg-emerald-50 dark:bg-emerald-900/20 text-emerald-600">
                            <History className="w-5 h-5" />
                        </div>
                        Movimientos
                    </h3>
                    <span className="text-[10px] font-bold uppercase tracking-widest text-slate-400 bg-slate-50 dark:bg-slate-800 px-2 py-1 rounded-lg">
                        Últimos 15
                    </span>
                </div>

                <div className="space-y-2">
                    {payments.slice(0, 15).map(p => (
                        <div key={p.id} className="flex items-center justify-between p-3 rounded-2xl hover:bg-slate-50 dark:hover:bg-slate-800/40 transition-all group">
                            <div className="flex items-center gap-4">
                                <div className="w-10 h-10 rounded-xl bg-slate-100 dark:bg-slate-800 flex items-center justify-center text-slate-400 group-hover:bg-emerald-100 group-hover:text-emerald-600 transition-colors">
                                    <ArrowUpRight className="w-5 h-5" />
                                </div>
                                <div>
                                    <div className="text-sm font-bold text-slate-900 dark:text-white">
                                        Pago Recibido <span className="text-[10px] font-mono opacity-50 ml-1">{p.travelFile?.fileNumber}</span>
                                    </div>
                                    <div className="text-[10px] font-semibold uppercase tracking-tight text-slate-400">
                                        {new Date(p.paidAt).toLocaleDateString()} • {p.method}
                                    </div>
                                </div>
                            </div>
                            <div className="text-right">
                                <div className="text-sm font-black text-emerald-600">
                                    +${p.amount?.toLocaleString('es-AR')}
                                </div>
                            </div>
                        </div>
                    ))}
                    {payments.length === 0 && (
                        <div className="py-12 text-center text-slate-400">
                            <History className="w-8 h-8 opacity-20 mx-auto mb-2" />
                            <p className="text-xs font-semibold uppercase tracking-widest">Sin movimientos</p>
                        </div>
                    )}
                </div>
            </div>

            {/* COMPROBANTES AFIP */}
            <div className="bg-white dark:bg-slate-900/40 rounded-3xl border border-slate-200 dark:border-slate-800 p-6 shadow-sm">
                <div className="flex items-center justify-between mb-8">
                    <h3 className="text-lg font-black text-slate-900 dark:text-white flex items-center gap-3">
                        <div className="p-2 rounded-xl bg-indigo-50 dark:bg-indigo-900/20 text-indigo-600">
                            <Receipt className="w-5 h-5" />
                        </div>
                        Facturación
                    </h3>
                    <span className="text-[10px] font-bold uppercase tracking-widest text-slate-400 bg-slate-50 dark:bg-slate-800 px-2 py-1 rounded-lg">
                        AFIP
                    </span>
                </div>

                <div className="space-y-4">
                    {invoices.slice(0, 15).map(i => (
                        <div key={i.id} className="relative p-4 rounded-2xl border border-slate-50 dark:border-slate-800/50 hover:border-indigo-100 dark:hover:border-indigo-900/50 hover:shadow-xl hover:shadow-indigo-500/5 transition-all group">
                            <div className="flex justify-between items-start mb-3">
                                <div className="flex-1 min-w-0 pr-4">
                                    <div className="flex items-center gap-2 mb-1">
                                        <span className={`text-[10px] font-black px-1.5 py-0.5 rounded ${i.resultado === 'A' ? 'bg-emerald-100 text-emerald-700' :
                                                i.resultado === 'R' ? 'bg-rose-100 text-rose-700' : 'bg-slate-100 text-slate-600 animate-pulse'
                                            }`}>
                                            {getInvoiceLabel(i.tipoComprobante)}
                                        </span>
                                        <span className="text-xs font-mono font-bold text-slate-400">
                                            {i.numeroComprobante ? i.numeroComprobante.toString().padStart(8, '0') : '-------'}
                                        </span>
                                    </div>
                                    <div className="text-sm font-bold text-slate-900 dark:text-white truncate">
                                        {i.travelFile?.payer?.fullName || `File ${i.travelFile?.fileNumber}`}
                                    </div>
                                </div>
                                <div className="text-right">
                                    <div className="text-base font-black text-slate-900 dark:text-white">
                                        ${i.importeTotal?.toLocaleString('es-AR')}
                                    </div>
                                    <div className="text-[10px] font-bold text-slate-400">{new Date(i.createdAt).toLocaleDateString()}</div>
                                </div>
                            </div>

                            {i.resultado === 'R' && (
                                <div className="flex items-start gap-2 p-2 rounded-lg bg-rose-50 dark:bg-rose-950/20 text-rose-600 dark:text-rose-400 mb-3">
                                    <AlertCircle className="w-3.5 h-3.5 mt-0.5 shrink-0" />
                                    <span className="text-[10px] font-semibold leading-tight">{i.observaciones}</span>
                                </div>
                            )}

                            <div className="flex items-center gap-2 pt-3 border-t border-slate-50 dark:border-slate-800/50">
                                {i.resultado === 'A' && (
                                    <>
                                        <button
                                            onClick={() => onDownloadPdf(i)}
                                            className="flex-1 flex items-center justify-center gap-2 py-2 rounded-xl bg-slate-900 dark:bg-white text-white dark:text-slate-900 text-xs font-bold hover:scale-[1.02] transition-transform"
                                        >
                                            <Download className="w-3.5 h-3.5" /> PDF
                                        </button>
                                        <button
                                            onClick={() => onAnnulInvoice(i)}
                                            className="flex items-center justify-center w-10 py-2 rounded-xl bg-slate-50 dark:bg-slate-800 text-slate-400 hover:text-rose-600 transition-colors"
                                            title="Anular"
                                        >
                                            <XCircle className="w-4 h-4" />
                                        </button>
                                    </>
                                )}
                                {i.resultado === 'R' && (
                                    <button
                                        onClick={() => onRetryInvoice(i)}
                                        className="w-full flex items-center justify-center gap-2 py-2.5 rounded-xl bg-orange-100 text-orange-700 text-xs font-bold hover:bg-orange-200 transition-colors"
                                    >
                                        <RotateCw className="w-4 h-4" /> Reintentar Emisión
                                    </button>
                                )}
                                {i.resultado === 'PENDING' && (
                                    <div className="w-full flex items-center justify-center gap-2 py-2 text-slate-400 text-xs font-bold italic">
                                        <Loader2 className="w-4 h-4 animate-spin" /> Procesando con AFIP...
                                    </div>
                                )}
                                <a
                                    href={`/files/${i.travelFile?.id}`}
                                    className="p-2 text-indigo-600 hover:bg-indigo-50 dark:hover:bg-indigo-900/30 rounded-xl transition-colors"
                                    title="Ir al File"
                                >
                                    <FileText className="w-4 h-4" />
                                </a>
                            </div>
                        </div>
                    ))}
                    {invoices.length === 0 && (
                        <div className="py-12 text-center text-slate-400">
                            <Receipt className="w-8 h-8 opacity-20 mx-auto mb-2" />
                            <p className="text-xs font-semibold uppercase tracking-widest">Sin comprobantes</p>
                        </div>
                    )}
                </div>
            </div>
        </div>
    );
}
