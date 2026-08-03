/**
 * Tests del chip fijo de vencimiento de DNI (semáforo cabotaje, 2026-08-03).
 *
 * Cómo correr:
 *   node --test src/features/reservas/lib/dniAlertChip.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

import { construirChipDni } from "./dniAlertChip.js";

test("sin dniAlertLevel no arma chip (motor no mandó alerta)", () => {
    const chip = construirChipDni({ dniAlertLevel: null }, { endDate: "2026-08-01" });
    assert.equal(chip, null);
});

test("nivel Expired con reserva CON fechas: texto corto 'para el viaje' (T-6)", () => {
    const chip = construirChipDni(
        { dniAlertLevel: "Expired", dniAlertText: "texto largo del motor" },
        { startDate: "2026-08-01", endDate: "2026-08-10" }
    );
    assert.equal(chip.label, "DNI vencido para el viaje");
    assert.equal(chip.title, "texto largo del motor");
    assert.match(chip.className, /rose/);
});

test("nivel Expired con reserva SIN fechas: texto corto genérico (T-6)", () => {
    const chip = construirChipDni(
        { dniAlertLevel: "Expired", dniAlertText: "texto largo del motor" },
        { startDate: null, endDate: null }
    );
    assert.equal(chip.label, "DNI vencido");
});

test("nivel desconocido: conservador, sin chip (no revienta con texto crudo)", () => {
    const chip = construirChipDni({ dniAlertLevel: "ValorRaro" }, {});
    assert.equal(chip, null);
});

test("sin dniAlertText: usa el texto de respaldo firmado, nunca deja el tooltip vacío", () => {
    const chip = construirChipDni({ dniAlertLevel: "Expired" }, {});
    assert.ok(chip.title.length > 0);
    assert.match(chip.title, /DNI de este pasajero se vence antes del viaje/);
});

test("un solo nivel: no existe versión ámbar 'vence justo' para el DNI (a diferencia del pasaporte)", () => {
    const chip = construirChipDni({ dniAlertLevel: "Tight" }, {});
    assert.equal(chip, null);
});
