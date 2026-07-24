/**
 * Tests de la Tanda 3 (2026-07-24), fix #41 "Caja sin carreras".
 *
 * Corre con: node --test src/features/payments/lib/cashRaceGuard.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

import { esRespuestaObsoleta, decidirAccionCargaCaja } from "./cashRaceGuard.js";

// ─── esRespuestaObsoleta: respuestas fuera de orden → gana la última ────────────────────

test("el pedido con el numero MAS ALTO (el mas nuevo) NO es obsoleto", () => {
    // Pedido #2 salio despues del #1; cuando responde, el numero vigente ya es 2.
    assert.equal(esRespuestaObsoleta(2, 2), false);
});

test("un pedido VIEJO que responde DESPUES de uno mas nuevo es obsoleto", () => {
    // Escenario del bug: pedido #1 sale, tarda; pedido #2 sale y responde primero
    // (requestIdRef.current pasa a 2); cuando el #1 finalmente responde, ya no es vigente.
    assert.equal(esRespuestaObsoleta(1, 2), true);
});

test("respuestas EN ORDEN (una sola en vuelo a la vez) nunca son obsoletas", () => {
    assert.equal(esRespuestaObsoleta(1, 1), false);
    assert.equal(esRespuestaObsoleta(2, 2), false);
    assert.equal(esRespuestaObsoleta(3, 3), false);
});

test("tres pedidos en carrera: solo el ULTIMO que salio gana, los anteriores se descartan", () => {
    // Simula: usuario cambia de mes tres veces rapido. Los tres pedidos (id 1, 2, 3) salen
    // casi juntos; supongamos que responden en orden 2, 1, 3 (fuera de orden por la red).
    const requestIdVigente = 3; // el ultimo que salio
    assert.equal(esRespuestaObsoleta(2, requestIdVigente), true);
    assert.equal(esRespuestaObsoleta(1, requestIdVigente), true);
    assert.equal(esRespuestaObsoleta(3, requestIdVigente), false); // este es el que debe ganar
});

// ─── decidirAccionCargaCaja: un solo pedido real por cambio del usuario ─────────────────

test("primera corrida (montaje del hook) -> siempre pide datos, nunca reinicia pagina", () => {
    const accion = decidirAccionCargaCaja({
        firmaAnterior: null,
        firmaActual: "[\"\",\"all\",\"all\",25,\"2026-07\"]",
        esPrimeraCorrida: true,
        page: 1,
    });
    assert.equal(accion, "pedir-datos");
});

test("cambio de filtro estando en pagina 1 -> pide datos directo (no hace falta reiniciar)", () => {
    const accion = decidirAccionCargaCaja({
        firmaAnterior: "[\"\",\"all\",\"all\",25,\"2026-07\"]",
        firmaActual: "[\"\",\"income\",\"all\",25,\"2026-07\"]",
        esPrimeraCorrida: false,
        page: 1,
    });
    assert.equal(accion, "pedir-datos");
});

test("cambio de filtro estando en pagina 3 -> SOLO reinicia la pagina (evita el pedido duplicado)", () => {
    const accion = decidirAccionCargaCaja({
        firmaAnterior: "[\"\",\"all\",\"all\",25,\"2026-07\"]",
        firmaActual: "[\"\",\"income\",\"all\",25,\"2026-07\"]",
        esPrimeraCorrida: false,
        page: 3,
    });
    assert.equal(accion, "reiniciar-pagina");
});

test("cambio de pagina SIN cambio de filtro -> pide datos directo (paginacion normal)", () => {
    const accion = decidirAccionCargaCaja({
        firmaAnterior: "[\"\",\"all\",\"all\",25,\"2026-07\"]",
        firmaActual: "[\"\",\"all\",\"all\",25,\"2026-07\"]", // misma firma: no cambio ningun filtro
        esPrimeraCorrida: false,
        page: 2,
    });
    assert.equal(accion, "pedir-datos");
});

test("cambio de mes estando en pagina 1 ya -> pide datos directo (nada que reiniciar)", () => {
    const accion = decidirAccionCargaCaja({
        firmaAnterior: "[\"\",\"all\",\"all\",25,\"2026-07\"]",
        firmaActual: "[\"\",\"all\",\"all\",25,\"2026-08\"]",
        esPrimeraCorrida: false,
        page: 1,
    });
    assert.equal(accion, "pedir-datos");
});
