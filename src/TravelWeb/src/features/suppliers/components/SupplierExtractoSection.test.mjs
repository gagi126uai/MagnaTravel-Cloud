/**
 * Tests de lógica pura para el chip "Anulación" del extracto del operador
 * (ADR-044 T4, 2026-07-10).
 *
 * Cómo correr: node --test src/features/suppliers/components/SupplierExtractoSection.test.mjs
 *
 * Patrón del proyecto (ver operatorRefundsPending.test.mjs): función replicada inline
 * (sin import del componente .jsx — node:test no parsea JSX). Si cambia la lógica en
 * `esLineaDeCircuitoCancelacion` de SupplierExtractoSection.jsx, actualizar acá también.
 */

import test from "node:test";
import assert from "node:assert/strict";

// ─── Réplica de esLineaDeCircuitoCancelacion (SupplierExtractoSection.jsx) ───────
function esLineaDeCircuitoCancelacion(kind) {
  return (
    kind === "PenaltyRetained" ||
    kind === "RefundReceived" ||
    kind === "OperatorChargeInvoiced" ||
    kind === "TreasuryFxAdjustment"
  );
}

test("esLineaDeCircuitoCancelacion: PenaltyRetained y RefundReceived (kinds originales) llevan el chip", () => {
  assert.equal(esLineaDeCircuitoCancelacion("PenaltyRetained"), true);
  assert.equal(esLineaDeCircuitoCancelacion("RefundReceived"), true);
});

test("esLineaDeCircuitoCancelacion: OperatorChargeInvoiced y TreasuryFxAdjustment (ADR-044 T4) TAMBIÉN llevan el chip", () => {
  assert.equal(esLineaDeCircuitoCancelacion("OperatorChargeInvoiced"), true);
  assert.equal(esLineaDeCircuitoCancelacion("TreasuryFxAdjustment"), true);
});

test("esLineaDeCircuitoCancelacion: una compra normal (Purchase) o un pago (Payment) NO llevan el chip", () => {
  assert.equal(esLineaDeCircuitoCancelacion("Purchase"), false);
  assert.equal(esLineaDeCircuitoCancelacion("Payment"), false);
});

test("esLineaDeCircuitoCancelacion: kind desconocido o ausente → false (degradación segura)", () => {
  assert.equal(esLineaDeCircuitoCancelacion("AlgoNuevoDelBackend"), false);
  assert.equal(esLineaDeCircuitoCancelacion(undefined), false);
});
