import React from 'react';

export function PaymentKPIs({ stats }) {
    return (
        <div className="flex flex-wrap gap-8 md:gap-16">
            <div>
                <div className="text-xs uppercase tracking-wider font-semibold text-slate-400 mb-1">Cuentas por Cobrar</div>
                <div className="text-3xl font-light text-slate-900 dark:text-white">
                    ${stats.totalPendingCollection.toLocaleString('es-AR')}
                </div>
            </div>
            <div>
                <div className="text-xs uppercase tracking-wider font-semibold text-slate-400 mb-1">Cobro Sin Facturar</div>
                <div className="text-3xl font-light text-slate-900 dark:text-white">
                    ${stats.totalPendingBilling.toLocaleString('es-AR')}
                </div>
            </div>
            <div>
                <div className="text-xs uppercase tracking-wider font-semibold text-slate-400 mb-1">Facturado (Mes)</div>
                <div className="text-3xl font-light text-slate-400 dark:text-slate-500">
                    ${stats.totalInvoicedMonth.toLocaleString('es-AR')}
                </div>
            </div>
        </div>
    );
}
