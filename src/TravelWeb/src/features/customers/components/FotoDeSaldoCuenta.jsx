/**
 * "Foto de saldo" de la cuenta corriente del cliente (Tanda D2, spec
 * `docs/ux/2026-07-16-extracto-profesional-cuenta-cliente.md`, §2).
 *
 * UN solo recuadro con la composición de lo que el cliente debe/tiene a favor, una
 * columna por moneda (nunca sumando ARS+USD). Reemplaza los 4-5 cartelitos apilados de
 * antes (chip "Debe", bloque "Multa pendiente de cobro", cartel "A FAVOR", cartel
 * "Crédito no aplicado").
 *
 * Toda la decisión de QUÉ filas mostrar y de qué color vive en balanceCompositionLogic.js
 * (función pura, testeada); este componente solo pinta lo que esa función ya decidió —
 * el front NUNCA recalcula saldos ni multas acá.
 *
 * Props:
 *   - composicion: summary.balanceCompositionByCurrency del backend (o undefined)
 *   - unappliedCreditByCurrency: summary.unappliedCreditByCurrency del backend (spec §7.3:
 *     campo APARTE de `composicion`; se pinta como nota chica bajo "Crédito a favor",
 *     nunca como cartel propio).
 *   - loading: boolean — el overview todavía no cargó
 *   - canUsarSaldo: boolean — permiso `cobranzas.edit` (ya evaluado por el padre)
 *   - monedaFichaAbierta: string|null — moneda cuya ficha "Usar saldo a favor" está abierta
 *   - onToggleUsarSaldo(moneda): abre/cierra la ficha inline de esa moneda
 */
import { Loader2, Wallet } from "lucide-react";
import { construirFotoDeSaldo, debeMostrarBotonUsarSaldo } from "../lib/balanceCompositionLogic";

const TONO_TEXTO = {
  neutral: "text-slate-700 dark:text-slate-300",
  amber: "text-amber-600 dark:text-amber-400",
  emerald: "text-emerald-600 dark:text-emerald-400",
  rose: "text-rose-600 dark:text-rose-400",
};

