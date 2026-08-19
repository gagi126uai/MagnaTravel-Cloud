import { LineChart, Line, XAxis, YAxis, Tooltip, ResponsiveContainer, CartesianGrid } from "recharts";
import { CurrencyBadge } from "../../../components/ui/CurrencyBadge";
import { formatCurrency } from "../../../lib/utils";
import { armarSeriesRitmoCobrosPagos } from "../lib/cashflowRhythmSeries";

// Azul boleto (cobros) y ámbar (pagos a operadores) — B.1 sección 0 de la spec:
// el ámbar del brief original se normaliza a este mismo tono que usa toda la app
// para "te pide algo/atención", no un naranja inventado para el gráfico.
const COLOR_COBROS = "#1D4ED8";
const COLOR_PAGOS = "#B45309";

/**
 * Tarjeta "Ritmo de cobros y pagos" — próximos 90 días (spec dashboard
 * 2026-08-18, sección 1.3 + respuesta firmada de Gastón, opción C/A: se pinta
 * la TENDENCIA que ya calcula `GET /reports/cashflow` — 30 días reales
 * estirados hacia adelante — con un título honesto que no promete un
 * cronograma real de vencimientos).
 *
 * Solo se monta cuando el que mira tiene `cobranzas.see_cost` (ver
 * `dashboardVisibility.js`): "por pagar a operadores" es información de costo.
 * Si hay más de una moneda con movimiento, se apilan varios gráficos chicos —
 * nunca una sola curva que mezcle pesos y dólares (P-3).
 */
export function CashflowRhythmCard({ cashflow }) {
  const { hayMovimiento, monedas, ejeXTicks } = armarSeriesRitmoCobrosPagos(cashflow);

  return (
    <div className="rounded-[14px] border border-slate-200 bg-white p-5 shadow-sm dark:border-slate-800 dark:bg-slate-900">
      <h2 className="text-sm font-bold text-slate-900 dark:text-white">Ritmo de cobros y pagos</h2>
      <p className="mt-0.5 text-xs text-slate-400">Próximos 90 días, según cómo veniste cobrando y pagando</p>

      {!hayMovimiento ? (
        <p className="py-10 text-center text-sm text-slate-400">Todavía no hay movimientos para proyectar.</p>
      ) : (
        <div className="mt-4 space-y-6">
          {monedas.map((serie) => (
            <div key={serie.currency}>
              <div className="mb-1 flex items-center gap-1.5">
                <CurrencyBadge currency={serie.currency} size="sm" />
              </div>
              <div className="h-[160px] w-full">
                <ResponsiveContainer width="100%" height="100%">
                  <LineChart data={serie.puntos} margin={{ top: 4, right: 8, left: 8, bottom: 0 }}>
                    <CartesianGrid strokeDasharray="3 3" vertical={false} stroke="#E2E8F0" />
                    <XAxis
                      dataKey="x"
                      type="number"
                      domain={["dataMin", "dataMax"]}
                      ticks={ejeXTicks.map((tick) => tick.x)}
                      tickFormatter={(valor) => ejeXTicks.find((tick) => tick.x === valor)?.etiqueta ?? ""}
                      stroke="#64748B"
                      fontSize={11}
                      tickLine={false}
                      axisLine={false}
                    />
                    <YAxis hide />
                    <Tooltip
                      formatter={(valor, nombre) => [
                        formatCurrency(valor, serie.currency),
                        nombre === "cobros" ? "Cobros (tendencia)" : "Pagos a operadores (tendencia)",
                      ]}
                      labelFormatter={(valor) => ejeXTicks.find((tick) => tick.x === valor)?.etiqueta ?? ""}
                      contentStyle={{ borderRadius: "8px", border: "1px solid #E2E8F0", fontSize: "12px" }}
                    />
                    <Line type="monotone" dataKey="cobros" stroke={COLOR_COBROS} strokeWidth={2} dot={false} />
                    <Line type="monotone" dataKey="pagos" stroke={COLOR_PAGOS} strokeWidth={2} dot={false} />
                  </LineChart>
                </ResponsiveContainer>
              </div>
            </div>
          ))}

          <div className="flex items-center gap-4 text-[11px] font-semibold text-slate-500">
            <span className="flex items-center gap-1.5">
              <span className="h-2 w-2 rounded-full" style={{ backgroundColor: COLOR_COBROS }} aria-hidden="true" />
              Cobros (tendencia)
            </span>
            <span className="flex items-center gap-1.5">
              <span className="h-2 w-2 rounded-full" style={{ backgroundColor: COLOR_PAGOS }} aria-hidden="true" />
              Pagos a operadores (tendencia)
            </span>
          </div>
        </div>
      )}
    </div>
  );
}
