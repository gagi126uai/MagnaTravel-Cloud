import React, { useState } from 'react';
import { Loader2, XCircle, Download, FileText, ArrowUpRight, History, Receipt, AlertCircle, RotateCw, ExternalLink, ChevronDown, ChevronUp, Clock, Wallet, Eye } from "lucide-react";

export function HistoryTab({ payments, invoices, onDownloadPdf, onViewPdf, onAnnulInvoice, onRetryInvoice }) {
    const [expandedInvoice, setExpandedInvoice] = useState(null);
    const [activeSubTab, setActiveSubTab] = useState('afip'); // 'collections' or 'afip'

    const getInvoiceLabel = (type) => {
        const labels = {
            1: "Factura A", 6: "Factura B", 11: "Factura C",
            3: "Nota de Crédito A", 8: "Nota de Crédito B", 13: "Nota de Crédito C",
            2: "Nota de Débito A", 7: "Nota de Débito B", 12: "Nota de Débito C",
            51: "Factura M", 53: "Nota de Crédito M", 52: "Nota de Débito M"
        };
        return labels[type] || `Comp (${type})`;
    };

    const hasData = payments.length > 0 || invoices.length > 0;

    if (!hasData) {
        return (
            <div className="py-20 text-center bg-slate-50/50 dark:bg-slate-800/20 rounded-3xl border-2 border-dashed border-slate-200 dark:border-slate-800">
                <div className="w-16 h-16 bg-white dark:bg-slate-900 rounded-full flex items-center justify-center mx-auto mb-4 shadow-sm border border-slate-100 dark:border-slate-800 text-slate-300">
                    <Clock className="w-8 h-8" />
                </div>
                <h3 className="text-lg font-bold text-slate-900 dark:text-white mb-1">Sin historial</h3>
                <p className="text-slate-500 dark:text-slate-400 text-sm">No se registran movimientos ni comprobantes aún.</p>
            </div>
        );
    }

    return (
        <div className="space-y-6 animate-in fade-in slide-in-from-bottom-2 duration-500 mt-6">

            {/* NESTED TABS */}
            <div className="flex gap-4 border-b border-slate-200 dark:border-slate-800 pb-px">
                <button
                    onClick={() => setActiveSubTab('afip')}
                    className={`pb-3 text-sm font-semibold transition-colors flex items-center gap-2 relative ${activeSubTab === 'afip' ? 'text-indigo-600 dark:text-indigo-400' : 'text-slate-500 hover:text-slate-700 dark:text-slate-400 dark:hover:text-slate-200'
                        }`}
                >
                    <Receipt className="w-4 h-4" />
                    Registro Fiscal AFIP
                    {activeSubTab === 'afip' && (
                        <div className="absolute -bottom-px left-0 right-0 h-0.5 bg-indigo-600 dark:bg-indigo-400 rounded-t-full"></div>
                    )}
                </button>
                <button
                    onClick={() => setActiveSubTab('collections')}
                    className={`pb-3 text-sm font-semibold transition-colors flex items-center gap-2 relative ${activeSubTab === 'collections' ? 'text-indigo-600 dark:text-indigo-400' : 'text-slate-500 hover:text-slate-700 dark:text-slate-400 dark:hover:text-slate-200'
                        }`}
                >
                    <Wallet className="w-4 h-4" />
                    Cobranzas Recientes
                    {activeSubTab === 'collections' && (
                        <div className="absolute -bottom-px left-0 right-0 h-0.5 bg-indigo-600 dark:bg-indigo-400 rounded-t-full"></div>
                    )}
                </button>
            </div>

            {/* --- SECCIÓN COBRANZAS --- */}
            {activeSubTab === 'collections' && (
                <div className="space-y-4 animate-in fade-in duration-300">
                    <div className="hidden md:block overflow-hidden rounded-xl border border-slate-200 dark:border-slate-800 bg-white dark:bg-slate-900/50 shadow-sm">
                        <table className="w-full text-left border-collapse">
                            <thead>
                                <tr className="bg-slate-50/50 dark:bg-slate-800/50 border-b border-slate-200 dark:border-slate-800">
                                    <th className="px-6 py-4 text-[11px] uppercase font-bold text-slate-500 tracking-wider w-16 text-center">#</th>
                                    <th className="px-6 py-4 text-[11px] uppercase font-bold text-slate-500 tracking-wider">Fecha</th>
                                    <th className="px-6 py-4 text-[11px] uppercase font-bold text-slate-500 tracking-wider">Detalle / Reserva</th>
                                    <th className="px-6 py-4 text-[11px] uppercase font-bold text-slate-500 tracking-wider">Método</th>
                                    <th className="px-6 py-4 text-[11px] uppercase font-bold text-slate-500 tracking-wider text-right pr-10">Importe</th>
                                </tr>
                            </thead>
                            <tbody className="divide-y divide-slate-100 dark:divide-slate-800">
                                {payments.slice(0, 15).map((p, idx) => (
                                    <tr key={p.id} className="hover:bg-slate-50/50 dark:hover:bg-slate-800/30 transition-colors group">
                                        <td className="px-6 py-4 text-center text-[10px] font-mono text-slate-400">{(idx + 1).toString().padStart(2, '0')}</td>
                                        <td className="px-6 py-4 text-sm text-slate-600 dark:text-slate-400">{new Date(p.paidAt).toLocaleDateString()}</td>
                                        <td className="px-6 py-4">
                                            <div className="flex items-center gap-3">
                                                <div className="p-2 rounded-lg bg-emerald-50 dark:bg-emerald-950/30 text-emerald-600 dark:text-emerald-400">
                                                    <ArrowUpRight className="w-4 h-4" />
                                                </div>
                                                <div className="flex flex-col">
                                                    <span className="text-sm font-bold text-slate-900 dark:text-white">Pago Recibido</span>
                                                    <a href={`/reservas/${p.reserva?.id}`} className="text-xs text-indigo-500 hover:underline font-mono uppercase tracking-tight">RES: {p.reserva?.numeroReserva || '---'}</a>
                                                </div>
                                            </div>
                                        </td>
                                        <td className="px-6 py-4">
                                            <span className="text-[10px] font-black text-slate-500 dark:text-slate-400 bg-slate-100 dark:bg-slate-800 px-2 py-0.5 rounded uppercase tracking-tighter">
                                                {p.method}
                                            </span>
                                        </td>
                                        <td className="px-6 py-4 text-right pr-10">
                                            <span className="text-sm font-black text-emerald-600 dark:text-emerald-500">
                                                +${p.amount?.toLocaleString('es-AR', { minimumFractionDigits: 2 })}
                                            </span>
                                        </td>
                                    </tr>
                                ))}
                            </tbody>
                        </table>
                    </div>

                    {/* Mobile Collections */}
                    <div className="md:hidden space-y-3">
                        {payments.slice(0, 10).map(p => (
                            <div key={p.id} className="bg-white dark:bg-slate-900 rounded-xl p-4 border border-slate-200 dark:border-slate-800 flex justify-between items-center">
                                <div className="flex items-center gap-3">
                                    <div className="p-2 rounded-lg bg-emerald-50 dark:bg-emerald-900/30 text-emerald-600">
                                        <ArrowUpRight className="w-4 h-4" />
                                    </div>
                                    <div>
                                        <div className="text-sm font-bold text-slate-900 dark:text-white">{p.reserva?.numeroReserva}</div>
                                        <div className="text-[10px] text-slate-400 uppercase font-black">{new Date(p.paidAt).toLocaleDateString()} • {p.method}</div>
                                    </div>
                                </div>
                                <div className="text-sm font-black text-emerald-600 dark:text-emerald-400">
                                    +${p.amount?.toLocaleString('es-AR')}
                                </div>
                            </div>
                        ))}
                    </div>
                </div>
            )}

            {/* --- SECCIÓN FACTURACIÓN --- */}
            {activeSubTab === 'afip' && (
                <div className="space-y-4 animate-in fade-in duration-300">
                    <div className="hidden md:block overflow-hidden rounded-xl border border-slate-200 dark:border-slate-800 bg-white dark:bg-slate-900/50 shadow-sm">
                        <table className="w-full text-left border-collapse">
                            <thead>
                                <tr className="bg-slate-50/50 dark:bg-slate-800/50 border-b border-slate-200 dark:border-slate-800">
                                    <th className="px-6 py-4 text-[11px] uppercase font-bold text-slate-500 tracking-wider">Comprobante</th>
                                    <th className="px-6 py-4 text-[11px] uppercase font-bold text-slate-500 tracking-wider">Fecha / Reserva</th>
                                    <th className="px-6 py-4 text-[11px] uppercase font-bold text-slate-500 tracking-wider text-right">Importe</th>
                                    <th className="px-6 py-4 text-[11px] uppercase font-bold text-slate-500 tracking-wider text-center">Resultado</th>
                                    <th className="px-6 py-4 text-[11px] uppercase font-bold text-slate-500 tracking-wider text-right pr-6">Acciones</th>
                                </tr>
                            </thead>
                            <tbody className="divide-y divide-slate-100 dark:divide-slate-800">
                                {invoices.slice(0, 15).map((i) => (
                                    <React.Fragment key={i.id}>
                                        <tr className="hover:bg-slate-50/50 dark:hover:bg-slate-800/30 transition-colors">
                                            <td className="px-6 py-4">
                                                <div className="flex flex-col">
                                                    <span className="text-xs font-black text-slate-800 dark:text-slate-200 uppercase">{getInvoiceLabel(i.tipoComprobante)}</span>
                                                    <span className="text-[10px] font-mono text-slate-400 font-bold">{i.numeroComprobante?.toString().padStart(8, '0') || '--------'}</span>
                                                </div>
                                            </td>
                                            <td className="px-6 py-4">
                                                <div className="text-sm font-medium text-slate-900 dark:text-white">{new Date(i.createdAt).toLocaleDateString()}</div>
                                                <a href={`/reservas/${i.reserva?.id}`} className="text-[10px] text-indigo-500 hover:underline font-bold uppercase tracking-tighter">
                                                    RES: {i.reserva?.numeroReserva || '---'}
                                                </a>
                                            </td>
                                            <td className="px-6 py-4 text-right">
                                                <span className={`text-sm font-black ${i.resultado === 'R' ? 'text-rose-500' : 'text-slate-900 dark:text-white'}`}>
                                                    ${i.importeTotal?.toLocaleString('es-AR', { minimumFractionDigits: 2 })}
                                                </span>
                                            </td>
                                            <td className="px-6 py-4 text-center">
                                                <div className="flex justify-center">
                                                    {i.resultado === 'A' ? (
                                                        <div className="flex items-center gap-1 text-emerald-600 dark:text-emerald-400 text-[10px] font-black uppercase">
                                                            <div className="w-1.5 h-1.5 rounded-full bg-emerald-500"></div>
                                                            Aceptado
                                                        </div>
                                                    ) : i.resultado === 'R' ? (
                                                        <button
                                                            onClick={() => setExpandedInvoice(expandedInvoice === i.id ? null : i.id)}
                                                            className="flex items-center gap-1 text-rose-600 dark:text-rose-400 text-[10px] font-black uppercase hover:underline decoration-2"
                                                        >
                                                            <AlertCircle className="w-3.5 h-3.5" />
                                                            Rechazado
                                                            {expandedInvoice === i.id ? <ChevronUp className="w-3 h-3" /> : <ChevronDown className="w-3 h-3" />}
                                                        </button>
                                                    ) : (
                                                        <div className="flex items-center gap-1 text-slate-400 text-[10px] font-black uppercase italic">
                                                            <Loader2 className="w-3 h-3 animate-spin" />
                                                            En Proceso
                                                        </div>
                                                    )}
                                                </div>
                                            </td>
                                            <td className="px-6 py-4 text-right pr-6">
                                                <div className="flex items-center justify-end gap-1.5">
                                                    {i.resultado === 'A' && (
                                                        <>
                                                            <button
                                                                onClick={() => onViewPdf(i)}
                                                                className="p-2 rounded-lg hover:bg-slate-100 dark:hover:bg-slate-800 text-slate-500 hover:text-indigo-600 dark:hover:text-indigo-400 transition-all"
                                                                title="Ver en Navegador"
                                                            >
                                                                <Eye className="w-4.5 h-4.5" />
                                                            </button>
                                                            <button
                                                                onClick={() => onDownloadPdf(i)}
                                                                className="p-2 rounded-lg hover:bg-slate-100 dark:hover:bg-slate-800 text-slate-500 hover:text-slate-900 dark:hover:text-white transition-all"
                                                                title="Descargar PDF"
                                                            >
                                                                <Download className="w-4.5 h-4.5" />
                                                            </button>
                                                            {/* Restricción: No anular notas de crédito/débito (Tipos 3, 8, 13, 53, 2, 7, 12, 52) */}
                                                            {![2, 3, 7, 8, 12, 13, 52, 53].includes(i.tipoComprobante) && (
                                                                <button
                                                                    onClick={() => onAnnulInvoice(i)}
                                                                    className="p-2 rounded-lg hover:bg-rose-50 dark:hover:bg-rose-900/20 text-slate-400 hover:text-rose-600 transition-all"
                                                                    title="Anular (Generar Nota de Crédito)"
                                                                >
                                                                    <XCircle className="w-4.5 h-4.5" />
                                                                </button>
                                                            )}
                                                        </>
                                                    )}
                                                    {i.resultado === 'R' && (
                                                        <button
                                                            onClick={() => onRetryInvoice(i)}
                                                            className="px-4 py-1.5 rounded-lg bg-indigo-600 text-white text-[10px] font-bold hover:bg-indigo-700 transition-all shadow-md shadow-indigo-100 dark:shadow-none"
                                                        >
                                                            REINTENTAR
                                                        </button>
                                                    )}
                                                    <a href={`/reservas/${i.reserva?.id}`} className="p-2 rounded-lg hover:bg-slate-100 dark:hover:bg-slate-800 text-slate-300 hover:text-indigo-600 transition-all">
                                                        <ExternalLink className="w-4.5 h-4.5" />
                                                    </a>
                                                </div>
                                            </td>
                                        </tr>
                                        {expandedInvoice === i.id && (
                                            <tr className="bg-rose-50/40 dark:bg-rose-950/20">
                                                <td colSpan="5" className="px-8 py-4">
                                                    <div className="flex items-start gap-3 border-l-2 border-rose-500 pl-4 py-1">
                                                        <div className="p-1 rounded bg-rose-100 dark:bg-rose-900/30 text-rose-600">
                                                            <AlertCircle className="w-4 h-4" />
                                                        </div>
                                                        <div>
                                                            <div className="text-[10px] font-black text-rose-800 dark:text-rose-300 uppercase tracking-wider mb-1">Motivo del Rechazo (AFIP)</div>
                                                            <p className="text-xs font-semibold text-rose-600 dark:text-rose-400 leading-relaxed italic">
                                                                "{i.observaciones || "AFIP rechazó el comprobante sin proporcionar una observación detallada. (Verificar CUIT/Condición IVA del cliente)."}"
                                                            </p>
                                                        </div>
                                                    </div>
                                                </td>
                                            </tr>
                                        )}
                                    </React.Fragment>
                                ))}
                            </tbody>
                        </table>
                    </div>

                    {/* Mobile Invoices */}
                    <div className="md:hidden space-y-4">
                        {invoices.slice(0, 10).map(i => (
                            <div key={i.id} className="bg-white dark:bg-slate-900 rounded-xl p-5 border border-slate-200 dark:border-slate-800">
                                <div className="flex justify-between items-start mb-4">
                                    <div>
                                        <div className="text-[10px] font-black text-slate-400 uppercase mb-1">{getInvoiceLabel(i.tipoComprobante)}</div>
                                        <div className="text-sm font-bold text-slate-900 dark:text-white capitalize">{i.reserva?.payer?.fullName || 'Cliente S/D'}</div>
                                        <div className="text-[10px] font-mono text-indigo-500 font-bold uppercase tracking-tight">{i.reserva?.numeroReserva}</div>
                                    </div>
                                    <div className="text-right">
                                        <div className="text-base font-black text-slate-900 dark:text-white">${i.importeTotal?.toLocaleString()}</div>
                                        <div className="text-[9px] font-bold text-slate-400">{new Date(i.createdAt).toLocaleDateString()}</div>
                                    </div>
                                </div>

                                <div className="flex items-center gap-2 pt-4 border-t border-slate-50 dark:border-slate-800/50">
                                    {i.resultado === 'A' ? (
                                        <>
                                            <button onClick={() => onViewPdf(i)} className="flex-1 py-2 bg-slate-900 dark:bg-white text-white dark:text-slate-900 rounded-lg text-xs font-bold shadow-lg shadow-slate-200 dark:shadow-none">VER PDF</button>
                                        </>
                                    ) : (
                                        <button onClick={() => onRetryInvoice(i)} className="flex-1 py-2 bg-indigo-600 text-white rounded-lg text-xs font-bold">REINTENTAR</button>
                                    )}
                                    <a href={`/reservas/${i.reserva?.id}`} className="p-2 bg-slate-50 dark:bg-slate-800 text-slate-400 rounded-lg"><ExternalLink className="w-4 h-4" /></a>
                                </div>

                                {i.resultado === 'R' && (
                                    <div className="mt-3 p-3 bg-rose-50 dark:bg-rose-950/20 rounded-lg text-[10px] text-rose-600 font-medium italic">
                                        {i.observaciones}
                                    </div>
                                )}
                            </div>
                        ))}
                    </div>
                </div>
            )}
        </div>
    );
}
