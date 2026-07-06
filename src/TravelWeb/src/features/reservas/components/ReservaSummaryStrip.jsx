import React from 'react';
import { formatCurrency } from "../../../lib/utils";
import { CurrencyBadge } from "../../../components/ui/CurrencyBadge";
import { isAdmin } from "../../../auth";
import { getMoneyStatus, isReservaAnulada } from "../moneyStatus";

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
 *
 * Tanda 6 (2026-07-05): en una reserva ANULADA el primer bloque ya NO muestra
 * "SALDO A COBRAR" en rojo pulsante — una anulada nunca "debe" en el sentido normal.
 * En su lugar se lee getMoneyStatus(reserva) y se muestra el contexto real
 * (saldo a favor del cliente / multa por anulación pendiente de cobro), o directamente
 * nada si el dato es "Inconsistente" (eso lo revisa un vigía interno, no el vendedor).
 * El contexto es a nivel de TODA la reserva (no por moneda), por eso se muestra igual
 * en modo mono-moneda y multimoneda.
 */
export function ReservaSummaryStrip({ reserva }) {
    const admin = isAdmin();
    const anulada = isReservaAnulada(reserva.status);
    const moneyStatus = getMoneyStatus(reserva);

    // --- Modo multimoneda (dos monedas en esta reserva) ---
    const esMultimoneda = reserva.esMultimoneda && Array.isArray(reserva.porMoneda) && reserva.porMoneda.length > 1;

    if (esMultimoneda) {
        return (
            <div className={`grid grid-cols-2 ${admin ? 'md:grid-cols-3' : 'md:grid-cols-2'} gap-6 mb-10 pb-8 border-b border-slate-100 dark:border-slate-800/50`}>

                {/* Saldo a Cobrar (vivo) / contexto de anulación (anulada) */}
                <div className="space-y-1">
                    {anulada ? (
                        <BloqueContextoAnulado moneyStatus={moneyStatus} reserva={reserva} />
                    ) : (
                        <>
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
                        </>
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
    //
    // Fix C1 (Tanda 6, 2026-07-05): "Recaudado" usaba una suma local de reserva.payments,
    // que incluye pagos PUENTE (AffectsCash=false, no es plata real) y podía divergir del
    // número que muestra EstadoCuentaResumen ("Cobrado") para la MISMA reserva. Ahora usa
    // reserva.totalPaid del backend directamente, igual que el path multimoneda de arriba.
    const collected = reserva.totalPaid ?? 0;

    return (
        <div className={`grid grid-cols-2 ${admin ? 'md:grid-cols-3' : 'md:grid-cols-2'} gap-6 mb-10 pb-8 border-b border-slate-100 dark:border-slate-800/50`}>

            {/* Saldo a cobrar (vivo) / contexto de anulación (anulada). */}
            <div className="space-y-1">
                {anulada ? (
                    <BloqueContextoAnulado moneyStatus={moneyStatus} reserva={reserva} />
                ) : (
                    <>
                        {/* ADR-020: Balance = ConfirmedSale - TotalPaid. Un servicio solicitado NO genera deuda.
                            Fix C2 (Tanda 6): el color/pulso ya NO lee reserva.balance > 0 a mano, sale de
                            moneyStatus.tone (calculado a partir de collectionStatus/hasOverdueDebt del backend). */}
                        <p className="text-[10px] uppercase tracking-widest font-bold text-slate-400 dark:text-slate-500">
                            Saldo a Cobrar
                        </p>
                        <p className={`text-3xl font-extrabold leading-none ${moneyStatus.tone === 'danger' ? 'text-rose-600 dark:text-rose-500' : 'text-slate-300 dark:text-slate-700'}`}>
                            {formatCurrency(reserva.balance)}
                            {moneyStatus.tone === 'danger' && <span className="inline-block ml-2 w-1.5 h-1.5 rounded-full bg-rose-500 animate-pulse align-middle" />}
                        </p>
                        {/* "de $X presupuestado" solo si totalSale difiere del balance (hay servicios no confirmados aún) */}
                        {reserva.totalSale > 0 && (
                            <p className="text-xs text-slate-400 dark:text-slate-500">
                                de {formatCurrency(reserva.totalSale)} presupuestado
                            </p>
                        )}
                    </>
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

/**
 * Reemplaza el bloque "Saldo a Cobrar" cuando la reserva está ANULADA.
 * Nunca dice "debe": muestra el contexto real (saldo a favor / multa por anulación)
 * o directamente nada si moneyStatus.kind === "none" (dato inconsistente — lo revisa
 * un vigía interno, no se le muestra al vendedor una cifra que podría estar mal).
 *
 * Tanda "multa fantasma" (2026-07-06): con multa, el monto SALE de moneyStatus
 * (amount/amountCurrency = el monto exacto de la multa, con su propio fallback al
 * balance si el backend todavía no lo manda) — este componente ya no recalcula nada.
 * Con saldo a favor, el monto sigue siendo el balance de la reserva (no cambia: el
 * contexto de anulación es a nivel de TODA la reserva, no por moneda).
 */
function BloqueContextoAnulado({ moneyStatus, reserva }) {
    if (moneyStatus.kind === 'none') {
        return (
            <>
                <p className="text-[10px] uppercase tracking-widest font-bold text-slate-400 dark:text-slate-500">
                    Saldo
                </p>
                <p className="text-3xl font-extrabold leading-none text-slate-300 dark:text-slate-700" data-testid="anulada-sin-plata-pendiente">
                    —
                </p>
            </>
        );
    }

    const esMultaEnAmbar = moneyStatus.kind === 'multaPorCobrar';
    const monto = esMultaEnAmbar
        ? formatCurrency(moneyStatus.amount, moneyStatus.amountCurrency)
        : formatCurrency(Math.abs(reserva.balance ?? 0), reserva.porMoneda?.[0]?.currency);

    return (
        <>
            <p className="text-[10px] uppercase tracking-widest font-bold text-slate-400 dark:text-slate-500">
                {moneyStatus.label}
            </p>
            <p
                className={`text-3xl font-extrabold leading-none ${esMultaEnAmbar ? 'text-amber-600 dark:text-amber-500' : 'text-emerald-600 dark:text-emerald-500'}`}
                data-testid={esMultaEnAmbar ? 'anulada-multa-por-cobrar' : 'anulada-saldo-a-favor'}
            >
                {monto}
            </p>
        </>
    );
}
