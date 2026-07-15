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
