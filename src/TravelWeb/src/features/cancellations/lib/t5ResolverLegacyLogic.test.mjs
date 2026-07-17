import test from "node:test";
import assert from "node:assert/strict";
import {
  T5_ROW_STATE,
  allLinesIssued,
  anyLineIsEmitting,
  buildEmitPayloadForLine,
  buildEmptyCurrencyMessage,
  buildInvoiceHelpText,
  buildInvoicePlaceholder,
  buildResolvePayload,
  buildResolvedRowText,
  buildResolverFormHeading,
  buildResolverLegacyHeaderText,
  canSaveResolverRow,
  filterInvoicesByCurrency,
  resolveRowSaveErrorMessage,
  resolveRowState,
} from "./t5ResolverLegacyLogic.js";
import { formatCurrency } from "../../../lib/utils.js";
import { SPANISH_NETWORK_GENERIC } from "../../../lib/errors.js";

// ─── resolveRowState: el estado visual de una fila, derivado del DTO del backend ──────────────

test("resolveRowState: sin factura elegida (isResolved=false) → unresolved", () => {
  assert.equal(resolveRowState({ isResolved: false, creditNoteStatus: null }), T5_ROW_STATE.UNRESOLVED);
});

test("resolveRowState: con factura y monto confirmados, todavía sin pedir emitir → resolved", () => {
  assert.equal(resolveRowState({ isResolved: true, creditNoteStatus: null }), T5_ROW_STATE.RESOLVED);
});

test("resolveRowState: creditNoteStatus manda por encima de isResolved (Pending/Succeeded/Failed)", () => {
  assert.equal(resolveRowState({ isResolved: true, creditNoteStatus: "Pending" }), T5_ROW_STATE.EMITTING);
  assert.equal(resolveRowState({ isResolved: true, creditNoteStatus: "Succeeded" }), T5_ROW_STATE.ISSUED);
  assert.equal(resolveRowState({ isResolved: true, creditNoteStatus: "Failed" }), T5_ROW_STATE.REJECTED);
});

// ─── anyLineIsEmitting / allLinesIssued: gobiernan el polling y el "todo emitido" ──────────────

test("anyLineIsEmitting: true si alguna fila está Pending, sin importar las demás", () => {
  const lines = [
    { isResolved: true, creditNoteStatus: "Succeeded" },
    { isResolved: true, creditNoteStatus: "Pending" },
  ];
  assert.equal(anyLineIsEmitting(lines), true);
});

test("anyLineIsEmitting: false cuando ninguna fila está Pending", () => {
  assert.equal(anyLineIsEmitting([{ isResolved: false, creditNoteStatus: null }]), false);
  assert.equal(anyLineIsEmitting([]), false);
});

test("allLinesIssued: true solo cuando TODAS las filas terminaron Succeeded", () => {
  const todasEmitidas = [
    { isResolved: true, creditNoteStatus: "Succeeded" },
    { isResolved: true, creditNoteStatus: "Succeeded" },
  ];
  const unaFalta = [
    { isResolved: true, creditNoteStatus: "Succeeded" },
    { isResolved: true, creditNoteStatus: "Pending" },
  ];
  assert.equal(allLinesIssued(todasEmitidas), true);
  assert.equal(allLinesIssued(unaFalta), false);
  assert.equal(allLinesIssued([]), false, "una lista vacía nunca cuenta como 'todo emitido'");
});

// ─── buildResolverLegacyHeaderText: título + contador de la spec §1 punto 1 ────────────────────

test("buildResolverLegacyHeaderText: singular con una sola devolución pendiente", () => {
  const { title, progress } = buildResolverLegacyHeaderText([{ isResolved: false, creditNoteStatus: null }]);
  assert.equal(title, "Falta resolver 1 devolución de un servicio cancelado");
  assert.equal(progress, "0 de 1 listas");
});

test("buildResolverLegacyHeaderText: plural con varias, contador sube apenas una fila queda resuelta", () => {
  const lines = [
    { isResolved: true, creditNoteStatus: null },
    { isResolved: false, creditNoteStatus: null },
  ];
  const { title, progress } = buildResolverLegacyHeaderText(lines);
  assert.equal(title, "Faltan resolver 2 devoluciones de servicios cancelados");
  assert.equal(progress, "1 de 2 listas");
});

// ─── Textos EXACTOS de la spec (§2 y §4) — palabra por palabra, moneda según el servicio ───────

test("buildInvoicePlaceholder / buildInvoiceHelpText: texto exacto por moneda", () => {
  assert.equal(buildInvoicePlaceholder("USD"), "Elegí la factura en dólares…");
  assert.equal(buildInvoicePlaceholder("ARS"), "Elegí la factura en pesos…");
  assert.equal(buildInvoiceHelpText("USD"), "Solo aparecen facturas en dólares: la moneda la manda la factura.");
  assert.equal(buildInvoiceHelpText("ARS"), "Solo aparecen facturas en pesos: la moneda la manda la factura.");
});

test("buildEmptyCurrencyMessage: cartel neutro EXACTO de la spec §4, sin derivar a ningún rol", () => {
  assert.equal(
    buildEmptyCurrencyMessage("USD"),
    "No encontramos una factura en dólares en esta reserva. Revisá que la factura de este servicio exista antes de emitir la devolución."
  );
  assert.equal(
    buildEmptyCurrencyMessage("ARS"),
    "No encontramos una factura en pesos en esta reserva. Revisá que la factura de este servicio exista antes de emitir la devolución."
  );
});

