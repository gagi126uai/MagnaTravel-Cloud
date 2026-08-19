/**
 * "Foto de saldo" de la cuenta corriente del OPERADOR — molde unificado de cuentas
 * corrientes (Tanda 3, spec `docs/ux/2026-08-18-spec-dashboard-y-cuentas-corrientes.md`,
 * §2.0/§2.2, firmada 18/08). Mismo esqueleto visual que `FotoDeSaldoCuenta` (cliente):
 * una tarjeta por moneda (pesos primero, apiladas verticalmente — nunca en columna, para
 * no sugerir que pesos y dólares se puedan sumar), franja izquierda de 170px con el
 * número grande del saldo + su palabra de estado, y el desglose de 3 líneas a la derecha.
 *
 * OJO — semántica INVERSA a la de la cuenta del cliente: acá saldo positivo significa
 * que LA AGENCIA le debe al operador ("Le debés" en rojo), no al revés. Reemplaza los
 * 3 recuadros lado a lado que usaba esta pantalla antes de esta tanda
 * (`SupplierBalanceThreeBoxesHeader`, eliminado del archivo de la página).
 *
 * Por qué NO se reusa/extrae una base compartida con `FotoDeSaldoCuenta` (cliente) en
 * esta tanda: esa pantalla ya está pusheada y la consigna de esta obra es explícita —
 * no tocarla. Extraer una base común de verdad implicaría modificar igual el archivo del
 * cliente para desacoplar sus datos, lo cual está fuera de alcance acá. Este componente
 * REPLICA el patrón visual (mismas clases, mismos 170px, mismos tokens B.1/B.2) en un
 * archivo propio del operador — si una tanda futura decide unificar de verdad, ahí se
 * factoriza una base común tocando los dos archivos a la vez, con su propio review.
 *
 * Toda la decisión de QUÉ tono/palabra/texto mostrar vive en `supplierBalanceCardLogic.js`
 * (función pura, testeada); este componente solo pinta lo que esa función ya decidió — el
 * front nunca recalcula saldos acá.
 *
 * Props:
 *   - currencies: SupplierAccountStatementDto.currencies[] (o [] si no cargó aún)
 *   - puedeVerMontos: boolean — cobranzas.see_cost. Sin este permiso, la tarjeta entera
 *     (número grande + las 3 líneas) se pinta en gris con "—" (mismo comportamiento que
 *     ya tenía el molde anterior, `AmountsVisible=false`).
 *   - loading: boolean — true mientras se pide /account/statement
 */
import { Loader2 } from "lucide-react";
import { construirFotoDeSaldoOperador } from "../lib/supplierBalanceCardLogic.js";

// Color del número grande de la franja izquierda: matchea el hex exacto firmado en la
// sección 0 de la spec (#B91C1C rojo / #047857 verde), que corresponde a red-700/emerald-700
// de Tailwind — mismo criterio que ya usa `FotoDeSaldoCuenta` del lado del cliente.
const TONO_STRIPE = {
  rose: "text-red-700",
  emerald: "text-emerald-700",
  neutral: "text-slate-500",
};

// Color de las líneas del desglose (Facturas por pagar / Te tiene que devolver / Saldo a
// favor tuyo). "amber" es el ÁMBAR normalizado #B45309 de la spec §0 — no un naranja custom.
const TONO_TEXTO_DESGLOSE = {
  neutral: "text-slate-700 dark:text-slate-300",
  amber: "text-amber-700 dark:text-amber-400",
  emerald: "text-emerald-700 dark:text-emerald-400",
};

export function FotoDeSaldoOperador({ currencies, puedeVerMontos, loading }) {
  if (loading) {
    return (
      <div
        className="rounded-[14px] border border-slate-200 bg-white p-6 shadow-sm dark:border-slate-800 dark:bg-slate-900/50"
        data-testid="foto-saldo-operador-cargando"
      >
        <div className="flex items-center gap-2 text-sm text-slate-400">
          <Loader2 className="h-4 w-4 animate-spin" />
          Cargando saldo con el operador…
        </div>
      </div>
    );
  }

  const tarjetas = construirFotoDeSaldoOperador(currencies, puedeVerMontos);

  // Sin datos de ninguna moneda (operador sin movimientos todavía): no hay nada que
  // mostrar acá — mismo comportamiento que ya tenía el molde de 3 recuadros anterior.
  if (tarjetas.length === 0) return null;

  return (
    <div className="space-y-3" data-testid="foto-saldo-operador">
      {tarjetas.map((tarjeta) => (
        <TarjetaSaldoOperadorMoneda key={tarjeta.currency} tarjeta={tarjeta} />
      ))}
    </div>
  );
}

/** UNA tarjeta de la foto de saldo, para UNA moneda puntual del operador. */
function TarjetaSaldoOperadorMoneda({ tarjeta }) {
  const claseNumeroGrande = TONO_STRIPE[tarjeta.tono] ?? TONO_STRIPE.neutral;

  return (
    <div
      className="flex overflow-hidden rounded-[14px] border border-slate-200 bg-white shadow-sm dark:border-slate-800 dark:bg-slate-900/50"
      data-testid={`tarjeta-saldo-operador-${tarjeta.currency}`}
    >
      {/* Franja izquierda de 170px, fondo Mesa (#F4F6F9) — acá vive el número grande. */}
      <div className="flex w-[170px] flex-shrink-0 flex-col justify-center gap-1 bg-[#F4F6F9] px-4 py-5 dark:bg-slate-950/40">
        <p className="text-[11px] font-bold uppercase tracking-wide text-slate-500">
          {tarjeta.currency === "USD" ? "En dólares" : "En pesos"}
        </p>
        <p
          className={`text-[22px] font-bold leading-tight tabular-nums ${claseNumeroGrande}`}
          data-testid={`foto-saldo-operador-monto-${tarjeta.currency}`}
        >
          {tarjeta.montoTexto}
        </p>
        <p className={`text-[11px] font-semibold uppercase tracking-wide ${claseNumeroGrande}`}>
          {tarjeta.palabra}
        </p>
      </div>

      {/* Desglose: etiqueta a la izquierda en gris dato, monto tabular a la derecha (B.5). */}
      <div className="flex-1 space-y-2.5 px-4 py-5">
        {tarjeta.filas.map((fila) => (
          <div key={fila.clave} className="flex items-baseline justify-between gap-3 text-[13px]">
            <span className="text-slate-500 dark:text-slate-400">{fila.etiqueta}</span>
            <span
              className={`font-semibold tabular-nums ${TONO_TEXTO_DESGLOSE[fila.tono] ?? TONO_TEXTO_DESGLOSE.neutral}`}
            >
              {fila.montoTexto}
            </span>
          </div>
        ))}
      </div>
    </div>
  );
}
