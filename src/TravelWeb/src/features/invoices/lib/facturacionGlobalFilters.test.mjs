/**
 * Tests de lógica pura para facturacionGlobalFilters.js
 *
 * Corren con Node puro sin bundler ni React:
 *   node --test src/features/invoices/lib/facturacionGlobalFilters.test.mjs
 *
 * Patrón del proyecto: los tests .mjs replican la lógica sin importar el módulo.
 * Si cambia facturacionGlobalFilters.js, actualizar también acá.
 *
 * Cobertura:
 *   - mapTipoToDocumentLetter: todos los tipoComprobante ARCA conocidos + casos borde
 *   - mapEstadoToServerParams: todos los valores de estado + caso desconocido
 *   - buildInvoiceQueryParams: filtros vacíos, filtros completos, casos parciales
 */

import test from "node:test";
import assert from "node:assert/strict";

// ─── Lógica replicada inline ─────────────────────────────────────────────────

// Tabla de conversión tipoComprobante → Document + Letter
const TIPO_A_DOCUMENT_LETTER = {
  "1":  { document: "factura",    letter: "A" },
  "6":  { document: "factura",    letter: "B" },
  "11": { document: "factura",    letter: "C" },
  "51": { document: "factura",    letter: "M" },
  "3":  { document: "creditnote", letter: "A" },
  "8":  { document: "creditnote", letter: "B" },
  "13": { document: "creditnote", letter: "C" },
  "53": { document: "creditnote", letter: "M" },
  "2":  { document: "debitnote",  letter: "A" },
  "7":  { document: "debitnote",  letter: "B" },
  "12": { document: "debitnote",  letter: "C" },
  "52": { document: "debitnote",  letter: "M" },
};

function mapTipoToDocumentLetter(tipo) {
  if (!tipo) return null;
  return TIPO_A_DOCUMENT_LETTER[tipo] ?? null;
}

function mapEstadoToServerParams(estado) {
  switch (estado) {
    case "aprobado":   return { result: "aprobado",  annulment: null };
    case "rechazado":  return { result: "rechazado", annulment: null };
    case "en_proceso": return { result: "pendiente", annulment: null };
    case "anulando":   return { result: null, annulment: "anulando" };
    case "anulada":    return { result: null, annulment: "anulada"  };
    default:           return { result: null, annulment: null };
  }
}

function buildInvoiceQueryParams(filters, page, pageSize) {
  const params = new URLSearchParams();

  params.set("page", String(page));
  params.set("pageSize", String(pageSize));
  params.set("sortBy", "createdAt");
  params.set("sortDir", "desc");

  const { desde, hasta, tipo, estado, moneda, buscarNumero } = filters || {};

  if (desde) params.set("DateFrom", desde);
  if (hasta) params.set("DateTo", hasta);

  const documentLetter = mapTipoToDocumentLetter(tipo);
  if (documentLetter) {
    params.set("Document", documentLetter.document);
    params.set("Letter", documentLetter.letter);
  }

  const estadoParams = mapEstadoToServerParams(estado);
  if (estadoParams.result)    params.set("Result",    estadoParams.result);
  if (estadoParams.annulment) params.set("Annulment", estadoParams.annulment);

  if (moneda) params.set("Currency", moneda);

  if (buscarNumero && buscarNumero.trim()) {
    params.set("VoucherNumber", buscarNumero.trim());
  }

  return params;
}

// ─── Tests: mapTipoToDocumentLetter ──────────────────────────────────────────

test("mapTipoToDocumentLetter — string vacío devuelve null (sin filtro)", () => {
  assert.strictEqual(mapTipoToDocumentLetter(""), null);
});

test("mapTipoToDocumentLetter — undefined devuelve null", () => {
  assert.strictEqual(mapTipoToDocumentLetter(undefined), null);
});

test("mapTipoToDocumentLetter — tipo desconocido devuelve null", () => {
  assert.strictEqual(mapTipoToDocumentLetter("999"), null);
});

test("mapTipoToDocumentLetter — Factura A (1)", () => {
  assert.deepEqual(mapTipoToDocumentLetter("1"), { document: "factura", letter: "A" });
});

test("mapTipoToDocumentLetter — Factura B (6)", () => {
  assert.deepEqual(mapTipoToDocumentLetter("6"), { document: "factura", letter: "B" });
});

