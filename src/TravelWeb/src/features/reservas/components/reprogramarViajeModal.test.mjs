/**
 * Tests de lógica pura de ReprogramarViajeModal.
 *
 * Cubre:
 *   - calcularDeltaDias: diferencia en días entre dos fechas (positiva, negativa, cero)
 *   - calcularNuevaFechaFin: nuevo regreso dado un delta
 *   - Casos límite: fechas inválidas, null, sin fecha de salida
 *   - Lógica de previsualización (texto del corrimiento)
 *   - Validación de formulario: no permitir enviar sin fecha
 *
 * Correr: node --test src/features/reservas/components/reprogramarViajeModal.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

// ─── Lógica pura copiada de ReprogramarViajeModal.jsx ────────────────────────
// Copiamos las funciones exportadas para poder testearlas sin cargar React.

function calcularDeltaDias(fechaDesde, fechaHasta) {
    if (!fechaDesde || !fechaHasta) return null;

    const parsePartes = (fecha) => {
        const soloFecha = fecha.split("T")[0];
        const partes = soloFecha.split("-");
        if (partes.length !== 3) return null;
        const [anio, mes, dia] = partes;
        const anioNum = Number(anio);
        const mesNum = Number(mes);
        const diaNum = Number(dia);
        if (isNaN(anioNum) || isNaN(mesNum) || isNaN(diaNum)) return null;
        return Date.UTC(anioNum, mesNum - 1, diaNum);
    };

    const desdeMs = parsePartes(fechaDesde);
    const hastaMs = parsePartes(fechaHasta);

    if (desdeMs === null || hastaMs === null) return null;

    return Math.round((hastaMs - desdeMs) / (1000 * 60 * 60 * 24));
}

function calcularNuevaFechaFin(endDateIso, deltaDias) {
    if (!endDateIso || deltaDias === null || deltaDias === undefined) return null;

    const soloFecha = endDateIso.split("T")[0];
    const partes = soloFecha.split("-");
    if (partes.length !== 3) return null;

    const [anio, mes, dia] = partes;
    const anioNum = Number(anio);
    const mesNum = Number(mes);
    const diaNum = Number(dia);
    if (isNaN(anioNum) || isNaN(mesNum) || isNaN(diaNum)) return null;

    const nuevaMs = Date.UTC(anioNum, mesNum - 1, diaNum) + deltaDias * 24 * 60 * 60 * 1000;
    const nuevaFecha = new Date(nuevaMs);

    const yyyy = nuevaFecha.getUTCFullYear();
    const mm = String(nuevaFecha.getUTCMonth() + 1).padStart(2, "0");
    const dd = String(nuevaFecha.getUTCDate()).padStart(2, "0");
    return `${yyyy}-${mm}-${dd}`;
}

/**
 * Replica la lógica de previsualización del componente.
 * Devuelve el texto del corrimiento dado el delta.
 */
function calcularTextoCorrimiento(deltaDias) {
    if (deltaDias === null) return null;
    if (deltaDias === 0) return "Sin corrimiento (es la misma fecha de salida).";
    if (deltaDias > 0) return `El viaje se adelanta ${deltaDias} ${deltaDias === 1 ? "día" : "días"}.`;
    return `El viaje se atrasa ${Math.abs(deltaDias)} ${Math.abs(deltaDias) === 1 ? "día" : "días"}.`;
}

/**
 * Replica la validación del formulario: no se puede enviar sin fecha elegida.
 */
function puedeEnviar(nuevaSalida, enviando) {
    return Boolean(nuevaSalida) && !enviando;
}

// ─── Tests: calcularDeltaDias ─────────────────────────────────────────────────

test("calcularDeltaDias: adelantar 7 días → delta positivo", () => {
    // Nueva salida 7 días después de la actual
    const delta = calcularDeltaDias("2026-07-01", "2026-07-08");
    assert.equal(delta, 7);
});

test("calcularDeltaDias: misma fecha → delta 0", () => {
    const delta = calcularDeltaDias("2026-07-01", "2026-07-01");
    assert.equal(delta, 0);
});

