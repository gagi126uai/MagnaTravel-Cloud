/**
 * Fix del hallazgo B1 (frontend-reviewer, 2026-07-24): la decisión toast-vs-Cartel emergente
 * para los rechazos de "Volver atrás" (ADR-050) NO puede depender solo del largo del texto,
 * porque los dos mensajes firmados miden 78 y 79 caracteres y caían por debajo del umbral de
 * 80. Estos tests prueban que con `code: "UNDO_ANNULMENT_BLOCKED"` esos mensajes van SIEMPRE
 * a Cartel, y que sin ese code el criterio viejo por longitud sigue funcionando igual.
 *
 * Corre con: node --test src/features/reservas/lib/revertRejectionPresentation.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

import {
    debeMostrarCartelPorRechazoDeRevert,
    CODE_UNDO_ANNULMENT_BLOCKED,
} from "./revertRejectionPresentation.js";

// Los dos mensajes firmados de ADR-050 (79 y 78 caracteres, ambos por DEBAJO del umbral
// de 80 que usa el fallback legacy por longitud).
const MENSAJE_SALDO_A_FAVOR_YA_USADO =
    "Ese saldo a favor ya se usó en otra reserva. No se puede deshacer la anulación.";
const MENSAJE_ND_MULTA_YA_EMITIDA =
    "Ya se emitió la nota de débito de la multa. No se puede deshacer la anulación.";

// ─── Los dos mensajes firmados de ADR-050: cortos, pero con code van a Cartel ──────────

test("mensaje firmado 'saldo a favor ya usado' (79 caracteres, corto) CON code -> SI va al cartel", () => {
    assert.equal(MENSAJE_SALDO_A_FAVOR_YA_USADO.length, 79);
    assert.ok(MENSAJE_SALDO_A_FAVOR_YA_USADO.length <= 80, "el mensaje es corto: sin code caería en toast");
    assert.equal(
        debeMostrarCartelPorRechazoDeRevert({ mensaje: MENSAJE_SALDO_A_FAVOR_YA_USADO, code: CODE_UNDO_ANNULMENT_BLOCKED }),
        true
    );
});

test("mensaje firmado 'nota de débito de la multa ya emitida' (78 caracteres, corto) CON code -> SI va al cartel", () => {
    assert.equal(MENSAJE_ND_MULTA_YA_EMITIDA.length, 78);
    assert.ok(MENSAJE_ND_MULTA_YA_EMITIDA.length <= 80, "el mensaje es corto: sin code caería en toast");
    assert.equal(
        debeMostrarCartelPorRechazoDeRevert({ mensaje: MENSAJE_ND_MULTA_YA_EMITIDA, code: CODE_UNDO_ANNULMENT_BLOCKED }),
        true
    );
});

// ─── Los mismos mensajes SIN code: demuestra que el largo por sí solo los mandaría a toast ──

test("el mismo mensaje corto SIN code -> cae en toast (por eso hacía falta el code)", () => {
    assert.equal(
        debeMostrarCartelPorRechazoDeRevert({ mensaje: MENSAJE_SALDO_A_FAVOR_YA_USADO, code: null }),
        false
    );
    assert.equal(
        debeMostrarCartelPorRechazoDeRevert({ mensaje: MENSAJE_ND_MULTA_YA_EMITIDA, code: undefined }),
        false
    );
});

// ─── Fallback legacy: sin code, se sigue decidiendo por el largo del texto ─────────────

test("rechazo corto SIN code -> toast (comportamiento de siempre, sin cambios)", () => {
    assert.equal(
        debeMostrarCartelPorRechazoDeRevert({ mensaje: "Mínimo 10 caracteres", code: null }),
        false
    );
});

test("rechazo largo (mas de 80 caracteres) SIN code -> cartel (comportamiento de siempre, sin cambios)", () => {
    const mensajeLargo =
        "No se puede revertir esta reserva porque tiene una factura emitida y confirmada que todavía no fue anulada por el circuito fiscal correspondiente.";
    assert.ok(mensajeLargo.length > 80);
    assert.equal(
        debeMostrarCartelPorRechazoDeRevert({ mensaje: mensajeLargo, code: null }),
        true
    );
});

// ─── Un code distinto (de otro endpoint/otro bloqueo) no debe disparar el cartel por code ──

test("code distinto a UNDO_ANNULMENT_BLOCKED -> se decide por el largo, no por el code", () => {
    assert.equal(
        debeMostrarCartelPorRechazoDeRevert({ mensaje: "Mínimo 10 caracteres", code: "OTRO_CODE_CUALQUIERA" }),
        false
    );
});

// ─── Caso borde: mensaje vacio SIN code sigue sin abrir el cartel (fallback legacy) ────

test("mensaje vacio o null SIN code -> nunca abre el cartel (nada que mostrar)", () => {
    assert.equal(debeMostrarCartelPorRechazoDeRevert({ mensaje: "", code: null }), false);
    assert.equal(debeMostrarCartelPorRechazoDeRevert({ mensaje: null, code: undefined }), false);
});
