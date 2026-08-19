/**
 * "Foto de saldo" de la cuenta corriente del cliente — molde unificado de cuentas
 * corrientes (Tanda 2, spec `docs/ux/2026-08-18-spec-dashboard-y-cuentas-corrientes.md`,
 * §2.0/§2.1, firmada 18/08).
 *
 * UNA tarjeta por moneda (ARS primero, apiladas verticalmente, nunca una columna al
 * lado de otra — así nunca se sugiere que pesos y dólares se puedan sumar). Cada
 * tarjeta tiene una franja izquierda fija de 170px con el número grande del saldo neto
 * (su color y su palabra: "Te debe" en rojo / "A favor" en verde / "Al día"
 * en gris) y a la derecha el desglose línea por línea (Facturado sin cobrar / Multas
 * abiertas si hay / Crédito a favor).
 *
 * Reemplaza el diseño anterior (Tanda D2, 2026-07-16) de una sola tabla con una columna
 * por moneda — ese diseño sigue vivo en la memoria del proyecto solo como referencia
 * histórica, esta pantalla ya no lo usa.
 *
 * Toda la decisión de QUÉ filas mostrar y de qué color vive en balanceCompositionLogic.js
 * (función pura, testeada); este componente solo pinta lo que esa función ya decidió —
 * el front NUNCA recalcula saldos ni multas acá.
 *
 * Props:
 *   - composicion: summary.balanceCompositionByCurrency del backend (o undefined)
 *   - unappliedCreditByCurrency: summary.unappliedCreditByCurrency del backend (spec §7.3
 *     de la spec vieja / §2.1 de esta: campo APARTE de `composicion`; se pinta como nota
 *     chica bajo "Crédito a favor", nunca como cartel propio).
 *   - loading: boolean — el overview todavía no cargó
 *
 * El botón "Usar saldo a favor" YA NO vive acá (spec §2.1): se mudó a la cabecera de la
 * página (CustomerAccountPage), que decide qué monedas necesitan su propio botón con
 * `obtenerMonedasConCreditoDisponible`.
 */
import { Loader2 } from "lucide-react";
import {
  construirFotoDeSaldo,
  ordenarMonedasPesosPrimero,
  resolverPalabraSaldoCliente,
} from "../lib/balanceCompositionLogic";

// Color de las líneas del desglose (Facturado sin cobrar / Multas abiertas / Crédito a
// favor). Estos NO son el número grande de la franja (ver TONO_STRIPE más abajo, que sí
// tiene que matchear el hex exacto de la spec) — acá se mantiene la paleta que ya usaba
// esta pantalla, más suave, para que el desglose no le compita en peso visual al número
// grande de la izquierda.
const TONO_TEXTO_DESGLOSE = {
  neutral: "text-slate-700 dark:text-slate-300",
  amber: "text-amber-700 dark:text-amber-400",
  emerald: "text-emerald-700 dark:text-emerald-400",
  rose: "text-rose-700 dark:text-rose-400",
};

// Color del número grande de la franja izquierda: acá SÍ importa matchear el hex exacto
// firmado en la sección 0 de la spec (#B91C1C rojo / #047857 verde), que corresponde a
// red-700/emerald-700 de Tailwind (verificado contra la paleta oficial, no aproximado).
const TONO_STRIPE = {
  rose: "text-red-700",
  emerald: "text-emerald-700",
  neutral: "text-slate-500",
};

