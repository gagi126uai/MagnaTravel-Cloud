export const creditNoteTypes = [3, 8, 13, 53];

export const currencyFormatter = new Intl.NumberFormat("es-AR", {
  style: "currency",
  currency: "ARS",
  minimumFractionDigits: 2,
});

export function formatCurrency(amount) {
  return currencyFormatter.format(Number(amount || 0));
}

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
