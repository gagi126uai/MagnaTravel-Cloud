/**
 * Tests de lógica pura de la Tanda P3 (spec
 * docs/ux/2026-07-22-p3-confirmar-costo-debajo-de-lo-pagado.md):
 * "confirmar que bajar el costo del operador genera saldo a favor".
 *
 * Corren con Node puro sin bundler: node --test src/features/reservas/lib/costConfirmationGuard.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

import {
  esRechazoCostoMenorAPagado,
  agregarConfirmacionCostoMenorAPagado,
  CODIGO_CONFIRMAR_COSTO_MENOR_A_PAGADO,
} from "./costConfirmationGuard.js";

const TEXTO_MOTOR =
  "Esto va a generar $ 45.000 de saldo a favor con este operador. ¿Confirmás?";

// ─── esRechazoCostoMenorAPagado: detección del 409 puntual (aviso ámbar, no error rojo) ───

test("code COST_BELOW_PAID_CONFIRMATION_REQUIRED → es el caso de confirmación", () => {
  const error = { status: 409, payload: { code: CODIGO_CONFIRMAR_COSTO_MENOR_A_PAGADO, message: TEXTO_MOTOR } };

  assert.equal(esRechazoCostoMenorAPagado(error), true);
});

test("409 con otro code (ej. rechazo de anular servicio) → NO es este caso, cae al cartel rojo", () => {
  const error = { status: 409, payload: { code: "CANCEL_SERVICE_UNANCHORED_OPERATOR_REFUND", message: "otro motivo" } };

  assert.equal(esRechazoCostoMenorAPagado(error), false);
});

test("409 sin code (rechazo genérico) → NO es este caso", () => {
  const error = { status: 409, payload: { message: "Algo salió mal." } };

  assert.equal(esRechazoCostoMenorAPagado(error), false);
});

test("error sin payload (network/parse) → NO explota, NO es este caso", () => {
  assert.equal(esRechazoCostoMenorAPagado({ status: 500 }), false);
  assert.equal(esRechazoCostoMenorAPagado(null), false);
  assert.equal(esRechazoCostoMenorAPagado(undefined), false);
});

test("code parecido pero no exacto (typo) → NO es este caso (comparación exacta)", () => {
  const error = { payload: { code: "cost_below_paid_confirmation_required" } };

  assert.equal(esRechazoCostoMenorAPagado(error), false);
});

// ─── agregarConfirmacionCostoMenorAPagado: reenvío del MISMO payload + la marca ───────────

test("agrega confirmCostBelowPaid:true sin tocar los demás campos del payload", () => {
  const payloadOriginal = { hotelName: "Hotel Maitei", netCost: 15000, salePrice: 40000 };

  const resultado = agregarConfirmacionCostoMenorAPagado(payloadOriginal);

  assert.deepEqual(resultado, {
    hotelName: "Hotel Maitei",
    netCost: 15000,
    salePrice: 40000,
    confirmCostBelowPaid: true,
  });
});

test("no muta el payload original (el guardado normal debe poder reintentarse sin la marca)", () => {
  const payloadOriginal = { netCost: 15000 };

  agregarConfirmacionCostoMenorAPagado(payloadOriginal);

  assert.deepEqual(payloadOriginal, { netCost: 15000 });
});

test("payload null/undefined → no explota, devuelve solo la marca", () => {
  assert.deepEqual(agregarConfirmacionCostoMenorAPagado(null), { confirmCostBelowPaid: true });
  assert.deepEqual(agregarConfirmacionCostoMenorAPagado(undefined), { confirmCostBelowPaid: true });
});
