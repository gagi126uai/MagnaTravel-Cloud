/**
 * Tests de lógica pura de la Tanda 2 (spec docs/ux/2026-07-18-t1-t2-contrato-pantalla-motor.md):
 * "pedir autorización desde la ficha de reserva" al fallar emitir/anular un comprobante.
 *
 * Corren con Node puro sin bundler: node --test src/features/reservas/lib/receiptApprovalFlow.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

import { resolverAccionAlFallarComprobante, armarEtiquetaComprobante } from "./receiptApprovalFlow.js";
import { formatCurrency } from "../../../lib/utils.js";

// ─── resolverAccionAlFallarComprobante ───────────────────────────────────────

test("409 con requiresApproval=true → pide autorización con los datos del motor", () => {
  const error = {
    status: 409,
    payload: {
      requiresApproval: true,
      requestType: "ReceiptVoidance",
      entityType: "PaymentReceipt",
      entityId: 4821,
    },
  };

  const resultado = resolverAccionAlFallarComprobante(error);

  assert.equal(resultado.requiereAutorizacion, true);
  assert.equal(resultado.requestType, "ReceiptVoidance");
  assert.equal(resultado.entityType, "PaymentReceipt");
  assert.equal(resultado.entityId, 4821);
});

test("409 común (sin requiresApproval) → sigue siendo un error de negocio normal", () => {
  const error = {
    status: 409,
    payload: { message: "Ese cargo ya fue procesado." },
  };

  const resultado = resolverAccionAlFallarComprobante(error);

  assert.equal(resultado.requiereAutorizacion, false);
});

test("409 con requiresApproval=false explícito → tampoco pide autorización", () => {
  const error = { status: 409, payload: { requiresApproval: false, message: "Otro motivo." } };

  const resultado = resolverAccionAlFallarComprobante(error);

  assert.equal(resultado.requiereAutorizacion, false);
});

test("error de otro status (ej. 500) con requiresApproval en el payload → se ignora, no es 409", () => {
  const error = { status: 500, payload: { requiresApproval: true } };

  const resultado = resolverAccionAlFallarComprobante(error);

  assert.equal(resultado.requiereAutorizacion, false);
});

test("error sin payload (falla de red: api.js deja status undefined) → nunca pide autorización", () => {
  const error = { message: "Failed to fetch" };

  const resultado = resolverAccionAlFallarComprobante(error);

  assert.equal(resultado.requiereAutorizacion, false);
});

test("error null/undefined no rompe la función", () => {
  assert.equal(resolverAccionAlFallarComprobante(null).requiereAutorizacion, false);
  assert.equal(resolverAccionAlFallarComprobante(undefined).requiereAutorizacion, false);
});

// ─── armarEtiquetaComprobante ─────────────────────────────────────────────────

test("con pago y fecha → arma el texto en criollo con monto y fecha", () => {
  // paidAt con el formato REAL que persiste el backend: fecha-solo-día en
  // medianoche UTC. Con toLocaleDateString esto mostraba 14/07 (fecha corrida
  // un día); formatDate la muestra bien en cualquier zona horaria.
  const payment = { amount: 80000, currency: "ARS", paidAt: "2026-07-15T00:00:00Z" };

  const etiqueta = armarEtiquetaComprobante(payment, formatCurrency);

  assert.match(etiqueta, /^Comprobante del cobro/);
  assert.match(etiqueta, /15\/07\/2026/);
  // No debe filtrar ningún ID interno del pago (ni publicId ni Guid).
  assert.equal(/[0-9a-f]{8}-[0-9a-f]{4}-/i.test(etiqueta), false);
});

test("con fecha no interpretable → omite la fecha antes que mostrar basura", () => {
  const payment = { amount: 500, currency: "USD", paidAt: "no-es-una-fecha" };

  const etiqueta = armarEtiquetaComprobante(payment, formatCurrency);

  assert.match(etiqueta, /^Comprobante del cobro/);
  assert.equal(etiqueta.includes("Invalid"), false);
  assert.equal(etiqueta.includes("·"), false);
});

test("con monto no numérico → cae al texto genérico, nunca muestra NaN", () => {
  const payment = { amount: "no-numérico", currency: "ARS", paidAt: "2026-07-15T00:00:00Z" };

  const etiqueta = armarEtiquetaComprobante(payment, formatCurrency);

  assert.equal(etiqueta, "Comprobante de pago");
});

test("con pago sin fecha → arma el texto solo con el monto", () => {
  const payment = { amount: 500, currency: "USD" };

  const etiqueta = armarEtiquetaComprobante(payment, formatCurrency);

  assert.match(etiqueta, /^Comprobante del cobro/);
  assert.equal(etiqueta.includes("·"), false);
});

test("sin pago (null) → cae al texto genérico, nunca revienta", () => {
  const etiqueta = armarEtiquetaComprobante(null, formatCurrency);

  assert.equal(etiqueta, "Comprobante de pago");
});
