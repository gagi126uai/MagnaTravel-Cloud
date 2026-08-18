import { formatCurrency } from "../../../lib/utils.js";
import { getMoneyStatus, isReservaAnulada } from "../moneyStatus.js";

/**
 * Módulo de presentación de "plata" para el listado de Reservas (Tanda 1 rediseño,
 * 2026-08-04). Es un archivo .js puro (sin JSX) a propósito: así ReservaKPIs.jsx,
 * ReservaTable.jsx y ReservaMobileList.jsx comparten la MISMA lógica en vez de cada
 * uno decidir por su cuenta cómo mostrar un monto — y este archivo se puede testear
 * directo con `node --test`, sin necesitar un motor de JSX.
 *
 * Regla que gobierna todo el archivo (P-3⭐, la más dura del producto): una reserva
 * puede mover pesos Y dólares al mismo tiempo, y esos dos montos NUNCA se suman ni
 * se muestran como un solo número. Por eso todas las funciones de acá devuelven
 * LISTAS (una línea por moneda), nunca un total mezclado.
 *
 * Mismo margen de tolerancia que usa moneyStatus.js: un resto de centavo que queda
 * por una conversión de moneda no debe marcarse como "debe" o "a favor".
 */
const EPSILON = 0.005;

/**
 * Une una lista de montos por moneda en un solo texto, separados por "·"
 * (ej. "$ 223.445,00 · US$1.200,00"). Se usa en la tira de KPIs, donde cada
 * número SIEMPRE tiene que verse (no hay pill/chip donde acomodarlo aparte).
 * Lista vacía = nada que mostrar en ninguna moneda → "$ 0,00" en gris (el color
 * lo decide el componente, acá solo se arma el texto).
 */
export function formatMontosPorMoneda(lineas) {
  if (!Array.isArray(lineas) || lineas.length === 0) {
    return formatCurrency(0, "ARS");
  }
  return lineas.map((linea) => formatCurrency(linea.amount, linea.currency)).join(" · ");
}

/**
 * Líneas de "venta" para la columna Finanzas del listado (plan B4): una por cada
 * moneda que mueve la reserva, para mostrarlas una debajo de la otra.
 *
 * Si el DTO no trae `porMoneda` (fila vieja o un test que arma un objeto a mano),
 * se arma una única línea con el escalar `totalSale` para no dejar la celda vacía
 * — mismo criterio defensivo que ya usaban ReservaTable/ReservaMobileList antes de
 * esta tanda.
 */
export function getReservaSaleLines(reserva) {
  if (Array.isArray(reserva?.porMoneda) && reserva.porMoneda.length > 0) {
    return reserva.porMoneda.map((linea) => ({
      currency: linea.currency,
      amount: Number(linea.totalSale || 0),
    }));
  }
  return [{ currency: "ARS", amount: Number(reserva?.totalSale || 0) }];
}

/**
 * Chip(s) de estado de cobro para la columna Finanzas. Devuelve SIEMPRE una lista
 * (casi siempre de un solo elemento) para poder mostrar, por ejemplo, "Debe" en
 * pesos y "A favor" en dólares al mismo tiempo, sin mezclar las dos monedas en un
 * solo cartel.
 *
 * El contexto de una reserva ANULADA (multa pendiente / saldo a favor por
 * anulación) es de TODA la reserva, no de una moneda puntual — ahí se sigue
 * leyendo `getMoneyStatus` tal cual la usa el resto de la app (moneyStatus.js es
 * la fuente única para "¿esta reserva debe plata?").
 */
export function getReservaFinanzasChips(reserva) {
  const moneyStatus = getMoneyStatus(reserva);

  if (isReservaAnulada(reserva)) {
    if (moneyStatus.kind === "multaPorCobrar") {
      return [{
        text: `Multa: ${formatCurrency(moneyStatus.amount, moneyStatus.amountCurrency ?? "ARS")}`,
        tone: "ambar",
      }];
    }
    if (moneyStatus.kind === "saldoAFavorAnulada") {
      const currency = reserva.porMoneda?.[0]?.currency ?? "ARS";
      return [{
        text: `A favor ${formatCurrency(Math.abs(Number(reserva.balance ?? 0)), currency)}`,
        tone: "verde",
      }];
    }
    // "MultaEnRevision"/"Inconsistente"/sin contexto: nada que mostrarle al vendedor
    // (ver el XML-doc de getMoneyStatus — esos casos los revisa el back-office).
    return [{ text: "Sin movimientos", tone: "gris" }];
  }

  if (moneyStatus.kind === "pagada") {
    return [{ text: "Saldado", tone: "verde" }];
  }

  if (moneyStatus.kind === "sinMovimientos") {
    return [{ text: "Sin movimientos", tone: "gris" }];
  }

  const lineas = Array.isArray(reserva?.porMoneda) ? reserva.porMoneda : [];

  // Fallback legado: sin porMoneda solo tenemos el escalar de balance de la
  // reserva (no sabemos en qué moneda está cada parte). No es lo ideal, pero es
  // mejor que mostrar "Sin movimientos" cuando en realidad SÍ hay deuda.
  if (lineas.length === 0) {
    const currency = "ARS";
    if (moneyStatus.tone === "danger") {
      return [{ text: `Debe ${formatCurrency(reserva?.balance, currency)}`, tone: "rojo" }];
    }
    if (moneyStatus.kind === "saldoAFavor") {
      return [{ text: `A favor ${formatCurrency(Math.abs(Number(reserva?.balance ?? 0)), currency)}`, tone: "verde" }];
    }
    return [{ text: "Sin movimientos", tone: "gris" }];
  }

  const lineasConDeuda = lineas.filter((linea) => Number(linea.balance) > EPSILON);
  const lineasAFavor = lineas.filter((linea) => Number(linea.balance) < -EPSILON);

  if (lineasConDeuda.length > 0) {
    return lineasConDeuda.map((linea) => ({
      text: `Debe ${formatCurrency(linea.balance, linea.currency)}`,
      tone: "rojo",
    }));
  }

  if (lineasAFavor.length > 0) {
    return lineasAFavor.map((linea) => ({
      text: `A favor ${formatCurrency(Math.abs(linea.balance), linea.currency)}`,
      tone: "verde",
    }));
  }

  return [{ text: "Sin movimientos", tone: "gris" }];
}

/** Clases Tailwind del chip según su "tono" (mismos colores que ya usaba la tabla). */
export const FINANZAS_CHIP_TONE_CLASSES = {
  rojo: "rounded bg-rose-50 px-1.5 py-0.5 text-[11px] font-semibold text-rose-600 dark:bg-rose-900/20 dark:text-rose-400",
  verde: "rounded bg-emerald-50 px-1.5 py-0.5 text-[11px] font-semibold text-emerald-700 dark:bg-emerald-900/20 dark:text-emerald-400",
  ambar: "rounded bg-amber-50 px-1.5 py-0.5 text-[11px] font-semibold text-amber-700 dark:bg-amber-900/20 dark:text-amber-400",
  gris: "rounded bg-slate-100 px-1.5 py-0.5 text-[11px] font-semibold text-slate-500 dark:bg-slate-800 dark:text-slate-400",
};
