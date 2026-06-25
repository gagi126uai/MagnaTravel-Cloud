/**
 * Utilidades de formato para comprobantes AFIP (facturas, NC, ND).
 *
 * Funciones puras, sin dependencias de React.
 * Se usan en dos contextos distintos:
 *   1. EmitirFacturaInline.jsx — cartel de ÉXITO (Paso 4a) con datos de InvoiceFiscalStatusDto.
 *   2. ReservaDetailPage.jsx — línea del Estado de Cuenta (Paso 5) con datos de InvoiceDto.
 *
 * Ambos DTOs usan los mismos nombres de campo en camelCase:
 *   invoiceType (letra: "A"/"B"/"C"/"M"), puntoDeVenta (int), numeroComprobante (long).
 *
 * Centralizamos el formato aquí para que no haya dos implementaciones que puedan divergir
 * (ej: si el padding cambia, cambia en un solo lugar).
 */

/**
 * Formatea el número de un comprobante AFIP en el formato estándar de pantalla.
 *
 * Produce: "Factura B 0001-00012345"
 *   - "Factura" es fijo (estas funciones son para facturas de venta, no NC/ND).
 *   - La letra viene de invoiceType ("A", "B", "C", "M").
 *   - El punto de venta va con 4 dígitos (padStart 4).
 *   - El número de comprobante va con 8 dígitos (padStart 8).
 *
 * @param {string|null|undefined} invoiceType      - letra del comprobante
 * @param {number|null|undefined} puntoDeVenta     - número del punto de venta
 * @param {number|null|undefined} numeroComprobante - número del comprobante
 * @returns {string}
 */
export function formatearEtiquetaFactura(invoiceType, puntoDeVenta, numeroComprobante) {
  const tipo = invoiceType || "?";
  const pdv = String(puntoDeVenta ?? 0).padStart(4, "0");
  const num = String(numeroComprobante ?? 0).padStart(8, "0");
  return `Factura ${tipo} ${pdv}-${num}`;
}

/**
 * Construye el nombre de archivo sugerido para descargar el PDF de una factura.
 * Ej: "Factura-B-0001-00012345.pdf"
 *
 * @param {string|null|undefined} invoiceType
 * @param {number|null|undefined} puntoDeVenta
 * @param {number|null|undefined} numeroComprobante
 * @returns {string}
 */
export function formatearNombreArchivoPdf(invoiceType, puntoDeVenta, numeroComprobante) {
  const tipo = invoiceType || "X";
  const pdv = String(puntoDeVenta ?? 0).padStart(4, "0");
  const num = String(numeroComprobante ?? 0).padStart(8, "0");
  return `Factura-${tipo}-${pdv}-${num}.pdf`;
}
