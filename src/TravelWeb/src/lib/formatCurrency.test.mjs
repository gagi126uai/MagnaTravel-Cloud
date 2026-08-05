/**
 * Tests de formatCurrency() — opción withSymbol (fix "símbolo duplicado", prueba
 * integral en PROD 2026-08-05: en bloques multimoneda con <CurrencyBadge> pegado
 * al monto se veía "US$ US$5.800,00", porque el badge YA pinta el símbolo y
 * formatCurrency lo repetía).
 *
 * Cómo correr: node --test src/lib/formatCurrency.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

import { formatCurrency } from "./utils.js";

// ─── Comportamiento de siempre: sin la opción, no cambia nada ──────────────────

test("sin opciones (2 argumentos): ARS sigue mostrando el símbolo, como siempre", () => {
  assert.match(formatCurrency(5800, "ARS"), /^\$\s?5\.800,00$/);
});

test("sin opciones (2 argumentos): USD sigue usando 'US$', como siempre", () => {
  assert.equal(formatCurrency(5800, "USD"), "US$5.800,00");
});

// ─── withSymbol:false — el número pelado, para usar junto a un CurrencyBadge ───

test("withSymbol:false + ARS → solo el número, sin '$'", () => {
  assert.match(formatCurrency(5800, "ARS", { withSymbol: false }), /^5\.800,00$/);
});

test("withSymbol:false + USD → solo el número, sin 'US$'", () => {
  assert.equal(formatCurrency(5800, "USD", { withSymbol: false }), "5.800,00");
});

test("withSymbol:false + monto null/undefined → '0,00' pelado, no revienta", () => {
  assert.equal(formatCurrency(null, "ARS", { withSymbol: false }), "0,00");
  assert.equal(formatCurrency(undefined, "USD", { withSymbol: false }), "0,00");
});

test("withSymbol:false sin currency (moneda desconocida) → número pelado en formato en-US", () => {
  assert.equal(formatCurrency(5800, undefined, { withSymbol: false }), "5,800.00");
});

test("withSymbol explícito true → mismo resultado que no pasar la opción", () => {
  assert.equal(
    formatCurrency(5800, "USD", { withSymbol: true }),
    formatCurrency(5800, "USD")
  );
});

// ─── withSymbol:false + negativos: documentado a propósito (F6(b), review 2026-08-05) ──
//
// formatCurrency() NUNCA oculta el signo "-" por sí mismo — ni con símbolo ni sin él.
// Ocultar/traducir un negativo a palabra ("a favor", "Pérdida de", etc.) es una decisión
// de UX que toma CADA pantalla con su propio criterio de negocio (ver
// accountStatementText.formatSaldoDelExtracto, invoicingSummaryLogic.formatearMargen):
// ahí es donde se decide "-$5.000,00" → "$5.000,00 a favor". formatCurrency es un
// formateador genérico, no debe adivinar esa regla.
//
// Por eso, HOY, ningún call site que pasa withSymbol:false (siempre junto a un
// CurrencyBadge) recibe un valor negativo crudo: o el dato de origen ya viene positivo
// (ej. ColumnaNumericaMulti con montos de venta/costo), o pasa antes por uno de esos
// helpers que resuelven el signo en palabras. Este test fija el comportamiento actual
// como una advertencia para el futuro: si algún día un call site nuevo con badge
// necesita mostrar un negativo, "-5.000,00" (guión pegado al número, sin agarrar la
// atención) es fácil de leer mal — hay que pasar por un helper de "signo en palabras",
// no confiar en que formatCurrency lo resuelva solo.

test("withSymbol:false + negativo en ARS → devuelve el signo '-' pegado al número, NO lo oculta ni lo traduce a palabra", () => {
  assert.equal(formatCurrency(-5000, "ARS", { withSymbol: false }), "-5.000,00");
});

test("withSymbol:false + negativo en USD → mismo comportamiento (signo '-' crudo)", () => {
  assert.equal(formatCurrency(-5000, "USD", { withSymbol: false }), "-5.000,00");
});

test("withSymbol:false + negativo sin currency → formato en-US con el signo '-' crudo", () => {
  assert.equal(formatCurrency(-5000, undefined, { withSymbol: false }), "-5,000.00");
});
