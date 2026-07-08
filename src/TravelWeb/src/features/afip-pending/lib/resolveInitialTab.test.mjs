/**
 * Tests de la lógica pura de solapa inicial de /pendientes-afip.
 * Correr: node --test src/features/afip-pending/lib/resolveInitialTab.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";
import {
  AFIP_PENDING_TABS,
  getAllowedAfipPendingTabs,
  resolveInitialAfipPendingTab,
} from "./resolveInitialTab.js";

// ─── getAllowedAfipPendingTabs ────────────────────────────────────────────

test("getAllowedAfipPendingTabs: usuario con los 3 permisos ve las 3 solapas, en orden", () => {
  const allowed = getAllowedAfipPendingTabs(() => true);
  assert.deepEqual(allowed.map((t) => t.key), ["multas", "notasCredito", "recibos"]);
});

test("getAllowedAfipPendingTabs: usuario sin ningún permiso no ve ninguna solapa", () => {
  const allowed = getAllowedAfipPendingTabs(() => false);
  assert.deepEqual(allowed, []);
});

test("getAllowedAfipPendingTabs: usuario con un solo permiso ve solo esa solapa", () => {
  const allowed = getAllowedAfipPendingTabs((p) => p === "cobranzas.view_all");
  assert.deepEqual(allowed.map((t) => t.key), ["notasCredito"]);
});

test("getAllowedAfipPendingTabs: usuario con 2 permisos mantiene el orden fijo multas→notasCredito→recibos", () => {
  // Le damos permiso de recibos y multas (en ESE orden al llamar), pero el resultado
  // respeta el orden fijo de AFIP_PENDING_TABS, no el orden en que se otorgaron.
  const allowed = getAllowedAfipPendingTabs(
    (p) => p === "approvals.review" || p === "cobranzas.invoice_annul"
  );
  assert.deepEqual(allowed.map((t) => t.key), ["multas", "recibos"]);
});

// ─── resolveInitialAfipPendingTab ─────────────────────────────────────────

test("resolveInitialAfipPendingTab: sin ?tab= y con los 3 permisos → arranca en 'multas' (primera del orden)", () => {
  const key = resolveInitialAfipPendingTab(null, () => true);
  assert.equal(key, "multas");
});

test("resolveInitialAfipPendingTab: sin ?tab= y solo con permiso de notasCredito → arranca en 'notasCredito'", () => {
  const key = resolveInitialAfipPendingTab(undefined, (p) => p === "cobranzas.view_all");
  assert.equal(key, "notasCredito");
});

test("resolveInitialAfipPendingTab: ?tab=recibos válido y permitido → arranca en 'recibos'", () => {
  const key = resolveInitialAfipPendingTab("recibos", () => true);
  assert.equal(key, "recibos");
});

test("resolveInitialAfipPendingTab: ?tab=recibos SIN el permiso de esa solapa → cae a la primera permitida", () => {
  // El usuario pide recibos por URL pero solo tiene permiso de multas.
  const key = resolveInitialAfipPendingTab("recibos", (p) => p === "cobranzas.invoice_annul");
  assert.equal(key, "multas");
});

test("resolveInitialAfipPendingTab: ?tab= con una key inexistente → cae a la primera permitida", () => {
  const key = resolveInitialAfipPendingTab("solapa-que-no-existe", () => true);
  assert.equal(key, "multas");
});

test("resolveInitialAfipPendingTab: usuario sin ningún permiso → null", () => {
  const key = resolveInitialAfipPendingTab("multas", () => false);
  assert.equal(key, null);
});

test("AFIP_PENDING_TABS: las 3 keys y permisos son los esperados por la spec", () => {
  assert.deepEqual(
    AFIP_PENDING_TABS.map((t) => [t.key, t.permission]),
    [
      ["multas", "cobranzas.invoice_annul"],
      ["notasCredito", "cobranzas.view_all"],
      ["recibos", "approvals.review"],
    ]
  );
});
