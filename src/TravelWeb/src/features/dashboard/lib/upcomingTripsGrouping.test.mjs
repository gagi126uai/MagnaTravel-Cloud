/**
 * Tests de upcomingTripsGrouping.js.
 * Cómo correr: node --test src/features/dashboard/lib/upcomingTripsGrouping.test.mjs
 */
import test from "node:test";
import assert from "node:assert/strict";

import { etiquetaDeDia, agruparSalidasPorDia, armarChipDeudaSalida } from "./upcomingTripsGrouping.js";

// ─── etiquetaDeDia ──────────────────────────────────────────────────────────

test("etiquetaDeDia: fecha-solo-día ISO -> 'Lun 24/08'", () => {
  // 2026-08-24 es lunes.
  const resultado = etiquetaDeDia("2026-08-24T00:00:00Z");
  assert.equal(resultado.clave, "2026-08-24");
  assert.equal(resultado.etiqueta, "Lun 24/08");
});

test("etiquetaDeDia: texto vacío o roto -> null, nunca una fecha inventada", () => {
  assert.equal(etiquetaDeDia(""), null);
  assert.equal(etiquetaDeDia(null), null);
  assert.equal(etiquetaDeDia("no-es-una-fecha"), null);
});

// ─── agruparSalidasPorDia ───────────────────────────────────────────────────

test("agruparSalidasPorDia: dos viajes el mismo día -> un solo grupo con dos viajes", () => {
  const grupos = agruparSalidasPorDia([
    { numeroReserva: "R-1042", startDate: "2026-08-24T00:00:00Z" },
    { numeroReserva: "R-1050", startDate: "2026-08-24T00:00:00Z" },
  ]);
  assert.equal(grupos.length, 1);
  assert.equal(grupos[0].etiqueta, "Lun 24/08");
  assert.equal(grupos[0].viajes.length, 2);
});

test("agruparSalidasPorDia: días distintos -> un grupo por día, en el orden en que llegan", () => {
  const grupos = agruparSalidasPorDia([
    { numeroReserva: "R-1042", startDate: "2026-08-24T00:00:00Z" },
    { numeroReserva: "R-1050", startDate: "2026-08-25T00:00:00Z" },
  ]);
  assert.equal(grupos.length, 2);
  assert.equal(grupos[0].etiqueta, "Lun 24/08");
  assert.equal(grupos[1].etiqueta, "Mar 25/08");
});

test("agruparSalidasPorDia: lista vacía o undefined -> sin grupos", () => {
  assert.deepEqual(agruparSalidasPorDia([]), []);
  assert.deepEqual(agruparSalidasPorDia(undefined), []);
});

// ─── armarChipDeudaSalida ───────────────────────────────────────────────────

test("armarChipDeudaSalida: sin PendingBalances -> chip verde 'Saldada'", () => {
  const chip = armarChipDeudaSalida([]);
  assert.equal(chip.tone, "success");
  assert.deepEqual(chip.lineas, []);
});

test("armarChipDeudaSalida: con deuda en una moneda -> chip rojo con esa línea", () => {
  const chip = armarChipDeudaSalida([{ currency: "USD", amount: 200 }]);
  assert.equal(chip.tone, "danger");
  assert.deepEqual(chip.lineas, [{ currency: "USD", amount: 200 }]);
});

test("armarChipDeudaSalida: deuda en dos monedas -> dos líneas, NUNCA sumadas (P-3)", () => {
  const chip = armarChipDeudaSalida([
    { currency: "ARS", amount: 50000 },
    { currency: "USD", amount: 200 },
  ]);
  assert.equal(chip.tone, "danger");
  assert.equal(chip.lineas.length, 2);
});

test("armarChipDeudaSalida: línea en $0 se ignora (no es deuda real)", () => {
  const chip = armarChipDeudaSalida([{ currency: "ARS", amount: 0 }]);
  assert.equal(chip.tone, "success");
});
