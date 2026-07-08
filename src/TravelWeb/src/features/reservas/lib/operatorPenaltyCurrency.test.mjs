/**
 * Tests de lógica pura de la moneda sugerida para el mini-form de multa del operador.
 *
 * Cómo correr:
 *   node --test src/features/reservas/lib/operatorPenaltyCurrency.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";
import { monedaDeLaFacturaEmitida, elegirMonedaSugeridaParaMulta } from "./operatorPenaltyCurrency.js";

// ============================================================================
// monedaDeLaFacturaEmitida
// ============================================================================

test("monedaDeLaFacturaEmitida: una sola moneda facturada → esa moneda", () => {
  const porMoneda = [
    { currency: "ARS", facturadoNeto: 150000 },
    { currency: "USD", facturadoNeto: 0 },
  ];
  assert.equal(monedaDeLaFacturaEmitida(porMoneda), "ARS");
});

test("monedaDeLaFacturaEmitida: dos monedas facturadas a la vez → null (no adivinamos)", () => {
  const porMoneda = [
    { currency: "ARS", facturadoNeto: 150000 },
    { currency: "USD", facturadoNeto: 500 },
  ];
  assert.equal(monedaDeLaFacturaEmitida(porMoneda), null);
});

test("monedaDeLaFacturaEmitida: ninguna moneda facturada → null", () => {
  const porMoneda = [
    { currency: "ARS", facturadoNeto: 0 },
    { currency: "USD", facturadoNeto: 0 },
  ];
  assert.equal(monedaDeLaFacturaEmitida(porMoneda), null);
});

test("monedaDeLaFacturaEmitida: porMoneda ausente (DTO viejo) → null, no rompe", () => {
  assert.equal(monedaDeLaFacturaEmitida(undefined), null);
});

test("monedaDeLaFacturaEmitida: porMoneda vacío → null", () => {
  assert.equal(monedaDeLaFacturaEmitida([]), null);
});

// ============================================================================
// elegirMonedaSugeridaParaMulta
// ============================================================================

test("elegirMonedaSugeridaParaMulta: situacionCurrency presente → manda esa, sin mirar la factura", () => {
  const resultado = elegirMonedaSugeridaParaMulta({
    situacionCurrency: "USD",
    porMoneda: [{ currency: "ARS", facturadoNeto: 150000 }],
  });
  assert.equal(resultado, "USD");
});

test("elegirMonedaSugeridaParaMulta: sin situacionCurrency, factura en una sola moneda → esa moneda", () => {
  const resultado = elegirMonedaSugeridaParaMulta({
    situacionCurrency: null,
    porMoneda: [{ currency: "ARS", facturadoNeto: 150000 }],
  });
  assert.equal(resultado, "ARS");
});

test("elegirMonedaSugeridaParaMulta: sin situacionCurrency ni factura → undefined (cae al default del componente)", () => {
  const resultado = elegirMonedaSugeridaParaMulta({ situacionCurrency: null, porMoneda: [] });
  assert.equal(resultado, undefined);
});

test("elegirMonedaSugeridaParaMulta: sin situacionCurrency, reserva multimoneda facturada en las dos → undefined", () => {
  const resultado = elegirMonedaSugeridaParaMulta({
    situacionCurrency: undefined,
    porMoneda: [
      { currency: "ARS", facturadoNeto: 150000 },
      { currency: "USD", facturadoNeto: 500 },
    ],
  });
  assert.equal(resultado, undefined);
});
