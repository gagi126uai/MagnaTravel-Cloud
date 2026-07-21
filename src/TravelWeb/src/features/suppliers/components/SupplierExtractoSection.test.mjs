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

// ─── Réplica de construirSufijoDestinoPago (rediseño "Registrar pago", 2026-07-20) ──────
// Mismo patrón que esLineaDeCircuitoCancelacion de arriba: función replicada inline
// porque node:test no parsea JSX. Si cambia la lógica en SupplierExtractoSection.jsx,
// actualizar acá también.
function construirSufijoDestinoPago(linea) {
    if (linea?.kind !== "Payment") return null;
    if (!linea?.reservaNumero) return null;
    return linea.servicioDescripcion
        ? ` · Reserva ${linea.reservaNumero} (${linea.servicioDescripcion})`
        : ` · Reserva ${linea.reservaNumero}`;
}

test("construirSufijoDestinoPago: pago imputado a reserva + servicio → sufijo con los dos datos", () => {
  const sufijo = construirSufijoDestinoPago({
    kind: "Payment",
    reservaNumero: "F-2026-1051",
    servicioDescripcion: "Hotel Bariloche",
  });
  assert.equal(sufijo, " · Reserva F-2026-1051 (Hotel Bariloche)");
});

test("construirSufijoDestinoPago: pago imputado a reserva SIN servicio puntual → solo la reserva", () => {
  const sufijo = construirSufijoDestinoPago({
    kind: "Payment",
    reservaNumero: "F-2026-1051",
    servicioDescripcion: null,
  });
  assert.equal(sufijo, " · Reserva F-2026-1051");
});

test("construirSufijoDestinoPago: pago 'a cuenta' (sin reservaNumero) → null (no se agrega nada)", () => {
  assert.equal(construirSufijoDestinoPago({ kind: "Payment", reservaNumero: null }), null);
  assert.equal(construirSufijoDestinoPago({ kind: "Payment" }), null);
});

test("construirSufijoDestinoPago: una compra (Purchase) nunca lleva sufijo, aunque traiga los campos", () => {
  assert.equal(
    construirSufijoDestinoPago({ kind: "Purchase", reservaNumero: "F-1", servicioDescripcion: "Hotel" }),
    null
  );
});

test("construirSufijoDestinoPago: linea null/undefined no rompe", () => {
  assert.equal(construirSufijoDestinoPago(null), null);
  assert.equal(construirSufijoDestinoPago(undefined), null);
});
