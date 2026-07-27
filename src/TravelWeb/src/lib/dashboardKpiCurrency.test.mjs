/**
 * Tests de construirLineasKpiPorMoneda (hallazgo B3, revisión 2026-07-27).
 *
 * Corre con: node --test src/lib/dashboardKpiCurrency.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

import { construirLineasKpiPorMoneda, construirLineasKpiConCompatibilidad } from "./dashboardKpiCurrency.js";

test("una moneda con movimiento -> una sola línea con esa moneda", () => {
    const lineas = construirLineasKpiPorMoneda([{ currency: "ARS", amount: 150000 }]);
    assert.deepEqual(lineas, [{ currency: "ARS", monto: 150000, esSaldoAFavor: false }]);
});

test("dos monedas con movimiento -> dos líneas, una por moneda (nunca se suman)", () => {
    const lineas = construirLineasKpiPorMoneda([
        { currency: "ARS", amount: 150000 },
        { currency: "USD", amount: 800 },
    ]);
    assert.deepEqual(lineas, [
        { currency: "ARS", monto: 150000, esSaldoAFavor: false },
        { currency: "USD", monto: 800, esSaldoAFavor: false },
    ]);
});

test("una moneda sin movimiento (amount=0) -> esa línea no se muestra", () => {
    const lineas = construirLineasKpiPorMoneda([
        { currency: "ARS", amount: 150000 },
        { currency: "USD", amount: 0 },
    ]);
    assert.deepEqual(lineas, [{ currency: "ARS", monto: 150000, esSaldoAFavor: false }]);
});

test("todas las monedas en 0 -> una única línea '$0' en ARS (nunca una tarjeta vacía)", () => {
    const lineas = construirLineasKpiPorMoneda([
        { currency: "ARS", amount: 0 },
        { currency: "USD", amount: 0 },
    ]);
    assert.deepEqual(lineas, [{ currency: "ARS", monto: 0, esSaldoAFavor: false }]);
});

test("lista vacía o ausente -> misma línea default '$0' ARS", () => {
    assert.deepEqual(construirLineasKpiPorMoneda([]), [{ currency: "ARS", monto: 0, esSaldoAFavor: false }]);
    assert.deepEqual(construirLineasKpiPorMoneda(null), [{ currency: "ARS", monto: 0, esSaldoAFavor: false }]);
    assert.deepEqual(construirLineasKpiPorMoneda(undefined), [{ currency: "ARS", monto: 0, esSaldoAFavor: false }]);
});

test("negativoEsSaldoAFavor=true y monto negativo -> esa línea se marca esSaldoAFavor y el monto vuelve positivo", () => {
    const lineas = construirLineasKpiPorMoneda(
        [{ currency: "ARS", amount: -5000 }],
        { negativoEsSaldoAFavor: true }
    );
    assert.deepEqual(lineas, [{ currency: "ARS", monto: 5000, esSaldoAFavor: true }]);
});

test("negativoEsSaldoAFavor=true: solo la moneda que está en negativo se marca, la otra no", () => {
    const lineas = construirLineasKpiPorMoneda(
        [
            { currency: "ARS", amount: -5000 },
            { currency: "USD", amount: 300 },
        ],
        { negativoEsSaldoAFavor: true }
    );
    assert.deepEqual(lineas, [
        { currency: "ARS", monto: 5000, esSaldoAFavor: true },
        { currency: "USD", monto: 300, esSaldoAFavor: false },
    ]);
});

test("negativoEsSaldoAFavor=false (default): un monto negativo se muestra tal cual, sin marcar", () => {
    const lineas = construirLineasKpiPorMoneda([{ currency: "ARS", amount: -5000 }]);
    assert.deepEqual(lineas, [{ currency: "ARS", monto: -5000, esSaldoAFavor: false }]);
});

// ─── construirLineasKpiConCompatibilidad (ítem 4 del re-review, 2026-07-27) ──────
//
// Esta función decide cuándo confiar en la lista por moneda del backend nuevo y
// cuándo caer al escalar viejo de compatibilidad (deploy en caché sin `porMoneda`).

test("lista real con movimiento -> se respeta tal cual, NO cae al escalar", () => {
    const lineas = construirLineasKpiConCompatibilidad([{ currency: "ARS", amount: 150000 }], 999999);
    assert.deepEqual(lineas, [{ currency: "ARS", monto: 150000, esSaldoAFavor: false }]);
});

test("lista real VACÍA (sin movimiento este mes, dato real) -> línea '$0' ARS, NO el escalar viejo", () => {
    // Este es el bug del ítem 4: antes, una lista vacía real caía al escalar de
    // compatibilidad como si el dato no hubiera llegado. Acá el escalar (500000) NO
    // debe aparecer — la lista vacía significa "sin movimiento", no "dato faltante".
    const lineas = construirLineasKpiConCompatibilidad([], 500000);
    assert.deepEqual(lineas, [{ currency: "ARS", monto: 0, esSaldoAFavor: false }]);
});

test("listaPorMoneda ausente (undefined, deploy viejo en caché) -> cae al escalar de compatibilidad en ARS", () => {
    const lineas = construirLineasKpiConCompatibilidad(undefined, 500000);
    assert.deepEqual(lineas, [{ currency: "ARS", monto: 500000, esSaldoAFavor: false }]);
});

test("listaPorMoneda null (mismo caso que undefined) -> cae al escalar de compatibilidad", () => {
    const lineas = construirLineasKpiConCompatibilidad(null, 500000);
    assert.deepEqual(lineas, [{ currency: "ARS", monto: 500000, esSaldoAFavor: false }]);
});

test("escalar de compatibilidad sin número válido -> cae a 0 (no rompe con NaN)", () => {
    const lineas = construirLineasKpiConCompatibilidad(undefined, undefined);
    assert.deepEqual(lineas, [{ currency: "ARS", monto: 0, esSaldoAFavor: false }]);
});

test("opciones (negativoEsSaldoAFavor) se propagan a la lista real, no solo al fallback", () => {
    const lineas = construirLineasKpiConCompatibilidad(
        [{ currency: "ARS", amount: -3000 }],
        500000,
        { negativoEsSaldoAFavor: true }
    );
    assert.deepEqual(lineas, [{ currency: "ARS", monto: 3000, esSaldoAFavor: true }]);
});
