/**
 * Tests de contrato para avisosFicha.js (spec UX 2026-07-05, respuestas 1C/5A del
 * rediseño "arriba la foto, abajo solo lo que hay que hacer" de la ficha de reserva).
 *
 * avisosFicha.js NO tiene JSX (funciones puras), así que a diferencia de otros tests
 * .mjs del proyecto se puede importar el módulo REAL directamente — no hace falta
 * copiar la lógica acá (mismo criterio que moneyStatus.test.mjs).
 *
 * Cómo correr: node --test src/features/reservas/components/avisosFicha.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

import {
  getServiciosSinConfirmar,
  getAdvertenciaCapacidad,
  construirAvisosInformativos,
  formatearContadorAvisos,
} from "../avisosFicha.js";

// ─── getServiciosSinConfirmar ───────────────────────────────────────────────

test("getServiciosSinConfirmar: reserva null → array vacío (degradación elegante)", () => {
  assert.deepEqual(getServiciosSinConfirmar(null), []);
});

test("getServiciosSinConfirmar: fuera de Confirmed → array vacío (InManagement tiene su propio resumen)", () => {
  const reserva = {
    status: "InManagement",
    hotelBookings: [{ name: "Hotel Sur", workflowStatus: "PendingConfirmation" }],
  };
  assert.deepEqual(getServiciosSinConfirmar(reserva), []);
});

test("getServiciosSinConfirmar: Confirmed con un hotel sin confirmar → lo devuelve", () => {
  const reserva = {
    status: "Confirmed",
    hotelBookings: [{ name: "Hotel Sur", workflowStatus: "PendingConfirmation" }],
  };
  const sinResolver = getServiciosSinConfirmar(reserva);
  assert.equal(sinResolver.length, 1);
  assert.equal(sinResolver[0].nombre, "Hotel Sur");
});

test("getServiciosSinConfirmar: servicios en estados resueltos no cuentan", () => {
  const reserva = {
    status: "Confirmed",
    hotelBookings: [{ name: "Hotel Sur", workflowStatus: "Confirmado" }],
    transferBookings: [{ name: "Traslado aeropuerto", workflowStatus: "Emitido" }],
  };
  assert.deepEqual(getServiciosSinConfirmar(reserva), []);
});

test("getServiciosSinConfirmar: servicios Cancelado no cuentan (no compiten por confirmación)", () => {
  const reserva = {
    status: "Confirmed",
    flightSegments: [{ name: "Aéreo Buenos Aires", workflowStatus: "Cancelado" }],
  };
  assert.deepEqual(getServiciosSinConfirmar(reserva), []);
});

test("getServiciosSinConfirmar: mezcla de tipos de servicio, cuenta solo los pendientes", () => {
  const reserva = {
    status: "Confirmed",
    hotelBookings: [{ name: "Hotel Sur", workflowStatus: "Confirmado" }],
    packageBookings: [{ packageName: "Paquete Bariloche", workflowStatus: "PendingConfirmation" }],
    assistanceBookings: [{ name: "Asistencia médica", workflowStatus: "PendingConfirmation" }],
  };
  const sinResolver = getServiciosSinConfirmar(reserva);
  assert.equal(sinResolver.length, 2);
});

// ─── getAdvertenciaCapacidad ─────────────────────────────────────────────────

test("getAdvertenciaCapacidad: pax dentro de la capacidad → null (no hace falta avisar)", () => {
  assert.equal(getAdvertenciaCapacidad(2, { hotel: 4, transfer: 4, package: 4, total: 4 }), null);
});

test("getAdvertenciaCapacidad: pax supera el total → devuelve detalle", () => {
  const advertencia = getAdvertenciaCapacidad(5, { hotel: 4, transfer: 0, package: 0, total: 4 });
  assert.notEqual(advertencia, null);
  assert.deepEqual(advertencia.detalle, ["hotel para 4"]);
});

test("getAdvertenciaCapacidad: capacity como número plano (legacy) → se interpreta como capacidad de hotel", () => {
  const advertencia = getAdvertenciaCapacidad(5, 4);
  assert.notEqual(advertencia, null);
  assert.equal(advertencia.total, 4);
});

test("getAdvertenciaCapacidad: sin pasajeros cargados (paxCount=0) → null", () => {
  assert.equal(getAdvertenciaCapacidad(0, { hotel: 2, transfer: 0, package: 0, total: 2 }), null);
});

// ─── construirAvisosInformativos ─────────────────────────────────────────────

test("construirAvisosInformativos: sin ningún aviso → array vacío", () => {
  const reserva = { status: "Confirmed", passengers: [] };
  const claves = construirAvisosInformativos({ reserva, paxCount: 0, capacity: { hotel: 0, transfer: 0, package: 0, total: 0 } });
  assert.deepEqual(claves, []);
});

test("construirAvisosInformativos: solo servicios sin confirmar → un solo aviso", () => {
  const reserva = {
    status: "Confirmed",
    hotelBookings: [{ name: "Hotel Sur", workflowStatus: "PendingConfirmation" }],
  };
  const claves = construirAvisosInformativos({ reserva, paxCount: 2, capacity: { hotel: 4, transfer: 0, package: 0, total: 4 } });
  assert.deepEqual(claves, ["serviciosSinConfirmar"]);
});

test("construirAvisosInformativos: solo capacidad excedida → un solo aviso", () => {
  const reserva = { status: "Confirmed" };
  const claves = construirAvisosInformativos({ reserva, paxCount: 6, capacity: { hotel: 4, transfer: 0, package: 0, total: 4 } });
  assert.deepEqual(claves, ["capacidad"]);
});

test("construirAvisosInformativos: los dos avisos a la vez → array con ambas claves, en orden", () => {
  const reserva = {
    status: "Confirmed",
    hotelBookings: [{ name: "Hotel Sur", workflowStatus: "PendingConfirmation" }],
  };
  const claves = construirAvisosInformativos({ reserva, paxCount: 6, capacity: { hotel: 4, transfer: 0, package: 0, total: 4 } });
  assert.deepEqual(claves, ["serviciosSinConfirmar", "capacidad"]);
});

// ─── formatearContadorAvisos ──────────────────────────────────────────────────

test("formatearContadorAvisos: 1 → singular '1 aviso más'", () => {
  assert.equal(formatearContadorAvisos(1), "1 aviso más");
});

test("formatearContadorAvisos: 2 → plural '2 avisos más'", () => {
  assert.equal(formatearContadorAvisos(2), "2 avisos más");
});

test("formatearContadorAvisos: 5 → plural '5 avisos más'", () => {
  assert.equal(formatearContadorAvisos(5), "5 avisos más");
});
