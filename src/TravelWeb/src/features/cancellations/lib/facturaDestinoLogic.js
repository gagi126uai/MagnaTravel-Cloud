/**
 * Lógica PURA compartida de "¿a qué factura del cliente corresponde este cargo?"
 * (ADR-044 T4, spec `docs/ux/2026-07-10-t4-multas-pantallas.md` sección 2).
 *
 * Se usa en TRES lugares de la ficha (por eso vive acá, compartida, y no duplicada
 * en cada componente):
 *   - ConfirmarMultaOperadorInline (modo "confirmar"): el panel de siempre, con el
 *     desplegable agregado SOLO si hay 2+ facturas activas.
 *   - AgregarOtroCargoOperadorInline: el formulario del segundo cargo.
 *   - ElegirFacturaDestinoInline: la corrección posterior cuando un cargo quedó
 *     trabado por falta de factura destino (accionTrabada "elegirFactura").
 *
 * Caso simple (1 factura activa): el sistema la usa sola, sin mostrar nada — regla
 * dura "la complejidad se esconde con defaults" (2026-07-08).
 */

import { formatCurrency } from "../../../lib/utils.js";

/**
 * True si la reserva tiene 2 o más facturas de venta activas — recién ahí hace falta
 * que el humano elija a cuál corresponde el cargo (con 1 sola, se autocompleta sola).
 *
 * @param {Array<{currency: string}>} saleInvoices - BookingCancellationDto.SaleInvoices
 * @returns {boolean}
 */
export function hayFacturaDestinoAmbigua(saleInvoices) {
  return Array.isArray(saleInvoices) && saleInvoices.length >= 2;
}

/**
 * Arma las opciones del desplegable "¿A qué factura del cliente corresponde?", con el
 * mismo formato ya aprobado (2026-07-01, anulación multifactura): número + moneda +
 * monto de cada factura. `value` es el `publicId` REAL de la factura (ADR-044 T4: el
 * backend ya lo expone en `CancellationSaleInvoiceDto.PublicId`).
 *
 * @param {Array<{publicId: string, comprobanteLabel: string, currency: string, amount: number}>} saleInvoices
 * @returns {Array<{value: string, label: string}>}
 */
export function construirOpcionesFacturaDestino(saleInvoices) {
  if (!Array.isArray(saleInvoices)) return [];
  return saleInvoices.map((factura) => ({
    value: factura.publicId,
    label: `${factura.comprobanteLabel} — ${formatCurrency(factura.amount, factura.currency)}`,
  }));
}

/**
 * Resuelve la moneda de la factura destino para decidir si hay que mostrar el
 * recuadro de tipo de cambio (regla dura: SOLO si el cargo cruza de moneda con la
 * factura elegida). Con 1 sola factura activa, se autocompleta sola (no hace falta
 * que el usuario elija nada) y esta función devuelve directamente esa moneda. Con
 * 2+ facturas, devuelve la moneda de la elegida, o `null` si todavía no se eligió
 * ninguna (el recuadro de TC se mantiene oculto hasta que haya una factura resuelta).
 *
 * @param {Array<{publicId: string, currency: string}>} saleInvoices
 * @param {string|null|undefined} targetInvoicePublicId - selección del usuario (solo aplica si hay 2+ facturas).
 * @returns {string|null}
 */
export function resolverMonedaFacturaDestino(saleInvoices, targetInvoicePublicId) {
  if (!Array.isArray(saleInvoices) || saleInvoices.length === 0) return null;
  if (saleInvoices.length === 1) return saleInvoices[0].currency ?? null;

  const elegida = saleInvoices.find((factura) => factura.publicId === targetInvoicePublicId);
  return elegida ? elegida.currency ?? null : null;
}

/**
 * True si hay que mostrar el recuadro de tipo de cambio: el cargo está en una moneda
 * distinta de la factura destino ya resuelta. Regla dura multimoneda (2026-06-09): el
 * recuadro SOLO aparece cuando de verdad cruza de moneda, y la palabra "diferencia de
 * cambio" nunca se muestra en ningún lado.
 *
 * @param {string} monedaCargo
 * @param {string|null} monedaFacturaDestino - resultado de `resolverMonedaFacturaDestino`.
 * @returns {boolean}
 */
export function debeMostrarRecuadroTipoCambio(monedaCargo, monedaFacturaDestino) {
  if (!monedaCargo || !monedaFacturaDestino) return false;
  return monedaCargo !== monedaFacturaDestino;
}

/**
 * True si, con 2+ facturas activas, el formulario puede enviarse en lo que respecta a
 * la factura destino: con 1 sola factura no hace falta elegir nada (siempre true); con
 * 2+, hace falta que el usuario haya elegido una (botón apagado hasta entonces, regla
 * P5 de la spec).
 *
 * @param {Array<object>} saleInvoices
 * @param {string|null|undefined} targetInvoicePublicId
 * @returns {boolean}
 */
export function facturaDestinoResuelta(saleInvoices, targetInvoicePublicId) {
  if (!hayFacturaDestinoAmbigua(saleInvoices)) return true;
  return Boolean(targetInvoicePublicId);
}