test("calcularDeltaDias: atrasar (nueva fecha anterior) → delta negativo", () => {
    // Nueva salida 3 días antes de la actual (atraso: el viaje se mueve al pasado)
    const delta = calcularDeltaDias("2026-07-10", "2026-07-07");
    assert.equal(delta, -3);
});

test("calcularDeltaDias: 1 día de diferencia", () => {
    const delta = calcularDeltaDias("2026-07-01", "2026-07-02");
    assert.equal(delta, 1);
});

test("calcularDeltaDias: cruce de mes", () => {
    // Del 28 de junio al 5 de julio = 7 días
    const delta = calcularDeltaDias("2026-06-28", "2026-07-05");
    assert.equal(delta, 7);
});

test("calcularDeltaDias: cruce de año", () => {
    // Del 28 de diciembre al 3 de enero = 6 días
    const delta = calcularDeltaDias("2026-12-28", "2027-01-03");
    assert.equal(delta, 6);
});

test("calcularDeltaDias: fecha con hora ISO (ignora la parte de hora)", () => {
    // Asegura que "2026-07-01T00:00:00Z" funciona igual que "2026-07-01"
    const delta = calcularDeltaDias("2026-07-01T00:00:00Z", "2026-07-08T12:30:00Z");
    assert.equal(delta, 7);
});

test("calcularDeltaDias: null en fechaDesde → null", () => {
    assert.equal(calcularDeltaDias(null, "2026-07-08"), null);
});

test("calcularDeltaDias: null en fechaHasta → null", () => {
    assert.equal(calcularDeltaDias("2026-07-01", null), null);
});

test("calcularDeltaDias: ambas null → null", () => {
    assert.equal(calcularDeltaDias(null, null), null);
});

test("calcularDeltaDias: cadena vacía → null", () => {
    assert.equal(calcularDeltaDias("", "2026-07-08"), null);
    assert.equal(calcularDeltaDias("2026-07-01", ""), null);
});

test("calcularDeltaDias: fecha inválida → null", () => {
    assert.equal(calcularDeltaDias("no-es-fecha", "2026-07-08"), null);
    assert.equal(calcularDeltaDias("2026-07-01", "abc-def-ghi"), null);
});

// ─── Tests: calcularNuevaFechaFin ─────────────────────────────────────────────

test("calcularNuevaFechaFin: regreso + 7 días", () => {
    // Regreso original: 15 julio → nueva fin: 22 julio
    const nuevaFin = calcularNuevaFechaFin("2026-07-15", 7);
    assert.equal(nuevaFin, "2026-07-22");
});

test("calcularNuevaFechaFin: regreso + 0 días = misma fecha", () => {
    const nuevaFin = calcularNuevaFechaFin("2026-07-15", 0);
    assert.equal(nuevaFin, "2026-07-15");
});

test("calcularNuevaFechaFin: regreso - 3 días (atraso)", () => {
    const nuevaFin = calcularNuevaFechaFin("2026-07-15", -3);
    assert.equal(nuevaFin, "2026-07-12");
});

test("calcularNuevaFechaFin: cruce de mes", () => {
    // 28 junio + 7 días = 5 julio
    const nuevaFin = calcularNuevaFechaFin("2026-06-28", 7);
    assert.equal(nuevaFin, "2026-07-05");
});

test("calcularNuevaFechaFin: cruce de año", () => {
    // 28 diciembre + 6 días = 3 enero siguiente año
    const nuevaFin = calcularNuevaFechaFin("2026-12-28", 6);
    assert.equal(nuevaFin, "2027-01-03");
});

test("calcularNuevaFechaFin: endDate null → null", () => {
    assert.equal(calcularNuevaFechaFin(null, 7), null);
});

test("calcularNuevaFechaFin: delta null → null", () => {
    assert.equal(calcularNuevaFechaFin("2026-07-15", null), null);
});

test("calcularNuevaFechaFin: endDate inválido → null", () => {
    assert.equal(calcularNuevaFechaFin("no-es-fecha", 7), null);
});

test("calcularNuevaFechaFin: endDate con hora ISO funciona", () => {
    const nuevaFin = calcularNuevaFechaFin("2026-07-15T00:00:00Z", 3);
    assert.equal(nuevaFin, "2026-07-18");
});

