/**
 * Título de la cabecera de la ficha de reserva (Lavado de cara, Tanda 2, 2026-08-11 —
 * ENMIENDA al estándar visual, docs/ux/2026-08-11-estandar-visual-y-lavado-de-cara.md).
 *
 * Fix bloqueante de review (2026-08-11, B1): la primera versión de este título colapsaba
 * Cotización y Presupuesto en una sola palabra ("Presupuesto" para las dos). Son etapas
 * DISTINTAS del ciclo (ADR-020: Quotation → Budget → InManagement → ...), así que cada
 * una tiene su propia palabra en el título:
 *   - Quotation (Cotización) → "Cotización"
 *   - Budget (Presupuesto)   → "Presupuesto"
 *   - cualquier otra etapa   → "Reserva"
 *
 * Archivo `.js` PURO (sin JSX) a propósito — misma convención que
 * `reservaEstadoSelloLogic.js`: se puede testear con `node --test` sin montar React.
 */
const PALABRA_TITULO_POR_ETAPA = {
  Quotation: 'Cotización',
  Budget: 'Presupuesto',
};

/**
 * Palabra que va antes del número en el título de la ficha ("Cotización 2026-1067",
 * "Presupuesto 2026-1067", "Reserva 2026-1067"). Cualquier status que no tenga
 * palabra propia cae en "Reserva" (default, conservador — nunca queda mudo).
 *
 * @param {string|null|undefined} status
 * @returns {string}
 */
export function palabraTituloReserva(status) {
  return PALABRA_TITULO_POR_ETAPA[status] || 'Reserva';
}

/**
 * Título completo de la ficha: "{palabra} {numero}", SIN "#" (decisión firmada
 * 11/08 — el listado sigue mostrando el "#" de siempre, el título de la ficha no).
 *
 * @param {string|null|undefined} status
 * @param {string|number|null|undefined} numero - reserva.numeroReserva
 * @returns {string}
 */
export function tituloReserva(status, numero) {
  const palabra = palabraTituloReserva(status);
  const numeroTexto = numero === null || numero === undefined ? '' : String(numero);
  return numeroTexto ? `${palabra} ${numeroTexto}` : palabra;
}

/**
 * True si la chapita de estado (ReservaStatusBadge) tiene que OCULTARSE al lado del
 * título — regla P-16, "un dato no se dice dos veces". Se oculta SOLO cuando la
 * chapita repetiría exactamente la palabra que ya dice el título: hoy eso pasa en
 * Quotation ("Cotización") y Budget ("Presupuesto"), las dos únicas etapas que
 * tienen palabra propia en PALABRA_TITULO_POR_ETAPA. En cualquier otra etapa el
 * título dice "Reserva" (genérico) y la chapita SÍ aporta información nueva
 * (Confirmada, En viaje, Anulada, etc.), así que sigue visible.
 *
 * @param {string|null|undefined} status
 * @returns {boolean}
 */
export function debeOcultarChapitaEstado(status) {
  return Object.prototype.hasOwnProperty.call(PALABRA_TITULO_POR_ETAPA, status);
}
