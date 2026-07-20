// SupplierInvoicingMode del backend: 0 = Compra y reventa (TotalToCustomer, default),
// 1 = Intermediación / factura directo al cliente (CommissionOnly). Mismos valores que
// usa el <select> "Cómo trabaja con la agencia" en SupplierAccountPage.jsx.
const INVOICING_MODE_COMMISSION_ONLY = 1;

/**
 * Un operador en modo "Intermediación (factura directo al cliente)" NUNCA genera una
 * cuenta por pagar de la agencia: el que le factura al pasajero es el propio operador,
 * no la agencia. Por eso "Nueva factura" (factura del operador HACIA la agencia) no
 * tiene sentido para este tipo de operador y se esconde (precedente ADR-036 punto 4,
 * P3=A: esconder un botón que estructuralmente nunca aplica, no agrisarlo con cartel).
 *
 * @param {number|null|undefined} invoicingMode
 * @returns {boolean}
 */
export function operadorFacturaDirectoAlCliente(invoicingMode) {
  return invoicingMode === INVOICING_MODE_COMMISSION_ONLY;
}

export function classifySupplierInvoices(balances = [], invoices = []) {
  const rows = {};
  for (const balance of balances) {
    rows[balance.currency] = {
      committed: balance.confirmedPurchases || 0,
      totalPaid: balance.totalPaid || 0,
      invoiced: 0,
      applied: 0,
      pending: 0,
    };
  }
  for (const invoice of invoices.filter((item) => item.status !== "anulada")) {
    rows[invoice.currency] ||= { committed: 0, totalPaid: 0, invoiced: 0, applied: 0, pending: 0 };
    rows[invoice.currency].invoiced += invoice.total || 0;
    rows[invoice.currency].applied += invoice.applied || 0;
    rows[invoice.currency].pending += invoice.pending || 0;
  }
  return Object.entries(rows).map(([currency, row]) => ({
    currency,
    committedUnbilled: Math.max(0, row.committed - row.invoiced),
    billedPending: row.pending,
    paymentsUnapplied: Math.max(0, row.totalPaid - row.applied),
  }));
}
