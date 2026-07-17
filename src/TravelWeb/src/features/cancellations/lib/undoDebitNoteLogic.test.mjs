/**
 * Tests de lógica pura de "Deshacer una multa YA emitida y aprobada por ARCA"
 * (ADR-044, spec `docs/ux/2026-07-14-deshacer-multa-emitida.md`).
 *
 * Cómo correr:
 *   node --test src/features/cancellations/lib/undoDebitNoteLogic.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";
import {
  validarMotivoDeshacerMulta,
  puedeEnviarDeshacerMulta,
  construirPayloadUndoDebitNote,
  debeMostrarReintentarDeshacer,
  debeMostrarMontoAFavor,
  esErrorSaldoAplicadoAlDeshacerMulta,
} from "./undoDebitNoteLogic.js";

// ============================================================================
// esErrorSaldoAplicadoAlDeshacerMulta (Tanda D1, 2026-07-16)
// ============================================================================

test("esErrorSaldoAplicadoAlDeshacerMulta: invariantCode INV-UNDO-CREDITBRIDGE → true", () => {
  const error = { payload: { invariantCode: "INV-UNDO-CREDITBRIDGE", detail: "Esta multa tiene saldo a favor aplicado; revertí la aplicación antes de deshacerla." } };
  assert.equal(esErrorSaldoAplicadoAlDeshacerMulta(error), true);
});

test("esErrorSaldoAplicadoAlDeshacerMulta: otro invariantCode → false", () => {
  const error = { payload: { invariantCode: "INV-UNDO-LIVEPAYMENT" } };
  assert.equal(esErrorSaldoAplicadoAlDeshacerMulta(error), false);
});

test("esErrorSaldoAplicadoAlDeshacerMulta: sin payload → false (nunca revienta)", () => {
  assert.equal(esErrorSaldoAplicadoAlDeshacerMulta({}), false);
  assert.equal(esErrorSaldoAplicadoAlDeshacerMulta(null), false);
  assert.equal(esErrorSaldoAplicadoAlDeshacerMulta(undefined), false);
});

// ============================================================================
// validarMotivoDeshacerMulta (5..500 caracteres, mismo límite que el resto de
// los "motivos" de la ficha de multas)
// ============================================================================

test("validarMotivoDeshacerMulta: vacío → error", () => {
  assert.equal(validarMotivoDeshacerMulta(""), "El motivo debe tener al menos 5 caracteres.");
});

test("validarMotivoDeshacerMulta: solo espacios (después de trim queda vacío) → error", () => {
  assert.equal(validarMotivoDeshacerMulta("    "), "El motivo debe tener al menos 5 caracteres.");
});

test("validarMotivoDeshacerMulta: 4 caracteres (justo debajo del mínimo) → error", () => {
  assert.equal(validarMotivoDeshacerMulta("abcd"), "El motivo debe tener al menos 5 caracteres.");
});

test("validarMotivoDeshacerMulta: exactamente 5 caracteres (borde inferior) → válido", () => {
  assert.equal(validarMotivoDeshacerMulta("abcde"), null);
});

test("validarMotivoDeshacerMulta: motivo razonable → válido", () => {
  assert.equal(
    validarMotivoDeshacerMulta("El operador cobró la multa en pesos, no en dólares."),
    null
  );
});

test("validarMotivoDeshacerMulta: exactamente 500 caracteres (borde superior) → válido", () => {
  const motivo500 = "a".repeat(500);
  assert.equal(validarMotivoDeshacerMulta(motivo500), null);
});

test("validarMotivoDeshacerMulta: 501 caracteres (justo arriba del máximo) → error", () => {
  const motivo501 = "a".repeat(501);
  assert.equal(
    validarMotivoDeshacerMulta(motivo501),
    "El motivo no puede superar los 500 caracteres."
  );
});

test("validarMotivoDeshacerMulta: undefined/null (defensivo) → error de mínimo, no rompe", () => {
  assert.equal(validarMotivoDeshacerMulta(undefined), "El motivo debe tener al menos 5 caracteres.");
  assert.equal(validarMotivoDeshacerMulta(null), "El motivo debe tener al menos 5 caracteres.");
});

// ============================================================================
// puedeEnviarDeshacerMulta
// ============================================================================

test("puedeEnviarDeshacerMulta: motivo válido y sin envío en curso → true", () => {
  assert.equal(
    puedeEnviarDeshacerMulta({ motivo: "El operador cobró mal la multa.", submitting: false }),
    true
  );
});

test("puedeEnviarDeshacerMulta: motivo inválido → false aunque no esté enviando", () => {
  assert.equal(puedeEnviarDeshacerMulta({ motivo: "abc", submitting: false }), false);
});

test("puedeEnviarDeshacerMulta: envío en curso → false aunque el motivo sea válido (evita doble submit)", () => {
  assert.equal(
    puedeEnviarDeshacerMulta({ motivo: "El operador cobró mal la multa.", submitting: true }),
    false
  );
});

// ============================================================================
// construirPayloadUndoDebitNote (POST /cancellations/{id}/undo-debit-note)
// ============================================================================

test("construirPayloadUndoDebitNote: arma { reason } con el motivo recortado", () => {
  assert.deepEqual(
    construirPayloadUndoDebitNote("  El operador cobró la multa en pesos, no en dólares.  "),
    { reason: "El operador cobró la multa en pesos, no en dólares." }
  );
});

test("construirPayloadUndoDebitNote: motivo vacío/ausente (defensivo) → reason vacío, nunca undefined", () => {
  assert.deepEqual(construirPayloadUndoDebitNote(""), { reason: "" });
  assert.deepEqual(construirPayloadUndoDebitNote(undefined), { reason: "" });
});

// ============================================================================
// debeMostrarReintentarDeshacer (FIX BLOQUEANTE B1, revisión 2026-07-14): gate
// ÚNICO y Admin-only para las dos puertas de "Deshacer" — el link "· Deshacer: el
// operador cobró mal esta multa" (cartel "confirmada") Y el botón "Reintentar"
// (cartel "accionTrabada", estado DebitNoteAnnulmentFailed). Matriz completa
// admin(true/false) × canUndoDebitNote(true/false).
// ============================================================================

test("debeMostrarReintentarDeshacer: canUndoDebitNote=true + esAdmin=true → visible", () => {
  assert.equal(debeMostrarReintentarDeshacer({ canUndoDebitNote: true, esAdmin: true }), true);
});

test("debeMostrarReintentarDeshacer: canUndoDebitNote=true + esAdmin=false → OCULTO (el bug que arregla B1)", () => {
  // Antes del fix: un usuario sin rol Admin pero con permiso de clasificar multas
  // podía ver este botón/link solo porque el backend decía canUndoDebitNote=true.
  assert.equal(debeMostrarReintentarDeshacer({ canUndoDebitNote: true, esAdmin: false }), false);
});

test("debeMostrarReintentarDeshacer: canUndoDebitNote=false + esAdmin=true → oculto (el backend no lo habilita)", () => {
  assert.equal(debeMostrarReintentarDeshacer({ canUndoDebitNote: false, esAdmin: true }), false);
});

test("debeMostrarReintentarDeshacer: canUndoDebitNote=false + esAdmin=false → oculto", () => {
  assert.equal(debeMostrarReintentarDeshacer({ canUndoDebitNote: false, esAdmin: false }), false);
});

test("debeMostrarReintentarDeshacer: ambos ausentes (DTO viejo / defensivo) → oculto, degradación segura", () => {
  assert.equal(debeMostrarReintentarDeshacer({}), false);
});

// ============================================================================
// debeMostrarMontoAFavor (variante "el cliente ya te había pagado esta multa",
// backend 2026-07-14: collectedPenaltyAmount)
// ============================================================================

test("debeMostrarMontoAFavor: monto > 0 → true (el cliente ya pagó algo)", () => {
  assert.equal(debeMostrarMontoAFavor(200), true);
  assert.equal(debeMostrarMontoAFavor(0.01), true);
});

test("debeMostrarMontoAFavor: monto exactamente 0 (impaga) → false, NO se muestra la línea", () => {
  assert.equal(debeMostrarMontoAFavor(0), false);
});

test("debeMostrarMontoAFavor: null (el backend no pudo calcularlo) → false, no se inventa un monto", () => {
  assert.equal(debeMostrarMontoAFavor(null), false);
});

test("debeMostrarMontoAFavor: undefined (defensivo, DTO viejo) → false", () => {
  assert.equal(debeMostrarMontoAFavor(undefined), false);
});

test("debeMostrarMontoAFavor: monto negativo (defensivo, no debería pasar nunca) → false", () => {
  assert.equal(debeMostrarMontoAFavor(-50), false);
});
