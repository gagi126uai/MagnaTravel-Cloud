import React from 'react';
import { formatCurrency } from "../../../lib/utils";
import { CurrencyBadge } from "../../../components/ui/CurrencyBadge";
import { isAdmin } from "../../../auth";
import { getMoneyStatus, isReservaAnulada } from "../moneyStatus";

// Mismo margen de tolerancia que usa moneyStatus.js (ReservaCollectionStatus.Epsilon):
// un resto de centavo por conversión de moneda no debe leerse como "hay plata en juego".
const EPSILON = 0.005;

/**
 * Franja de números clave de la reserva — aparece debajo del header en la página de detalle.
 *
 * P10 (Tanda 2 del rediseño de Reservas, 2026-08-03, regla firmada): "SOLO el que
 * tiene plata grita". El Saldo a Cobrar (o, en una Anulada, el contexto de esa
 * anulación) es el ÚNICO número grande de la franja; Recaudado e Inversión pasan a
 * una línea chica gris al lado. Si TODO está en cero, los tres van chicos y grises
 * — no hay ningún número grande. También se elimina el puntito rojo que latía
 * (animate-pulse): competía visualmente con los avisos de verdad.
 *
 * Multimoneda (2026-06-11, y P-3⭐ del rediseño): cuando reserva.esMultimoneda es
 * true, el número grande trae UNA LÍNEA POR MONEDA (nunca se suman pesos y dólares
 * en un solo número) — Recaudado/Inversión hacen lo mismo, solo que en chico.
 * Si es mono-moneda, se sigue el mismo criterio con una sola línea.
 */
export function ReservaSummaryStrip({ reserva }) {
    const admin = isAdmin();
    const anulada = isReservaAnulada(reserva);
    const moneyStatus = getMoneyStatus(reserva);
    const esMultimoneda = reserva.esMultimoneda && Array.isArray(reserva.porMoneda) && reserva.porMoneda.length > 1;

    return (
        <div className="mb-8 border-b border-slate-100 pb-6 dark:border-slate-800/50" data-testid="numeros-ficha">
            {esMultimoneda
                ? <NumerosMultimoneda reserva={reserva} anulada={anulada} moneyStatus={moneyStatus} admin={admin} />
                : <NumerosMonoMoneda reserva={reserva} anulada={anulada} moneyStatus={moneyStatus} admin={admin} />}
        </div>
    );
}

/** Etiqueta chiquita en mayúsculas (mismo estilo que usaban los tres números antes). */
function Rotulo({ children }) {
    return (
        <p className="text-[10px] uppercase tracking-widest font-bold text-slate-400 dark:text-slate-500">
            {children}
        </p>
    );
}

/** El único número "grande" permitido por P10 — Saldo a Cobrar, o el contexto de una Anulada. */
function NumeroGrande({ label, value, colorClass, leyenda, testId }) {
    return (
        <div className="space-y-1">
            <Rotulo>{label}</Rotulo>
            <p className={`text-3xl font-extrabold leading-none ${colorClass}`} data-testid={testId}>
                {value}
            </p>
            {leyenda && <p className="text-xs text-slate-400 dark:text-slate-500">{leyenda}</p>}
        </div>
    );
}

/** Recaudado / Inversión: SIEMPRE chicos y grises, nunca compiten con el número grande. */
function NumeroChico({ label, value }) {
    return (
        <p className="text-sm text-slate-500 dark:text-slate-400">
            {label} <b className="font-semibold text-slate-700 dark:text-slate-200">{value}</b>
        </p>
    );
}

// ─── Mono-moneda ────────────────────────────────────────────────────────────────

function NumerosMonoMoneda({ reserva, anulada, moneyStatus, admin }) {
    const currency = reserva.porMoneda?.[0]?.currency ?? "ARS";
    const collected = reserva.totalPaid ?? 0;
    const cost = reserva.totalCost ?? 0;
    const balance = reserva.balance ?? 0;

    if (anulada) {
        return (
            <div className="space-y-2">
                <BloqueGrandeAnulada reserva={reserva} moneyStatus={moneyStatus} currency={currency} />
                <div className="flex flex-wrap items-baseline gap-x-6 gap-y-1">
                    <NumeroChico label="Recaudado" value={formatCurrency(collected, currency)} />
                    {admin && <NumeroChico label="Inversión" value={formatCurrency(cost, currency)} />}
                </div>
            </div>
        );
    }

    // Reserva viva con todo en $0: ni siquiera el saldo grita — los tres quedan
    // chicos y grises en una sola línea (regla P10: "si todo está en $0…").
    const todoEnCero = Math.abs(balance) < EPSILON && Math.abs(collected) < EPSILON && Math.abs(cost) < EPSILON;
    if (todoEnCero) {
        return (
            <p className="text-sm text-slate-400 dark:text-slate-500">
                Saldo a cobrar <b className="font-semibold">{formatCurrency(0, currency)}</b>
                {" · "}Recaudado <b className="font-semibold">{formatCurrency(0, currency)}</b>
                {admin && <> {" · "}Inversión <b className="font-semibold">{formatCurrency(0, currency)}</b></>}
            </p>
        );
    }

    return (
        <div className="space-y-2">
            <NumeroGrande
                label="Saldo a Cobrar"
                value={formatCurrency(balance, currency)}
                colorClass={moneyStatus.tone === 'danger' ? 'text-rose-600 dark:text-rose-500' : 'text-slate-300 dark:text-slate-700'}
                leyenda={reserva.totalSale > 0 ? `de ${formatCurrency(reserva.totalSale, currency)} presupuestado` : null}
            />
            <div className="flex flex-wrap items-baseline gap-x-6 gap-y-1">
                <NumeroChico label="Recaudado" value={formatCurrency(collected, currency)} />
                {admin && <NumeroChico label="Inversión" value={formatCurrency(cost, currency)} />}
            </div>
        </div>
    );
}

