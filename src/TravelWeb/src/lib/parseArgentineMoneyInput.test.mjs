/**
 * Tests de parseArgentineMoneyInput() — bug real en PROD (QA 11/08/2026): los campos de
 * plata de la ficha de carga eran <input type="number"> nativos, que descartan la coma
 * decimal ("250,50" quedaba vacío). Esta función es la que ahora entiende lo que el
 * vendedor tipeó, en MoneyInput.jsx (components/ui).
 *
 * Cómo correr: node --test src/lib/parseArgentineMoneyInput.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

import { parseArgentineMoneyInput } from "./utils.js";

// ─── Caso principal del bug: coma decimal ──────────────────────────────────────

test("coma decimal simple: '250,50' → 250.5", () => {
    assert.equal(parseArgentineMoneyInput("250,50"), 250.5);
});

test("miles con punto + coma decimal: '1.250,50' → 1250.5", () => {
    assert.equal(parseArgentineMoneyInput("1.250,50"), 1250.5);
});

test("varios miles: '12.345.678,90' → 12345678.9", () => {
    assert.equal(parseArgentineMoneyInput("12.345.678,90"), 12345678.9);
});

// ─── Formato con punto (compatibilidad con lo que ya andaba) ───────────────────

test("con punto y 2 decimales (compatibilidad): '250.50' → 250.5", () => {
    assert.equal(parseArgentineMoneyInput("250.50"), 250.5);
});

test("con punto y 1 decimal: '250.5' → 250.5", () => {
    assert.equal(parseArgentineMoneyInput("250.5"), 250.5);
});

test("un solo punto con 3 dígitos después: '1.250' → 1250 (separador de miles)", () => {
    assert.equal(parseArgentineMoneyInput("1.250"), 1250);
});

// ─── Enteros y vacío ────────────────────────────────────────────────────────────

test("entero sin separadores: '250' → 250", () => {
    assert.equal(parseArgentineMoneyInput("250"), 250);
});

test("vacío o solo espacios → null (no 0, no NaN)", () => {
    assert.equal(parseArgentineMoneyInput(""), null);
    assert.equal(parseArgentineMoneyInput("   "), null);
});

test("null/undefined → null", () => {
    assert.equal(parseArgentineMoneyInput(null), null);
    assert.equal(parseArgentineMoneyInput(undefined), null);
});

test("texto sin ningún número → null", () => {
    assert.equal(parseArgentineMoneyInput("abc"), null);
});

// ─── Bordes de tipeo a medio camino (mientras el vendedor sigue escribiendo) ───

test("coma al final ('250,'): trata la parte decimal como vacía → 250", () => {
    assert.equal(parseArgentineMoneyInput("250,"), 250);
});

test("coma al PRINCIPIO (',50', el vendedor arrancó tipeando la coma): entiende 0,50 → 0.5", () => {
    assert.equal(parseArgentineMoneyInput(",50"), 0.5);
});

test("punto al final ('250.'): mismo caso que la coma al final → 250", () => {
    assert.equal(parseArgentineMoneyInput("250."), 250);
});

// ─── Texto irrecuperable (ni fecha ni miles: dos puntos no tienen sentido) ─────

test("dos puntos ('1.250.50', ninguna lectura válida como miles+decimal) → null, no un número inventado", () => {
    // No es "miles" (el último grupo '50' no tiene 3 dígitos) y tampoco hay una coma
    // que marque el decimal — MoneyInput.jsx usa este null en el onBlur (fix I4) para
    // saber que hay que revertir a lo último válido en vez de mostrar algo raro.
    assert.equal(parseArgentineMoneyInput("1.250.50"), null);
});