test("buildResolverFormHeading: ancla el servicio y su moneda, formato exacto 'Resolver la devolución de: X — $Y'", () => {
  const line = { serviceName: "Hotel Maitei (Posadas)", currency: "USD", suggestedAmount: 700 };
  assert.equal(buildResolverFormHeading(line), `Resolver la devolución de: Hotel Maitei (Posadas) — ${formatCurrency(700, "USD")}`);
});

test("buildResolvedRowText: 'Resuelto ✓ · factura · monto', con la moneda de la fila (no la de otra)", () => {
  const line = { targetInvoiceLabel: "Factura B 0001-00012345", confirmedGrossCreditAmount: 700, currency: "USD" };
  assert.equal(buildResolvedRowText(line), `Resuelto ✓  ·  Factura B 0001-00012345  ·  ${formatCurrency(700, "USD")}`);
});

// ─── filterInvoicesByCurrency: regla dura multimoneda — nunca ofrecer otra moneda ──────────────

test("filterInvoicesByCurrency: se queda SOLO con las facturas de la moneda del servicio", () => {
  const invoices = [
    { publicId: "f-usd", currency: "USD" },
    { publicId: "f-ars", currency: "ARS" },
  ];
  assert.deepEqual(filterInvoicesByCurrency(invoices, "USD").map((i) => i.publicId), ["f-usd"]);
  assert.deepEqual(filterInvoicesByCurrency(invoices, "ARS").map((i) => i.publicId), ["f-ars"]);
});

test("filterInvoicesByCurrency: array vacío/undefined no revienta", () => {
  assert.deepEqual(filterInvoicesByCurrency(undefined, "USD"), []);
  assert.deepEqual(filterInvoicesByCurrency([], "USD"), []);
});

// ─── canSaveResolverRow: habilita "Guardar esta devolución" (spec §2 punto 5) ──────────────────

test("canSaveResolverRow: requiere factura + monto > 0 + motivo de al menos 10 caracteres", () => {
  assert.equal(canSaveResolverRow({ targetInvoicePublicId: "f1", amount: "700", reason: "Coincide con el total de la factura" }), true);
  assert.equal(canSaveResolverRow({ targetInvoicePublicId: "", amount: "700", reason: "Coincide con el total de la factura" }), false, "sin factura elegida");
  assert.equal(canSaveResolverRow({ targetInvoicePublicId: "f1", amount: "0", reason: "Coincide con el total de la factura" }), false, "monto en cero");
  assert.equal(canSaveResolverRow({ targetInvoicePublicId: "f1", amount: "-5", reason: "Coincide con el total de la factura" }), false, "monto negativo");
  assert.equal(canSaveResolverRow({ targetInvoicePublicId: "f1", amount: "700", reason: "corto" }), false, "motivo menor a 10 caracteres");
  assert.equal(canSaveResolverRow({ targetInvoicePublicId: "f1", amount: "abc", reason: "Coincide con el total de la factura" }), false, "monto no numérico");
});

// ─── buildResolvePayload / buildEmitPayloadForLine: shape EXACTO que espera el backend ─────────

test("buildResolvePayload: manda SIEMPRE bookingCancellationLinePublicId (varios pendientes al mismo tiempo)", () => {
  const line = { linePublicId: "line-guid-1" };
  const payload = buildResolvePayload(line, {
    targetInvoicePublicId: "invoice-guid",
    confirmedGrossCreditAmount: "700.50",
    reason: "  Coincide con el total facturado del hotel  ",
  });
  assert.deepEqual(payload, {
    targetInvoicePublicId: "invoice-guid",
    confirmedGrossCreditAmount: 700.5,
    reason: "Coincide con el total facturado del hotel",
    bookingCancellationLinePublicId: "line-guid-1",
  });
});

test("buildEmitPayloadForLine: manda la factura destino de ESTA fila puntual", () => {
  const line = { targetInvoicePublicId: "invoice-guid-2" };
  assert.deepEqual(buildEmitPayloadForLine(line), { targetInvoicePublicId: "invoice-guid-2" });
});

// ─── resolveRowSaveErrorMessage: distingue falla de red vs. rechazo real del backend (spec §5) ─

test("resolveRowSaveErrorMessage: mensaje de negocio del backend se muestra tal cual (ya viene limpio)", () => {
  const error = { payload: { message: "El monto supera el saldo disponible de la factura." } };
  assert.equal(resolveRowSaveErrorMessage(error), "El monto supera el saldo disponible de la factura.");
});

test("resolveRowSaveErrorMessage: falla de red pura → texto EXACTO de la spec para este formulario", () => {
  const errorDeRed = { message: "Failed to fetch" };
  assert.equal(resolveRowSaveErrorMessage(errorDeRed), "No se pudo guardar. Revisá la conexión y probá de nuevo.");
  // Con el genérico compartido tal cual, también cae en el mismo texto propio del formulario.
  const errorGenerico = { message: SPANISH_NETWORK_GENERIC };
  assert.equal(resolveRowSaveErrorMessage(errorGenerico), "No se pudo guardar. Revisá la conexión y probá de nuevo.");
});

test("resolveRowSaveErrorMessage: sin payload del servidor, NUNCA texto crudo de la librería HTTP (retoque post-review)", () => {
  // Este string ni siquiera está en la lista de genéricos conocidos de lib/errors.js — antes se
  // habría mostrado tal cual en la pantalla del agente. Sin `error.payload` (no hay body real del
  // servidor), ahora siempre cae al texto fijo de la spec, sin importar qué diga error.message.
  const errorSinPayload = { message: "Request failed with status code 500" };
  assert.equal(resolveRowSaveErrorMessage(errorSinPayload), "No se pudo guardar. Revisá la conexión y probá de nuevo.");
});
