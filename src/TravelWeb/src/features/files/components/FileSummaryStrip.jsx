import React from 'react';

export function FileSummaryStrip({ file }) {
    const collected = file.payments?.filter(p => p.status !== 'Cancelled').reduce((acc, p) => acc + p.amount, 0) || 0;

    return (
        <div className="flex flex-wrap gap-8 md:gap-16 mb-10 pb-6 border-b border-slate-100 dark:border-slate-800/50">
            <div>
                <p className="text-xs uppercase tracking-wider font-semibold text-slate-400 mb-1">Total Venta</p>
                <p className="text-3xl font-light text-slate-900 dark:text-white">${file.totalSale?.toLocaleString()}</p>
            </div>
            <div>
                <p className="text-xs uppercase tracking-wider font-semibold text-slate-400 mb-1">Total Costo</p>
                <p className="text-3xl font-light text-slate-400 dark:text-slate-500">${file.totalCost?.toLocaleString()}</p>
            </div>
            <div>
                <p className="text-xs uppercase tracking-wider font-semibold text-slate-400 mb-1">Cobrado</p>
                <p className="text-3xl font-light text-slate-900 dark:text-white">
                    ${collected.toLocaleString()}
                </p>
            </div>
            <div>
                <div className="flex items-center gap-2 mb-1">
                    <p className="text-xs uppercase tracking-wider font-semibold text-slate-400">Saldo Pendiente</p>
                    {file.balance > 0 && <span className="w-2 h-2 rounded-full bg-red-500"></span>}
                </div>
                <p className={`text-3xl font-light ${file.balance > 0 ? 'text-red-500' : 'text-slate-400'}`}>
                    ${file.balance?.toLocaleString()}
                </p>
            </div>
        </div>
    );
}
