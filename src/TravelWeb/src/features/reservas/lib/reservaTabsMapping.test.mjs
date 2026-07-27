/**
 * Tests de la Tanda 3 (2026-07-24), fix 1: pestaña "Anuladas" + migración de views legacy (#38/#40).
 *
 * Cubre el mapeo pestaña → clave de contador (tabCountKey) que usa ReservasPage.jsx para leer
 * el número de cada pestaña desde summary. Ver docs/ux/2026-07-06-listado-finalizadas-vs-anuladas.md
 * (firmada) para la spec completa de la pestaña Anuladas.
 *
 * Corre con: node --test src/features/reservas/lib/reservaTabsMapping.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

import { tabCountKey, calcularContadorTodas } from "./reservaTabsMapping.js";

test("tabCountKey: in-management es el unico caso especial (guion en la URL, camelCase en el resumen)", () => {
  assert.equal(tabCountKey("in-management"), "inManagement");
});

test("tabCountKey: confirmed pasa igual (ya no es 'reserved', F3 migrada)", () => {
  assert.equal(tabCountKey("confirmed"), "confirmed");
});

test("tabCountKey: traveling pasa igual (ya no es 'operative', F3 migrada)", () => {
  assert.equal(tabCountKey("traveling"), "traveling");
});

test("tabCountKey: cancelled pasa igual (pestaña nueva Anuladas)", () => {
  assert.equal(tabCountKey("cancelled"), "cancelled");
});

test("tabCountKey: archived pasa igual", () => {
  assert.equal(tabCountKey("archived"), "archived");
});

test("tabCountKey: closed pasa igual (Finalizadas ahora solo trae Closed)", () => {
  assert.equal(tabCountKey("closed"), "closed");
});

test("tabCountKey: quotation, budget y lost pasan igual", () => {
  assert.equal(tabCountKey("quotation"), "quotation");
  assert.equal(tabCountKey("budget"), "budget");
  assert.equal(tabCountKey("lost"), "lost");
});

// ─── calcularContadorTodas (H20, pestaña "Todas", 2026-07-25) ──────────────────

test("calcularContadorTodas: suma los 9 estados excluyentes, sin contar activeCount aparte", () => {
  const total = calcularContadorTodas({
    quotationCount: 1,
    budgetCount: 2,
    inManagementCount: 3,
    activeCount: 999, // no debe sumarse: es inManagement+confirmed combinado, no un estado propio
    reservedCount: 4,
    operativeCount: 5,
    closedCount: 6,
    cancelledCount: 7,
    lostCount: 8,
    archivedCount: 9,
  });
  assert.equal(total, 1 + 2 + 3 + 4 + 5 + 6 + 7 + 8 + 9);
});

test("calcularContadorTodas: summary vacio -> 0", () => {
  assert.equal(calcularContadorTodas({}), 0);
});

test("calcularContadorTodas: summary null/undefined -> 0 (conservador)", () => {
  assert.equal(calcularContadorTodas(null), 0);
  assert.equal(calcularContadorTodas(undefined), 0);
});
