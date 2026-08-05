/**
 * Tests del chip fijo de vencimiento de pasaporte (F11, D2, 2026-07-31).
 *
 * Cómo correr:
 *   node --test src/features/reservas/lib/passportAlertChip.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

import { construirChipPasaporte } from "./passportAlertChip.js";

test("sin passportAlertLevel no arma chip (motor no mandó alerta)", () => {
    const chip = construirChipPasaporte({ passportAlertLevel: null }, { endDate: "2026-08-01" });
    assert.equal(chip, null);
});

test("nivel Expired con reserva CON fechas: texto corto 'para el viaje'", () => {
    const chip = construirChipPasaporte(
        { passportAlertLevel: "Expired", passportAlertText: "texto largo del motor" },
        { startDate: "2026-08-01", endDate: "2026-08-10" }
    );
    assert.equal(chip.label, "Pasaporte vencido para el viaje");
    assert.equal(chip.title, "texto largo del motor");
    assert.match(chip.className, /rose/);
});

test("nivel Expired con reserva SIN fechas: texto corto genérico", () => {
    const chip = construirChipPasaporte(
        { passportAlertLevel: "Expired", passportAlertText: "texto largo del motor" },
        { startDate: null, endDate: null }
    );
    assert.equal(chip.label, "Pasaporte vencido");
});

test("nivel Tight: texto corto 'vence justo', color ámbar", () => {
    const chip = construirChipPasaporte(
        { passportAlertLevel: "Tight", passportAlertText: "le quedan menos de 6 meses" },
        { startDate: "2026-08-01", endDate: "2026-08-10" }
    );
    assert.equal(chip.label, "Pasaporte vence justo");
    assert.equal(chip.title, "le quedan menos de 6 meses");
    assert.match(chip.className, /amber/);
});

test("nivel desconocido: conservador, sin chip (no revienta con texto crudo)", () => {
    const chip = construirChipPasaporte({ passportAlertLevel: "ValorRaro" }, {});
    assert.equal(chip, null);
});

test("sin passportAlertText: usa un texto de respaldo, nunca deja el tooltip vacío", () => {
    const chip = construirChipPasaporte({ passportAlertLevel: "Expired" }, {});
    assert.ok(chip.title.length > 0);
});

// Fija el respaldo ámbar como COPIA LITERAL del motor (PassportExpiryRules.TightMarginAfterTripWarning).
// El 2026-08-05 el respaldo quedó truncado respecto del motor y la suite no lo agarró porque ningún
// test comparaba el string completo — este test cierra esa puerta (T-6).
test("Tight sin passportAlertText: el respaldo es la copia literal completa del texto del motor", () => {
    const chip = construirChipPasaporte(
        { passportAlertLevel: "Tight" },
        { startDate: "2026-08-01", endDate: "2026-08-10" }
    );
    assert.equal(
        chip.title,
        "El pasaporte vence cerca de la fecha del viaje. Verificá el requisito del destino: cada país pide una vigencia distinta."
    );
});
