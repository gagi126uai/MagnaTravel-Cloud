/**
 * Elige qué moneda precargar en el selector de "Confirmar multa del operador" / "Corregir
 * monto y moneda" (spec "el paso de multa vive en la ficha", 2026-07-08).
 *
 * Por qué existe: antes de este cambio el selector siempre arrancaba en USD sin mirar la
 * reserva — si la agencia ya facturó esa reserva en pesos, el vendedor tenía que acordarse
 * de cambiar la moneda a mano. La regla del dueño es simple: "nunca dejar el default USD
 * silencioso si hay factura de la cual precargar".
 *
 * Función pura (sin React, sin fetch) para poder testearla con node --test, mismo patrón
 * que moneyStatus.js / invoiceCurrencyDefault.js en este mismo feature.
 */

/**
 * De la plata por moneda de la reserva (reserva.porMoneda), devuelve la moneda de la
 * factura YA EMITIDA — solo si hay una única moneda con algo facturado (facturadoNeto > 0).
 * Si la reserva es multimoneda y factura en ARS Y en USD a la vez, no adivinamos cuál
 * corresponde a la multa: se devuelve null y quien llama decide el fallback.
 *
 * @param {Array<{currency: string, facturadoNeto: number}>|undefined} porMoneda
 * @returns {string|null}
 */
export function monedaDeLaFacturaEmitida(porMoneda) {
  if (!Array.isArray(porMoneda)) return null;
  const monedasConFactura = porMoneda.filter((linea) => Number(linea?.facturadoNeto) > 0);
  if (monedasConFactura.length === 1) return monedasConFactura[0].currency;
  return null;
}

/**
 * Moneda sugerida para el mini-form de la multa del operador. Orden de prioridad:
 *   1) La moneda YA CONFIRMADA de la multa (operatorPenaltySituation.currency) — es el dato
 *      más autoritativo: si la multa ya tiene monto y moneda cargados (estados posteriores
 *      a PendingDecision, como una corrección), esa es la moneda real, no una sugerencia.
 *   2) La moneda de la factura emitida de la reserva (ver monedaDeLaFacturaEmitida) —
 *      todavía no hay multa cargada (PendingDecision), pero sabemos en qué moneda le
 *      cobramos al cliente, así que probablemente el operador también retenga en esa moneda.
 *   3) undefined — deja que ConfirmarMultaOperadorInline use su propio default (USD).
 *
 * @param {{ situacionCurrency: string|null|undefined, porMoneda: Array|undefined }} params
 * @returns {string|undefined}
 */
export function elegirMonedaSugeridaParaMulta({ situacionCurrency, porMoneda }) {
  if (situacionCurrency) return situacionCurrency;
  return monedaDeLaFacturaEmitida(porMoneda) ?? undefined;
}
