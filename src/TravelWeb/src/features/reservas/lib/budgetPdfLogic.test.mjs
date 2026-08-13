/**
 * Tests del interruptor "Por persona / Total del viaje" (spec
 * docs/ux/2026-08-12-spec-pdf-emision-y-formas-de-pago.md, §3). Cubren especialmente el
 * mapeo a los DOS contratos distintos del backend (string en el GET, booleano en el POST) —
 * es el punto exacto donde el brief original de esta tanda suponía un contrato equivocado.
 *
 * Cómo correr: node --test src/features/reservas/lib/budgetPdfLogic.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

import {
  MODO_PRECIO_PRESUPUESTO,
  etiquetaChipPrecioPresupuesto,
  alternarModoPrecioPresupuesto,
  queryParamPricingParaModo,
  porPersonaBooleanParaModo,
} from "./budgetPdfLogic.js";

// ─── etiquetaChipPrecioPresupuesto ──────────────────────────────────────────

test("etiquetaChipPrecioPresupuesto: modo Por persona muestra su etiqueta con flecha", () => {
  assert.equal(etiquetaChipPrecioPresupuesto(MODO_PRECIO_PRESUPUESTO.PorPersona), "Por persona ⇄");
});

test("etiquetaChipPrecioPresupuesto: modo Total muestra su etiqueta con flecha", () => {
  assert.equal(etiquetaChipPrecioPresupuesto(MODO_PRECIO_PRESUPUESTO.Total), "Total del viaje ⇄");
});

test("etiquetaChipPrecioPresupuesto: un modo desconocido cae al default Por persona, nunca queda mudo", () => {
  assert.equal(etiquetaChipPrecioPresupuesto("cualquier-otra-cosa"), "Por persona ⇄");
  assert.equal(etiquetaChipPrecioPresupuesto(undefined), "Por persona ⇄");
});

// ─── alternarModoPrecioPresupuesto ──────────────────────────────────────────

test("alternarModoPrecioPresupuesto: de Por persona pasa a Total", () => {
  assert.equal(
    alternarModoPrecioPresupuesto(MODO_PRECIO_PRESUPUESTO.PorPersona),
    MODO_PRECIO_PRESUPUESTO.Total
  );
});

test("alternarModoPrecioPresupuesto: de Total vuelve a Por persona", () => {
  assert.equal(
    alternarModoPrecioPresupuesto(MODO_PRECIO_PRESUPUESTO.Total),
    MODO_PRECIO_PRESUPUESTO.PorPersona
  );
});

// ─── queryParamPricingParaModo (GET /budget-pdf?pricing=…) ─────────────────

test("queryParamPricingParaModo: Por persona manda 'porPersona'", () => {
  assert.equal(queryParamPricingParaModo(MODO_PRECIO_PRESUPUESTO.PorPersona), "porPersona");
});

test("queryParamPricingParaModo: Total manda 'total'", () => {
  assert.equal(queryParamPricingParaModo(MODO_PRECIO_PRESUPUESTO.Total), "total");
});

// ─── porPersonaBooleanParaModo (POST /messages/budget → body.porPersona) ───

test("porPersonaBooleanParaModo: Por persona manda true", () => {
  assert.equal(porPersonaBooleanParaModo(MODO_PRECIO_PRESUPUESTO.PorPersona), true);
});

test("porPersonaBooleanParaModo: Total manda false", () => {
  assert.equal(porPersonaBooleanParaModo(MODO_PRECIO_PRESUPUESTO.Total), false);
});
