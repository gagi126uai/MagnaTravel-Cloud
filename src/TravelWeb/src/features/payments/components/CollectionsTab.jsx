import React from 'react';
import { DollarSign } from "lucide-react";

export function CollectionsTab({ files, onPay }) {
    const filtered = files.filter(f => f.pendingCollection > 0);

    return (
        <div className="animate-in fade-in slide-in-from-bottom-2 duration-300">
            <table className="w-full text-left border-collapse">
                <thead>
                    <tr className="border-b border-slate-100 dark:border-slate-800">
                        <th className="pb-3 text-xs uppercase text-slate-400 font-medium">Expediente</th>
                        <th className="pb-3 text-xs uppercase text-slate-400 font-medium">Cliente</th>
                        <th className="pb-3 text-xs uppercase text-slate-400 font-medium text-right">Saldo Comercial</th>
                        <th className="pb-3 text-xs uppercase text-slate-400 font-medium text-right pr-4">Acción</th>
                    </tr>
                </thead>
                <tbody>
                    {filtered.map(file => (
                        <tr key={file.id} className="group border-b border-slate-50 dark:border-slate-800/50 hover:bg-slate-50/50 dark:hover:bg-slate-800/20 transition-colors">
                            <td className="py-4 align-middle">
                                <a href={`/files/${file.id}`} className="font-medium text-slate-900 dark:text-white hover:text-slate-600">{file.fileNumber}</a>
                                <div className="text-xs text-slate-400">Total: ${file.totalSaleAmount?.toLocaleString()}</div>
                            </td>
                            <td className="py-4 align-middle text-sm text-slate-600 dark:text-slate-300">
                                {file.payer?.fullName || file.customerName || "-"}
                            </td>
                            <td className="py-4 align-middle text-right">
                                <div className="font-semibold text-slate-900 dark:text-white">
                                    ${file.pendingCollection?.toLocaleString('es-AR')}
                                </div>
                                {file.computedPaid > 0 && <div className="text-[10px] text-slate-400">Abonó: ${file.computedPaid?.toLocaleString()}</div>}
                            </td>
                            <td className="py-4 align-middle text-right pr-4">
                                <button
                                    onClick={() => onPay(file)}
                                    className="group-hover:opacity-100 md:opacity-0 transition-opacity inline-flex items-center justify-center w-8 h-8 rounded-full bg-slate-100 text-slate-600 hover:bg-slate-800 hover:text-white dark:bg-slate-800 dark:text-slate-300 dark:hover:bg-slate-700"
                                    title="Registrar Cobro"
                                >
                                    <DollarSign className="w-4 h-4" />
                                </button>
                            </td>
                        </tr>
                    ))}
                    {filtered.length === 0 && (
                        <tr>
                            <td colSpan="4" className="py-12 text-center text-slate-400 text-sm">No hay expedientes con deuda comercial pendiente.</td>
                        </tr>
                    )}
                </tbody>
            </table>
        </div>
    );
}
