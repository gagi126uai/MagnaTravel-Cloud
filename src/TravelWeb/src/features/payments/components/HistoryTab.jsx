import React from 'react';
import { Loader2, XCircle, Download } from "lucide-react";

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
        <div className="animate-in fade-in slide-in-from-bottom-2 duration-300 grid grid-cols-1 lg:grid-cols-2 gap-12 mt-4">
            {/* HISTORIAL CAJA */}
            <div>
                <h3 className="text-sm font-semibold text-slate-900 dark:text-slate-100 mb-4 flex items-center gap-2">
                    <div className="w-2 h-2 rounded-full bg-slate-300"></div> Movimientos Recientes
                </h3>
                <div className="space-y-4">
                    {payments.slice(0, 15).map(p => (
                        <div key={p.id} className="flex justify-between items-center group">
                            <div>
                                <div className="text-sm font-medium text-slate-900 dark:text-slate-200">
                                    Cobro en File <a href={`/files/${p.travelFile?.id}`} className="hover:underline text-blue-600 text-xs font-mono">{p.travelFile?.fileNumber}</a>
                                </div>
                                <div className="text-xs text-slate-400">{new Date(p.paidAt).toLocaleDateString()} • {p.method}</div>
                            </div>
                            <div className="text-right font-medium text-slate-600 dark:text-slate-400">
                                +${p.amount?.toLocaleString()}
                            </div>
                        </div>
                    ))}
                    {payments.length === 0 && <div className="text-sm text-slate-400">No hay movimientos.</div>}
                </div>
            </div>

            {/* HISTORIAL AFIP */}
            <div>
                <h3 className="text-sm font-semibold text-slate-900 dark:text-slate-100 mb-4 flex items-center gap-2">
                    <div className="w-2 h-2 rounded-full bg-slate-300"></div> Comprobantes Emitidos
                </h3>
                <div className="space-y-4">
                    {invoices.slice(0, 15).map(i => (
                        <div key={i.id} className="flex justify-between items-center group relative">
                            <div className="pr-4 flex-1">
                                <div className="text-sm font-medium flex items-center gap-1.5 dark:text-slate-200">
                                    {i.resultado === 'A' ? (
                                        <span className="text-slate-900 dark:text-slate-200">{getInvoiceLabel(i.tipoComprobante)} {i.numeroComprobante}</span>
                                    ) : i.resultado === 'PENDING' ? (
                                        <span className="text-slate-500 flex items-center gap-1"><Loader2 className="w-3 h-3 animate-spin" /> Procesando</span>
                                    ) : (
                                        <span className="text-red-600 flex items-center gap-1"><XCircle className="w-3 h-3" /> Rechazada</span>
                                    )}
                                </div>
                                <div className="text-xs text-slate-400 truncate max-w-[200px]" title={i.travelFile?.payer?.fullName}>
                                    {i.travelFile?.payer?.fullName || `File ${i.travelFile?.fileNumber}`} • {new Date(i.createdAt).toLocaleDateString()}
                                </div>
                                {i.resultado === 'R' && <div className="text-[10px] text-red-500 truncate" title={i.observaciones}>{i.observaciones}</div>}
                            </div>

                            <div className="flex items-center gap-3">
                                <div className="text-right font-medium text-slate-900 dark:text-slate-200">
                                    ${i.importeTotal?.toLocaleString()}
                                </div>

                                <div className="group-hover:opacity-100 md:opacity-0 transition-opacity flex gap-1 bg-white dark:bg-slate-900 pl-2">
                                    {i.resultado === 'A' && (
                                        <>
                                            <button onClick={() => onDownloadPdf(i)} className="p-1.5 text-slate-400 hover:text-slate-900 dark:hover:text-white bg-slate-50 dark:bg-slate-800 rounded" title="Descargar"><Download className="w-3.5 h-3.5" /></button>
                                            <button onClick={() => onAnnulInvoice(i)} className="p-1.5 text-slate-400 hover:text-red-600 bg-slate-50 dark:bg-slate-800 rounded" title="Anular"><XCircle className="w-3.5 h-3.5" /></button>
                                        </>
                                    )}
                                    {i.resultado === 'R' && (
                                        <button onClick={() => onRetryInvoice(i)} className="text-xs bg-slate-100 dark:bg-slate-800 hover:bg-slate-200 text-slate-700 dark:text-slate-300 px-2 py-1 rounded">Reintentar</button>
                                    )}
                                </div>
                            </div>
                        </div>
                    ))}
                    {invoices.length === 0 && <div className="text-sm text-slate-400">No hay comprobantes.</div>}
                </div>
            </div>
        </div>
    );
}
