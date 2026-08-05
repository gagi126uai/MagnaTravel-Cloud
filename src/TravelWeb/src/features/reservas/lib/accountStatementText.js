/**
 * Textos derivados del extracto de cuenta de la reserva (solapa "Estado de
 * Cuenta", componente EstadoCuentaExtracto.jsx).
 *
 * Tanda 4 del rediseño de fichas (2026-08-04, maqueta docs/ux/maquetas/
 * 2026-08-03-reservas-rediseno.html sección 9, "como un extracto del banco").
 * Vive en un archivo `.js` puro (sin JSX) a propósito, mismo criterio que
 * `reservaTimelineText.js`: así se puede testear con `node --test` sin montar
 * React.
 */

import { formatCurrency } from "../../../lib/utils.js";

// Nombre en criollo de cada moneda que maneja hoy el producto. Cualquier otra
// (que hoy no existe en el sistema) cae al código ISO tal cual — nunca inventa
// un nombre para una moneda que no conoce.
function nombreMoneda(currency) {
  if (currency === "ARS") return "pesos";
  if (currency === "USD") return "dólares";
  return currency;
}

/**
 * Arma la frase de cierre del extracto ("Este cliente debe US$ 300,00 y no
 * debe nada en pesos.") a partir de los saldos de cierre YA calculados por
 * bloque de moneda — no inventa ningún número nuevo, solo los lee en una
 * oración. Solo tiene sentido llamarla con más de un bloque (con una sola
 * moneda, la cabecera del bloque ya lo dice todo — ver EstadoCuentaExtracto).
 *
 * @param {{ currency: string, closingBalance: number|null|undefined }[]} bloques
 * @returns {string}
 */
export function construirFraseResumenSaldos(bloques) {
  const frases = (bloques ?? []).map((bloque) => {
    const saldo = bloque.closingBalance ?? 0;
    if (saldo > 0) return `debe ${formatCurrency(saldo, bloque.currency)}`;
    if (saldo < 0) return `tiene ${formatCurrency(Math.abs(saldo), bloque.currency)} a favor`;
    return `no debe nada en ${nombreMoneda(bloque.currency)}`;
  });

  if (frases.length === 0) return "";

  // Une con "y" la última frase, con "," las anteriores — gramática básica de lista.
  const texto =
    frases.length === 1
      ? frases[0]
      : `${frases.slice(0, -1).join(", ")} y ${frases[frases.length - 1]}`;

  return `Este cliente ${texto}.`;
}

/**
 * Formatea un saldo del extracto (cabecera, pie o columna "Saldo" de una fila) —
 * FIX 2026-08-05 (prueba integral): un saldo negativo (a favor del cliente) se
 * mostraba pelado con el signo, ej. "-$ 5.000,00", que confunde con un error de
 * cuenta. La plata siempre se dice con palabra, nunca con el signo "-": mismo
 * criterio que ya usa la frase "Saldo a favor del cliente" del resumen por moneda
 * (EstadoCuentaResumen.jsx) — acá se aplica a CADA número del extracto (cabecera,
 * fila y pie), no solo a la frase de cierre.
 *
 * @param {number|string|null|undefined} saldo - positivo = el cliente debe,
 *   negativo = el cliente tiene a favor.
 * @param {string} currency - "ARS" | "USD".
 * @returns {string} - ej. "$ 5.000,00" (debe) o "$ 5.000,00 a favor" (a favor).
 */
export function formatSaldoDelExtracto(saldo, currency) {
  const valor = Number(saldo ?? 0);
  if (valor < 0) {
    return `${formatCurrency(Math.abs(valor), currency)} a favor`;
  }
  return formatCurrency(valor, currency);
}
