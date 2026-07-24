/**
 * Tests de la Tanda 3 (2026-07-24), fix #39 "Buscador global honesto".
 *
 * Corre con: node --test src/lib/searchPaletteLogic.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

import {
    MENSAJE_ERROR_BUSQUEDA,
    AVISO_ALCANCE_PROPIO,
    debeMostrarAvisoAlcancePropio,
    resolverEstadoBusqueda,
} from "./searchPaletteLogic.js";

// ─── debeMostrarAvisoAlcancePropio ──────────────────────────────────────────────────────

test("reservas recortadas a lo propio (sin reservas.view_all) -> muestra el aviso", () => {
    const scope = { reservasScopedToOwn: true, paymentsScopedToOwn: false };
    assert.equal(debeMostrarAvisoAlcancePropio(scope, "reservas"), true);
});

test("reservas SIN recorte (tiene reservas.view_all) -> no muestra nada", () => {
    const scope = { reservasScopedToOwn: false, paymentsScopedToOwn: false };
    assert.equal(debeMostrarAvisoAlcancePropio(scope, "reservas"), false);
});

test("pagos recortados a lo propio -> muestra el aviso en esa sección, no en reservas", () => {
    const scope = { reservasScopedToOwn: false, paymentsScopedToOwn: true };
    assert.equal(debeMostrarAvisoAlcancePropio(scope, "payments"), true);
    assert.equal(debeMostrarAvisoAlcancePropio(scope, "reservas"), false);
});

test("sin scope (backend viejo que todavía no manda el campo) -> nunca inventa el aviso", () => {
    assert.equal(debeMostrarAvisoAlcancePropio(null, "reservas"), false);
    assert.equal(debeMostrarAvisoAlcancePropio(undefined, "payments"), false);
});

test("sección desconocida -> false (conservador)", () => {
    const scope = { reservasScopedToOwn: true, paymentsScopedToOwn: true };
    assert.equal(debeMostrarAvisoAlcancePropio(scope, "customers"), false);
});

// ─── resolverEstadoBusqueda ─────────────────────────────────────────────────────────────

test("sin query -> estado 'inicial'", () => {
    const estado = resolverEstadoBusqueda({ query: "", loading: false, results: null, errorMensaje: null });
    assert.equal(estado, "inicial");
});

test("con query, cargando, sin resultados todavía -> 'cargando'", () => {
    const estado = resolverEstadoBusqueda({ query: "garcia", loading: true, results: null, errorMensaje: null });
    assert.equal(estado, "cargando");
});

test("con query, resultados con al menos una sección con datos -> 'con-resultados'", () => {
    const estado = resolverEstadoBusqueda({
        query: "garcia",
        loading: false,
        results: { reservas: [{ id: 1 }], customers: [], payments: [] },
        errorMensaje: null,
    });
    assert.equal(estado, "con-resultados");
});

test("con query, resultados con TODAS las secciones vacías -> 'sin-resultados'", () => {
    const estado = resolverEstadoBusqueda({
        query: "zzz-no-existe",
        loading: false,
        results: { reservas: [], customers: [], payments: [] },
        errorMensaje: null,
    });
    assert.equal(estado, "sin-resultados");
});

test("error de red/permiso -> 'error', tiene prioridad sobre cualquier otro estado", () => {
    const estado = resolverEstadoBusqueda({
        query: "garcia",
        loading: false,
        results: null,
        errorMensaje: MENSAJE_ERROR_BUSQUEDA,
    });
    assert.equal(estado, "error");
});

test("error con resultados viejos todavía en memoria -> igual gana 'error' (no mezclar datos viejos con el aviso)", () => {
    const estado = resolverEstadoBusqueda({
        query: "garcia",
        loading: false,
        results: { reservas: [{ id: 1 }] },
        errorMensaje: MENSAJE_ERROR_BUSQUEDA,
    });
    assert.equal(estado, "error");
});

// ─── Textos exactos (nunca se reescriben en el componente) ─────────────────────────────

test("el mensaje de error es DISTINTO del de 'sin resultados' (no deben confundirse)", () => {
    assert.notEqual(MENSAJE_ERROR_BUSQUEDA, "No se encontraron resultados");
    assert.equal(MENSAJE_ERROR_BUSQUEDA, "No se pudo buscar. Probá de nuevo.");
});

test("el aviso de alcance propio tiene el texto exacto esperado", () => {
    assert.equal(AVISO_ALCANCE_PROPIO, "Mostrando solo lo tuyo");
});
