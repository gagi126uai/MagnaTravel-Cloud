import { test } from "node:test";
import assert from "node:assert/strict";
import {
  hayFacturaDestinoAmbigua,
  construirOpcionesFacturaDestino,
  resolverMonedaFacturaDestino,
  debeMostrarRecuadroTipoCambio,
  facturaDestinoResuelta,
} from "./facturaDestinoLogic.js";

test("hayFacturaDestinoAmbigua: false con 0 o 1 factura, true con 2+", () => {
  assert.equal(hayFacturaDestinoAmbigua([]), false);
  assert.equal(hayFacturaDestinoAmbigua([{ currency: "USD" }]), false);
  assert.equal(hayFacturaDestinoAmbigua([{ currency: "USD" }, { currency: "ARS" }]), true);
});

test("construirOpcionesFacturaDestino: usa el publicId real como value, formatea el label con moneda+monto", () => {
  const saleInvoices = [
    { publicId: "inv-1", comprobanteLabel: "Factura B 0001-00012345", currency: "USD", amount: 200 },
    { publicId: "inv-2", comprobanteLabel: "Factura B 0001-00012346", currency: "ARS", amount: 150000 },
  ];
  const opciones = construirOpcionesFacturaDestino(saleInvoices);
  assert.equal(opciones.length, 2);
  assert.equal(opciones[0].value, "inv-1");
  assert.ok(opciones[0].label.includes("Factura B 0001-00012345"));
  assert.ok(opciones[0].label.includes("US$"));
  assert.equal(opciones[1].value, "inv-2");
  assert.ok(opciones[1].label.includes("Factura B 0001-00012346"));
});

test("construirOpcionesFacturaDestino: lista ausente no rompe", () => {
  assert.deepEqual(construirOpcionesFacturaDestino(null), []);
  assert.deepEqual(construirOpcionesFacturaDestino(undefined), []);
});

test("resolverMonedaFacturaDestino: con 1 sola factura se autocompleta sola, sin elegir nada", () => {
  const saleInvoices = [{ publicId: "inv-1", currency: "USD" }];
  assert.equal(resolverMonedaFacturaDestino(saleInvoices, null), "USD");
  assert.equal(resolverMonedaFacturaDestino(saleInvoices, undefined), "USD");
});

test("resolverMonedaFacturaDestino: con 2+ facturas, null hasta que se elija una", () => {
  const saleInvoices = [{ publicId: "inv-1", currency: "USD" }, { publicId: "inv-2", currency: "ARS" }];
  assert.equal(resolverMonedaFacturaDestino(saleInvoices, null), null);
  assert.equal(resolverMonedaFacturaDestino(saleInvoices, "inv-2"), "ARS");
  assert.equal(resolverMonedaFacturaDestino(saleInvoices, "inv-inexistente"), null);
});

test("resolverMonedaFacturaDestino: sin facturas → null (defensivo)", () => {
  assert.equal(resolverMonedaFacturaDestino([], "algo"), null);
  assert.equal(resolverMonedaFacturaDestino(null, "algo"), null);
});

test("debeMostrarRecuadroTipoCambio: aparece solo si cruza de moneda con la factura ya resuelta", () => {
  assert.equal(debeMostrarRecuadroTipoCambio("USD", "USD"), false);
  assert.equal(debeMostrarRecuadroTipoCambio("USD", "ARS"), true);
  assert.equal(debeMostrarRecuadroTipoCambio("USD", null), false, "sin factura resuelta todavía, no se muestra");
});

test("facturaDestinoResuelta: con 1 factura siempre resuelta (no hay que elegir nada)", () => {
  assert.equal(facturaDestinoResuelta([{ currency: "USD" }], null), true);
});

test("facturaDestinoResuelta: con 2+ facturas, requiere que targetInvoicePublicId venga cargado", () => {
  const saleInvoices = [{ currency: "USD" }, { currency: "ARS" }];
  assert.equal(facturaDestinoResuelta(saleInvoices, null), false);
  assert.equal(facturaDestinoResuelta(saleInvoices, "inv-1"), true);
});
