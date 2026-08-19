/**
 * Tests de cashflowRhythmSeries.js.
 * Cómo correr: node --test src/features/dashboard/lib/cashflowRhythmSeries.test.mjs
 */
import test from "node:test";
import assert from "node:assert/strict";

import { armarSeriesRitmoCobrosPagos } from "./cashflowRhythmSeries.js";

function diaArs({ cashIn = 0, cashOut = 0 } = {}) {
  return {
    cashInByCurrency: cashIn ? [{ currency: "ARS", amount: cashIn }] : [],
    cashOutByCurrency: cashOut ? [{ currency: "ARS", amount: cashOut }] : [],
  };
}

test("sin movimiento real en los 30 días históricos -> hayMovimiento false (estado vacío)", () => {
  const historical = Array.from({ length: 31 }, () => diaArs());
  const projected = Array.from({ length: 90 }, () => diaArs());
  const resultado = armarSeriesRitmoCobrosPagos({ historical, projected });
  assert.equal(resultado.hayMovimiento, false);
  assert.deepEqual(resultado.monedas, []);
});

test("con movimiento en ARS -> arma una serie ARS con un punto por día", () => {
  const historical = Array.from({ length: 31 }, (_, i) => diaArs({ cashIn: i === 30 ? 1000 : 0 }));
  const projected = Array.from({ length: 90 }, () => diaArs({ cashIn: 33.33 }));
  const resultado = armarSeriesRitmoCobrosPagos({ historical, projected });
  assert.equal(resultado.hayMovimiento, true);
  assert.equal(resultado.monedas.length, 1);
  assert.equal(resultado.monedas[0].currency, "ARS");
  assert.equal(resultado.monedas[0].puntos.length, 31 + 90);
});

test("dos monedas con movimiento -> DOS series separadas, ARS primero (P-3, nunca se mezclan)", () => {
  const historical = Array.from({ length: 31 }, (_, i) => ({
    cashInByCurrency: i === 30 ? [{ currency: "USD", amount: 200 }, { currency: "ARS", amount: 1000 }] : [],
    cashOutByCurrency: [],
  }));
  const projected = [];
  const resultado = armarSeriesRitmoCobrosPagos({ historical, projected });
  assert.deepEqual(resultado.monedas.map((m) => m.currency), ["ARS", "USD"]);
});

test("ejeXTicks: Hoy es el último índice histórico, +30/+60/+90 se calculan desde ahí", () => {
  const historical = Array.from({ length: 31 }, (_, i) => diaArs({ cashIn: i === 30 ? 1000 : 0 }));
  const projected = Array.from({ length: 90 }, () => diaArs({ cashIn: 33 }));
  const resultado = armarSeriesRitmoCobrosPagos({ historical, projected });
  // historical.length = 31 -> índice de "hoy" = 30.
  assert.deepEqual(resultado.ejeXTicks, [
    { x: 30, etiqueta: "Hoy" },
    { x: 60, etiqueta: "+30" },
    { x: 90, etiqueta: "+60" },
    { x: 120, etiqueta: "+90" },
  ]);
});

test("pagos vacíos (sin cobranzas.see_cost, backend manda la lista vacía) -> puntos.pagos siempre 0", () => {
  const historical = Array.from({ length: 31 }, (_, i) => diaArs({ cashIn: i === 30 ? 1000 : 0 }));
  const resultado = armarSeriesRitmoCobrosPagos({ historical, projected: [] });
  assert.ok(resultado.monedas[0].puntos.every((p) => p.pagos === 0));
});

test("cashflow undefined/vacío no rompe -> estado vacío", () => {
  assert.deepEqual(armarSeriesRitmoCobrosPagos(undefined), { hayMovimiento: false, monedas: [], ejeXTicks: [] });
  assert.deepEqual(armarSeriesRitmoCobrosPagos({}), { hayMovimiento: false, monedas: [], ejeXTicks: [] });
});
