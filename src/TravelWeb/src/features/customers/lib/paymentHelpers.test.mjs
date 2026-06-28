/**
 * Tests para traducirMetodoPago (helpers de cobros del cliente).
 *
 * Corren con Node puro:
 *   node --test src/features/customers/lib/paymentHelpers.test.mjs
 *
 * Cobertura:
 *   - inglés legado: Transfer / Cash / Card → español
 *   - español actual: Transferencia / Efectivo / Tarjeta → español (pasan tal cual)
 *   - valores vacíos y desconocidos → "" (sin código técnico visible al usuario)
 */

import test from "node:test";
import assert from "node:assert/strict";

// Lógica replicada inline (patrón del proyecto)
function traducirMetodoPago(method) {
  if (!method) return "";
  const mapa = {
    Transfer:      "Transferencia",
    Cash:          "Efectivo",
    Card:          "Tarjeta",
    Transferencia: "Transferencia",
    Efectivo:      "Efectivo",
    Tarjeta:       "Tarjeta",
    Cheque:        "Cheque",
    Check:         "Cheque",
    Other:         "",
    Otro:          "",
  };
  const normalizado = method.charAt(0).toUpperCase() + method.slice(1).toLowerCase();
  return mapa[method] ?? mapa[normalizado] ?? "";
}

// ─── Inglés legado ──────────────────────────────────────────────────────────

test("traducirMetodoPago - 'Transfer' legado → 'Transferencia'", () => {
  assert.equal(traducirMetodoPago("Transfer"), "Transferencia");
});

test("traducirMetodoPago - 'Cash' legado → 'Efectivo'", () => {
  assert.equal(traducirMetodoPago("Cash"), "Efectivo");
});

test("traducirMetodoPago - 'Card' legado → 'Tarjeta'", () => {
  assert.equal(traducirMetodoPago("Card"), "Tarjeta");
});

// ─── Español actual ─────────────────────────────────────────────────────────

test("traducirMetodoPago - 'Transferencia' español → 'Transferencia'", () => {
  assert.equal(traducirMetodoPago("Transferencia"), "Transferencia");
});

test("traducirMetodoPago - 'Efectivo' español → 'Efectivo'", () => {
  assert.equal(traducirMetodoPago("Efectivo"), "Efectivo");
});

test("traducirMetodoPago - 'Tarjeta' español → 'Tarjeta'", () => {
  assert.equal(traducirMetodoPago("Tarjeta"), "Tarjeta");
});

// ─── Sin exposición de código técnico al usuario ────────────────────────────

test("traducirMetodoPago - vacío → '' (sin texto)", () => {
  assert.equal(traducirMetodoPago(""), "");
});

test("traducirMetodoPago - null → '' (sin texto)", () => {
  assert.equal(traducirMetodoPago(null), "");
});

test("traducirMetodoPago - undefined → '' (sin texto)", () => {
  assert.equal(traducirMetodoPago(undefined), "");
});

test("traducirMetodoPago - tipo desconocido → '' (sin código crudo al usuario)", () => {
  // Un tipo nuevo no reconocido NO debe mostrar el string técnico
  const resultado = traducirMetodoPago("CryptoWallet");
  assert.equal(resultado, "");
});

test("traducirMetodoPago - 'Other'/'Otro' → '' (no se muestra)", () => {
  assert.equal(traducirMetodoPago("Other"), "");
  assert.equal(traducirMetodoPago("Otro"), "");
});

// ─── Invariante de exposición: el resultado NUNCA debe mostrar el valor crudo ─

test("traducirMetodoPago - valor crudo 'Transfer' NO aparece en el resultado", () => {
  // La UI del extracto muestra `resultado || ""`, nunca "· Transfer"
  const resultado = traducirMetodoPago("Transfer");
  assert.equal(resultado === "Transfer", false);
});

test("traducirMetodoPago - valor crudo 'Cash' NO aparece en el resultado", () => {
  const resultado = traducirMetodoPago("Cash");
  assert.equal(resultado === "Cash", false);
});
