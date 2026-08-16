/**
 * Tests de reservaDestinoFicha.js (Tanda 2 del rediseño de Reservas, regla P7).
 *
 * Cómo correr:
 *   node --test src/features/reservas/lib/reservaDestinoFicha.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";
import {
  listarDestinosDeServiciosCargados,
  armarLineaDestinoYPasajeros,
  armarAvisoPasajerosFaltantes,
} from "./reservaDestinoFicha.js";

test("sin servicios cargados → lista de destinos vacía", () => {
  assert.deepEqual(listarDestinosDeServiciosCargados({}), []);
  assert.deepEqual(listarDestinosDeServiciosCargados(null), []);
});

test("un hotel con ciudad → un destino", () => {
  const reserva = { hotelBookings: [{ city: "Mendoza" }] };
  assert.deepEqual(listarDestinosDeServiciosCargados(reserva), ["Mendoza"]);
});

test("vuelo + hotel en la misma ciudad → no se repite (sin distinguir mayus/minus)", () => {
  const reserva = {
    flightSegments: [{ destinationCity: "mendoza" }],
    hotelBookings: [{ city: "Mendoza" }],
  };
  assert.deepEqual(listarDestinosDeServiciosCargados(reserva), ["mendoza"]);
});

test("vuelo cancelado no aporta destino", () => {
  const reserva = {
    flightSegments: [{ destinationCity: "Bariloche", workflowStatus: "Cancelado" }],
  };
  assert.deepEqual(listarDestinosDeServiciosCargados(reserva), []);
});

test("hotel cancelado (campo status legacy) no aporta destino", () => {
  const reserva = {
    hotelBookings: [{ city: "Salta", status: "Cancelado" }],
  };
  assert.deepEqual(listarDestinosDeServiciosCargados(reserva), []);
});

test("paquete con destino cargado → un destino", () => {
  const reserva = { packageBookings: [{ destination: "Bariloche" }] };
  assert.deepEqual(listarDestinosDeServiciosCargados(reserva), ["Bariloche"]);
});

test("ciudad con espacios en blanco no cuenta como destino real", () => {
  const reserva = { hotelBookings: [{ city: "   " }] };
  assert.deepEqual(listarDestinosDeServiciosCargados(reserva), []);
});

test("armarLineaDestinoYPasajeros: con destino y 1 pasajero (singular)", () => {
  const reserva = { hotelBookings: [{ city: "Mendoza" }], passengers: [{}] };
  assert.equal(armarLineaDestinoYPasajeros(reserva), "Mendoza · 1 pasajero");
});

test("armarLineaDestinoYPasajeros: con destino y varios pasajeros (plural)", () => {
  const reserva = { hotelBookings: [{ city: "Mendoza" }], passengers: [{}, {}, {}] };
  assert.equal(armarLineaDestinoYPasajeros(reserva), "Mendoza · 3 pasajeros");
});

test("armarLineaDestinoYPasajeros: sin ningún destino → solo pasajeros (nunca inventa)", () => {
  const reserva = { passengers: [{}, {}] };
  assert.equal(armarLineaDestinoYPasajeros(reserva), "2 pasajeros");
});

test("armarLineaDestinoYPasajeros: sin pasajeros cargados → 0 pasajeros", () => {
  assert.equal(armarLineaDestinoYPasajeros({}), "0 pasajeros");
});

test("armarLineaDestinoYPasajeros: varios destinos distintos se unen con '·'", () => {
  const reserva = {
    flightSegments: [{ destinationCity: "Bariloche" }],
    hotelBookings: [{ city: "Mendoza" }],
    passengers: [{}],
  };
  assert.equal(armarLineaDestinoYPasajeros(reserva), "Bariloche · Mendoza · 1 pasajero");
});

// ─── Total DECLARADO (ADR-031) por sobre cargados (Tanda A UX 2026-08-16) ───

test("armarLineaDestinoYPasajeros: con declarado (adultCount+childCount+infantCount) > 0, prioriza el declarado sobre los cargados", () => {
  const reserva = {
    hotelBookings: [{ city: "Mendoza" }],
    adultCount: 2,
    childCount: 1,
    infantCount: 0,
    passengers: [{ fullName: "Titular" }], // solo el titular tiene nombre cargado todavía
  };
  assert.equal(armarLineaDestinoYPasajeros(reserva), "Mendoza · 3 pasajeros");
});

test("armarLineaDestinoYPasajeros: declarado en 0 (reserva vieja, sin ADR-031) cae al conteo de cargados", () => {
  const reserva = {
    hotelBookings: [{ city: "Mendoza" }],
    passengers: [{}, {}],
  };
  assert.equal(armarLineaDestinoYPasajeros(reserva), "Mendoza · 2 pasajeros");
});

// ─── armarAvisoPasajerosFaltantes ───────────────────────────────────────────

test("armarAvisoPasajerosFaltantes: declarado > cargados -> texto en plural", () => {
  const reserva = { adultCount: 2, childCount: 2, infantCount: 0, passengers: [{ fullName: "Titular" }] };
  assert.equal(armarAvisoPasajerosFaltantes(reserva), "Faltan cargar 3 pasajeros");
});

test("armarAvisoPasajerosFaltantes: falta exactamente 1 -> singular", () => {
  const reserva = { adultCount: 2, childCount: 0, infantCount: 0, passengers: [{ fullName: "Titular" }] };
  assert.equal(armarAvisoPasajerosFaltantes(reserva), "Falta cargar 1 pasajero");
});

test("armarAvisoPasajerosFaltantes: declarado y cargados coinciden -> null (nada que avisar)", () => {
  const reserva = { adultCount: 2, childCount: 0, infantCount: 0, passengers: [{}, {}] };
  assert.equal(armarAvisoPasajerosFaltantes(reserva), null);
});

test("armarAvisoPasajerosFaltantes: sin nada declarado (reserva vieja) -> null, no inventa un faltante", () => {
  assert.equal(armarAvisoPasajerosFaltantes({ passengers: [{}] }), null);
});
