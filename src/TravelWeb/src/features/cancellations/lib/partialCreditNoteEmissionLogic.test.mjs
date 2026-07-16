import test from "node:test";
import assert from "node:assert/strict";
import {
  T5_STATE,
  getActiveSaleInvoices,
  getT5InvariantCode,
  resolvePartialCreditNoteEmissionState,
  t5ErrorMessage,
} from "./partialCreditNoteEmissionLogic.js";
// Se usa para construir el label ESPERADO dinámicamente en vez de escribirlo a mano
// en el test: Intl.NumberFormat("es-AR", { style: "currency" }) inserta un espacio
// "no separable" (U+00A0) entre "$" y el número, no un espacio común — armarlo a
// mano en el string del test es una fuente fácil de typos invisibles.
import { formatCurrency } from "../../../lib/utils.js";

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

// ─── Tests: label enriquecido con moneda + monto (2026-07-16) ────────────────
// InvoiceDto.Currency ya viene del backend (ISO "ARS"/"USD", default "ARS" para
// facturas legacy). El label usa el mismo formatCurrency que
// construirOpcionesFacturaDestino (facturaDestinoLogic.js) para que ambos
// desplegables muestren la plata exactamente igual.

test("getActiveSaleInvoices: ARS → label = número + formatCurrency(monto, 'ARS') (idéntico a construirOpcionesFacturaDestino)", () => {
  const rows = getActiveSaleInvoices([
    {
      publicId: "f1",
      tipoComprobante: 1,
      resultado: "A",
      cae: "1",
      invoiceType: "A",
      puntoDeVenta: 1,
      numeroComprobante: 51,
      importeTotal: 125000.5,
      currency: "ARS",
    },
  ]);
  assert.equal(rows.length, 1);
  assert.equal(rows[0].label, `Factura A 0001-00000051 — ${formatCurrency(125000.5, "ARS")}`);
  assert.ok(rows[0].label.includes("$"), "Debe llevar el símbolo de pesos");
  assert.equal(rows[0].amount, 125000.5);
  assert.equal(rows[0].currency, "ARS");
});

test("getActiveSaleInvoices: USD → label = número + formatCurrency(monto, 'USD') con 'US$' (idéntico a construirOpcionesFacturaDestino)", () => {
  const rows = getActiveSaleInvoices([
    {
      publicId: "f1",
      tipoComprobante: 6,
      resultado: "A",
      cae: "1",
      invoiceType: "B",
      puntoDeVenta: 1,
      numeroComprobante: 3,
      importeTotal: 500,
      currency: "USD",
    },
  ]);
  assert.equal(rows[0].label, `Factura B 0001-00000003 — ${formatCurrency(500, "USD")}`);
  assert.ok(rows[0].label.includes("US$"), "El dólar se distingue con 'US$', nunca solo '$'");
  assert.equal(rows[0].currency, "USD");
});

test("getActiveSaleInvoices: sin currency (factura legacy) → cae a ARS, igual que hace el backend por default", () => {
  const rows = getActiveSaleInvoices([
    { publicId: "f1", tipoComprobante: 1, resultado: "A", cae: "1", invoiceType: "C", puntoDeVenta: 1, numeroComprobante: 1, importeTotal: 1000 },
  ]);
  assert.equal(rows[0].currency, "ARS");
  assert.equal(rows[0].label, `Factura C 0001-00000001 — ${formatCurrency(1000, "ARS")}`);
});

test("getActiveSaleInvoices: importeTotal ausente → monto $0,00 (no revienta)", () => {
  const rows = getActiveSaleInvoices([
    { publicId: "f1", tipoComprobante: 1, resultado: "A", cae: "1", invoiceType: "C", puntoDeVenta: 1, numeroComprobante: 1, currency: "ARS" },
  ]);
  assert.equal(rows[0].label, `Factura C 0001-00000001 — ${formatCurrency(0, "ARS")}`);
});

test("getActiveSaleInvoices: expone servicePublicIds de la factura para la sugerencia de cancelación", () => {
  const rows = getActiveSaleInvoices([
    {
      publicId: "f1",
      tipoComprobante: 1,
      resultado: "A",
      cae: "1",
      servicePublicIds: ["srv-1", "srv-2"],
    },
  ]);
  assert.deepEqual(rows[0].servicePublicIds, ["srv-1", "srv-2"]);
});

test("getActiveSaleInvoices: factura vieja sin servicePublicIds → array vacío, no undefined", () => {
  const rows = getActiveSaleInvoices([
    { publicId: "f1", tipoComprobante: 1, resultado: "A", cae: "1" },
  ]);
  assert.deepEqual(rows[0].servicePublicIds, []);
});
