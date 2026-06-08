import React from 'react';
import { formatCurrency } from "../../../lib/utils";
import { isAdmin } from "../../../auth";

/**
 * Franja de numeros clave de la reserva — aparece debajo del header en la pagina de detalle.
 *
 * Decision 5 (guia UX 2026-06-08): DOS numeros de plata diferenciados, SIN duplicar:
 *   - "SALDO A COBRAR" (grande): lo que el cliente debe HOY por servicios ya confirmados.
 *     El "de $X presupuestado" (chico) va debajo cuando totalSale > balance.
 *   - "RECAUDADO": lo que el cliente ya pagó.
 *   - "INVERSIÓN" (admin only): el costo neto total.
 *
 * N4 del reviewer: la columna "Total Venta" fue eliminada porque duplicaba la info
 * que ya aparece como "de $X presupuestado" dentro de Saldo a Cobrar (decision #5).
 * Mantener ambas era confuso para el vendedor.
 */
export function ReservaSummaryStrip({ reserva }) {
    const collected = reserva.payments?.filter(p => p.status !== 'Cancelled').reduce((acc, p) => acc + p.amount, 0) || 0;
    const admin = isAdmin();

    return (
        <div className={`grid grid-cols-2 ${admin ? 'md:grid-cols-3' : 'md:grid-cols-2'} gap-6 mb-10 pb-8 border-b border-slate-100 dark:border-slate-800/50`}>

            {/* Saldo a cobrar: lo que el cliente debe HOY por servicios resueltos.
                ADR-020: Balance = ConfirmedSale - TotalPaid. Un servicio solicitado NO genera deuda. */}
            <div className="space-y-1">
                <p className="text-[10px] uppercase tracking-widest font-bold text-slate-400 dark:text-slate-500">
                    Saldo a Cobrar
                </p>
                <p className={`text-3xl font-extrabold leading-none ${reserva.balance > 0 ? 'text-rose-600 dark:text-rose-500' : 'text-slate-300 dark:text-slate-700'}`}>
                    {formatCurrency(reserva.balance)}
                    {reserva.balance > 0 && <span className="inline-block ml-2 w-1.5 h-1.5 rounded-full bg-rose-500 animate-pulse align-middle" />}
                </p>
                {/* "de $X presupuestado" solo si totalSale difiere del balance (hay servicios no confirmados aún) */}
                {reserva.totalSale > 0 && (
                    <p className="text-xs text-slate-400 dark:text-slate-500">
                        de {formatCurrency(reserva.totalSale)} presupuestado
                    </p>
                )}
            </div>

            <div className="space-y-1">
                <p className="text-[10px] uppercase tracking-widest font-bold text-slate-400 dark:text-slate-500">Recaudado</p>
                <p className="text-3xl font-extrabold text-emerald-600 dark:text-emerald-500 leading-none">
                    {formatCurrency(collected)}
                </p>
            </div>

            {admin && (
                <div className="space-y-1">
                    <p className="text-[10px] uppercase tracking-widest font-bold text-slate-400 dark:text-slate-500">Inversión (Costo)</p>
                    <p className="text-3xl font-bold text-slate-400 dark:text-slate-600 leading-none">
                        {formatCurrency(reserva.totalCost)}
                    </p>
                </div>
            )}
        </div>
    );
}
