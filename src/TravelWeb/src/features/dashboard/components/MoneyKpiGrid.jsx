import { CurrencyBadge } from "../../../components/ui/CurrencyBadge";
import { formatCurrency } from "../../../lib/utils";
import { construirLineasKpiConCompatibilidad } from "../../../lib/dashboardKpiCurrency";

/**
 * Grid de KPIs de plata del dashboard (spec dashboard 2026-08-18, sección 1.3,
 * columna PLATA). 4 tarjetas para dueño/colaborador completo (2×2), 3 para un
 * vendedor sin `cobranzas.see_cost` (una sola fila, spec 1.5) — el llamador ya
 * decide eso con `dashboardVisibility.js`, acá solo se arma el grid con la
 * cantidad de columnas que le pasan.
 *
 * Reusa `construirLineasKpiConCompatibilidad` (lib/dashboardKpiCurrency.js): la
 * MISMA función que ya usaban AdminDashboard/AgentDashboard, para no reinventar
 * la regla "una línea por moneda, nunca sumadas" (P-3).
 */
export function MoneyKpiGrid({ porMoneda, ventasDelMes, cobrosDelMes, saldoPendiente, margenBruto, verMargenBruto, columnas }) {
  return (
    <div className={`grid grid-cols-2 gap-4 ${columnas === 3 ? "lg:grid-cols-3" : ""}`}>
      <MoneyKpiCard
        titulo="Por cobrar"
        lineas={construirLineasKpiConCompatibilidad(porMoneda?.saldoPendiente, saldoPendiente, { negativoEsSaldoAFavor: true })}
        colorClass="text-red-700"
      />
      <MoneyKpiCard
        titulo="Vendido del mes"
        lineas={construirLineasKpiConCompatibilidad(porMoneda?.ventasDelMes, ventasDelMes)}
        colorClass="text-slate-900 dark:text-white"
      />
      <MoneyKpiCard
        titulo="Cobrado del mes"
        lineas={construirLineasKpiConCompatibilidad(porMoneda?.cobrosDelMes, cobrosDelMes)}
        colorClass="text-slate-900 dark:text-white"
      />
      {verMargenBruto ? (
        <MoneyKpiCard
          titulo="Margen bruto"
          lineas={construirLineasKpiConCompatibilidad(porMoneda?.margenBruto, margenBruto)}
          colorClass="text-emerald-700"
        />
      ) : null}
    </div>
  );
}

function MoneyKpiCard({ titulo, lineas, colorClass }) {
  return (
    <div className="rounded-[14px] border border-slate-200 bg-white p-4 shadow-sm dark:border-slate-800 dark:bg-slate-900">
      <p className="text-[11px] font-bold uppercase tracking-wide text-slate-400">{titulo}</p>
      <div className="mt-2 space-y-2">
        {lineas.map((linea) => (
          <div key={linea.currency}>
            <div className="flex items-center gap-1.5">
              <CurrencyBadge currency={linea.currency} size="sm" />
              <span className={`text-[22px] font-bold leading-tight ${linea.esSaldoAFavor ? "text-emerald-700" : colorClass}`}>
                {formatCurrency(linea.monto, linea.currency, { withSymbol: false })}
              </span>
            </div>
            {/* BL-3 (heredado de dashboardKpiCurrency.js): un saldo negativo en una moneda
                puntual es saldo A FAVOR de los clientes, no un error — la leyenda va POR
                LÍNEA (no una sola al pie de la tarjeta), porque puede haber ARS a favor
                y USD en deuda al mismo tiempo y no hay que confundir cuál es cuál. */}
            {linea.esSaldoAFavor ? (
              <p className="text-[11px] font-semibold text-emerald-700">A favor de clientes</p>
            ) : null}
          </div>
        ))}
      </div>
    </div>
  );
}
