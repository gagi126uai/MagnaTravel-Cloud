/**
 * Tests de la lógica por moneda de AnalyticsPage.jsx (solapas Vendedores, Destinos
 * e Interanual). Corre con: node --test src/lib/analyticsByCurrency.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

import {
    armarRankingVendedoresPorMoneda,
    armarRankingDestinosPorMoneda,
    armarComparativaInteranualPorMoneda,
} from "./analyticsByCurrency.js";

// ─── armarRankingVendedoresPorMoneda ─────────────────────────────────────────

test("vendedores: una sola moneda -> un único bloque, sin reordenar (respeta el orden del backend)", () => {
    const sellers = [
        { userId: "u1", sellerName: "Ana", totalSales: 100000, filesCreated: 5, marginPercent: 20, totalSalesByCurrency: [{ currency: "ARS", amount: 100000 }] },
        { userId: "u2", sellerName: "Beto", totalSales: 50000, filesCreated: 3, marginPercent: 10, totalSalesByCurrency: [{ currency: "ARS", amount: 50000 }] },
    ];
    const resultado = armarRankingVendedoresPorMoneda(sellers);
    assert.equal(resultado.hayMasDeUnaMoneda, false);
    assert.equal(resultado.bloques.length, 1);
    assert.deepEqual(resultado.bloques[0].vendedores.map((v) => v.userId), ["u1", "u2"]);
    assert.equal(resultado.bloques[0].maxMonto, 100000);
    // Una sola moneda: el conteo de files es honesto (no hay riesgo de contarlo
    // doble en ningún otro bloque), así que viaja tal cual, como siempre.
    assert.equal(resultado.bloques[0].vendedores[0].filesCreated, 5);
});

test("vendedores: sin totalSalesByCurrency (backend viejo) -> cae al mismo bloque único legacy", () => {
    const sellers = [{ userId: "u1", sellerName: "Ana", totalSales: 100000, filesCreated: 5, marginPercent: 20 }];
    const resultado = armarRankingVendedoresPorMoneda(sellers);
    assert.equal(resultado.hayMasDeUnaMoneda, false);
    assert.equal(resultado.bloques[0].vendedores[0].monto, 100000);
});

test("vendedores: dos monedas -> dos bloques, cada uno ordenado por SU propia moneda", () => {
    const sellers = [
        {
            userId: "u1", sellerName: "Ana", filesCreated: 5,
            totalSalesByCurrency: [{ currency: "ARS", amount: 100000 }, { currency: "USD", amount: 200 }],
            grossMarginByCurrency: [{ currency: "ARS", amount: 20000 }, { currency: "USD", amount: 40 }],
        },
        {
            userId: "u2", sellerName: "Beto", filesCreated: 3,
            totalSalesByCurrency: [{ currency: "USD", amount: 500 }],
            grossMarginByCurrency: [{ currency: "USD", amount: 100 }],
        },
    ];
    const resultado = armarRankingVendedoresPorMoneda(sellers);
    assert.equal(resultado.hayMasDeUnaMoneda, true);
    assert.deepEqual(resultado.bloques.map((b) => b.currency), ["ARS", "USD"]);

    const bloqueArs = resultado.bloques.find((b) => b.currency === "ARS");
    assert.equal(bloqueArs.vendedores.length, 1);
    assert.equal(bloqueArs.vendedores[0].userId, "u1");
    assert.equal(bloqueArs.vendedores[0].margenPercent, 20);

    const bloqueUsd = resultado.bloques.find((b) => b.currency === "USD");
    // Beto vendió más en USD, tiene que quedar primero en ESTA moneda aunque en ARS no aparezca.
    assert.deepEqual(bloqueUsd.vendedores.map((v) => v.userId), ["u2", "u1"]);
});

test("vendedores: dos monedas -> filesCreated NO se repite por bloque (bloqueante de review: contarlo doble)", () => {
    // Ana tiene 5 files en total (mezcla ARS+USD) — si cada bloque de moneda mostrara
    // "5 files", alguien que sume Vendedores ARS + Vendedores USD leería 10 files, el
    // doble de los que realmente creó.
    const sellers = [
        {
            userId: "u1", sellerName: "Ana", filesCreated: 5,
            totalSalesByCurrency: [{ currency: "ARS", amount: 100000 }, { currency: "USD", amount: 200 }],
        },
    ];
    const resultado = armarRankingVendedoresPorMoneda(sellers);
    assert.equal(resultado.hayMasDeUnaMoneda, true);
    for (const bloque of resultado.bloques) {
        assert.equal(bloque.vendedores[0].filesCreated, null);
    }
});

test("vendedores: sin permiso de costo (grossMarginByCurrency vacío) -> margenPercent null, no 0", () => {
    const sellers = [
        {
            userId: "u1", sellerName: "Ana", filesCreated: 5,
            totalSalesByCurrency: [{ currency: "ARS", amount: 100000 }, { currency: "USD", amount: 200 }],
            grossMarginByCurrency: [],
        },
    ];
    const resultado = armarRankingVendedoresPorMoneda(sellers);
    for (const bloque of resultado.bloques) {
        assert.equal(bloque.vendedores[0].margenPercent, null);
    }
});

// ─── armarRankingDestinosPorMoneda ───────────────────────────────────────────

test("destinos: una sola moneda -> un único bloque sin reordenar", () => {
    const destinations = [
        { destination: "BARILOCHE", totalRevenue: 500000, margin: 100000, bookingCount: 4, passengerCount: 8, totalRevenueByCurrency: [{ currency: "ARS", amount: 500000 }] },
    ];
    const resultado = armarRankingDestinosPorMoneda(destinations);
    assert.equal(resultado.hayMasDeUnaMoneda, false);
    assert.equal(resultado.bloques[0].destinos[0].monto, 500000);
    assert.equal(resultado.bloques[0].destinos[0].margenMonto, 100000);
    // Una sola moneda: bookingCount/passengerCount viajan tal cual, como siempre.
    assert.equal(resultado.bloques[0].destinos[0].bookingCount, 4);
    assert.equal(resultado.bloques[0].destinos[0].passengerCount, 8);
});

test("destinos: dos monedas -> un destino solo aparece en la moneda en la que tuvo ventas", () => {
    const destinations = [
        {
            destination: "MIAMI", bookingCount: 2, passengerCount: 4,
            totalRevenueByCurrency: [{ currency: "USD", amount: 3000 }],
            marginByCurrency: [{ currency: "USD", amount: 600 }],
        },
        {
            destination: "BARILOCHE", bookingCount: 4, passengerCount: 8,
            totalRevenueByCurrency: [{ currency: "ARS", amount: 500000 }],
            marginByCurrency: [{ currency: "ARS", amount: 100000 }],
        },
    ];
    const resultado = armarRankingDestinosPorMoneda(destinations);
    assert.equal(resultado.hayMasDeUnaMoneda, true);
    const bloqueUsd = resultado.bloques.find((b) => b.currency === "USD");
    assert.deepEqual(bloqueUsd.destinos.map((d) => d.destination), ["MIAMI"]);
    const bloqueArs = resultado.bloques.find((b) => b.currency === "ARS");
    assert.deepEqual(bloqueArs.destinos.map((d) => d.destination), ["BARILOCHE"]);
});

test("destinos: dos monedas -> bookingCount/passengerCount NO se repiten por bloque (bloqueante de review: contarlos doble)", () => {
    const destinations = [
        {
            destination: "MIAMI", bookingCount: 3, passengerCount: 6,
            totalRevenueByCurrency: [{ currency: "ARS", amount: 100000 }, { currency: "USD", amount: 200 }],
        },
    ];
    const resultado = armarRankingDestinosPorMoneda(destinations);
    assert.equal(resultado.hayMasDeUnaMoneda, true);
    for (const bloque of resultado.bloques) {
        assert.equal(bloque.destinos[0].bookingCount, null);
        assert.equal(bloque.destinos[0].passengerCount, null);
    }
});

test("destinos: sin permiso de costo (marginByCurrency vacío) -> margenMonto null", () => {
    const destinations = [
        {
            destination: "MIAMI", bookingCount: 2, passengerCount: 4,
            totalRevenueByCurrency: [{ currency: "USD", amount: 3000 }],
            marginByCurrency: [],
        },
    ];
    const resultado = armarRankingDestinosPorMoneda(destinations);
    assert.equal(resultado.bloques[0].destinos[0].margenMonto, null);
});

// ─── armarComparativaInteranualPorMoneda ─────────────────────────────────────

function mesVacio(month, monthNumber) {
    return { month, monthNumber, sales: 0, costs: 0, margin: 0, reservaCount: 0, salesByCurrency: [], costsByCurrency: [], marginByCurrency: [] };
}

test("yoy: una sola moneda -> un único bloque usando los totales legacy tal cual", () => {
    const currentYear = [
        { ...mesVacio("Ene", 1), sales: 1000, salesByCurrency: [{ currency: "ARS", amount: 1000 }] },
        ...Array.from({ length: 11 }, (_, i) => mesVacio("M", i + 2)),
    ];
    const previousYear = [
        { ...mesVacio("Ene", 1), sales: 500, salesByCurrency: [{ currency: "ARS", amount: 500 }] },
        ...Array.from({ length: 11 }, (_, i) => mesVacio("M", i + 2)),
    ];
    const yoy = { currentYear, previousYear, currentYearTotal: 1000, previousYearTotal: 500, growthPercent: 100 };
    const resultado = armarComparativaInteranualPorMoneda(yoy);
    assert.equal(resultado.hayMasDeUnaMoneda, false);
    assert.equal(resultado.bloques[0].totalActual, 1000);
    assert.equal(resultado.bloques[0].totalAnterior, 500);
    assert.equal(resultado.bloques[0].crecimientoPercent, 100);
});

test("yoy: dos monedas -> total anual y % de crecimiento calculados por moneda, sumando SOLO dentro de esa moneda", () => {
    const currentYear = [
        { ...mesVacio("Ene", 1), salesByCurrency: [{ currency: "ARS", amount: 1000 }, { currency: "USD", amount: 100 }] },
        { ...mesVacio("Feb", 2), salesByCurrency: [{ currency: "ARS", amount: 1000 }] },
        ...Array.from({ length: 10 }, (_, i) => mesVacio("M", i + 3)),
    ];
    const previousYear = [
        { ...mesVacio("Ene", 1), salesByCurrency: [{ currency: "ARS", amount: 800 }, { currency: "USD", amount: 200 }] },
        { ...mesVacio("Feb", 2), salesByCurrency: [{ currency: "ARS", amount: 800 }] },
        ...Array.from({ length: 10 }, (_, i) => mesVacio("M", i + 3)),
    ];
    const yoy = { currentYear, previousYear, currentYearTotal: 2100, previousYearTotal: 1800, growthPercent: 16.7 };
    const resultado = armarComparativaInteranualPorMoneda(yoy);
    assert.equal(resultado.hayMasDeUnaMoneda, true);

    const bloqueArs = resultado.bloques.find((b) => b.currency === "ARS");
    assert.equal(bloqueArs.totalActual, 2000); // 1000 + 1000, nunca mezclado con USD
    assert.equal(bloqueArs.totalAnterior, 1600);

    const bloqueUsd = resultado.bloques.find((b) => b.currency === "USD");
    assert.equal(bloqueUsd.totalActual, 100);
    assert.equal(bloqueUsd.totalAnterior, 200);
    assert.equal(bloqueUsd.crecimientoPercent, -50); // cayó de 200 a 100
});

test("yoy: mes sin ninguna reserva (salesByCurrency vacío) -> ese mes aporta 0 en todas las monedas del bloque", () => {
    const currentYear = [
        { ...mesVacio("Ene", 1), salesByCurrency: [{ currency: "USD", amount: 100 }] },
        mesVacio("Feb", 2), // mes sin ventas: array vacío, no debe romper el cálculo
        ...Array.from({ length: 10 }, (_, i) => mesVacio("M", i + 3)),
    ];
    const previousYear = Array.from({ length: 12 }, (_, i) => mesVacio("M", i + 1));
    const yoy = { currentYear, previousYear, currentYearTotal: 100, previousYearTotal: 0, growthPercent: 0 };
    const resultado = armarComparativaInteranualPorMoneda(yoy);
    const bloqueUsd = resultado.bloques.find((b) => b.currency === "USD");
    assert.equal(bloqueUsd.meses[1].actual, 0);
    assert.equal(bloqueUsd.totalActual, 100);
});
