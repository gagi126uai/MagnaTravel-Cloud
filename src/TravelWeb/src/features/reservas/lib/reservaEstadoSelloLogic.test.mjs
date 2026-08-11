/**
 * Tests de qué estados de Reserva llevan el SELLO en el listado (Lavado de cara,
 * Tanda 1, fix bloqueante de review 2026-08-11, I1).
 *
 * Cómo correr: node --test src/features/reservas/lib/reservaEstadoSelloLogic.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

import { debeMostrarComoSello } from "./reservaEstadoSelloLogic.js";

test("Anulada (Cancelled) -> sello", () => {
  assert.equal(debeMostrarComoSello({ status: "Cancelled" }), true);
});

test("Perdida (Lost) -> sello", () => {
  assert.equal(debeMostrarComoSello({ status: "Lost" }), true);
});

test("Finalizada (Closed) -> sello", () => {
  assert.equal(debeMostrarComoSello({ status: "Closed" }), true);
});

// ─── El caso que motivó el fix: PendingOperatorRefund es una reserva VIVA ────────

test("Esperando reembolso (PendingOperatorRefund) -> NUNCA sello (reserva viva, no anulada de verdad)", () => {
  assert.equal(debeMostrarComoSello({ status: "PendingOperatorRefund" }), false);
});

// ─── El resto de los estados vivos: siguen con el chip de siempre ────────────────

test("estados vivos (Quotation/Budget/InManagement/Confirmed/Traveling) -> sin sello", () => {
  assert.equal(debeMostrarComoSello({ status: "Quotation" }), false);
  assert.equal(debeMostrarComoSello({ status: "Budget" }), false);
  assert.equal(debeMostrarComoSello({ status: "InManagement" }), false);
  assert.equal(debeMostrarComoSello({ status: "Confirmed" }), false);
  assert.equal(debeMostrarComoSello({ status: "Traveling" }), false);
});

test("Archivada -> sin sello (el sello es solo para los 3 estados 'muertos', Archivada sigue con chip)", () => {
  assert.equal(debeMostrarComoSello({ status: "Archived" }), false);
});

test("reserva null/undefined -> false, no revienta", () => {
  assert.equal(debeMostrarComoSello(null), false);
  assert.equal(debeMostrarComoSello(undefined), false);
});

test("status desconocido -> false (conservador, nunca sella algo que no reconoce)", () => {
  assert.equal(debeMostrarComoSello({ status: "EstadoQueNoExiste" }), false);
});
