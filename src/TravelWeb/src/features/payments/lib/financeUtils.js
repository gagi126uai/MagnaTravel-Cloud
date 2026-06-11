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

export function formatDate(date, options) {
  if (!date) {
    return "-";
  }

  return new Date(date).toLocaleDateString("es-AR", options);
}

export function formatDateTime(date) {
  if (!date) {
    return "-";
  }

  return new Date(date).toLocaleString("es-AR");
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
