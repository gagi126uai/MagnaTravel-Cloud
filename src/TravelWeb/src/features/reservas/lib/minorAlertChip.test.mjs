/**
 * Tests del chip fijo de "menor en tramo internacional" (decision UX 2026-08-05 derivada de patrones firmados: P11=A ambar + spec DNI 2026-08-03; label a validar por Gaston).
 *
 * Cómo correr:
 *   node --test src/features/reservas/lib/minorAlertChip.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

import { construirChipMenor } from "./minorAlertChip.js";

test("sin minorAlertLevel no arma chip (motor no mandó alerta)", () => {
    const chip = construirChipMenor({ minorAlertLevel: null });
    assert.equal(chip, null);
});

test("nivel Notice: arma el chip con label y clases ámbar", () => {
    const chip = construirChipMenor({ minorAlertLevel: "Notice", minorAlertText: "texto largo del motor" });
    assert.equal(chip.label, "Menor: revisar autorización de salida");
    assert.equal(chip.title, "texto largo del motor");
    assert.match(chip.className, /amber/);
});

test("nivel desconocido: conservador, sin chip (no revienta con texto crudo)", () => {
    const chip = construirChipMenor({ minorAlertLevel: "ValorRaro" });
    assert.equal(chip, null);
});

test("sin minorAlertText: usa el texto de respaldo firmado, nunca deja el tooltip vacío", () => {
    const chip = construirChipMenor({ minorAlertLevel: "Notice" });
    assert.ok(chip.title.length > 0);
    assert.match(chip.title, /Revisá si necesita autorización para salir del país/);
});
