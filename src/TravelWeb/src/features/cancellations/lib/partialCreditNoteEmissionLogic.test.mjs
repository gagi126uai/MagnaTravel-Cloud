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

// ─── Spec UX 2026-07-17 (T5 "resolver devoluciones VIEJAS"): con 2+ renglones (Lines), el panel
// se queda en la vista de LISTA (BLOCKED) mientras falte resolver o emitir CUALQUIER fila — sin
// importar el estado de la última nota de crédito del array plano `creditNotes`, que podría ser
// la de OTRA fila que ya terminó (el caso real de Gastón: hotel USD + excursión ARS).

test("T5 con 2+ líneas: se queda BLOCKED aunque la ÚLTIMA nota de crédito del array ya esté Succeeded", () => {
  const cancellation = {
    partialCreditNoteEmission: {
      isResolved: true,
      lines: [
        { linePublicId: "l1", creditNoteStatus: "Succeeded" },
        { linePublicId: "l2", creditNoteStatus: null }, // esta fila todavía no se resolvió/emitió
      ],
    },
    creditNotes: [{ status: "Succeeded" }], // la NC de la fila 1, ya emitida
  };
  assert.equal(resolvePartialCreditNoteEmissionState(cancellation), T5_STATE.BLOCKED);
});

test("T5 con 2+ líneas: pasa a SUCCEEDED recién cuando TODAS las filas quedaron Succeeded", () => {
  const cancellation = {
    partialCreditNoteEmission: {
      isResolved: true,
      lines: [
        { linePublicId: "l1", creditNoteStatus: "Succeeded" },
        { linePublicId: "l2", creditNoteStatus: "Succeeded" },
      ],
    },
    creditNotes: [{ status: "Succeeded" }, { status: "Succeeded" }],
  };
  assert.equal(resolvePartialCreditNoteEmissionState(cancellation), T5_STATE.SUCCEEDED);
});

test("T5 con 0 o 1 línea (DTO legacy sin Lines, o un solo pendiente): sigue el comportamiento de siempre", () => {
  // Con una sola línea (o ninguna), NO aplicamos el atajo de "2+" — se sigue el camino de
  // siempre mirando la última NC del array plano, igual que antes de esta obra.
  assert.equal(
    resolvePartialCreditNoteEmissionState({ ...resolved, creditNotes: [{ status: "Succeeded" }], partialCreditNoteEmission: { ...resolved.partialCreditNoteEmission, lines: [{ linePublicId: "l1", creditNoteStatus: "Succeeded" }] } }),
    T5_STATE.SUCCEEDED
  );
});

test("errores T5 aceptan el shape del middleware de invariantes", () => {
  const error = { payload: { invariantCode: "INV-T5-EMIT-CAP" } };
  assert.equal(getT5InvariantCode(error), "INV-T5-EMIT-CAP");
  assert.equal(t5ErrorMessage(error), "El saldo de la factura cambió; revisá el monto.");
});

// ─── t5ErrorMessage: fallback genérico endurecido (retoque post-review 2026-07-17) ─────────────
// Invariantes T5 sin mapeo específico (ej. INV-T5-EMIT-SIBLING-UNRESOLVED, INV-T5-ABORT-ALREADY-
// EMITTED): el backend ya manda un texto limpio en criollo — se muestra tal cual, sin inventar uno.

test("t5ErrorMessage: invariante SIN mapeo específico → se muestra el mensaje limpio del backend tal cual", () => {
  const error = {
    payload: {
      invariantCode: "INV-T5-EMIT-SIBLING-UNRESOLVED",
      message: "Todavía hay servicios sin resolver que podrían corresponder a esta misma factura. Resolvélos primero y después emití.",
    },
  };
  assert.equal(
    t5ErrorMessage(error),
    "Todavía hay servicios sin resolver que podrían corresponder a esta misma factura. Resolvélos primero y después emití."
  );
});

test("t5ErrorMessage: falla de red/framework sin body útil → NUNCA texto crudo en inglés, cae al genérico en criollo", () => {
  // Antes de este retoque, el fallback leía error.message directo: un statusText de librería
  // ("Request failed with status code 500") se filtraba tal cual a la pantalla del agente.
  const errorStatusTextCrudo = { message: "Request failed with status code 500" };
  assert.equal(t5ErrorMessage(errorStatusTextCrudo), "No se pudo emitir la devolución. Intentá de nuevo.");

  const errorSinNada = {};
  assert.equal(t5ErrorMessage(errorSinNada), "No se pudo emitir la devolución. Intentá de nuevo.");
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
