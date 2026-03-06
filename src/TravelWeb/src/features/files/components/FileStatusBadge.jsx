import React from 'react';

export const statusConfig = {
    'Presupuesto': { color: 'bg-blue-50 text-blue-700 border-blue-200 dark:bg-blue-900/20 dark:text-blue-300 dark:border-blue-800', icon: '📋' },
    'Reservado': { color: 'bg-amber-50 text-amber-700 border-amber-200 dark:bg-amber-900/20 dark:text-amber-300 dark:border-amber-800', icon: '📌' },
    'Operativo': { color: 'bg-emerald-50 text-emerald-700 border-emerald-200 dark:bg-emerald-900/20 dark:text-emerald-300 dark:border-emerald-800', icon: '✈️' },
    'Cerrado': { color: 'bg-slate-100 text-slate-600 border-slate-200 dark:bg-slate-800 dark:text-slate-400 dark:border-slate-700', icon: '✅' },
    'Cancelado': { color: 'bg-rose-50 text-rose-700 border-rose-200 dark:bg-rose-900/20 dark:text-rose-300 dark:border-rose-800', icon: '❌' },
};

export function FileStatusBadge({ status }) {
    const cfg = statusConfig[status] || statusConfig['Presupuesto'];
    return (
        <span className={`px-2.5 py-0.5 rounded-full text-xs font-medium border ${cfg.color}`}>
            {status}
        </span>
    );
}
