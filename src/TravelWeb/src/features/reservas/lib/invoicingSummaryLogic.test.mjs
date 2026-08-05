/**
 * Tests de lógica pura del KPI "Falta facturar" (barrido de PROD 2026-07-24, hallazgo #23):
 * cuando se facturó más de lo vendido firme, el número da negativo y hay que explicarlo
 * en criollo en vez de mostrarlo pelado.
 *
 * Corren con Node puro sin bundler: node --test src/features/reservas/lib/invoicingSummaryLogic.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

import { formatearFaltaFacturar, formatearMargen } from "./invoicingSummaryLogic.js";

// ─── Caso normal: todavía falta facturar algo (positivo) ───────────────────────

test("valor positivo en ARS → muestra el monto formateado tal cual, sin explicación", () => {
  const resultado = formatearFaltaFacturar(15000, "ARS");

  assert.equal(resultado.esExceso, false);
  assert.match(resultado.texto, /15\.000,00/);
  assert.equal(resultado.texto.includes("de más"), false);
});

test("valor cero → muestra $0,00, no es exceso", () => {
  const resultado = formatearFaltaFacturar(0, "ARS");

  assert.equal(resultado.esExceso, false);
  assert.match(resultado.texto, /0,00/);
});

// ─── Caso confuso reportado en el barrido: negativo (se facturó de más) ────────

test("valor negativo en ARS → explica 'Facturaste $X de más', no muestra el signo pelado", () => {
  const resultado = formatearFaltaFacturar(-1500, "ARS");

  assert.equal(resultado.esExceso, true);
  // Nota: Intl.NumberFormat("es-AR") pone un espacio NO separable (U+00A0) entre el
  // símbolo y el número — por eso acá se usa una regex en vez de comparar el string
  // exacto a mano (evita que el test dependa de tipear ese carácter invisible bien).
  assert.match(resultado.texto, /^Facturaste \$\s?1\.500,00 de más$/);
  assert.equal(resultado.texto.startsWith("-"), false);
});

test("valor negativo en USD → usa el símbolo US$ y el monto en positivo", () => {
  const resultado = formatearFaltaFacturar(-300, "USD");

  assert.equal(resultado.esExceso, true);
  assert.match(resultado.texto, /^Facturaste US\$300,00 de más$/);
});

// ─── Valores raros que no deberían romper la pantalla ──────────────────────────

test("null/undefined → tratado como 0, no revienta", () => {
  assert.equal(formatearFaltaFacturar(null, "ARS").esExceso, false);
  assert.equal(formatearFaltaFacturar(undefined, "ARS").esExceso, false);
});

test("string numérico negativo (por si el backend lo manda como texto) → también se explica", () => {
  const resultado = formatearFaltaFacturar("-500", "ARS");

  assert.equal(resultado.esExceso, true);
  assert.match(resultado.texto, /^Facturaste \$\s?500,00 de más$/);
});

// ─── withSymbol=false: se usa junto a un CurrencyBadge (fix símbolo duplicado 2026-08-05) ──

test("withSymbol:false y valor positivo → número pelado, sin '$' (el badge de al lado ya lo muestra)", () => {
  const resultado = formatearFaltaFacturar(15000, "ARS", { withSymbol: false });

  assert.match(resultado.texto, /^15\.000,00$/);
});

test("withSymbol:false y valor negativo (exceso) → la frase tampoco repite el símbolo", () => {
  const resultado = formatearFaltaFacturar(-300, "USD", { withSymbol: false });

  assert.equal(resultado.texto, "Facturaste 300,00 de más");
});

// ============================================================================
// formatearMargen (FIX margen negativo pintado como ganancia, prueba integral 2026-08-05)
// ============================================================================

test("margen positivo → monto formateado tal cual, no es pérdida", () => {
  const resultado = formatearMargen(15000, "ARS");

  assert.equal(resultado.esPerdida, false);
  assert.match(resultado.texto, /15\.000,00/);
  assert.equal(resultado.texto.includes("Pérdida"), false);
});

test("margen cero → $0,00, no es pérdida", () => {
  const resultado = formatearMargen(0, "ARS");

  assert.equal(resultado.esPerdida, false);
  assert.match(resultado.texto, /0,00/);
});

test("margen negativo en ARS → 'Pérdida de $X', monto en positivo, sin el signo pelado", () => {
  const resultado = formatearMargen(-600, "ARS");

  assert.equal(resultado.esPerdida, true);
  assert.match(resultado.texto, /^Pérdida de \$\s?600,00$/);
  assert.equal(resultado.texto.startsWith("-"), false);
});

test("margen negativo en USD → 'Pérdida de US$X'", () => {
  const resultado = formatearMargen(-5800, "USD");

  assert.equal(resultado.esPerdida, true);
  assert.match(resultado.texto, /^Pérdida de US\$5\.800,00$/);
});

test("cada moneda evalúa su propio signo (P-3): ganancia en ARS y pérdida en USD a la vez", () => {
  const margenArs = formatearMargen(600, "ARS");
  const margenUsd = formatearMargen(-5800, "USD");

  assert.equal(margenArs.esPerdida, false);
  assert.equal(margenUsd.esPerdida, true);
});

test("withSymbol:false → 'Pérdida de X' sin símbolo (se usa junto a un CurrencyBadge)", () => {
  const resultado = formatearMargen(-600, "ARS", { withSymbol: false });

  assert.equal(resultado.texto, "Pérdida de 600,00");
});

test("string numérico negativo (por si el backend lo manda como texto) → también se detecta como pérdida", () => {
  const resultado = formatearMargen("-600", "ARS");

  assert.equal(resultado.esPerdida, true);
  assert.match(resultado.texto, /^Pérdida de \$\s?600,00$/);
});

test("null/undefined → tratado como 0, no revienta ni es pérdida", () => {
  assert.equal(formatearMargen(null, "ARS").esPerdida, false);
  assert.equal(formatearMargen(undefined, "ARS").esPerdida, false);
});
