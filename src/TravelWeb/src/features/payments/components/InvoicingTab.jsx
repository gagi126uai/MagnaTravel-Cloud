import React from 'react';

export function InvoicingTab({ files, onInvoice }) {
    const filtered = files.filter(f => f.pendingBilling > 0);

    return (
        <div className="animate-in fade-in slide-in-from-bottom-2 duration-300">
            <table className="w-full text-left border-collapse">
                <thead>
                    <tr className="border-b border-slate-100 dark:border-slate-800">
                        <th className="pb-3 text-xs uppercase text-slate-400 font-medium">Expediente</th>
                        <th className="pb-3 text-xs uppercase text-slate-400 font-medium">Dinero Ingresado</th>
                        <th className="pb-3 text-xs uppercase text-slate-400 font-medium text-right">A Facturar (Sin Comprobante)</th>
                        <th className="pb-3 text-xs uppercase text-slate-400 font-medium text-right pr-4">Acción</th>
                    </tr>
                </thead>
                <tbody>
                    {filtered.map(file => (
                        <tr key={file.id} className="group border-b border-slate-50 dark:border-slate-800/50 hover:bg-slate-50/50 dark:hover:bg-slate-800/20 transition-colors">
                            <td className="py-4 align-middle">
                                <a href={`/files/${file.id}`} className="font-medium text-slate-900 dark:text-white hover:text-slate-600">{file.fileNumber}</a>
                                <div className="text-xs text-slate-400">{file.payer?.fullName || file.customerName}</div>
                            </td>
                            <td className="py-4 align-middle text-sm text-slate-600 dark:text-slate-300">
                                <div className="flex items-center gap-1.5">
                                    <div className="w-1.5 h-1.5 rounded-full bg-green-400"></div>
                                    <span>${file.computedPaid?.toLocaleString('es-AR')}</span>
                                </div>
                            </td>
                            <td className="py-4 align-middle text-right">
                                <div className="font-semibold text-slate-900 dark:text-white">
                                    ${file.pendingBilling?.toLocaleString('es-AR')}
                                </div>
                                {file.computedInvoiced > 0 && <div className="text-[10px] text-slate-400">Ya facturado: ${file.computedInvoiced?.toLocaleString()}</div>}
                            </td>
                            <td className="py-4 align-middle text-right pr-4">
                                <button
                                    onClick={() => onInvoice(file)}
                                    className="group-hover:opacity-100 md:opacity-0 transition-opacity inline-flex items-center justify-center px-3 py-1.5 text-sm rounded-full bg-slate-900 text-white hover:bg-slate-800 dark:bg-white dark:text-slate-900 hover:shadow-md"
                                    title="Emitir Factura"
                                >
                                    Emitir
                                </button>
                            </td>
                        </tr>
                    ))}
                    {filtered.length === 0 && (
                        <tr>
                            <td colSpan="4" className="py-12 text-center text-slate-400 text-sm">Todo el dinero ingresado tiene comprobante fiscal.</td>
                        </tr>
                    )}
                </tbody>
            </table>
        </div>
    );
}
