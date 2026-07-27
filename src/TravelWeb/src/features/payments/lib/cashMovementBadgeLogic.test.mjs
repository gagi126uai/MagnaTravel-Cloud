/**
 * Tests del badge "Reemplazado"/"Anulado" del Libro de Caja (Obra 2, firma 2026-07-27).
 *
 * Cómo correr: node --test src/features/payments/lib/cashMovementBadgeLogic.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

import {
  obtenerEstadoBadgeMovimiento,
  debeApagarBotonesMovimiento,
} from "./cashMovementBadgeLogic.js";

// ─── obtenerEstadoBadgeMovimiento ────────────────────────────────────────────────

test("isReplaced=true -> badge 'Reemplazado' con su motivo propio y estado='reemplazado'", () => {
  const resultado = obtenerEstadoBadgeMovimiento({ isReplaced: true, isAnnulled: false });
  assert.deepEqual(resultado, {
    etiqueta: "Reemplazado",
    estado: "reemplazado",
    motivoBotonesApagados: "Fue reemplazado por una edición, no se puede editar ni anular.",
  });
});

test("isReplaced=true e isAnnulled=true (el reemplazo se implementa como anular+crear puertas adentro) -> gana 'Reemplazado'", () => {
  const resultado = obtenerEstadoBadgeMovimiento({ isReplaced: true, isAnnulled: true });
  assert.equal(resultado.etiqueta, "Reemplazado");
});

test("isAnnulled=true sin isReplaced -> sigue siendo 'Anulado' como hoy, estado='anulado'", () => {
  const resultado = obtenerEstadoBadgeMovimiento({ isReplaced: false, isAnnulled: true });
  assert.deepEqual(resultado, {
    etiqueta: "Anulado",
    estado: "anulado",
    motivoBotonesApagados: "Ya está anulado, no se puede editar ni anular de nuevo.",
  });
});

test("ni reemplazado ni anulado -> no hay badge (null)", () => {
  assert.equal(obtenerEstadoBadgeMovimiento({ isReplaced: false, isAnnulled: false }), null);
});

test("fila vieja sin isReplaced en el DTO (undefined) y sin isAnnulled -> comportamiento actual, sin badge", () => {
  assert.equal(obtenerEstadoBadgeMovimiento({ isAnnulled: false }), null);
});

test("fila vieja sin isReplaced en el DTO (undefined) pero isAnnulled=true -> comportamiento actual, badge 'Anulado' (fallback)", () => {
  const resultado = obtenerEstadoBadgeMovimiento({ isAnnulled: true });
  assert.equal(resultado.etiqueta, "Anulado");
});

test("movement null/undefined -> no rompe, sin badge", () => {
  assert.equal(obtenerEstadoBadgeMovimiento(null), null);
  assert.equal(obtenerEstadoBadgeMovimiento(undefined), null);
});

// ─── debeApagarBotonesMovimiento ─────────────────────────────────────────────────

test("isReplaced=true -> apaga los botones", () => {
  assert.equal(debeApagarBotonesMovimiento({ isReplaced: true }), true);
});

test("isAnnulled=true -> apaga los botones", () => {
  assert.equal(debeApagarBotonesMovimiento({ isAnnulled: true }), true);
});

test("ninguno de los dos -> botones habilitados", () => {
  assert.equal(debeApagarBotonesMovimiento({ isReplaced: false, isAnnulled: false }), false);
});

test("movement null/undefined -> botones habilitados, no rompe", () => {
  assert.equal(debeApagarBotonesMovimiento(null), false);
  assert.equal(debeApagarBotonesMovimiento(undefined), false);
});
