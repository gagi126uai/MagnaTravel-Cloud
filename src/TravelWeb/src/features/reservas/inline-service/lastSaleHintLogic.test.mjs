import { describe, it } from "node:test";
import assert from "node:assert/strict";
import { buildLastSaleHintText } from "./lastSaleHintLogic.js";

// ─── buildLastSaleHintText (spec firmada 2026-08-06, §3.2 / P9=A) ─────────────────────
// Renglón gris "Último precio: operador · precio · fecha" bajo el campo precargado.

describe("buildLastSaleHintText", () => {
  it("sin lastSale (producto nuevo o solo con rateFallback): no hay renglón que mostrar", () => {
    assert.equal(buildLastSaleHintText({ rateFallback: { salePrice: 100 } }, { canSeeCost: true }), null);
    assert.equal(buildLastSaleHintText({}, { canSeeCost: true }), null);
    assert.equal(buildLastSaleHintText(null, { canSeeCost: true }), null);
  });

  it("con permiso de costos: usa el costo (netCost) de la última venta", () => {
    const catalogResult = {
      lastSale: {
        supplierName: "Ola Mayorista",
        netCost: 48000,
        salePrice: 60000,
        currency: "ARS",
        soldAt: "2026-05-22T14:00:00Z",
      },
    };
    const texto = buildLastSaleHintText(catalogResult, { canSeeCost: true });
    assert.match(texto, /Ola Mayorista/);
    assert.match(texto, /48\.000/); // usa el costo, no la venta
    assert.match(texto, /22\/05\/2026/);
  });

  it("sin permiso de costos: usa el precio de VENTA, nunca el costo (F-14)", () => {
    const catalogResult = {
      lastSale: {
        supplierName: "Ola Mayorista",
        netCost: 48000,
        salePrice: 60000,
        currency: "ARS",
        soldAt: "2026-05-22T14:00:00Z",
      },
    };
    const texto = buildLastSaleHintText(catalogResult, { canSeeCost: false });
    assert.match(texto, /60\.000/);
    assert.doesNotMatch(texto, /48\.000/);
  });

  it("caller sin permiso de costos: netCost viene null del backend, igual arma la línea con la venta", () => {
    const catalogResult = {
      lastSale: {
        supplierName: "Julia Tours",
        netCost: null,
        salePrice: 39000,
        currency: "USD",
        soldAt: "2026-06-01T00:00:00Z",
      },
    };
    const texto = buildLastSaleHintText(catalogResult, { canSeeCost: false });
    assert.match(texto, /Julia Tours/);
    assert.match(texto, /US\$/);
  });

  it("sin fecha de venta: arma la línea igual, sin la parte de fecha", () => {
    const catalogResult = {
      lastSale: { supplierName: "Aeromundo", netCost: 500, salePrice: 700, currency: "USD", soldAt: null },
    };
    const texto = buildLastSaleHintText(catalogResult, { canSeeCost: true });
    assert.equal(texto, `Aeromundo · US$500,00`);
  });

  it("sin monto disponible (ni costo ni venta): no hay nada que mostrar", () => {
    const catalogResult = { lastSale: { supplierName: "Aeromundo", netCost: null, salePrice: null, currency: "USD" } };
    assert.equal(buildLastSaleHintText(catalogResult, { canSeeCost: true }), null);
  });
});
