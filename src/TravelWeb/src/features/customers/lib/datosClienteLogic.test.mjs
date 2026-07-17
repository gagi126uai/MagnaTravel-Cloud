/**
 * Tests de la lógica pura de la solapa "Datos" del cliente (spec
 * docs/ux/2026-07-17-ficha-cliente-solapa-datos.md).
 *
 * Corren con: node --test src/features/customers/lib/datosClienteLogic.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

import {
  TAX_CONDITION_OPTIONS,
  mapearCondicionFiscalATexto,
  construirEstadoInicialDatosCliente,
  puedeGuardarDatosCliente,
  construirPayloadDatosCliente,
  debeDeshabilitarCuit,
  debeMostrarBannerDatosFiscales,
} from "./datosClienteLogic.js";

// ============================================================================
// Sección 1: mapearCondicionFiscalATexto — nunca mostrar el código crudo
// ============================================================================

test("mapea cada código de condición fiscal a su etiqueta en criollo", () => {
  assert.equal(mapearCondicionFiscalATexto(1), "Responsable Inscripto");
  assert.equal(mapearCondicionFiscalATexto(6), "Monotributo");
  assert.equal(mapearCondicionFiscalATexto(4), "Exento");
  assert.equal(mapearCondicionFiscalATexto(5), "Consumidor Final");
});

test("acepta el código como string (viene de un <select>) sin romper", () => {
  assert.equal(mapearCondicionFiscalATexto("1"), "Responsable Inscripto");
});

test("código desconocido o vacío -> guion, nunca el número crudo", () => {
  assert.equal(mapearCondicionFiscalATexto(999), "—");
  assert.equal(mapearCondicionFiscalATexto(null), "—");
  assert.equal(mapearCondicionFiscalATexto(undefined), "—");
});

test("TAX_CONDITION_OPTIONS respeta el orden exacto de la spec (§2)", () => {
  assert.deepEqual(
    TAX_CONDITION_OPTIONS.map((o) => o.label),
    ["Responsable Inscripto", "Monotributo", "Exento", "Consumidor Final"]
  );
});

// ============================================================================
// Sección 2: construirEstadoInicialDatosCliente — mapeo de GET /customers/{id}
// ============================================================================

test("arma el formulario con todos los campos del cliente", () => {
  const cliente = {
    fullName: "Fam. García",
    documentNumber: "30111222",
    taxId: "20-30111222-3",
    taxConditionId: 1,
    email: "garcia@mail.com",
    phone: "11-4444-5555",
    address: "Av. Corrientes 1234, CABA",
    isActive: true,
  };
  assert.deepEqual(construirEstadoInicialDatosCliente(cliente), {
    fullName: "Fam. García",
    documentNumber: "30111222",
    taxId: "20-30111222-3",
    taxConditionId: 1,
    email: "garcia@mail.com",
    phone: "11-4444-5555",
    address: "Av. Corrientes 1234, CABA",
    isActive: true,
  });
});

test("cliente sin condición fiscal cargada -> default Consumidor Final (5), igual que el alta", () => {
  const cliente = { fullName: "Cliente viejo", taxConditionId: null };
  assert.equal(construirEstadoInicialDatosCliente(cliente).taxConditionId, 5);
});

test("cliente null/undefined -> formulario vacío con los defaults, no rompe", () => {
  const formulario = construirEstadoInicialDatosCliente(null);
  assert.equal(formulario.fullName, "");
  assert.equal(formulario.taxConditionId, 5);
  assert.equal(formulario.isActive, true);
});

test("isActive false del cliente se respeta (no se pisa con el default true)", () => {
  const formulario = construirEstadoInicialDatosCliente({ fullName: "X", isActive: false });
  assert.equal(formulario.isActive, false);
});

// ============================================================================
// Sección 3: puedeGuardarDatosCliente — únicos obligatorios: nombre + condición fiscal
// ============================================================================

test("con nombre y condición fiscal -> se puede guardar", () => {
  assert.equal(puedeGuardarDatosCliente({ fullName: "Fam. García", taxConditionId: 5 }), true);
});

test("nombre vacío -> NO se puede guardar", () => {
  assert.equal(puedeGuardarDatosCliente({ fullName: "", taxConditionId: 5 }), false);
});

test("nombre con solo espacios -> NO se puede guardar", () => {
  assert.equal(puedeGuardarDatosCliente({ fullName: "   ", taxConditionId: 5 }), false);
});

test("sin condición fiscal (null/undefined) -> NO se puede guardar", () => {
  assert.equal(puedeGuardarDatosCliente({ fullName: "Fam. García", taxConditionId: null }), false);
  assert.equal(puedeGuardarDatosCliente({ fullName: "Fam. García", taxConditionId: undefined }), false);
});

test("condición fiscal = 0 (valor falsy pero válido) NO debe bloquear -> nunca ocurre en este catálogo, pero la función no debe confundir 0 con \"vacío\"", () => {
  // Ninguna opción real usa el código 0, pero la función se testea igual para dejar
  // documentado que el chequeo es contra null/undefined, no contra "falsy".
  assert.equal(puedeGuardarDatosCliente({ fullName: "Fam. García", taxConditionId: 0 }), true);
});

// ============================================================================
// Sección 4: construirPayloadDatosCliente — el PUT nunca pierde datos no editados acá
// ============================================================================

test("arma el payload con los campos del formulario", () => {
  const formData = {
    fullName: "  Fam. García  ",
    documentNumber: "30111222",
    taxId: "20-30111222-3",
    taxConditionId: 1,
    email: "garcia@mail.com",
    phone: "11-4444-5555",
    address: "Av. Corrientes 1234, CABA",
    isActive: true,
  };
  const payload = construirPayloadDatosCliente(formData, "Nota vieja del vendedor");

  assert.equal(payload.fullName, "Fam. García"); // recortado
  assert.equal(payload.notes, "Nota vieja del vendedor");
  assert.equal(payload.taxId, "20-30111222-3");
  assert.equal(payload.taxConditionId, 1);
  assert.equal(payload.isActive, true);
});

test("NUNCA manda el campo taxCondition (texto) -> el backend lo deriva del código", () => {
  const payload = construirPayloadDatosCliente(
    { fullName: "X", taxConditionId: 6, email: "", phone: "", address: "", documentNumber: "", taxId: "", isActive: true },
    null
  );
  assert.equal("taxCondition" in payload, false);
});

test("sin notas originales (cliente nuevo/legacy sin notes) -> manda null, no undefined ni string vacío inventado", () => {
  const payload = construirPayloadDatosCliente(
    { fullName: "X", taxConditionId: 5, email: "", phone: "", address: "", documentNumber: "", taxId: "", isActive: true },
    undefined
  );
  assert.equal(payload.notes, null);
});

test("preserva las notas originales tal cual, aunque esta solapa no las muestre", () => {
  const payload = construirPayloadDatosCliente(
    { fullName: "X", taxConditionId: 5, email: "", phone: "", address: "", documentNumber: "", taxId: "", isActive: true },
    "Cliente VIP, siempre pide upgrade"
  );
  assert.equal(payload.notes, "Cliente VIP, siempre pide upgrade");
});

// ============================================================================
// Sección 5: debeDeshabilitarCuit — candado SOLO por el veredicto del backend
// ============================================================================

test("taxIdLocked true -> deshabilita el CUIT", () => {
  assert.equal(debeDeshabilitarCuit(true), true);
});

test("taxIdLocked false/null/undefined -> CUIT editable", () => {
  assert.equal(debeDeshabilitarCuit(false), false);
  assert.equal(debeDeshabilitarCuit(null), false);
  assert.equal(debeDeshabilitarCuit(undefined), false);
});

// ============================================================================
// Sección 6: debeMostrarBannerDatosFiscales — SOLO el veredicto del backend
// ============================================================================

test("hasPendingTaxData true -> muestra el banner", () => {
  assert.equal(debeMostrarBannerDatosFiscales(true), true);
});

test("hasPendingTaxData false/null/undefined -> no muestra el banner", () => {
  assert.equal(debeMostrarBannerDatosFiscales(false), false);
  assert.equal(debeMostrarBannerDatosFiscales(null), false);
  assert.equal(debeMostrarBannerDatosFiscales(undefined), false);
});

test("regla dura de la spec: NUNCA se deriva del taxConditionId, solo del flag del backend", () => {
  // Un cliente con taxConditionId null (dato incompleto de verdad) pero que el backend
  // todavía no marcó como pendiente (hasPendingTaxData: false) -> el banner NO debe
  // encenderse. Si esta función alguna vez empezara a mirar taxConditionId, este test
  // se rompería y avisaría del desvío a la spec.
  assert.equal(debeMostrarBannerDatosFiscales(false), false);
});
