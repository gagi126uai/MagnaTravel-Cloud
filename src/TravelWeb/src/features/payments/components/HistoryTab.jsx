import React from 'react';
import { Loader2, XCircle, Download, FileText, ArrowUpRight, History, Receipt, AlertCircle, RotateCw, ExternalLink, Calendar } from "lucide-react";

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
        <div className="animate-in fade-in slide-in-from-bottom-4 duration-700 space-y-8 mt-6">
            <div className="grid grid-cols-1 lg:grid-cols-2 gap-6 items-start">

                {/* MOVIMIENTOS DE CAJA (COBROS) - LISTA COMPACTA */}
                <div className="bg-white dark:bg-slate-900/40 rounded-2xl border border-slate-200 dark:border-slate-800 flex flex-col h-full shadow-sm overflow-hidden">
                    <div className="px-5 py-4 border-b border-slate-100 dark:border-slate-800 bg-slate-50/30 dark:bg-slate-800/20 flex items-center justify-between">
                        <div className="flex items-center gap-2">
                            <History className="w-4 h-4 text-emerald-500" />
                            <h3 className="text-sm font-bold text-slate-800 dark:text-slate-200 uppercase tracking-tight">Cobranzas Recientes</h3>
                        </div>
                        <span className="text-[10px] font-black text-slate-400 bg-slate-100 dark:bg-slate-800 px-1.5 py-0.5 rounded">AUTO-SYNC</span>
                    </div>

                    <div className="divide-y divide-slate-50 dark:divide-slate-800/50 max-h-[500px] overflow-y-auto">
                        {payments.slice(0, 15).map(p => (
                            <div key={p.id} className="flex items-center justify-between p-3.5 hover:bg-slate-50 dark:hover:bg-slate-800/40 transition-colors group">
                                <div className="flex items-center gap-3">
                                    <div className="w-8 h-8 rounded-lg bg-emerald-50 dark:bg-emerald-900/20 flex items-center justify-center text-emerald-500 shrink-0">
                                        <ArrowUpRight className="w-4 h-4" />
                                    </div>
                                    <div className="min-w-0">
                                        <div className="flex items-center gap-2">
                                            <span className="text-xs font-bold text-slate-900 dark:text-white truncate">Pago Recibido</span>
                                            <a href={`/files/${p.travelFile?.id}`} className="text-[10px] font-mono text-indigo-500 hover:underline">{p.travelFile?.fileNumber}</a>
                                        </div>
                                        <div className="text-[10px] text-slate-400 font-medium">
                                            {new Date(p.paidAt).toLocaleDateString()} • {p.method}
                                        </div>
                                    </div>
                                </div>
                                <div className="text-right pl-4">
                                    <div className="text-sm font-black text-emerald-600 dark:text-emerald-400">
                                        +${p.amount?.toLocaleString('es-AR')}
                                    </div>
                                </div>
                            </div>
                        ))}
                        {payments.length === 0 && (
                            <div className="py-12 text-center text-slate-400">
                                <p className="text-xs font-bold uppercase tracking-widest opacity-30">Sin movimientos</p>
                            </div>
                        )}
                    </div>
                </div>

                {/* FACTURACIÓN AFIP - DISEÑO TIPO FEED */}
                <div className="bg-white dark:bg-slate-900/40 rounded-2xl border border-slate-200 dark:border-slate-800 flex flex-col h-full shadow-sm overflow-hidden">
                    <div className="px-5 py-4 border-b border-slate-100 dark:border-slate-800 bg-slate-50/30 dark:bg-slate-800/20 flex items-center justify-between">
                        <div className="flex items-center gap-2">
                            <Receipt className="w-4 h-4 text-indigo-500" />
                            <h3 className="text-sm font-bold text-slate-800 dark:text-slate-200 uppercase tracking-tight">Registro AFIP</h3>
                        </div>
                        <div className="flex items-center gap-1.5">
                            <div className="w-1.5 h-1.5 rounded-full bg-emerald-500 animate-pulse"></div>
                            <span className="text-[10px] font-bold text-slate-400">LIVE</span>
                        </div>
                    </div>

                    <div className="divide-y divide-slate-50 dark:divide-slate-800/50 max-h-[500px] overflow-y-auto">
                        {invoices.slice(0, 15).map(i => (
                            <div key={i.id} className="p-4 hover:bg-slate-50 dark:hover:bg-slate-800/40 transition-all group">
                                <div className="flex justify-between items-start mb-2">
                                    <div className="min-w-0 pr-4">
                                        <div className="flex items-center gap-2 mb-0.5">
                                            <span className={`text-[9px] font-black px-1.5 py-0.5 rounded uppercase tracking-wider ${i.resultado === 'A' ? 'bg-emerald-100 text-emerald-700' :
                                                i.resultado === 'R' ? 'bg-rose-100 text-rose-700' : 'bg-slate-100 text-slate-600'
                                                }`}>
                                                {getInvoiceLabel(i.tipoComprobante)}
                                            </span>
                                            <span className="text-[10px] font-mono font-bold text-slate-400">
                                                {i.numeroComprobante?.toString().padStart(8, '0') || '--------'}
                                            </span>
                                        </div>
                                        <div className="text-xs font-bold text-slate-900 dark:text-white truncate max-w-[180px]">
                                            {i.travelFile?.payer?.fullName || `File ${i.travelFile?.fileNumber}`}
                                        </div>
                                    </div>
                                    <div className="text-right shrink-0">
                                        <div className={`text-sm font-black ${i.resultado === 'R' ? 'text-rose-500' : 'text-slate-900 dark:text-white'}`}>
                                            ${i.importeTotal?.toLocaleString('es-AR')}
                                        </div>
                                        <div className="text-[10px] font-bold text-slate-400 flex items-center justify-end gap-1">
                                            <Calendar className="w-2.5 h-2.5" />
                                            {new Date(i.createdAt).toLocaleDateString()}
                                        </div>
                                    </div>
                                </div>

                                {i.resultado === 'R' && (
                                    <div className="mt-2 mb-3 px-3 py-2 rounded-lg bg-rose-50 dark:bg-rose-900/10 border border-rose-100 dark:border-rose-900/20 text-rose-600 dark:text-rose-400">
                                        <div className="flex items-center gap-1.5 mb-1">
                                            <AlertCircle className="w-3 h-3" />
                                            <span className="text-[10px] font-black uppercase tracking-tight">Error de AFIP</span>
                                        </div>
                                        <p className="text-[10px] font-medium leading-relaxed italic">{i.observaciones || "Error desconocido en el servidor de AFIP."}</p>
                                    </div>
                                )}

                                <div className="flex items-center gap-2 mt-3">
                                    {i.resultado === 'A' ? (
                                        <>
                                            <button
                                                onClick={() => onDownloadPdf(i)}
                                                className="flex-1 flex items-center justify-center gap-2 py-1.5 rounded-lg bg-slate-900 dark:bg-white text-white dark:text-slate-900 text-[10px] font-bold hover:scale-[1.02] active:scale-95 transition-all"
                                            >
                                                <Download className="w-3 h-3" /> PDF
                                            </button>
                                            <button
                                                onClick={() => onAnnulInvoice(i)}
                                                className="px-2.5 py-1.5 rounded-lg bg-slate-50 dark:bg-slate-800 text-slate-400 hover:text-rose-500 transition-colors border border-slate-100 dark:border-slate-800"
                                                title="Anular"
                                            >
                                                <XCircle className="w-3.5 h-3.5" />
                                            </button>
                                        </>
                                    ) : i.resultado === 'R' ? (
                                        <button
                                            onClick={() => onRetryInvoice(i)}
                                            className="w-full flex items-center justify-center gap-2 py-2 rounded-xl bg-orange-500 text-white text-[10px] font-bold hover:bg-orange-600 transition-all shadow-md shadow-orange-200 dark:shadow-none"
                                        >
                                            <RotateCw className="w-3 h-3" /> REINTENTAR EMISIÓN
                                        </button>
                                    ) : (
                                        <div className="w-full flex items-center justify-center gap-2 py-1.5 text-slate-400 text-[10px] font-bold italic animate-pulse">
                                            <Loader2 className="w-3 h-3 animate-spin" /> PROCESANDO...
                                        </div>
                                    )}
                                    <a
                                        href={`/files/${i.travelFile?.id}`}
                                        className="p-1.5 text-indigo-500 hover:bg-indigo-50 dark:hover:bg-indigo-900/30 rounded-lg transition-colors border border-transparent hover:border-indigo-100 dark:hover:border-indigo-800 shrink-0"
                                        title="Ir al Expediente"
                                    >
                                        <ExternalLink className="w-3.5 h-3.5" />
                                    </a>
                                </div>
                            </div>
                        ))}
                        {invoices.length === 0 && (
                            <div className="py-12 text-center text-slate-400">
                                <p className="text-xs font-bold uppercase tracking-widest opacity-30">Sin facturas</p>
                            </div>
                        )}
                    </div>
                </div>
            </div>
        </div>
    );
}
