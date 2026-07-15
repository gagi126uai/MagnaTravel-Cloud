import test from "node:test";
import assert from "node:assert/strict";
import { supplierDueState } from "./supplierAging.js";

const now = new Date(2026, 6, 15, 12, 0, 0);

test("aging proveedor distingue vencida, hoy y por vencer", () => {
  assert.equal(supplierDueState("2026-07-13T00:00:00Z", now).tone, "overdue");
  assert.equal(supplierDueState("2026-07-15T00:00:00Z", now).tone, "today");
  assert.equal(supplierDueState("2026-07-18T00:00:00Z", now).tone, "soon");
});

test("aging proveedor no inventa vencimiento sin plazo", () => {
  assert.equal(supplierDueState(null, now), null);
});
