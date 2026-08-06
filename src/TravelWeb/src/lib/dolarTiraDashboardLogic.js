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

// ─── Botón "actualizar" (2026-08-05, orden textual del dueño mirando el ──────
// dashboard EN VIVO: "yo pondría un botón para actualizar" — el scraper del
// BNA venía roto desde el 8/7 y su recuerdo viejo le ganaba al dato fresco de
// la libreta. Ver TRABAJO 2 del backend: POST /api/exchange-rates/refresh.

/**
 * Los dos únicos estados del botón: "quieto" (nada en curso, se puede
 * apretar) y "buscando" (ya se pidió la actualización, se espera la
 * respuesta del job en background — el botón queda deshabilitado para no
 * encolar el pedido mil veces si el usuario lo aprieta a repetición).
 */
export const ESTADO_ACTUALIZAR_DOLAR = Object.freeze({
  QUIETO: "quieto",
  BUSCANDO: "buscando",
});

/**
 * Cuánto esperar (en milisegundos) antes de volver a pedir
 * `/reports/dashboard` después de haber encolado la sincronización, para
 * darle tiempo al job en background a traer el dato nuevo. Una sola vez, no
 * polling: si en esa ventana el job no llegó a terminar, el usuario ve el
 * dato de siempre y puede volver a apretar "actualizar".
 */
export const ESPERA_REFRESCO_DOLAR_MS = 9000;

/**
 * Texto que se muestra adentro del botón según el estado.
 *
 * @param {string} estado uno de los valores de {@link ESTADO_ACTUALIZAR_DOLAR}
 * @returns {string}
 */
export function textoBotonActualizarDolar(estado) {
  return estado === ESTADO_ACTUALIZAR_DOLAR.BUSCANDO ? "buscando…" : "actualizar";
}

/**
 * El botón se deshabilita mientras está "buscando": el backend ya tiene su
 * propio candado de 5 minutos (no encola el job dos veces), pero acá alcanza
 * con no dejar apretarlo de nuevo mientras la primera respuesta todavía no
 * volvió — es una mejora de UX, no una segunda fuente de verdad.
 *
 * @param {string} estado
 * @returns {boolean}
 */
export function botonActualizarDolarDeshabilitado(estado) {
  return estado === ESTADO_ACTUALIZAR_DOLAR.BUSCANDO;
}
