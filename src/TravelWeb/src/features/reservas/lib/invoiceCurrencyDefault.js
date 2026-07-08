/**
 * Default automático de moneda al armar una factura AFIP para una reserva.
 *
 * Pedido textual de Gaston (2026-07-07): "estaría bueno que si detecta servicios
 * en dólares, elija automáticamente la opción dólares y si detecta servicios en
 * pesos, elija automáticamente pesos". Esta es la lógica que resuelve eso.
 *
 * Función pura, sin dependencias de React, para poder testearla con Node solo
 * (mismo patrón que moneyStatus.js / pasajeroHint.js en este mismo feature).
 * La usa EmitirFacturaInline.jsx, la ficha inline que se abre desde la ficha
 * de la reserva (ReservaDetailPage.jsx) al tocar "Emitir factura".
 *
 * Regla de negocio (ADR-042 — la reserva puede ser multimoneda, se factura
 * SIEMPRE por moneda, nunca se mezclan renglones de ARS y USD en un mismo
 * comprobante):
 *   - Si TODOS los servicios facturables están en una única moneda (solo ARS,
 *     o solo USD) → se precarga esa moneda como default. El usuario la puede
 *     cambiar a mano después; esto es solo el valor inicial del selector.
 *   - Si hay servicios en ambas monedas a la vez → NO se adivina cuál facturar
 *     primero. Se mantiene el comportamiento de siempre (ARS preferido como
 *     punto de partida) y el usuario elige con el selector.
 *   - Si no hay servicios facturables → no hay nada que precargar (null).
 */

/**
 * Elige qué grupo de renglones sugeridos (por moneda) precargar al abrir el
 * formulario de emisión de factura.
 *
 * Regla de seguridad fiscal (fix B1, 2026-06):
 *   Con la configuración "Facturar en más de una moneda" APAGADA, la moneda
 *   efectiva de la agencia siempre es ARS. Por eso, con el flag OFF, esta
 *   función SOLO puede devolver el grupo ARS. Si la reserva tiene nada más que
 *   servicios en USD, se devuelve null en vez del grupo USD: cargar esos montos
 *   como si fueran pesos sería un comprobante fiscal incorrecto (dólares
 *   facturados como pesos). El componente muestra un aviso en ese caso.
 *
 *   Con el flag ON, la agencia puede facturar en ARS o en USD:
 *     - Si la reserva tiene servicios en una sola moneda, se precarga esa
 *       moneda (esto es lo que resuelve el pedido de Gaston).
 *     - Si tiene de las dos monedas a la vez, se prefiere ARS como punto de
 *       partida (comportamiento histórico) y el usuario elige con el selector.
 *
 * @param {Array<{currency: string, items: Array, suggestedTotal: number}>} grupos
 *   - grupos por moneda de InvoiceSuggestedItemsResponse (backend)
 * @param {boolean} flagMultimonedaOn - valor de afipSettings.enableMultiCurrencyInvoicing
 * @returns {{ currency: string, items: Array, suggestedTotal: number } | null}
 */
export function elegirGrupoPrecarga(grupos, flagMultimonedaOn) {
  if (!Array.isArray(grupos) || grupos.length === 0) return null;

  if (!flagMultimonedaOn) {
    // Con la configuración apagada: SOLO cargar ARS. Nunca cargar el grupo USD
    // "por defecto" porque eso facturaría dólares como si fueran pesos.
    return grupos.find((g) => g.currency === "ARS") ?? null;
  }

  // Con la configuración prendida: si hay un solo grupo de moneda (todos los
  // servicios facturables están en ARS, o todos están en USD), ese es el
  // default automático. Si hay de las dos monedas, preferimos ARS como punto
  // de partida sin bloquear — el usuario elige con el selector.
  const grupoARS = grupos.find((g) => g.currency === "ARS");
  return grupoARS ?? grupos[0];
}