test("mapTipoToDocumentLetter — Factura C (11)", () => {
  assert.deepEqual(mapTipoToDocumentLetter("11"), { document: "factura", letter: "C" });
});

test("mapTipoToDocumentLetter — Factura M (51)", () => {
  assert.deepEqual(mapTipoToDocumentLetter("51"), { document: "factura", letter: "M" });
});

test("mapTipoToDocumentLetter — Nota de Crédito A (3)", () => {
  assert.deepEqual(mapTipoToDocumentLetter("3"), { document: "creditnote", letter: "A" });
});

test("mapTipoToDocumentLetter — Nota de Crédito B (8)", () => {
  assert.deepEqual(mapTipoToDocumentLetter("8"), { document: "creditnote", letter: "B" });
});

test("mapTipoToDocumentLetter — Nota de Crédito C (13)", () => {
  assert.deepEqual(mapTipoToDocumentLetter("13"), { document: "creditnote", letter: "C" });
});

test("mapTipoToDocumentLetter — Nota de Crédito M (53)", () => {
  assert.deepEqual(mapTipoToDocumentLetter("53"), { document: "creditnote", letter: "M" });
});

test("mapTipoToDocumentLetter — Nota de Débito A (2)", () => {
  assert.deepEqual(mapTipoToDocumentLetter("2"), { document: "debitnote", letter: "A" });
});

test("mapTipoToDocumentLetter — Nota de Débito B (7)", () => {
  assert.deepEqual(mapTipoToDocumentLetter("7"), { document: "debitnote", letter: "B" });
});

test("mapTipoToDocumentLetter — Nota de Débito C (12)", () => {
  assert.deepEqual(mapTipoToDocumentLetter("12"), { document: "debitnote", letter: "C" });
});

test("mapTipoToDocumentLetter — Nota de Débito M (52)", () => {
  assert.deepEqual(mapTipoToDocumentLetter("52"), { document: "debitnote", letter: "M" });
});

// ─── Tests: mapEstadoToServerParams ──────────────────────────────────────────

test("mapEstadoToServerParams — vacío: sin filtro de resultado ni anulación", () => {
  assert.deepEqual(mapEstadoToServerParams(""), { result: null, annulment: null });
});

test("mapEstadoToServerParams — undefined: sin filtro", () => {
  assert.deepEqual(mapEstadoToServerParams(undefined), { result: null, annulment: null });
});

test("mapEstadoToServerParams — valor desconocido: sin filtro", () => {
  assert.deepEqual(mapEstadoToServerParams("pagada"), { result: null, annulment: null });
});

test("mapEstadoToServerParams — aprobado: Result=aprobado, Annulment=null", () => {
  assert.deepEqual(mapEstadoToServerParams("aprobado"), { result: "aprobado", annulment: null });
});

test("mapEstadoToServerParams — rechazado: Result=rechazado, Annulment=null", () => {
  assert.deepEqual(mapEstadoToServerParams("rechazado"), { result: "rechazado", annulment: null });
});

test("mapEstadoToServerParams — en_proceso: Result=pendiente (no-A-no-R en backend)", () => {
  assert.deepEqual(mapEstadoToServerParams("en_proceso"), { result: "pendiente", annulment: null });
});

test("mapEstadoToServerParams — anulando: Annulment=anulando (no Result)", () => {
  assert.deepEqual(mapEstadoToServerParams("anulando"), { result: null, annulment: "anulando" });
});

test("mapEstadoToServerParams — anulada: Annulment=anulada (AnnulmentStatus.Succeeded)", () => {
  assert.deepEqual(mapEstadoToServerParams("anulada"), { result: null, annulment: "anulada" });
});

// ─── Tests: buildInvoiceQueryParams ──────────────────────────────────────────

test("buildInvoiceQueryParams — filtros vacíos: solo paginación y orden", () => {
  const params = buildInvoiceQueryParams({}, 1, 25);
  assert.strictEqual(params.get("page"), "1");
  assert.strictEqual(params.get("pageSize"), "25");
  assert.strictEqual(params.get("sortBy"), "createdAt");
  assert.strictEqual(params.get("sortDir"), "desc");
  // Sin filtros activos: no deben aparecer
  assert.strictEqual(params.get("DateFrom"), null);
  assert.strictEqual(params.get("DateTo"), null);
  assert.strictEqual(params.get("Document"), null);
  assert.strictEqual(params.get("Letter"), null);
  assert.strictEqual(params.get("Result"), null);
  assert.strictEqual(params.get("Annulment"), null);
  assert.strictEqual(params.get("Currency"), null);
  assert.strictEqual(params.get("VoucherNumber"), null);
});

