/**
 * Tests de la ayuda del casillero de documento (mini-tanda firmada 2026-07-31).
 * Corre con Node puro: node --test src/lib/documentoAyuda.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";
import { ayudaNumeroDocumento } from "./documentoAyuda.js";

test("DNI avisa el formato exacto que exige el motor (7 u 8 números, sin puntos)", () => {
    assert.equal(ayudaNumeroDocumento("DNI"), "7 u 8 números, sin puntos");
});

test("Pasaporte no promete formato: se copia como figura en el documento", () => {
    assert.equal(ayudaNumeroDocumento("Pasaporte"), "Como figura en el pasaporte");
});

test("Cédula y Otro caen en la ayuda genérica", () => {
    assert.equal(ayudaNumeroDocumento("Cedula"), "Número de documento");
    assert.equal(ayudaNumeroDocumento("Otro"), "Número de documento");
});

test("CUIT y CUIL (solo en la ficha de cliente) muestran el ejemplo con guiones", () => {
    assert.equal(ayudaNumeroDocumento("CUIT"), "20-30111222-0");
    assert.equal(ayudaNumeroDocumento("CUIL"), "20-30111222-0");
});

test("sin tipo elegido (null/undefined/desconocido) no rompe: ayuda genérica", () => {
    assert.equal(ayudaNumeroDocumento(null), "Número de documento");
    assert.equal(ayudaNumeroDocumento(undefined), "Número de documento");
    assert.equal(ayudaNumeroDocumento("Libreta civica"), "Número de documento");
});
