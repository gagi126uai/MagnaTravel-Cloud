/**
 * Tests de "Por persona / Total del viaje" — la elección de formato que ahora se pregunta
 * EN EL MOMENTO de emitir (decisión del dueño, 2026-08-16). Cubren especialmente el mapeo a
 * los DOS contratos distintos del backend (string en el GET, booleano en el POST) — es el
 * punto exacto donde el brief original de esta tanda suponía un contrato equivocado.
 *
 * Tanda A UX (2026-08-16): se sacaron los tests de `etiquetaChipPrecioPresupuesto` y
 * `alternarModoPrecioPresupuesto` — el interruptor con el chip "⇄" que esas funciones
 * apoyaban dejó de existir (ahora la elección es un renglón con dos botones explícitos, ver
 * ReservaHeader.jsx), así que esas dos funciones se borraron del módulo por quedar muertas.
 *
 * Cómo correr: node --test src/features/reservas/lib/budgetPdfLogic.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

import {
  MODO_PRECIO_PRESUPUESTO,
  queryParamPricingParaModo,
  porPersonaBooleanParaModo,
} from "./budgetPdfLogic.js";

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