// ─── Tests: texto del corrimiento ─────────────────────────────────────────────

test("textoCorrimiento: 0 → 'Sin corrimiento'", () => {
    const texto = calcularTextoCorrimiento(0);
    assert.equal(texto, "Sin corrimiento (es la misma fecha de salida).");
});

test("textoCorrimiento: 1 día (singular) → 'adelanta 1 día'", () => {
    const texto = calcularTextoCorrimiento(1);
    assert.equal(texto, "El viaje se adelanta 1 día.");
});

test("textoCorrimiento: 7 días (plural) → 'adelanta 7 días'", () => {
    const texto = calcularTextoCorrimiento(7);
    assert.equal(texto, "El viaje se adelanta 7 días.");
});

test("textoCorrimiento: -1 día (singular) → 'atrasa 1 día'", () => {
    const texto = calcularTextoCorrimiento(-1);
    assert.equal(texto, "El viaje se atrasa 1 día.");
});

test("textoCorrimiento: -5 días (plural) → 'atrasa 5 días'", () => {
    const texto = calcularTextoCorrimiento(-5);
    assert.equal(texto, "El viaje se atrasa 5 días.");
});

test("textoCorrimiento: null → null (sin previsualización)", () => {
    const texto = calcularTextoCorrimiento(null);
    assert.equal(texto, null);
});

// ─── Tests: validación del formulario ────────────────────────────────────────

test("puedeEnviar: con fecha y sin enviando → true", () => {
    assert.equal(puedeEnviar("2026-07-08", false), true);
});

test("puedeEnviar: sin fecha → false (aunque no esté enviando)", () => {
    assert.equal(puedeEnviar("", false), false);
    assert.equal(puedeEnviar(null, false), false);
    assert.equal(puedeEnviar(undefined, false), false);
});

test("puedeEnviar: con fecha pero enviando → false (previene doble clic)", () => {
    assert.equal(puedeEnviar("2026-07-08", true), false);
});

// ─── Tests: flujo completo "ida y vuelta" ────────────────────────────────────

test("flujo completo: nueva salida → delta → nuevo regreso", () => {
    // Reserva con salida 1 julio, regreso 10 julio.
    // Se reprograma a salida 8 julio (delta = +7).
    // Nuevo regreso esperado: 17 julio.
    const salidaActual = "2026-07-01";
    const regresoActual = "2026-07-10";
    const nuevaSalida = "2026-07-08";

    const delta = calcularDeltaDias(salidaActual, nuevaSalida);
    assert.equal(delta, 7, "delta debe ser 7 días");

    const nuevoRegreso = calcularNuevaFechaFin(regresoActual, delta);
    assert.equal(nuevoRegreso, "2026-07-17", "nuevo regreso debe ser 10 julio + 7 días");

    const texto = calcularTextoCorrimiento(delta);
    assert.equal(texto, "El viaje se adelanta 7 días.");
});

test("flujo completo: atraso de 3 días", () => {
    const salidaActual = "2026-08-15";
    const regresoActual = "2026-08-22";
    const nuevaSalida = "2026-08-12";

    const delta = calcularDeltaDias(salidaActual, nuevaSalida);
    assert.equal(delta, -3);

    const nuevoRegreso = calcularNuevaFechaFin(regresoActual, delta);
    assert.equal(nuevoRegreso, "2026-08-19");

    const texto = calcularTextoCorrimiento(delta);
    assert.equal(texto, "El viaje se atrasa 3 días.");
});

test("flujo completo: sin fecha de salida en reserva → delta es null", () => {
    // Si la reserva no tiene startDate, el campo salidaActualInput queda "".
    // El delta no se puede calcular → la previsualización no se muestra.
    const salidaActual = "";
    const nuevaSalida = "2026-07-08";

    const delta = calcularDeltaDias(salidaActual, nuevaSalida);
    assert.equal(delta, null, "sin fecha de salida actual el delta es null");

    // Sin delta, el nuevo regreso también es null
    const nuevoRegreso = calcularNuevaFechaFin("2026-07-10", delta);
    assert.equal(nuevoRegreso, null);
});
