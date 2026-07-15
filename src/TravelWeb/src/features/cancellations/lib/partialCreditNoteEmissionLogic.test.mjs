import test from "node:test";
import assert from "node:assert/strict";
import {
  T5_STATE,
  getActiveSaleInvoices,
  getT5InvariantCode,
  resolvePartialCreditNoteEmissionState,
  t5ErrorMessage,
} from "./partialCreditNoteEmissionLogic.js";

const resolved = { partialCreditNoteEmission: { isResolved: true } };

test("T5 prioriza estados fiscales de la NC y no vuelve a ofrecer emitir", () => {
  assert.equal(resolvePartialCreditNoteEmissionState({ ...resolved, creditNotes: [{ status: "Pending" }] }), T5_STATE.PROCESSING);
  assert.equal(resolvePartialCreditNoteEmissionState({ ...resolved, creditNotes: [{ status: "Succeeded" }] }), T5_STATE.SUCCEEDED);
  assert.equal(resolvePartialCreditNoteEmissionState({ ...resolved, creditNotes: [{ status: "Failed" }] }), T5_STATE.FAILED);
});

test("T5 bloquea datos ambiguos/RI y habilita solo un snapshot resuelto", () => {
  assert.equal(resolvePartialCreditNoteEmissionState({ partialCreditNoteEmission: { isResolved: false } }), T5_STATE.BLOCKED);
  assert.equal(resolvePartialCreditNoteEmissionState({ partialCreditNoteEmission: { isResolved: true, requiresAccountantSignoffForRi: true } }), T5_STATE.BLOCKED);
  assert.equal(resolvePartialCreditNoteEmissionState(resolved), T5_STATE.READY);
});

test("errores T5 aceptan el shape del middleware de invariantes", () => {
  const error = { payload: { invariantCode: "INV-T5-EMIT-CAP" } };
  assert.equal(getT5InvariantCode(error), "INV-T5-EMIT-CAP");
  assert.equal(t5ErrorMessage(error), "El saldo de la factura cambió; revisá el monto.");
});

test("selector conserva solo facturas de venta activas con CAE", () => {
  const rows = getActiveSaleInvoices([
    { publicId: "ok", tipoComprobante: 6, resultado: "A", cae: "1", puntoDeVenta: 2, numeroComprobante: 3 },
    { publicId: "nc", tipoComprobante: 8, resultado: "A", cae: "2" },
    { publicId: "annulled", tipoComprobante: 6, resultado: "A", cae: "3", annulmentStatus: "Succeeded" },
  ]);
  assert.deepEqual(rows.map((x) => x.publicId), ["ok"]);
  assert.match(rows[0].label, /0002-00000003/);
});
