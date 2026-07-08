/**
 * Tests de lógica pura de la bandeja pasiva "Cargos de cancelación pendientes"
 * (Parte D de la spec "el paso de multa vive en la ficha", 2026-07-08).
 *
 * Cómo correr:
 *   node --test src/features/cancellations/components/debitNoteInboxLogic.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";
import { textoQueFalta, textoTiempoRelativo } from "../debitNoteInboxLogic.js";

// ============================================================================
// textoQueFalta
// ============================================================================

test("textoQueFalta: los 5 estados conocidos de la bandeja", () => {
  assert.equal(textoQueFalta("EstimatedPendingConfirmation"), "Falta confirmar el monto de la multa");
  assert.equal(textoQueFalta("ConfirmedWithoutDebitNote"), "Falta cobrarle la multa al cliente");
  assert.equal(textoQueFalta("Pending"), "El cobro de la multa está en proceso");
  assert.equal(textoQueFalta("Failed"), "El cobro de la multa no salió — hay que reintentar");
  assert.equal(textoQueFalta("ManualReview"), "Falta corregir el monto y la moneda");
});

test("textoQueFalta: nunca menciona 'nota de débito' (regla de voz 2026-07-08, término fiscal solo en facturación)", () => {
  for (const estado of ["EstimatedPendingConfirmation", "ConfirmedWithoutDebitNote", "Pending", "Failed", "ManualReview"]) {
    assert.ok(!/nota de d[ée]bito/i.test(textoQueFalta(estado)), `el texto de ${estado} no debería nombrar la nota de débito`);
  }
});

test("textoQueFalta: estado desconocido nunca muestra el string técnico crudo", () => {
  const texto = textoQueFalta("AlgunEnumRaroDelBackend");
  assert.equal(texto, "Hay que revisar esta cancelación");
  assert.ok(!texto.includes("AlgunEnumRaroDelBackend"));
});

// ============================================================================
// textoTiempoRelativo
// ============================================================================

test("textoTiempoRelativo: sin fecha → guion", () => {
  assert.equal(textoTiempoRelativo(null), "—");
  assert.equal(textoTiempoRelativo(undefined), "—");
});

test("textoTiempoRelativo: fecha invalida → guion", () => {
  assert.equal(textoTiempoRelativo("no-es-una-fecha"), "—");
});

test("textoTiempoRelativo: menos de 1 minuto → 'recién'", () => {
  const ahora = new Date("2026-07-08T12:00:00Z");
  const hace30seg = new Date("2026-07-08T11:59:30Z");
  assert.equal(textoTiempoRelativo(hace30seg, ahora), "recién");
});

test("textoTiempoRelativo: minutos, singular y plural", () => {
  const ahora = new Date("2026-07-08T12:00:00Z");
  assert.equal(textoTiempoRelativo(new Date("2026-07-08T11:59:00Z"), ahora), "hace 1 minuto");
  assert.equal(textoTiempoRelativo(new Date("2026-07-08T11:55:00Z"), ahora), "hace 5 minutos");
});

test("textoTiempoRelativo: horas, singular y plural", () => {
  const ahora = new Date("2026-07-08T12:00:00Z");
  assert.equal(textoTiempoRelativo(new Date("2026-07-08T11:00:00Z"), ahora), "hace 1 hora");
  assert.equal(textoTiempoRelativo(new Date("2026-07-08T09:00:00Z"), ahora), "hace 3 horas");
});

test("textoTiempoRelativo: días, singular y plural", () => {
  const ahora = new Date("2026-07-08T12:00:00Z");
  assert.equal(textoTiempoRelativo(new Date("2026-07-07T12:00:00Z"), ahora), "hace 1 día");
  assert.equal(textoTiempoRelativo(new Date("2026-07-01T12:00:00Z"), ahora), "hace 7 días");
});
