import test from "node:test";
import assert from "node:assert/strict";

// Importar desde .js — node:test no procesa JSX, solo logica pura.
import { getMovementActions } from "./movementActions.js";

// invoice
test("invoice Approved → ver pdf, descargar, anular", () => {
  assert.deepEqual(getMovementActions("invoice", "Approved"), ["view_pdf", "download_pdf", "annul"]);
});

test("invoice Rejected → reintentar", () => {
  assert.deepEqual(getMovementActions("invoice", "Rejected"), ["retry"]);
});

test("invoice InProgress → sin acciones", () => {
  assert.deepEqual(getMovementActions("invoice", "InProgress"), []);
});

test("invoice Annulled → sin acciones", () => {
  assert.deepEqual(getMovementActions("invoice", "Annulled"), []);
});

// credit_note
test("credit_note Approved → ver pdf, descargar (sin anular)", () => {
  assert.deepEqual(getMovementActions("credit_note", "Approved"), ["view_pdf", "download_pdf"]);
});

test("credit_note Rejected → reintentar", () => {
  assert.deepEqual(getMovementActions("credit_note", "Rejected"), ["retry"]);
});

test("credit_note InProgress → sin acciones", () => {
  assert.deepEqual(getMovementActions("credit_note", "InProgress"), []);
});

test("credit_note Annulled → sin acciones", () => {
  assert.deepEqual(getMovementActions("credit_note", "Annulled"), []);
});

// debit_note (misma matriz que credit_note — restriccion fiscal: ND no se anulan)
test("debit_note Approved → ver pdf, descargar (sin anular)", () => {
  assert.deepEqual(getMovementActions("debit_note", "Approved"), ["view_pdf", "download_pdf"]);
});

test("debit_note Rejected → reintentar", () => {
  assert.deepEqual(getMovementActions("debit_note", "Rejected"), ["retry"]);
});

test("debit_note InProgress → sin acciones", () => {
  assert.deepEqual(getMovementActions("debit_note", "InProgress"), []);
});

test("debit_note Annulled → sin acciones", () => {
  assert.deepEqual(getMovementActions("debit_note", "Annulled"), []);
});

// payment y otros
test("payment Paid → sin acciones", () => {
  assert.deepEqual(getMovementActions("payment", "Paid"), []);
});

test("credit_note_reversal Paid → sin acciones", () => {
  assert.deepEqual(getMovementActions("credit_note_reversal", "Paid"), []);
});

test("kind desconocido → sin acciones", () => {
  assert.deepEqual(getMovementActions("unknown_kind", "Approved"), []);
});
