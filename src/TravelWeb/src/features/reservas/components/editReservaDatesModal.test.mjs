/**
 * Tests de toDateInputValue() de EditReservaDatesModal — bug "fechas corridas un
 * día" (dueño, 2026-07-16).
 *
 * Esta función PRE-RELLENA los campos "Salida" y "Regreso" del modal de edición
 * manual de fechas. Antes leía los componentes en hora LOCAL del navegador
 * (new Date(value).getFullYear()/getMonth()/getDate()); como el backend guarda
 * esas fechas como medianoche UTC, en Argentina (UTC-3) el campo se prellenaba
 * con el día ANTERIOR al real. Si el usuario abría el modal sin tocar nada y
 * guardaba, el sistema le pisaba la fecha real de la reserva con la incorrecta
 * — no era solo un problema visual, corrompía el dato guardado.
 *
 * EditReservaDatesModal.jsx tiene JSX (no se puede importar directo en
 * node --test), así que copiamos la función — mismo patrón que el resto de los
 * tests de este directorio (ver reprogramarViajeModal.test.mjs).
 *
 * Cómo correr: node --test src/features/reservas/components/editReservaDatesModal.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

// ─── Lógica pura copiada de EditReservaDatesModal.jsx ────────────────────────

function toDateInputValue(value) {
    if (!value) return "";
    const soloFecha = String(value).split("T")[0];
    const partes = soloFecha.split("-");
    if (partes.length !== 3) return "";
    const [anio, mes, dia] = partes;
    if (!/^\d{4}$/.test(anio) || !/^\d{2}$/.test(mes) || !/^\d{2}$/.test(dia)) return "";
    return `${anio}-${mes}-${dia}`;
}

// ─── Tests ─────────────────────────────────────────────────────────────────

test("toDateInputValue: medianoche UTC con Z → mismo día calendario", () => {
    assert.equal(toDateInputValue("2026-05-23T00:00:00Z"), "2026-05-23");
});

test("toDateInputValue: fecha-solo-día sin hora → mismo día", () => {
    assert.equal(toDateInputValue("2026-05-23"), "2026-05-23");
});

test("toDateInputValue: fin de mes (31/05) → no salta a junio", () => {
    assert.equal(toDateInputValue("2026-05-31T00:00:00Z"), "2026-05-31");
});

test("toDateInputValue: fin de año (31/12) → no salta al año siguiente", () => {
    assert.equal(toDateInputValue("2026-12-31T00:00:00Z"), "2026-12-31");
});

test("toDateInputValue: 1 de enero → no retrocede al 31/12 del año anterior", () => {
    // Caso exacto del bug original reportado por el dueño.
    assert.equal(toDateInputValue("2026-01-01T00:00:00Z"), "2026-01-01");
});

test("toDateInputValue: 29 de febrero bisiesto → se preserva", () => {
    assert.equal(toDateInputValue("2028-02-29T00:00:00Z"), "2028-02-29");
});

test("toDateInputValue: valor vacío/null → cadena vacía", () => {
    assert.equal(toDateInputValue(null), "");
    assert.equal(toDateInputValue(undefined), "");
    assert.equal(toDateInputValue(""), "");
});

test("toDateInputValue: texto sin formato de fecha → cadena vacía", () => {
    assert.equal(toDateInputValue("no-es-fecha"), "");
});

test("toDateInputValue: ida y vuelta — abrir el modal sin tocar nada preserva el día real", () => {
    // Este es el caso crítico: si esto fallara, "Guardar" sin cambios movería
    // la reserva un día hacia atrás.
    const diaRealDeLaReserva = "2026-05-23";
    const respuestaSimuladaDelBackend = `${diaRealDeLaReserva}T00:00:00Z`;
    assert.equal(toDateInputValue(respuestaSimuladaDelBackend), diaRealDeLaReserva);
});
