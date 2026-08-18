/**
 * Tests de la lógica pura del formulario en línea de pasajero.
 *
 * Cómo correr:
 *   node --test src/features/reservas/lib/pasajeroInlineFormLogic.test.mjs
 *
 * Qué cubre:
 *   - Si "+ Más detalles" arranca abierta o plegada según el pasajero editado.
 *   - El payload que se manda al backend, con y sin conFuncionesCompletas.
 */

import test from "node:test";
import assert from "node:assert/strict";

import {
    debeAbrirMasDetallesPorDefecto,
    construirPayloadPasajero,
} from "./pasajeroInlineFormLogic.js";

// ─── debeAbrirMasDetallesPorDefecto ──────────────────────────────────────────

test("debeAbrirMasDetallesPorDefecto: alta nueva (sin pasajero) arranca plegada", () => {
    assert.equal(debeAbrirMasDetallesPorDefecto(null), false);
});

test("debeAbrirMasDetallesPorDefecto: pasajero sin ningún dato extra arranca plegada", () => {
    const pasajero = { fullName: "Juan Pérez", documentType: "DNI", documentNumber: "30111222" };
    assert.equal(debeAbrirMasDetallesPorDefecto(pasajero), false);
});

test("debeAbrirMasDetallesPorDefecto: solo 'gender' cargado (default del backend) sigue plegada", () => {
    // gender siempre viene con algo (M/F/X) aunque nadie lo haya elegido a mano —
    // no cuenta como "dato cargado a propósito".
    const pasajero = { fullName: "Juan Pérez", gender: "M" };
    assert.equal(debeAbrirMasDetallesPorDefecto(pasajero), false);
});

test("debeAbrirMasDetallesPorDefecto: con fecha de nacimiento cargada arranca abierta", () => {
    const pasajero = { fullName: "Juan Pérez", birthDate: "1990-05-15" };
    assert.equal(debeAbrirMasDetallesPorDefecto(pasajero), true);
});

test("debeAbrirMasDetallesPorDefecto: con notas cargadas arranca abierta", () => {
    const pasajero = { fullName: "Juan Pérez", notes: "Vegetariano" };
    assert.equal(debeAbrirMasDetallesPorDefecto(pasajero), true);
});

// ─── construirPayloadPasajero ────────────────────────────────────────────────

function formCompleto(overrides = {}) {
    return {
        fullName: "  Juan Pérez  ",
        documentType: "DNI",
        documentNumber: "30111222",
        birthDate: "1990-05-15",
        passportExpiry: "2030-01-01",
        documentExpiry: "2028-01-01",
        nationality: "  Argentina  ",
        gender: "M",
        phone: "  +54 9 11 1234-5678  ",
        email: "  juan@ejemplo.com  ",
        notes: "  Vegetariano  ",
        ...overrides,
    };
}

test("construirPayloadPasajero: con conFuncionesCompletas manda todos los campos, recortados", () => {
    const payload = construirPayloadPasajero({
        form: formCompleto(),
        conFuncionesCompletas: true,
        passengerToEdit: null,
    });

    assert.deepEqual(payload, {
        fullName: "Juan Pérez",
        documentType: "DNI",
        documentNumber: "30111222",
        birthDate: "1990-05-15",
        passportExpiry: "2030-01-01",
        documentExpiry: "2028-01-01",
        nationality: "Argentina",
        gender: "M",
        phone: "+54 9 11 1234-5678",
        email: "juan@ejemplo.com",
        notes: "Vegetariano",
    });
});

test("construirPayloadPasajero: con conFuncionesCompletas y campos extra vacíos, manda null (no string vacío)", () => {
    const payload = construirPayloadPasajero({
        form: formCompleto({ nationality: "", phone: "", email: "", notes: "", passportExpiry: "", documentExpiry: "" }),
        conFuncionesCompletas: true,
        passengerToEdit: null,
    });

    assert.equal(payload.nationality, null);
    assert.equal(payload.phone, null);
    assert.equal(payload.email, null);
    assert.equal(payload.notes, null);
    assert.equal(payload.passportExpiry, null);
    assert.equal(payload.documentExpiry, null);
});

test("construirPayloadPasajero: sin conFuncionesCompletas, preserva los campos extra del pasajero existente", () => {
    const passengerToEdit = {
        nationality: "Uruguay",
        phone: "+598 99 999999",
        email: "vieja@ejemplo.com",
        gender: "F",
        notes: "Alergia al maní",
    };

    const payload = construirPayloadPasajero({
        form: formCompleto(),
        conFuncionesCompletas: false,
        passengerToEdit,
    });

    // Los campos "reducidos" (fullName/documentType/documentNumber/birthDate) siguen
    // saliendo del formulario — solo los de "más detalles" se copian del existente.
    assert.equal(payload.fullName, "Juan Pérez");
    assert.equal(payload.nationality, "Uruguay");
    assert.equal(payload.phone, "+598 99 999999");
    assert.equal(payload.email, "vieja@ejemplo.com");
    assert.equal(payload.gender, "F");
    assert.equal(payload.notes, "Alergia al maní");
    // Y NO manda passportExpiry/documentExpiry en modo reducido (nunca estuvieron en pantalla).
    assert.equal("passportExpiry" in payload, false);
    assert.equal("documentExpiry" in payload, false);
});

test("construirPayloadPasajero: sin conFuncionesCompletas ni pasajero existente (alta reducida), manda null en los extra", () => {
    const payload = construirPayloadPasajero({
        form: formCompleto(),
        conFuncionesCompletas: false,
        passengerToEdit: null,
    });

    assert.equal(payload.nationality, null);
    assert.equal(payload.phone, null);
    assert.equal(payload.email, null);
    assert.equal(payload.gender, null);
    assert.equal(payload.notes, null);
});
