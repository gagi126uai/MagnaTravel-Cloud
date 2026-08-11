import React from 'react';
import { formatMontosPorMoneda } from "../lib/reservaMoneyDisplay";

/**
 * Tira fina de KPIs arriba del listado de Reservas.
 *
 * Tanda 1 rediseño listado (2026-08-04, B1): reemplaza los CINCO números viejos
 * (que mezclaban pesos y dólares en un solo escalar, violando P-3⭐ — la regla más
 * dura del producto: las monedas nunca se suman) por TRES, en una sola línea,
 * separados por "│": reservas activas, por cobrar y vendido. "Operativos" y
 * "Rentabilidad estimada" (antes solo-admin) murieron en este rediseño.
 *
 * Cada importe muestra sus monedas SEPARADAS con "·" (ej. "$ 223.445,00 ·
 * US$1.200,00"), nunca sumadas. Sin plata en ninguna moneda → "$ 0,00" en gris,
 * para no dejar el número en blanco.
 */
export function ReservaKPIs({ stats }) {
    return (
        <div className="flex flex-wrap items-baseline gap-x-6 gap-y-2 rounded-xl border border-slate-200 bg-slate-50 px-4 py-3 dark:border-slate-800 dark:bg-slate-900/40">
            <Kpi label="Reservas activas">
                <span className="text-base font-extrabold text-slate-900 dark:text-white">
                    {stats.activeCount}
                </span>
            </Kpi>

            <Separador />

            <Kpi label="Por cobrar">
                <MontoPorMoneda lineas={stats.porCobrarPorMoneda} colorClass="text-rose-600 dark:text-rose-400" />
            </Kpi>

            <Separador />

            {/* "(solo confirmado)" — decisión del dueño 2026-08-11 (hallazgo de la prueba con
                navegador en PROD): "Vendido" mezclaba presupuestos sin confirmar con venta
                firme, mostrando plata que nadie compró todavía. El backend YA filtra por
                estado (ver EstadoReserva.SoldKpiStatuses en ReservaService.cs) — esta
                aclaración solo hace visible en pantalla lo que el número ya representa. */}
            <Kpi label="Vendido" hint="(solo confirmado)">
                <MontoPorMoneda lineas={stats.vendidoPorMoneda} colorClass="text-indigo-600 dark:text-indigo-400" />
            </Kpi>
        </div>
    );
}

function Separador() {
    return (
        <span className="hidden text-slate-300 dark:text-slate-700 sm:inline" aria-hidden="true">
            │
        </span>
    );
}

/** `hint`: aclaración chica y opcional al lado del label (ej. "(solo confirmado)"). */
function Kpi({ label, hint, children }) {
    return (
        <div className="flex items-baseline gap-2">
            <span className="text-[11px] font-bold uppercase tracking-wide text-slate-400 dark:text-slate-500">
                {label}
                {hint ? <span className="ml-1 font-medium normal-case tracking-normal">{hint}</span> : null}
            </span>
            {children}
        </div>
    );
}

/**
 * Texto del importe, con sus monedas separadas por "·" (nunca sumadas, P-3⭐).
 * Sin líneas (nada que cobrar/vender en ninguna moneda), se ve en gris "$ 0,00"
 * para que un mes vacío no deje el número en blanco.
 */
function MontoPorMoneda({ lineas, colorClass }) {
    const sinDatos = !lineas || lineas.length === 0;
    return (
        <span className={`text-base font-extrabold ${sinDatos ? "text-slate-300 dark:text-slate-700" : colorClass}`}>
            {formatMontosPorMoneda(lineas)}
        </span>
    );
}
