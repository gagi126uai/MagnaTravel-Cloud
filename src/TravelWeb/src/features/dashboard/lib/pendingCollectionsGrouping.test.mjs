/**
 * Tests de pendingCollectionsGrouping.js.
 * Cómo correr: node --test src/features/dashboard/lib/pendingCollectionsGrouping.test.mjs
 */
import test from "node:test";
import assert from "node:assert/strict";

import { agruparCobrosPendientesPorReserva } from "./pendingCollectionsGrouping.js";

test("una reserva con deuda en ARS y USD (dos filas del backend) -> una sola fila con dos líneas", () => {
  const grupos = agruparCobrosPendientesPorReserva([
    { publicId: "r1042", numeroReserva: "R-1042", name: "María Pérez", balance: 450000, currency: "ARS" },
    { publicId: "r1042", numeroReserva: "R-1042", name: "María Pérez", balance: 200, currency: "USD" },
  ]);
  assert.equal(grupos.length, 1);
  assert.equal(grupos[0].publicId, "r1042");
  assert.deepEqual(grupos[0].lineas, [
    { currency: "ARS", amount: 450000 },
    { currency: "USD", amount: 200 },
  ]);
});

test("reservas distintas -> una fila por reserva, en orden de aparición", () => {
  const grupos = agruparCobrosPendientesPorReserva([
    { publicId: "r1042", numeroReserva: "R-1042", name: "María Pérez", balance: 450000, currency: "ARS" },
    { publicId: "r1050", numeroReserva: "R-1050", name: "Juan Gómez", balance: 200, currency: "USD" },
  ]);
  assert.equal(grupos.length, 2);
  assert.equal(grupos[0].publicId, "r1042");
  assert.equal(grupos[1].publicId, "r1050");
});

test("lista vacía o undefined -> sin grupos", () => {
  assert.deepEqual(agruparCobrosPendientesPorReserva([]), []);
  assert.deepEqual(agruparCobrosPendientesPorReserva(undefined), []);
});

test("fila sin publicId se descarta (dato roto, no se inventa una fila)", () => {
  const grupos = agruparCobrosPendientesPorReserva([
    { numeroReserva: "R-1042", name: "María Pérez", balance: 450000, currency: "ARS" },
  ]);
  assert.deepEqual(grupos, []);
});
