/**
 * Lógica pura de la tira fina del dólar Banco Nación (dashboard Admin/Vendedor,
 * componente `DolarBnaTira.jsx`). Vive en un .js sin JSX para poder testearse con
 * Node puro, sin bundler (mismo patrón que cartelEmergenteLogic.js):
 *   node --test src/lib/dolarTiraDashboardLogic.test.mjs
 *
 * Decisiones firmadas: docs/ux/specs/2026-08-06-dolar-en-dashboard.md (P1..P6).
 */

/**
 * Arma el texto de fecha en gris del final de la tira: "al DD/MM" cuando el dato
 * es reciente, y "al DD/MM (sin actualizar)" cuando `isStale` viene prendido.
 *
 * P6=A (firmado): la fecha en gris REEMPLAZA al badge verde/ámbar "Actualizado /
 * Dato desactualizado" de la tarjeta vieja — violaba P11=A (el color se reserva
 * para lo que pide hacer algo; un dato viejo no pide ninguna acción, solo hay que
 * saberlo).
 *
 * @param {string|null|undefined} publishedDate fecha del BNA tal como la manda el
 *   backend, formato "DD/MM/YYYY" (ver BnaUsdSellerRateDto.PublishedDate en
 *   IReportService.cs). Nunca se reformatea con Date() acá: es un string ya
 *   armado por el motor, solo se recorta a "DD/MM".
 * @param {boolean} isStale
 * @returns {string} "" si no hay fecha para mostrar (el llamador no pinta nada).
 */
export function formatearFechaDolarTira(publishedDate, isStale) {
  const diaYMes = extraerDiaYMes(publishedDate);
  if (!diaYMes) return "";
  const sufijo = isStale ? " (sin actualizar)" : "";
  return `al ${diaYMes}${sufijo}`;
}

function extraerDiaYMes(publishedDate) {
  if (!publishedDate || typeof publishedDate !== "string") return null;
  const partes = publishedDate.split("/");
  if (partes.length !== 3) return null;
  const [dia, mes] = partes;
  if (!dia || !mes) return null;
  return `${dia}/${mes}`;
}

/**
 * Decide si hay que dibujar el desplegable "otras monedas ▾": SOLO si el lector
 * del banco trajo por lo menos euro o real con dato real (P4=C, firmado). Nunca se
 * arma el desplegable para fingir "$0,00" en una moneda que en realidad no llegó
 * — mismo criterio que ya usaba BnaUsdSellerRateCard.jsx con sus RateTile.
 *
 * @param {{ euroValue?: number|null, realValue?: number|null }|null|undefined} rate
 * @returns {boolean}
 */
export function hayOtrasMonedasParaMostrar(rate) {
  if (!rate) return false;
  return rate.euroValue != null || rate.realValue != null;
}

/**
 * Estado vacío honesto: sin ningún dato del BNA en absoluto, la tira NO
 * desaparece (dejaría un hueco que desordena la pantalla) — se dibuja igual, en
 * una línea, con "sin dato por ahora" en gris.
 *
 * @param {{ value?: number|null }|null|undefined} rate
 * @returns {boolean}
 */
export function faltaDatoDelDolar(rate) {
  return !rate || rate.value === null || rate.value === undefined;
}