export function FotoDeSaldoCuenta({
  composicion,
  unappliedCreditByCurrency,
  loading,
  canUsarSaldo,
  monedaFichaAbierta,
  onToggleUsarSaldo,
}) {
  if (loading) {
    return (
      <div
        className="rounded-xl border border-slate-200 bg-white p-6 shadow-sm dark:border-slate-800 dark:bg-slate-900/50"
        data-testid="foto-saldo-cargando"
      >
        <div className="text-xs font-bold uppercase tracking-wider text-slate-400">Saldo de la cuenta</div>
        <div className="mt-2 flex items-center gap-2 text-sm text-slate-400">
          <Loader2 className="h-4 w-4 animate-spin" />
          …
        </div>
      </div>
    );
  }

  const foto = construirFotoDeSaldo(composicion, unappliedCreditByCurrency);

  if (foto.estado === "vacio") {
    return (
      <div
        className="rounded-xl border border-slate-200 bg-slate-50 p-6 dark:border-slate-800 dark:bg-slate-900/40"
        data-testid="foto-saldo-vacio"
      >
        <div className="text-xs font-bold uppercase tracking-wider text-slate-400">Saldo de la cuenta</div>
        <div className="mt-1 text-lg font-bold text-slate-500 dark:text-slate-400">Al día — sin movimientos</div>
      </div>
    );
  }

  if (foto.estado === "alDia") {
    return (
      <div
        className="rounded-xl border border-emerald-100 bg-emerald-50 p-6 shadow-sm dark:border-emerald-900/30 dark:bg-emerald-900/10"
        data-testid="foto-saldo-al-dia"
      >
        <div className="text-xs font-bold uppercase tracking-wider text-slate-400">Saldo de la cuenta</div>
        <div className="mt-1 text-2xl font-bold text-emerald-600 dark:text-emerald-400">Al día</div>
        <div className="mt-1 text-xs font-medium text-slate-400">Sin deuda pendiente</div>
      </div>
    );
  }

  const { monedas, filas, saldoPorMoneda } = foto;

  // Botón "Usar saldo a favor": uno por moneda con crédito > 0 (spec §2). Si hay más de
  // una moneda con crédito a la vez (caso raro), se aclara la moneda en el label para
  // que el usuario sepa cuál ficha está por abrir.
  const filaCredito = filas.find((f) => f.clave === "creditoAFavor");
  const monedasConBoton = monedas.filter((moneda) =>
    debeMostrarBotonUsarSaldo({ creditoAFavor: filaCredito?.porMoneda?.[moneda]?.monto ?? 0, canUsarSaldo })
  );

  return (
    <div
      className="rounded-xl border border-slate-200 bg-white p-6 shadow-sm dark:border-slate-800 dark:bg-slate-900/50"
      data-testid="foto-saldo-cuenta"
    >
      <div className="text-xs font-bold uppercase tracking-wider text-slate-400">Saldo de la cuenta</div>

      <div className="mt-4 overflow-x-auto">
        <table className="w-full min-w-[380px] text-sm">
          <thead>
            <tr>
              <th className="text-left font-normal"></th>
              {monedas.map((moneda) => (
                <th
                  key={moneda}
                  className="pl-4 text-right text-xs font-semibold text-slate-500 dark:text-slate-400"
                >
                  {moneda === "USD" ? "En dólares (US$)" : "En pesos ($)"}
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {filas.map((fila) => (
              <tr key={fila.clave} className="border-t border-slate-100 dark:border-slate-800">
                <td className="py-2 pr-4 text-slate-600 dark:text-slate-300">{fila.etiqueta}</td>
                {monedas.map((moneda) => {
                  const celda = fila.porMoneda[moneda];
                  // Notas chicas debajo del monto: "(incluye $X en trámite)" en "Multas
                  // abiertas", "(incluye $X sin aplicar)" en "Crédito a favor" (spec §7.3).
                  // Una fila nunca tiene las dos a la vez (son filas distintas), pero se
                  // arma como lista para no repetir el mismo bloque de JSX dos veces.
                  const notas = [fila.notaTramitePorMoneda?.[moneda], fila.notaNoAplicadoPorMoneda?.[moneda]]
                    .filter(Boolean);
                  return (
                    <td key={moneda} className="py-2 pl-4 text-right align-top">
                      <div className={`font-semibold ${TONO_TEXTO[celda.tono]}`}>{celda.montoTexto}</div>
                      {notas.map((nota) => (
                        <div key={nota} className="text-[10px] font-medium text-amber-600 dark:text-amber-500">
                          {nota}
                        </div>
                      ))}
                    </td>
                  );
                })}
              </tr>
            ))}
            <tr className="border-t-2 border-slate-200 dark:border-slate-700">
              <td className="py-2 pr-4 font-bold text-slate-900 dark:text-white">SALDO</td>
              {monedas.map((moneda) => {
                const saldo = saldoPorMoneda[moneda];
                return (
                  <td key={moneda} className="py-2 pl-4 text-right" data-testid={`foto-saldo-monto-${moneda}`}>
                    <div className={`text-lg font-extrabold ${TONO_TEXTO[saldo.tono]}`}>{saldo.montoTexto}</div>
                    {saldo.etiqueta && (
                      <div
                        className={`text-[10px] font-semibold uppercase tracking-wider ${TONO_TEXTO[saldo.tono]}`}
                      >
                        {saldo.etiqueta}
                      </div>
                    )}
                  </td>
                );
              })}
            </tr>
          </tbody>
        </table>
      </div>

      {monedasConBoton.length > 0 && (
        <div className="mt-4 flex flex-wrap justify-end gap-2">
          {monedasConBoton.map((moneda) => (
            <button
              key={moneda}
              type="button"
              onClick={() => onToggleUsarSaldo(moneda)}
              className="flex items-center gap-2 rounded-lg border border-emerald-300 bg-white px-4 py-2 text-sm font-semibold text-emerald-700 shadow-sm hover:bg-emerald-50 dark:border-emerald-800 dark:bg-slate-900 dark:text-emerald-400 dark:hover:bg-emerald-950/30 transition-colors"
              data-testid={`usar-saldo-btn-${moneda}`}
            >
              <Wallet className="h-4 w-4" />
              {monedaFichaAbierta === moneda
                ? "Cerrar"
                : monedasConBoton.length > 1
                ? `Usar saldo a favor (${moneda === "USD" ? "US$" : "$"})`
                : "Usar saldo a favor"}
            </button>
          ))}
        </div>
      )}
    </div>
  );
}
