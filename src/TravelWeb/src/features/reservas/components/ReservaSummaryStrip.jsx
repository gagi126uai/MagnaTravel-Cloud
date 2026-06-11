import React from 'react';
import { formatCurrency } from "../../../lib/utils";
import { CurrencyBadge } from "../../../components/ui/CurrencyBadge";
import { isAdmin } from "../../../auth";

/**
 * Franja de números clave de la reserva — aparece debajo del header en la página de detalle.
 *
 * Decision 5 (guia UX 2026-06-08): DOS números de plata diferenciados:
 *   - "SALDO A COBRAR" (grande): lo que el cliente debe HOY por servicios ya confirmados.
 *   - "RECAUDADO": lo que el cliente ya pagó.
 *   - "INVERSIÓN" (admin only): el costo neto total.
 *
 * Multimoneda (2026-06-11): cuando reserva.esMultimoneda === true, cada número muestra
 * DOS cifras (pesos arriba, dólares abajo), tomadas de reserva.porMoneda[i].
 * Si es mono-moneda, se ve EXACTAMENTE igual que antes.
 */
export function ReservaSummaryStrip({ reserva }) {
    const admin = isAdmin();

    // --- Modo multimoneda (dos monedas en esta reserva) ---
    const esMultimoneda = reserva.esMultimoneda && Array.isArray(reserva.porMoneda) && reserva.porMoneda.length > 1;

    if (esMultimoneda) {
        return (
            <div className={`grid grid-cols-2 ${admin ? 'md:grid-cols-3' : 'md:grid-cols-2'} gap-6 mb-10 pb-8 border-b border-slate-100 dark:border-slate-800/50`}>

                {/* Saldo a Cobrar — dos líneas (pesos arriba, dólares abajo) */}
                <div className="space-y-1">
                    <p className="text-[10px] uppercase tracking-widest font-bold text-slate-400 dark:text-slate-500">
                        Saldo a Cobrar
                    </p>
                    <div className="space-y-1.5">
                        {reserva.porMoneda.map((pm) => {
                            const hayDeuda = pm.balance > 0;
                            return (
                                <div key={pm.currency} className="flex items-center gap-1.5">
                                    <CurrencyBadge currency={pm.currency} size="sm" />
                                    <span className={`text-2xl font-extrabold leading-none ${hayDeuda ? 'text-rose-600 dark:text-rose-500' : 'text-slate-300 dark:text-slate-700'}`}>
                                        {formatCurrency(pm.balance, pm.currency)}
                                        {hayDeuda && <span className="inline-block ml-2 w-1.5 h-1.5 rounded-full bg-rose-500 animate-pulse align-middle" />}
                                    </span>
                                </div>
                            );
                        })}
                    </div>
                    {/* "de $X / US$Y presupuestado" — ambas monedas en una línea, separadas por "/" */}
                    {reserva.porMoneda.some((pm) => pm.totalSale > 0) && (
                        <p className="text-xs text-slate-400 dark:text-slate-500">
                            de {reserva.porMoneda
                                .filter((pm) => pm.totalSale > 0)
                                .map((pm) => formatCurrency(pm.totalSale, pm.currency))
                                .join(" / ")
                            } presupuestado
                        </p>
                    )}
                </div>

                {/* Recaudado — dos líneas */}
                <div className="space-y-1">
                    <p className="text-[10px] uppercase tracking-widest font-bold text-slate-400 dark:text-slate-500">Recaudado</p>
                    <div className="space-y-1.5">
                        {reserva.porMoneda.map((pm) => (
                            <div key={pm.currency} className="flex items-center gap-1.5">
                                <CurrencyBadge currency={pm.currency} size="sm" />
                                <span className="text-2xl font-extrabold text-emerald-600 dark:text-emerald-500 leading-none">
                                    {formatCurrency(pm.totalPaid, pm.currency)}
                                </span>
                            </div>
                        ))}
                    </div>
                </div>

                {/* Inversión (solo admin / see_cost) — dos líneas */}
                {admin && (
                    <div className="space-y-1">
                        <p className="text-[10px] uppercase tracking-widest font-bold text-slate-400 dark:text-slate-500">Inversión (Costo)</p>
                        <div className="space-y-1.5">
                            {reserva.porMoneda.map((pm) => (
                                <div key={pm.currency} className="flex items-center gap-1.5">
                                    <CurrencyBadge currency={pm.currency} size="sm" />
                                    <span className="text-2xl font-bold text-slate-400 dark:text-slate-600 leading-none">
                                        {formatCurrency(pm.totalCost, pm.currency)}
                                    </span>
                                </div>
                            ))}
                        </div>
                    </div>
                )}
            </div>
        );
    }

    // --- Modo mono-moneda: IDÉNTICO al comportamiento previo ---
    // Regla ③: si hay una sola moneda, la pantalla se ve exactamente igual que antes.
    const collected = reserva.payments?.filter(p => p.status !== 'Cancelled').reduce((acc, p) => acc + p.amount, 0) || 0;

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
