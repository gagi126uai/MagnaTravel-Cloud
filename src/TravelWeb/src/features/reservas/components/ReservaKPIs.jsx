import React from 'react';
import { FolderOpen, Plane, TrendingUp, Wallet, AlertCircle } from "lucide-react";
import { formatCurrency } from "../../../lib/utils";

export function ReservaKPIs({ stats }) {
    return (
        <div className="grid grid-cols-2 md:grid-cols-5 gap-3">
            <div className="rounded-xl border border-slate-200 bg-white p-3 shadow-sm dark:border-slate-800 dark:bg-slate-900/50">
                <div className="flex items-center gap-2 text-xs text-slate-500 dark:text-slate-400 mb-1">
                    <FolderOpen className="h-3.5 w-3.5" />
                    Reservas Activas
                </div>
                <div className="text-xl font-bold text-slate-900 dark:text-white">{stats.activeCount}</div>
            </div>
            <div className="rounded-xl border border-slate-200 bg-white p-3 shadow-sm dark:border-slate-800 dark:bg-slate-900/50">
                <div className="flex items-center gap-2 text-xs text-slate-500 dark:text-slate-400 mb-1">
                    <Plane className="h-3.5 w-3.5" />
                    Operativos
                </div>
                <div className="text-xl font-bold text-emerald-600 dark:text-emerald-400">{stats.operativeCount}</div>
            </div>
            <div className="rounded-xl border border-slate-200 bg-white p-3 shadow-sm dark:border-slate-800 dark:bg-slate-900/50">
                <div className="flex items-center gap-2 text-xs text-slate-500 dark:text-slate-400 mb-1">
                    <TrendingUp className="h-3.5 w-3.5" />
                    Venta Total
                </div>
                <div className="text-xl font-bold text-indigo-600 dark:text-indigo-400">{formatCurrency(stats.totalSaleActive)}</div>
            </div>
            <div className="rounded-xl border border-slate-200 bg-white p-3 shadow-sm dark:border-slate-800 dark:bg-slate-900/50">
                <div className="flex items-center gap-2 text-xs text-slate-500 dark:text-slate-400 mb-1">
                    <Wallet className="h-3.5 w-3.5" />
                    Rentabilidad Est.
                </div>
                <div className="text-xl font-bold text-blue-600 dark:text-blue-400">{formatCurrency(stats.grossProfit)}</div>
            </div>
            <div className="rounded-xl border border-slate-200 bg-white p-3 shadow-sm dark:border-slate-800 dark:bg-slate-900/50">
                <div className="flex items-center gap-2 text-xs text-slate-500 dark:text-slate-400 mb-1">
                    <AlertCircle className="h-3.5 w-3.5" />
                    Por Cobrar
                </div>
                <div className={`text-xl font-bold ${stats.totalPendingBalance > 0 ? 'text-rose-600 dark:text-rose-400' : 'text-emerald-600 dark:text-emerald-400'}`}>
                    {formatCurrency(stats.totalPendingBalance)}
                </div>
            </div>
        </div>
    );
}
