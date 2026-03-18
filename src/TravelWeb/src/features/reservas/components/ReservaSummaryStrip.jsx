import React from 'react';
import { formatCurrency } from "../../../lib/utils";

export function ReservaSummaryStrip({ reserva }) {
    const collected = reserva.payments?.filter(p => p.status !== 'Cancelled').reduce((acc, p) => acc + p.amount, 0) || 0;

    return (
        <div className="grid grid-cols-2 md:grid-cols-4 gap-6 mb-10 pb-8 border-b border-slate-100 dark:border-slate-800/50">
            <div className="space-y-1">
                <p className="text-[10px] uppercase tracking-widest font-bold text-slate-400 dark:text-slate-500">Total Venta</p>
                <p className="text-3xl font-extrabold text-slate-900 dark:text-white leading-none">
                    {formatCurrency(reserva.totalSale)}
                </p>
            </div>
            <div className="space-y-1">
                <p className="text-[10px] uppercase tracking-widest font-bold text-slate-400 dark:text-slate-500">Inversión (Costo)</p>
                <p className="text-3xl font-bold text-slate-400 dark:text-slate-600 leading-none">
                    {formatCurrency(reserva.totalCost)}
                </p>
            </div>
            <div className="space-y-1">
                <p className="text-[10px] uppercase tracking-widest font-bold text-slate-400 dark:text-slate-500">Recaudado</p>
                <p className="text-3xl font-extrabold text-emerald-600 dark:text-emerald-500 leading-none">
                    {formatCurrency(collected)}
                </p>
            </div>
            <div className="space-y-1">
                <div className="flex items-center gap-2">
                    <p className="text-[10px] uppercase tracking-widest font-bold text-slate-400 dark:text-slate-500">Pendiente</p>
                    {reserva.balance > 0 && <span className="w-1.5 h-1.5 rounded-full bg-rose-500 animate-pulse"></span>}
                </div>
                <p className={`text-3xl font-extrabold leading-none ${reserva.balance > 0 ? 'text-rose-600 dark:text-rose-500' : 'text-slate-300 dark:text-slate-700'}`}>
                    {formatCurrency(reserva.balance)}
                </p>
            </div>
        </div>
    );
}
