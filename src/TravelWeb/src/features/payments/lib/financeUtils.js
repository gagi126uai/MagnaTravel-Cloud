import { formatDate as formatDateCentral, formatDateTime as formatDateTimeCentral } from "../../../lib/utils.js";

export const creditNoteTypes = [3, 8, 13, 53];

/**
 * Formateadores internos: uno por moneda para reusar el objeto Intl.NumberFormat
 * (construirlo es costoso; reusar instancias es la práctica correcta).
 */
const arsFormatter = new Intl.NumberFormat("es-AR", {
  style: "currency",
  currency: "ARS",
  minimumFractionDigits: 2,
});

/**
 * Formatea un monto con el símbolo de la moneda indicada.
 * Default: ARS — mantiene el comportamiento de todos los call sites que no pasan moneda.
 *
 * Regla de negocio (2026-06-09): pesos y dólares siempre separados, nunca sumados.
 * "US$" (no "$") distingue visualmente el dólar del peso en pantalla.
 *
 * @param {number|string|null|undefined} amount
 * @param {"ARS"|"USD"} currency - Default "ARS"
 */
export function formatCurrency(amount, currency = "ARS") {
  const number = Number(amount || 0);

  if (currency === "USD") {
    return "US$" + new Intl.NumberFormat("es-AR", {
      minimumFractionDigits: 2,
      maximumFractionDigits: 2,
    }).format(number);
  }

  // ARS por default — todos los call sites legacy que no pasan moneda siguen igual
  return arsFormatter.format(number);
}

// Exportamos por compatibilidad con imports legacy que desestructuraban el formatter
/** @deprecated Usar formatCurrency(amount, "ARS") */
export const currencyFormatter = arsFormatter;

/**
 * Formatea una fecha para mostrarla al usuario.
 *
 * Bug "fechas corridas un día" (2026-07-16, módulo de plata — un día corrido acá es
 * grave: cambia a qué mes/cobranza pertenece un movimiento). Reusamos la formatDate()
 * central de utils.js, que ya distingue fecha-solo-día (input date, o medianoche UTC
 * guardada por el backend, ej. item.startDate de un viaje) de un instante real con
 * hora (ej. invoice.createdAt) — ver el comentario de esa función para el detalle.
 *
 * El parámetro `options` se mantiene por compatibilidad de firma: ningún call site
 * actual lo usa, pero si alguno llega a pasarlo, respetamos el comportamiento viejo
 * (Intl.DateTimeFormat vía toLocaleDateString) en vez de forzar el formato DD/MM/AAAA
 * fijo de la función central.
 *
 * Fix 2026-07-23 (hallazgo del reviewer): esta rama armaba el Intl.DateTimeFormat con
 * el locale "es-AR" pero SIN `timeZone` explícito — el locale solo define el orden
 * día/mes/año, no la zona horaria de la conversión. Sin `timeZone` fijo, la conversión
 * dependería de dónde esté el navegador/servidor, violando la regla del dueño (la fecha
 * que se muestra es SIEMPRE la de Argentina). Fijamos America/Argentina/Buenos_Aires como
 * default, pero dejamos que un caller explícito lo pise si algún día hace falta.
 */
export function formatDate(date, options) {
  if (!date) {
    return "-";
  }

  if (options) {
    return new Date(date).toLocaleDateString("es-AR", {
      timeZone: "America/Argentina/Buenos_Aires",
      ...options,
    });
  }

  return formatDateCentral(date);
}

/**
 * Fix 2026-07-22 (mismo bug de "fechas corridas un día" del comentario de arriba, aplicado
 * acá también): antes esta función SIEMPRE convertía a hora local del navegador con
 * toLocaleString(), lo que corría un día para atrás cualquier fecha de negocio (ej. un cobro
 * fechado 22/07 aparecía como "21/7, 21:00" en Movimientos/Historial). Ahora delega a la
 * formatDateTime() central, que distingue fecha de negocio (medianoche UTC, sin hora real) de
 * un instante real — ver el comentario de esa función en lib/utils.js.
 */
export function formatDateTime(date) {
  return formatDateTimeCentral(date);
}

export function getInvoiceNetAmount(invoice) {
  if (invoice?.resultado !== "A") {
    return 0;
  }

  return creditNoteTypes.includes(invoice.tipoComprobante)
    ? -Number(invoice.importeTotal || 0)
    : Number(invoice.importeTotal || 0);
}

export function getInvoiceLabel(type) {
  const labels = {
    1: "Factura A",
    6: "Factura B",
    11: "Factura C",
    3: "Nota de Credito A",
    8: "Nota de Credito B",
    13: "Nota de Credito C",
    2: "Nota de Debito A",
    7: "Nota de Debito B",
    12: "Nota de Debito C",
    51: "Factura M",
    53: "Nota de Credito M",
    52: "Nota de Debito M",
  };

  return labels[type] || `Comp. ${type}`;
}

export function isCreditNote(invoice) {
  return creditNoteTypes.includes(invoice?.tipoComprobante);
}
