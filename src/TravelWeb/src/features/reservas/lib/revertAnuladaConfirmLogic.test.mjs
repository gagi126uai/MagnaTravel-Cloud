/**
 * Tests de la última pieza de la Tanda 3 (2026-07-24): confirmación P-14 al "Volver atrás"
 * desde una reserva ANULADA (ADR-050, "deshacer la anulación entera").
 *
 * Corre con: node --test src/features/reservas/lib/revertAnuladaConfirmLogic.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

import { construirConfirmacionDeshacerAnulacion } from "./revertAnuladaConfirmLogic.js";

test("texto FIJO (T-6): título, cuerpo y botón exactos, palabra por palabra", () => {
    const confirmacion = construirConfirmacionDeshacerAnulacion();
    assert.equal(confirmacion.title, "Deshacer la anulación");
    assert.equal(
        confirmacion.text,
        "Los servicios anulados vuelven a como estaban y se retira el registro de la " +
        "devolución del operador. Si el cliente tenía saldo a favor por esta anulación " +
        "y no se usó, también se retira. ¿Confirmás?"
    );
    assert.equal(confirmacion.confirmText, "Sí, deshacer");
});

test("el texto no dice 'Cancelada' (regla dura de vocabulario: siempre 'anulación')", () => {
    const confirmacion = construirConfirmacionDeshacerAnulacion();
    assert.ok(!confirmacion.text.includes("Cancelada"));
    assert.ok(!confirmacion.title.includes("Cancelada"));
});

test("las tres consecuencias del ADR-050 están explicadas en criollo: servicios, reembolso del operador, saldo a favor", () => {
    const confirmacion = construirConfirmacionDeshacerAnulacion();
    assert.ok(confirmacion.text.includes("servicios anulados vuelven"));
    assert.ok(confirmacion.text.includes("devolución del operador"));
    assert.ok(confirmacion.text.includes("saldo a favor"));
});

test("devuelve un objeto compatible con showConfirm({ title, text, confirmText, confirmColor })", () => {
    const confirmacion = construirConfirmacionDeshacerAnulacion();
    assert.equal(typeof confirmacion.title, "string");
    assert.equal(typeof confirmacion.text, "string");
    assert.equal(typeof confirmacion.confirmText, "string");
    assert.equal(typeof confirmacion.confirmColor, "string");
});
