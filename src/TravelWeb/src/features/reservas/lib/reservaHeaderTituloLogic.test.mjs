/**
 * Tests del título dinámico de la cabecera de la ficha (Lavado de cara, Tanda 2,
 * fix bloqueante de review 2026-08-11, B1): Cotización y Presupuesto son etapas
 * DISTINTAS, cada una con su propia palabra — no se colapsan en una sola.
 *
 * Cómo correr: node --test src/features/reservas/lib/reservaHeaderTituloLogic.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

import {
  palabraTituloReserva,
  tituloReserva,
  debeOcultarChapitaEstado,
} from "./reservaHeaderTituloLogic.js";

// ─── Las 3 palabras del título ────────────────────────────────────────────────

test("Quotation -> palabra 'Cotización'", () => {
  assert.equal(palabraTituloReserva("Quotation"), "Cotización");
});

test("Budget -> palabra 'Presupuesto'", () => {
  assert.equal(palabraTituloReserva("Budget"), "Presupuesto");
});

test("resto de los estados (InManagement/Confirmed/Traveling/Closed/Lost/Cancelled/Archived) -> 'Reserva'", () => {
  assert.equal(palabraTituloReserva("InManagement"), "Reserva");
  assert.equal(palabraTituloReserva("Confirmed"), "Reserva");
  assert.equal(palabraTituloReserva("Traveling"), "Reserva");
  assert.equal(palabraTituloReserva("Closed"), "Reserva");
  assert.equal(palabraTituloReserva("Lost"), "Reserva");
  assert.equal(palabraTituloReserva("Cancelled"), "Reserva");
  assert.equal(palabraTituloReserva("Archived"), "Reserva");
});

test("status desconocido o vacío -> 'Reserva' (conservador, nunca queda mudo)", () => {
  assert.equal(palabraTituloReserva("EstadoQueNoExiste"), "Reserva");
  assert.equal(palabraTituloReserva(null), "Reserva");
  assert.equal(palabraTituloReserva(undefined), "Reserva");
});

// ─── Título completo: "{palabra} {numero}", sin "#" ──────────────────────────

test("tituloReserva: Quotation + numero -> 'Cotización 2026-1067'", () => {
  assert.equal(tituloReserva("Quotation", "2026-1067"), "Cotización 2026-1067");
});

test("tituloReserva: Budget + numero -> 'Presupuesto 2026-1067'", () => {
  assert.equal(tituloReserva("Budget", "2026-1067"), "Presupuesto 2026-1067");
});

test("tituloReserva: InManagement + numero -> 'Reserva 2026-1067' (nunca 'Presupuesto')", () => {
  assert.equal(tituloReserva("InManagement", "2026-1067"), "Reserva 2026-1067");
});

test("tituloReserva: nunca lleva '#' antes del número", () => {
  const titulo = tituloReserva("Confirmed", "2026-1067");
  assert.equal(titulo.includes("#"), false);
});

test("tituloReserva: numero null/undefined -> solo la palabra, sin espacio colgando", () => {
  assert.equal(tituloReserva("Budget", null), "Presupuesto");
  assert.equal(tituloReserva("Confirmed", undefined), "Reserva");
});

// ─── Chapita: se oculta SOLO cuando repetiría la palabra del título ──────────

test("Quotation -> chapita oculta (el título ya dice 'Cotización')", () => {
  assert.equal(debeOcultarChapitaEstado("Quotation"), true);
});

test("Budget -> chapita oculta (el título ya dice 'Presupuesto')", () => {
  assert.equal(debeOcultarChapitaEstado("Budget"), true);
});

test("InManagement/Confirmed/Cancelled -> chapita visible (el título dice 'Reserva', la chapita aporta el estado real)", () => {
  assert.equal(debeOcultarChapitaEstado("InManagement"), false);
  assert.equal(debeOcultarChapitaEstado("Confirmed"), false);
  assert.equal(debeOcultarChapitaEstado("Cancelled"), false);
});

test("status desconocido -> chapita visible (conservador: ante la duda, se muestra)", () => {
  assert.equal(debeOcultarChapitaEstado("EstadoQueNoExiste"), false);
  assert.equal(debeOcultarChapitaEstado(null), false);
});
