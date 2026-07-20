import test from "node:test";
import assert from "node:assert/strict";
import { classifySupplierInvoices, operadorFacturaDirectoAlCliente } from "./supplierInvoiceClassification.js";

test("factura reclasifica compromiso sin duplicar la cuenta por pagar", () => {
  const result = classifySupplierInvoices(
    [{ currency: "ARS", confirmedPurchases: 1000, totalPaid: 300 }],
    [{ currency: "ARS", status: "pago_parcial", total: 600, applied: 200, pending: 400 }],
  );
  assert.deepEqual(result, [{ currency: "ARS", committedUnbilled: 400, billedPending: 400, paymentsUnapplied: 100 }]);
});
test("clasificacion de facturas nunca mezcla ARS con USD ni cuenta anuladas", () => {
  const result = classifySupplierInvoices(
    [{ currency: "ARS", confirmedPurchases: 100, totalPaid: 0 }, { currency: "USD", confirmedPurchases: 50, totalPaid: 0 }],
    [{ currency: "ARS", status: "anulada", total: 80, applied: 0, pending: 80 }, { currency: "USD", status: "pendiente", total: 20, applied: 0, pending: 20 }],
  );
  assert.deepEqual(result, [
    { currency: "ARS", committedUnbilled: 100, billedPending: 0, paymentsUnapplied: 0 },
    { currency: "USD", committedUnbilled: 30, billedPending: 20, paymentsUnapplied: 0 },
  ]);
});

// ─── operadorFacturaDirectoAlCliente (Tanda 1, contrato pantalla-motor, 2026-07-18) ────
// Un operador "Intermediación (factura directo al cliente)" (invoicingMode=1) nunca
// genera cuenta por pagar de la agencia → "Nueva factura" se esconde para él.

test("operadorFacturaDirectoAlCliente — invoicingMode 1 (Intermediación) es true", () => {
  assert.equal(operadorFacturaDirectoAlCliente(1), true);
});

test("operadorFacturaDirectoAlCliente — invoicingMode 0 (Compra y reventa) es false", () => {
  assert.equal(operadorFacturaDirectoAlCliente(0), false);
});

test("operadorFacturaDirectoAlCliente — invoicingMode null/undefined (legacy) es false", () => {
  assert.equal(operadorFacturaDirectoAlCliente(null), false);
  assert.equal(operadorFacturaDirectoAlCliente(undefined), false);
});
