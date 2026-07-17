/**
 * Tests de formato de la columna "Documento" del extracto del cliente (Tanda D2).
 *
 * Cómo correr: node --test src/features/customers/lib/estadoCuentaFormatting.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

import { formatEtiquetaDocumentoExtracto, formatCierreExtracto } from "./estadoCuentaFormatting.js";

test("Invoice → 'Factura {número}'", () => {
  const texto = formatEtiquetaDocumentoExtracto({ kind: "Invoice", documentRef: "0001-00009" });
  assert.strictEqual(texto, "Factura 0001-00009");
});

test("DebitNote → 'Nota de débito {número} (multa)' — ND es siempre multa en este dominio", () => {
  const texto = formatEtiquetaDocumentoExtracto({ kind: "DebitNote", documentRef: "0002-00003" });
  assert.strictEqual(texto, "Nota de débito 0002-00003 (multa)");
});

test("CreditNote → 'Nota de crédito {número}', SIN motivo (puede ser anulación total o NC parcial T5)", () => {
  const texto = formatEtiquetaDocumentoExtracto({ kind: "CreditNote", documentRef: "0003-00002" });
  assert.strictEqual(texto, "Nota de crédito 0003-00002");
  assert.ok(!texto.includes("("), "No debe inventar un motivo entre paréntesis para CreditNote");
});

test("Payment → usa la descripción que ya armó el backend, tal cual", () => {
  const texto = formatEtiquetaDocumentoExtracto({ kind: "Payment", description: "Cobro recibo 0001-00045" });
  assert.strictEqual(texto, "Cobro recibo 0001-00045");
});

test("CreditApplication → usa la descripción del backend, tal cual", () => {
  const texto = formatEtiquetaDocumentoExtracto({ kind: "CreditApplication", description: "Saldo a favor aplicado" });
  assert.strictEqual(texto, "Saldo a favor aplicado");
});

test("sin documentRef, el tipo se muestra solo (sin número pegado con espacio de más)", () => {
  const texto = formatEtiquetaDocumentoExtracto({ kind: "Invoice", documentRef: null });
  assert.strictEqual(texto, "Factura");
});

test("kind desconocido y sin descripción → '—' (nunca vacío)", () => {
  const texto = formatEtiquetaDocumentoExtracto({ kind: "OtroFuturo", description: null });
  assert.strictEqual(texto, "—");
});

// ============================================================================
// formatCierreExtracto (fix de revisión 2026-07-17: cierre AL PIE del bloque)
// ============================================================================

test("saldo positivo → 'Saldo al día (debe): $X'", () => {
  const texto = formatCierreExtracto(185000, "ARS");
  assert.ok(texto.startsWith("Saldo al día (debe):"));
  assert.ok(texto.includes("185.000"));
});

test("saldo negativo → 'Saldo al día (a favor): $X', SIN el signo negativo pegado al número", () => {
  const texto = formatCierreExtracto(-500, "ARS");
  assert.ok(texto.startsWith("Saldo al día (a favor):"));
  assert.ok(!texto.includes("-500"), "El monto se muestra en valor absoluto, la palabra 'a favor' ya dice el signo");
});

test("saldo en 0 → 'Saldo al día: $0', sin '(debe)' ni '(a favor)'", () => {
  const texto = formatCierreExtracto(0, "ARS");
  assert.ok(texto.startsWith("Saldo al día:"));
  assert.ok(!texto.includes("debe"));
  assert.ok(!texto.includes("favor"));
});

test("diferencia de un centavo por redondeo → se trata como 0 (tolerancia)", () => {
  const texto = formatCierreExtracto(0.005, "ARS");
  assert.ok(texto.startsWith("Saldo al día:"));
});

test("USD usa el símbolo US$", () => {
  const texto = formatCierreExtracto(300, "USD");
  assert.ok(texto.includes("US$"));
});