export function FotoDeSaldoCuenta({ composicion, unappliedCreditByCurrency, loading }) {
  if (loading) {
    return (
      <div
        className="rounded-[14px] border border-slate-200 bg-white p-6 shadow-sm dark:border-slate-800 dark:bg-slate-900/50"
        data-testid="foto-saldo-cargando"
      >
        <div className="flex items-center gap-2 text-sm text-slate-400">
          <Loader2 className="h-4 w-4 animate-spin" />
          Cargando saldo…
        </div>
      </div>
    );
  }

  const foto = construirFotoDeSaldo(composicion, unappliedCreditByCurrency);

  if (foto.estado === "vacio") {
    return (
      <div
        className="rounded-[14px] border border-slate-200 bg-slate-50 p-6 dark:border-slate-800 dark:bg-slate-900/40"
        data-testid="foto-saldo-vacio"
      >
        <div className="text-lg font-bold text-slate-500 dark:text-slate-400">Al día — sin movimientos</div>
      </div>
    );
  }

  if (foto.estado === "alDia") {
    return (
      <div
        className="rounded-[14px] border border-emerald-100 bg-emerald-50 p-6 shadow-sm dark:border-emerald-900/30 dark:bg-emerald-900/10"
        data-testid="foto-saldo-al-dia"
      >
        <div className="text-2xl font-bold text-emerald-700">Al día</div>
        <div className="mt-1 text-xs font-medium text-slate-400">Sin deuda pendiente</div>
      </div>
    );
  }

  const monedasOrdenadas = ordenarMonedasPesosPrimero(foto.monedas);

  return (
    <div className="space-y-3" data-testid="foto-saldo-cuenta">
      {monedasOrdenadas.map((moneda) => (
        <TarjetaSaldoMoneda
          key={moneda}
          moneda={moneda}
          filas={foto.filas}
          saldo={foto.saldoPorMoneda[moneda]}
        />
      ))}
    </div>
  );
}

/**
 * UNA tarjeta de la foto de saldo, para UNA moneda puntual.
 * Franja izquierda (170px, fondo Mesa) con el número grande + a la derecha el desglose.
 */
function TarjetaSaldoMoneda({ moneda, filas, saldo }) {
  const claseNumeroGrande = TONO_STRIPE[saldo.tono] ?? TONO_STRIPE.neutral;

  // "Multas abiertas SOLO se muestra si MultasAbiertas > 0" (spec §2.1) — para ESTA
  // moneda puntual. Las otras filas (Facturado sin cobrar, Crédito a favor) siempre se
  // muestran si la fila existe globalmente, aunque en esta moneda valgan "—" (mismo
  // criterio que ya tenía la tabla vieja, no se inventa un comportamiento nuevo).
  const filasVisibles = filas.filter((fila) => {
    if (fila.clave !== "multasAbiertas") return true;
    return (fila.porMoneda[moneda]?.monto ?? 0) !== 0;
  });

  return (
    <div
      className="flex overflow-hidden rounded-[14px] border border-slate-200 bg-white shadow-sm dark:border-slate-800 dark:bg-slate-900/50"
      data-testid={`tarjeta-saldo-${moneda}`}
    >
      {/* Franja izquierda de 170px, fondo Mesa (#F4F6F9) — acá vive el número grande. */}
      <div className="flex w-[170px] flex-shrink-0 flex-col justify-center gap-1 bg-[#F4F6F9] px-4 py-5 dark:bg-slate-950/40">
        <p className="text-[11px] font-bold uppercase tracking-wide text-slate-500">
          {moneda === "USD" ? "En dólares" : "En pesos"}
        </p>
        <p
          className={`text-[22px] font-bold leading-tight tabular-nums ${claseNumeroGrande}`}
          data-testid={`foto-saldo-monto-${moneda}`}
        >
          {saldo.montoTexto}
        </p>
        <p className={`text-[11px] font-semibold uppercase tracking-wide ${claseNumeroGrande}`}>
          {resolverPalabraSaldoCliente(saldo.tono)}
        </p>
      </div>

      {/* Desglose: etiqueta a la izquierda en gris dato, monto tabular a la derecha (B.5). */}
      <div className="flex-1 space-y-2.5 px-4 py-5">
        {filasVisibles.map((fila) => {
          const celda = fila.porMoneda[moneda];
          // Una fila nunca tiene las dos notas a la vez (son filas distintas), pero se
          // arma como lista para no repetir el mismo bloque de JSX dos veces.
          const notas = [fila.notaTramitePorMoneda?.[moneda], fila.notaNoAplicadoPorMoneda?.[moneda]].filter(Boolean);
          return (
            <div key={fila.clave}>
              <div className="flex items-baseline justify-between gap-3 text-[13px]">
                <span className="text-slate-500 dark:text-slate-400">{fila.etiqueta}</span>
                <span className={`font-semibold tabular-nums ${TONO_TEXTO_DESGLOSE[celda.tono]}`}>
                  {celda.montoTexto}
                </span>
              </div>
              {notas.map((nota) => (
                <p key={nota} className="text-right text-[11px] font-medium text-amber-700 dark:text-amber-500">
                  {nota}
                </p>
              ))}
            </div>
          );
        })}
      </div>
    </div>
  );
}
