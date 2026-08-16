/**
 * Tests de ventaPorMonedaFicha.js (decisión del dueño, 2026-08-16: "Total del
 * viaje" y "Por persona" a la vista en la ficha del presupuesto).
 *
 * Cómo correr:
 *   node --test src/features/reservas/lib/ventaPorMonedaFicha.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";
import {
  armarLineasVentaPorMoneda,
  debeMostrarAvisoSinPasajerosDeclarados,
} from "./ventaPorMonedaFicha.js";

test("Budget con 1 moneda y pasajeros declarados → una línea con total y por persona", () => {
  const reserva = {
    status: "Budget",
    ventaPorMoneda: [{ currency: "ARS", total: 400000, perPerson: 100000 }],
  };
  assert.deepEqual(armarLineasVentaPorMoneda(reserva), [
    { currency: "ARS", total: 400000, perPerson: 100000 },
  ]);
});

test("Budget con 2 monedas → una línea por moneda, nunca mezcladas (P-3)", () => {
  const reserva = {
    status: "Budget",
    ventaPorMoneda: [
      { currency: "ARS", total: 400000, perPerson: 100000 },
      { currency: "USD", total: 800, perPerson: 200 },
    ],
  };
  const lineas = armarLineasVentaPorMoneda(reserva);
  assert.equal(lineas.length, 2);
  assert.equal(lineas[0].currency, "ARS");
  assert.equal(lineas[1].currency, "USD");
});

test("perPerson null (sin pasajeros declarados) → se conserva null, no se inventa un número", () => {
  const reserva = {
    status: "Budget",
    ventaPorMoneda: [{ currency: "ARS", total: 400000, perPerson: null }],
  };
  const lineas = armarLineasVentaPorMoneda(reserva);
  assert.equal(lineas[0].perPerson, null);
  assert.equal(debeMostrarAvisoSinPasajerosDeclarados(lineas), true);
});

test("ventaPorMoneda ausente (API vieja cacheada) → no rompe, devuelve null", () => {
  const reserva = { status: "Budget" };
  assert.equal(armarLineasVentaPorMoneda(reserva), null);
});

test("ventaPorMoneda vacía → devuelve null, no se muestra nada", () => {
  const reserva = { status: "Budget", ventaPorMoneda: [] };
  assert.equal(armarLineasVentaPorMoneda(reserva), null);
});

test("status distinto de Budget → nunca se muestra, aunque venga ventaPorMoneda", () => {
  const reserva = {
    status: "InManagement",
    ventaPorMoneda: [{ currency: "ARS", total: 400000, perPerson: 100000 }],
  };
  assert.equal(armarLineasVentaPorMoneda(reserva), null);
});

test("reserva null/undefined → no rompe", () => {
  assert.equal(armarLineasVentaPorMoneda(null), null);
  assert.equal(armarLineasVentaPorMoneda(undefined), null);
});

test("debeMostrarAvisoSinPasajerosDeclarados: con al menos una moneda con perPerson → false", () => {
  const lineas = [
    { currency: "ARS", total: 400000, perPerson: 100000 },
    { currency: "USD", total: 800, perPerson: null },
  ];
  // Caso defensivo (no debería pasar en la práctica: perPerson es un dato de TODA
  // la reserva, no por moneda) — igual se cubre para que la función no mienta.
  assert.equal(debeMostrarAvisoSinPasajerosDeclarados(lineas), false);
});

test("debeMostrarAvisoSinPasajerosDeclarados: lista vacía o null → false", () => {
  assert.equal(debeMostrarAvisoSinPasajerosDeclarados([]), false);
  assert.equal(debeMostrarAvisoSinPasajerosDeclarados(null), false);
});