test("buildInvoiceQueryParams — filtros completos con Factura A aprobada en pesos", () => {
  const filters = {
    desde: "2026-01-01",
    hasta: "2026-06-30",
    tipo: "1",
    estado: "aprobado",
    moneda: "ARS",
    buscarNumero: "12345",
  };
  const params = buildInvoiceQueryParams(filters, 2, 50);

  assert.strictEqual(params.get("page"), "2");
  assert.strictEqual(params.get("pageSize"), "50");
  assert.strictEqual(params.get("DateFrom"), "2026-01-01");
  assert.strictEqual(params.get("DateTo"), "2026-06-30");
  assert.strictEqual(params.get("Document"), "factura");
  assert.strictEqual(params.get("Letter"), "A");
  assert.strictEqual(params.get("Result"), "aprobado");
  assert.strictEqual(params.get("Annulment"), null);
  assert.strictEqual(params.get("Currency"), "ARS");
  assert.strictEqual(params.get("VoucherNumber"), "12345");
});

test("buildInvoiceQueryParams — NC B en proceso en dólares", () => {
  const filters = {
    desde: "",
    hasta: "",
    tipo: "8",
    estado: "en_proceso",
    moneda: "USD",
    buscarNumero: "",
  };
  const params = buildInvoiceQueryParams(filters, 1, 25);

  assert.strictEqual(params.get("Document"), "creditnote");
  assert.strictEqual(params.get("Letter"), "B");
  assert.strictEqual(params.get("Result"), "pendiente");
  assert.strictEqual(params.get("Currency"), "USD");
  assert.strictEqual(params.get("VoucherNumber"), null);
});

test("buildInvoiceQueryParams — filtro anulando (sin filtro de resultado)", () => {
  const filters = { desde: "", hasta: "", tipo: "", estado: "anulando", moneda: "", buscarNumero: "" };
  const params = buildInvoiceQueryParams(filters, 1, 25);

  assert.strictEqual(params.get("Annulment"), "anulando");
  assert.strictEqual(params.get("Result"), null);
});

test("buildInvoiceQueryParams — filtro anulada (AnnulmentStatus.Succeeded)", () => {
  const filters = { desde: "", hasta: "", tipo: "", estado: "anulada", moneda: "", buscarNumero: "" };
  const params = buildInvoiceQueryParams(filters, 1, 25);

  assert.strictEqual(params.get("Annulment"), "anulada");
  assert.strictEqual(params.get("Result"), null);
});

test("buildInvoiceQueryParams — búsqueda por número con espacios se limpia", () => {
  const filters = { desde: "", hasta: "", tipo: "", estado: "", moneda: "", buscarNumero: "  00001  " };
  const params = buildInvoiceQueryParams(filters, 1, 25);
  assert.strictEqual(params.get("VoucherNumber"), "00001");
});

test("buildInvoiceQueryParams — búsqueda por número solo espacios no se envía", () => {
  const filters = { desde: "", hasta: "", tipo: "", estado: "", moneda: "", buscarNumero: "   " };
  const params = buildInvoiceQueryParams(filters, 1, 25);
  assert.strictEqual(params.get("VoucherNumber"), null);
});

test("buildInvoiceQueryParams — tipo vacío no agrega Document ni Letter", () => {
  const filters = { desde: "", hasta: "", tipo: "", estado: "", moneda: "", buscarNumero: "" };
  const params = buildInvoiceQueryParams(filters, 1, 25);
  assert.strictEqual(params.get("Document"), null);
  assert.strictEqual(params.get("Letter"), null);
});

test("buildInvoiceQueryParams — ND M mapea correctamente", () => {
  const filters = { tipo: "52", estado: "", desde: "", hasta: "", moneda: "", buscarNumero: "" };
  const params = buildInvoiceQueryParams(filters, 1, 25);
  assert.strictEqual(params.get("Document"), "debitnote");
  assert.strictEqual(params.get("Letter"), "M");
});
