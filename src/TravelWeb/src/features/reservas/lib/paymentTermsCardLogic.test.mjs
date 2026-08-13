/**
 * Tests de la precarga de "Formas de pago" en la ficha de la reserva (spec
 * docs/ux/2026-08-12-spec-pdf-emision-y-formas-de-pago.md, §1.2).
 *
 * Cómo correr: node --test src/features/reservas/lib/paymentTermsCardLogic.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

import {
  resolverTextoFormasDePagoPrecargado,
  textoFormasDePagoFueEditado,
} from "./paymentTermsCardLogic.js";

// ─── resolverTextoFormasDePagoPrecargado ────────────────────────────────────

test("resolverTextoFormasDePagoPrecargado: si la reserva ya tiene texto propio, ese gana (no se pisa con la plantilla)", () => {
  assert.equal(
    resolverTextoFormasDePagoPrecargado("Seña 30% + saldo en 3 cuotas", "Plantilla general de la agencia"),
    "Seña 30% + saldo en 3 cuotas"
  );
});

test("resolverTextoFormasDePagoPrecargado: sin texto propio, cae a la plantilla de Configuración", () => {
  assert.equal(
    resolverTextoFormasDePagoPrecargado(null, "Seña del 30% al reservar. Saldo 21 días antes."),
    "Seña del 30% al reservar. Saldo 21 días antes."
  );
});

test("resolverTextoFormasDePagoPrecargado: texto propio vacío/solo espacios se trata como 'no tiene', cae a la plantilla", () => {
  assert.equal(
    resolverTextoFormasDePagoPrecargado("   ", "Plantilla de la agencia"),
    "Plantilla de la agencia"
  );
});

test("resolverTextoFormasDePagoPrecargado: ninguna de las dos fuentes tiene contenido → string vacío (placeholder)", () => {
  assert.equal(resolverTextoFormasDePagoPrecargado(null, null), "");
  assert.equal(resolverTextoFormasDePagoPrecargado(undefined, "   "), "");
});

// ─── textoFormasDePagoFueEditado ─────────────────────────────────────────────

test("textoFormasDePagoFueEditado: false cuando el texto sigue igual al precargado", () => {
  assert.equal(textoFormasDePagoFueEditado("Seña 30%", "Seña 30%"), false);
});

test("textoFormasDePagoFueEditado: true apenas el vendedor escribe algo distinto", () => {
  assert.equal(textoFormasDePagoFueEditado("Seña 30% al reservar", "Seña 30%"), true);
});

test("textoFormasDePagoFueEditado: null/undefined se tratan como string vacío, no explota", () => {
  assert.equal(textoFormasDePagoFueEditado(null, undefined), false);
  assert.equal(textoFormasDePagoFueEditado("algo", null), true);
});
