import test from "node:test";
import assert from "node:assert/strict";
import { sugerirFacturaParaServicios } from "./serviceInvoiceMatch.js";

const facturaUnica = { publicId: "fac-1", servicePublicIds: ["srv-1", "srv-2"] };
const facturaOtra = { publicId: "fac-2", servicePublicIds: ["srv-3"] };
const facturaVieja = { publicId: "fac-3" }; // sin trazabilidad (sin servicePublicIds)

test("un solo servicio, aparece en exactamente una factura → la sugiere", () => {
  const resultado = sugerirFacturaParaServicios(["srv-1"], [facturaUnica, facturaOtra]);
  assert.equal(resultado, "fac-1");
});

test("un solo servicio, no aparece en ninguna factura → null", () => {
  const resultado = sugerirFacturaParaServicios(["srv-99"], [facturaUnica, facturaOtra]);
  assert.equal(resultado, null);
});

test("un solo servicio, aparece en dos facturas (caso raro pero defensivo) → null, ambiguo", () => {
  const facturaDuplicada = { publicId: "fac-4", servicePublicIds: ["srv-1"] };
  const resultado = sugerirFacturaParaServicios(["srv-1"], [facturaUnica, facturaDuplicada]);
  assert.equal(resultado, null);
});

test("varios servicios, TODOS en la misma única factura → la sugiere", () => {
  const resultado = sugerirFacturaParaServicios(["srv-1", "srv-2"], [facturaUnica, facturaOtra]);
  assert.equal(resultado, "fac-1");
});

test("varios servicios repartidos en facturas distintas → null (no se sugiere partir la devolución)", () => {
  const resultado = sugerirFacturaParaServicios(["srv-1", "srv-3"], [facturaUnica, facturaOtra]);
  assert.equal(resultado, null);
});

test("factura vieja sin servicePublicIds (trazabilidad ausente) → nunca matchea, no revienta", () => {
  const resultado = sugerirFacturaParaServicios(["srv-1"], [facturaVieja]);
  assert.equal(resultado, null);
});

test("lista de servicios vacía → null (nada que sugerir)", () => {
  const resultado = sugerirFacturaParaServicios([], [facturaUnica]);
  assert.equal(resultado, null);
});

test("lista de facturas vacía → null", () => {
  const resultado = sugerirFacturaParaServicios(["srv-1"], []);
  assert.equal(resultado, null);
});

test("inputs null/undefined → null, no lanza excepción", () => {
  assert.equal(sugerirFacturaParaServicios(null, null), null);
  assert.equal(sugerirFacturaParaServicios(undefined, [facturaUnica]), null);
  assert.equal(sugerirFacturaParaServicios(["srv-1"], undefined), null);
});

test("servicePublicIds con valores falsy (null/undefined) se ignoran, no rompen la comparación", () => {
  const resultado = sugerirFacturaParaServicios(["srv-1", null, undefined], [facturaUnica]);
  assert.equal(resultado, "fac-1");
});

test("único servicio buscado, única factura activa que lo contiene entre varias sin ese servicio → la sugiere", () => {
  const facturaSinNada = { publicId: "fac-5", servicePublicIds: [] };
  const resultado = sugerirFacturaParaServicios(["srv-2"], [facturaSinNada, facturaUnica, facturaOtra]);
  assert.equal(resultado, "fac-1");
});
