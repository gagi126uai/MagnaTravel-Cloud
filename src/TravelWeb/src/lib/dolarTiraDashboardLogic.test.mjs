/**
 * Tests de lógica pura de la tira fina del dólar (spec docs/ux/specs/2026-08-06-dolar-en-dashboard.md).
 * Corren con Node puro sin bundler: node --test src/lib/dolarTiraDashboardLogic.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

import {
  faltaDatoDelDolar,
  formatearFechaDolarTira,
  hayOtrasMonedasParaMostrar,
} from "./dolarTiraDashboardLogic.js";

// ─── formatearFechaDolarTira ───────────────────────────────────────────────────

test("dato de hoy → 'al DD/MM', sin sufijo", () => {
  assert.equal(formatearFechaDolarTira("05/08/2026", false), "al 05/08");
});

test("dato viejo (isStale) → agrega '(sin actualizar)' (P6=A, reemplaza al badge de color)", () => {
  assert.equal(formatearFechaDolarTira("02/08/2026", true), "al 02/08 (sin actualizar)");
});

test("sin fecha (null/undefined) → cadena vacía, el llamador no pinta nada", () => {
  assert.equal(formatearFechaDolarTira(null, false), "");
  assert.equal(formatearFechaDolarTira(undefined, false), "");
});

test("fecha con formato inesperado (no DD/MM/YYYY) → cadena vacía, no revienta", () => {
  assert.equal(formatearFechaDolarTira("no-es-una-fecha", false), "");
  assert.equal(formatearFechaDolarTira("2026-08-05", false), "");
});

// ─── hayOtrasMonedasParaMostrar ────────────────────────────────────────────────

test("euro y real ambos con dato → true", () => {
  assert.equal(hayOtrasMonedasParaMostrar({ euroValue: 1660, realValue: 268 }), true);
});

test("solo euro con dato → true (alcanza con uno)", () => {
  assert.equal(hayOtrasMonedasParaMostrar({ euroValue: 1660, realValue: null }), true);
});

test("euro y real ambos null (respaldo de API pública, ADR-011) → false, nunca finge $0,00", () => {
  assert.equal(hayOtrasMonedasParaMostrar({ euroValue: null, realValue: null }), false);
});

test("rate sin las claves euroValue/realValue en absoluto → false", () => {
  assert.equal(hayOtrasMonedasParaMostrar({ value: 1515 }), false);
});

test("rate null/undefined → false", () => {
  assert.equal(hayOtrasMonedasParaMostrar(null), false);
  assert.equal(hayOtrasMonedasParaMostrar(undefined), false);
});

// ─── faltaDatoDelDolar ─────────────────────────────────────────────────────────

test("rate null/undefined → falta el dato (estado vacío honesto)", () => {
  assert.equal(faltaDatoDelDolar(null), true);
  assert.equal(faltaDatoDelDolar(undefined), true);
});

test("rate presente pero value null/undefined → falta el dato", () => {
  assert.equal(faltaDatoDelDolar({ value: null }), true);
  assert.equal(faltaDatoDelDolar({ value: undefined }), true);
});

test("rate con value numérico (incluido 0) → NO falta el dato", () => {
  assert.equal(faltaDatoDelDolar({ value: 1515 }), false);
  assert.equal(faltaDatoDelDolar({ value: 0 }), false);
});
