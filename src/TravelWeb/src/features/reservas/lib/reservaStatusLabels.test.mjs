/**
 * Tests del mapa criollo de estados de reserva para dashboards (Obra 6, firma 2026-07-27).
 *
 * Cómo correr: node --test src/features/reservas/lib/reservaStatusLabels.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

import { RESERVA_STATUS_LABELS, traducirEstadoReserva } from "./reservaStatusLabels.js";

// ─── Cobertura exhaustiva: los 10 estados que puede traer reserva.status ─────────

test("traduce los 10 estados conocidos del ciclo de reserva", () => {
  assert.equal(traducirEstadoReserva("Quotation"), "Cotizacion");
  assert.equal(traducirEstadoReserva("Budget"), "Presupuesto");
  assert.equal(traducirEstadoReserva("InManagement"), "En gestion");
  assert.equal(traducirEstadoReserva("Confirmed"), "Confirmada");
  assert.equal(traducirEstadoReserva("Traveling"), "En viaje");
  assert.equal(traducirEstadoReserva("Closed"), "Finalizada");
  assert.equal(traducirEstadoReserva("Lost"), "Perdido");
  assert.equal(traducirEstadoReserva("Cancelled"), "Anulada");
  assert.equal(traducirEstadoReserva("PendingOperatorRefund"), "Esperando reembolso");
  assert.equal(traducirEstadoReserva("Archived"), "Archivada");
});

test("RESERVA_STATUS_LABELS tiene exactamente esos 10 estados (nadie agregó uno sin traducción)", () => {
  assert.deepEqual(Object.keys(RESERVA_STATUS_LABELS).sort(), [
    "Archived",
    "Budget",
    "Cancelled",
    "Closed",
    "Confirmed",
    "InManagement",
    "Lost",
    "PendingOperatorRefund",
    "Quotation",
    "Traveling",
  ]);
});

// ─── Regla dura del hallazgo (Obra 6): JAMÁS la clave cruda como fallback ────────

test("status desconocido (nunca visto) -> '—' neutro, NUNCA la clave cruda", () => {
  assert.equal(traducirEstadoReserva("EstadoQueNoExiste"), "—");
});

test("status null/undefined/vacío -> '—', no rompe", () => {
  assert.equal(traducirEstadoReserva(null), "—");
  assert.equal(traducirEstadoReserva(undefined), "—");
  assert.equal(traducirEstadoReserva(""), "—");
});