/**
 * Bloque "grande" cuando la reserva está Anulada: nunca dice "debe" — muestra el
 * contexto real (saldo a favor / multa) o, si no hay ningún contexto (dato para el
 * vigía interno, ver moneyStatus.js), directamente NO se renderiza nada (null) —
 * en ese caso solo quedan Recaudado/Inversión en chico, resueltos por el caller.
 * Antes acá se veía "Saldo —" con Recaudado/Inversión en 3xl al lado; la maqueta
 * firmada (sección 11) lo saca. El contexto de anulación es de TODA la reserva
 * (no por moneda), por eso este bloque es el mismo en mono y multimoneda.
 */
function BloqueGrandeAnulada({ reserva, moneyStatus, currency }) {
    if (moneyStatus.kind === 'none') return null;

    const esMultaEnAmbar = moneyStatus.kind === 'multaPorCobrar';
    const monto = esMultaEnAmbar
        ? formatCurrency(moneyStatus.amount, moneyStatus.amountCurrency ?? currency)
        : formatCurrency(Math.abs(reserva.balance ?? 0), currency);

    return (
        <NumeroGrande
            label={moneyStatus.label}
            value={monto}
            colorClass={esMultaEnAmbar ? 'text-amber-600 dark:text-amber-500' : 'text-emerald-600 dark:text-emerald-500'}
            // P10: leyenda firmada SOLO para el saldo a favor — la multa no la trae.
            leyenda={moneyStatus.kind === 'saldoAFavorAnulada' ? 'se puede usar en otra reserva o devolver' : null}
            testId={esMultaEnAmbar ? 'anulada-multa-por-cobrar' : 'anulada-saldo-a-favor'}
        />
    );
}

/**
 * Recaudado + Inversión, una línea por moneda (P-3⭐: nunca se suman pesos y
 * dólares). Se usa tanto en la reserva viva como en la Anulada — el número
 * cobrado/invertido es el mismo dato objetivo en los dos casos, cambia solo si
 * hay o no un número "grande" arriba.
 */
function RecaudadoInversionPorMoneda({ reserva, admin }) {
    return (
        <div className="flex flex-wrap gap-x-8 gap-y-1">
            <div className="space-y-0.5">
                <span className="text-sm text-slate-500 dark:text-slate-400">Recaudado</span>
                {reserva.porMoneda.map((pm) => (
                    <div key={pm.currency} className="flex items-center gap-1.5">
                        <CurrencyBadge currency={pm.currency} size="sm" />
                        <span className="text-sm font-semibold text-slate-700 dark:text-slate-200">
                            {formatCurrency(pm.totalPaid, pm.currency)}
                        </span>
                    </div>
                ))}
            </div>
            {admin && (
                <div className="space-y-0.5">
                    <span className="text-sm text-slate-500 dark:text-slate-400">Inversión</span>
                    {reserva.porMoneda.map((pm) => (
                        <div key={pm.currency} className="flex items-center gap-1.5">
                            <CurrencyBadge currency={pm.currency} size="sm" />
                            <span className="text-sm font-semibold text-slate-700 dark:text-slate-200">
                                {formatCurrency(pm.totalCost, pm.currency)}
                            </span>
                        </div>
                    ))}
                </div>
            )}
        </div>
    );
}

// ─── Multimoneda ────────────────────────────────────────────────────────────────

function NumerosMultimoneda({ reserva, anulada, moneyStatus, admin }) {
    if (anulada) {
        // El contexto de una anulación (saldo a favor / multa) es de TODA la reserva,
        // no por moneda — mismo criterio que ya usaba este componente antes de esta
        // tanda. Recaudado/Inversión sí van una línea por moneda (P-3⭐).
        const currencyFallback = reserva.porMoneda?.[0]?.currency ?? "ARS";
        return (
            <div className="space-y-2">
                <BloqueGrandeAnulada reserva={reserva} moneyStatus={moneyStatus} currency={currencyFallback} />
                <RecaudadoInversionPorMoneda reserva={reserva} admin={admin} />
            </div>
        );
    }

    return (
        <div className="space-y-2">
            <div>
                <Rotulo>Saldo a Cobrar</Rotulo>
                <div className="space-y-0.5">
                    {reserva.porMoneda.map((pm) => {
                        const hayDeuda = pm.balance > EPSILON;
                        return (
                            <div key={pm.currency} className="flex items-center gap-1.5">
                                <CurrencyBadge currency={pm.currency} size="sm" />
                                <span className={`text-2xl font-extrabold leading-none ${hayDeuda ? 'text-rose-600 dark:text-rose-500' : 'text-slate-300 dark:text-slate-700'}`}>
                                    {formatCurrency(pm.balance, pm.currency)}
                                </span>
                            </div>
                        );
                    })}
                </div>
                {reserva.porMoneda.some((pm) => pm.totalSale > 0) && (
                    <p className="text-xs text-slate-400 dark:text-slate-500 mt-0.5">
                        de {reserva.porMoneda
                            .filter((pm) => pm.totalSale > 0)
                            .map((pm) => formatCurrency(pm.totalSale, pm.currency))
                            .join(" / ")
                        } presupuestado
                    </p>
                )}
            </div>

            <RecaudadoInversionPorMoneda reserva={reserva} admin={admin} />
        </div>
    );
}
