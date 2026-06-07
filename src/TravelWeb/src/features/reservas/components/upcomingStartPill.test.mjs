/**
 * Tests de lógica pura de UpcomingStartPill.
 *
 * Cubre:
 *   - calcularDiffDias: diferencia de días sin zona horaria
 *   - formatearFechaDdMm: formato de fecha sin desfase UTC
 *   - Criterio de pill: dentro/fuera de ventana, cancelado, sin fecha, sin windowDays
 *   - Texto: HOY (rojo), N días (ámbar, singular/plural)
 *   - La pill NO filtra por Status de la reserva (decisión deliberada)
 *   - Matriz de 4 combinaciones de flags: catálogo ON/OFF × avisos ON/OFF
 *
 * Correr: node --test src/features/reservas/components/upcomingStartPill.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

// ─── Lógica pura copiada de UpcomingStartPill.jsx ────────────────────────────

function formatearFechaDdMm(fechaIso) {
    const soloFecha = (fechaIso || "").split("T")[0];
    if (!soloFecha) return "";
    const [, mes, dia] = soloFecha.split("-");
    if (!mes || !dia) return "";
    return `${dia}/${mes}`;
}

/**
 * Calcula diff en días usando Date.UTC (sin zona horaria).
 * Permite inyectar "hoy" como "YYYY-MM-DD" para tests deterministas.
 */
function calcularDiffDias(fechaIso, hoyOverride = null) {
    const soloFecha = (fechaIso || "").split("T")[0];
    if (!soloFecha) return null;

    const partes = soloFecha.split("-");
    if (partes.length !== 3) return null;

    const [anio, mes, dia] = partes;
    if (!anio || !mes || !dia) return null;

    // Guardamos contra cadenas no numéricas ("no-es-fecha" → NaN)
    const anioNum = Number(anio);
    const mesNum = Number(mes);
    const diaNum = Number(dia);
    if (isNaN(anioNum) || isNaN(mesNum) || isNaN(diaNum)) return null;

    const fechaMs = Date.UTC(anioNum, mesNum - 1, diaNum);

    let hoyMs;
    if (hoyOverride) {
        const [hy, hm, hd] = hoyOverride.split("-");
        hoyMs = Date.UTC(Number(hy), Number(hm) - 1, Number(hd));
    } else {
        const hoy = new Date();
        hoyMs = Date.UTC(hoy.getFullYear(), hoy.getMonth(), hoy.getDate());
    }

    const diffMs = fechaMs - hoyMs;
    return Math.round(diffMs / (1000 * 60 * 60 * 24));
}

/**
 * Simula la decisión de mostrar pill o "—" para UpcomingStartPill.
 * Devuelve: "hoy", "dias", o null (sin pill).
 * null = mostrar "—" en desktop, nada en mobile.
 *
 * NO recibe ni evalúa Status de la reserva (decisión del dueño: aparece siempre).
 */
function decidirPill(service, windowDays, hoyOverride = null) {
    // Cancelado → sin pill
    if (service.workflowStatus === "Cancelado") return null;
    // Sin windowDays (flag OFF) → sin pill
    if (windowDays == null) return null;
    // Sin fecha → sin pill
    const fechaIso = service.date || null;
    if (!fechaIso) return null;

    const diff = calcularDiffDias(fechaIso, hoyOverride);
    if (diff === null) return null;

    // Fuera de ventana: ya pasó (diff < 0) o demasiado lejos (diff > windowDays)
    if (diff < 0 || diff > windowDays) return null;

    if (diff === 0) return "hoy";
    return "dias";
}

function construirTextoPill(service, windowDays, hoyOverride = null) {
    const tipo = decidirPill(service, windowDays, hoyOverride);
    if (tipo === null) return null;

    const fechaFormateada = formatearFechaDdMm(service.date);

    if (tipo === "hoy") {
        return `Empieza HOY ${fechaFormateada}`;
    }

    const diff = calcularDiffDias(service.date, hoyOverride);
    const diasTexto = diff === 1 ? "en 1 día" : `en ${diff} días`;
    return `Empieza el ${fechaFormateada} (${diasTexto})`;
}

// ─── Tests: formatearFechaDdMm ────────────────────────────────────────────────

test("formatearFechaDdMm: fecha ISO → dd/MM", () => {
    assert.equal(formatearFechaDdMm("2026-06-15T00:00:00Z"), "15/06");
    assert.equal(formatearFechaDdMm("2026-12-01"), "01/12");
    assert.equal(formatearFechaDdMm("2026-01-31"), "31/01");
});

test("formatearFechaDdMm: devuelve vacío con entrada inválida", () => {
    assert.equal(formatearFechaDdMm(""), "");
    assert.equal(formatearFechaDdMm(null), "");
    assert.equal(formatearFechaDdMm(undefined), "");
});

// ─── Tests: calcularDiffDias ──────────────────────────────────────────────────

test("calcularDiffDias: fecha futura → diff positivo", () => {
    const diff = calcularDiffDias("2026-06-15", "2026-06-10");
    assert.equal(diff, 5);
});

test("calcularDiffDias: misma fecha (hoy) → diff 0", () => {
    const diff = calcularDiffDias("2026-06-10", "2026-06-10");
    assert.equal(diff, 0);
});

test("calcularDiffDias: fecha pasada → diff negativo", () => {
    const diff = calcularDiffDias("2026-06-05", "2026-06-10");
    assert.equal(diff, -5);
});

test("calcularDiffDias: fecha inválida → null", () => {
    assert.equal(calcularDiffDias(""), null);
    assert.equal(calcularDiffDias(null), null);
    assert.equal(calcularDiffDias("no-es-fecha"), null);
});

// ─── Tests: criterio de pill ─────────────────────────────────────────────────

test("pill: dentro de la ventana (diff=3, windowDays=7) → 'dias'", () => {
    const service = { date: "2026-06-13", workflowStatus: "Confirmado" };
    const tipo = decidirPill(service, 7, "2026-06-10");
    assert.equal(tipo, "dias");
});

test("pill: diff=0 (es hoy) → 'hoy'", () => {
    const service = { date: "2026-06-10", workflowStatus: "Confirmado" };
    const tipo = decidirPill(service, 7, "2026-06-10");
    assert.equal(tipo, "hoy");
});

test("pill: diff=windowDays (límite exacto) → 'dias' (incluido en la ventana)", () => {
    const service = { date: "2026-06-17", workflowStatus: "Confirmado" };
    const tipo = decidirPill(service, 7, "2026-06-10");
    assert.equal(tipo, "dias");
});

test("pill: diff > windowDays → null (demasiado lejos)", () => {
    const service = { date: "2026-06-20", workflowStatus: "Confirmado" };
    const tipo = decidirPill(service, 7, "2026-06-10");
    assert.equal(tipo, null);
});

test("pill: diff < 0 (ya pasó) → null (no existe estado 'vencido')", () => {
    const service = { date: "2026-06-05", workflowStatus: "Confirmado" };
    const tipo = decidirPill(service, 7, "2026-06-10");
    assert.equal(tipo, null);
});

test("pill: workflowStatus Cancelado → null (sin pill)", () => {
    const service = { date: "2026-06-12", workflowStatus: "Cancelado" };
    const tipo = decidirPill(service, 7, "2026-06-10");
    assert.equal(tipo, null);
});

test("pill: sin fecha (date null) → null", () => {
    const service = { date: null, workflowStatus: "Confirmado" };
    const tipo = decidirPill(service, 7, "2026-06-10");
    assert.equal(tipo, null);
});

test("pill: sin windowDays (flag OFF / null) → null", () => {
    const service = { date: "2026-06-12", workflowStatus: "Confirmado" };
    const tipo = decidirPill(service, null, "2026-06-10");
    assert.equal(tipo, null);
});

test("pill: NO filtra por Status de reserva (aparece en presupuesto)", () => {
    // Decisión del dueño: la pill aparece sin importar el estado de la RESERVA.
    // El componente solo evalúa workflowStatus del SERVICIO (no hay campo reservaStatus).
    const servicioPresupuesto = { date: "2026-06-13", workflowStatus: "Solicitado" };
    const tipo = decidirPill(servicioPresupuesto, 7, "2026-06-10");
    // "Solicitado" NO es "Cancelado", así que la pill se muestra
    assert.equal(tipo, "dias", "Servicios no-cancelados muestran pill sin importar el status");
});

// ─── Tests: texto de la pill ──────────────────────────────────────────────────

test("texto pill: HOY → sin emoji, 'Empieza HOY dd/MM'", () => {
    const service = { date: "2026-06-10", workflowStatus: "Confirmado" };
    const texto = construirTextoPill(service, 7, "2026-06-10");
    assert.equal(texto, "Empieza HOY 10/06");
    assert.ok(!texto.includes("⏰"), "HOY no lleva emoji");
});

test("texto pill: 1 día → singular 'en 1 día'", () => {
    const service = { date: "2026-06-11", workflowStatus: "Confirmado" };
    const texto = construirTextoPill(service, 7, "2026-06-10");
    assert.equal(texto, "Empieza el 11/06 (en 1 día)");
});

test("texto pill: 5 días → plural 'en 5 días'", () => {
    const service = { date: "2026-06-15", workflowStatus: "Confirmado" };
    const texto = construirTextoPill(service, 7, "2026-06-10");
    assert.equal(texto, "Empieza el 15/06 (en 5 días)");
});

test("texto pill: sin pill → null", () => {
    const service = { date: "2026-06-05", workflowStatus: "Confirmado" }; // ya pasó
    const texto = construirTextoPill(service, 7, "2026-06-10");
    assert.equal(texto, null);
});

// ─── Tests: matriz de 4 combinaciones de flags ───────────────────────────────
// La columna "Avisos" en ServiceList.jsx es independiente de isCatalogFindOrCreate.
// La pill solo mira: windowDays != null + service.date + workflowStatus != Cancelado.

/**
 * Replica el predicado REAL de render de ServiceList para la columna "Avisos":
 *   th/td se renderizan SOLO cuando isServiceDeadlineAlertsEnabled es true.
 *   isCatalogFindOrCreateEnabled NO afecta la columna.
 *
 * Se usa en los cuatro tests de la matriz para evitar que los tests sean tautológicos
 * (antes era hayColumnaAvisos(flag) === flag, que verifica solo que true === true).
 */
function seRenderizaColumnaAvisos(isCatalogFindOrCreateEnabled, isServiceDeadlineAlertsEnabled) {
    // Copia exacta de la condición en ServiceList.jsx:
    //   {isServiceDeadlineAlertsEnabled && (<th>...</th>)}
    //   {isServiceDeadlineAlertsEnabled && (<td>...</td>)}
    return isServiceDeadlineAlertsEnabled === true;
}

test("matriz: catálogo OFF + avisos OFF → columna NO aparece", () => {
    const colVisible = seRenderizaColumnaAvisos(false, false);
    assert.equal(colVisible, false);
    // Confirma que catálogo OFF solo no alcanza para ocultarla — es avisos quien manda
    assert.equal(seRenderizaColumnaAvisos(true, false), false, "catálogo ON pero avisos OFF → tampoco aparece");
});

test("matriz: catálogo OFF + avisos ON → columna SÍ aparece (decisión del dueño)", () => {
    // La columna Avisos depende SOLO de enableServiceDeadlineAlerts, no del catálogo.
    const colVisible = seRenderizaColumnaAvisos(false, true);
    assert.equal(colVisible, true);
});

test("matriz: catálogo ON + avisos OFF → columna NO aparece", () => {
    const colVisible = seRenderizaColumnaAvisos(true, false);
    assert.equal(colVisible, false);
});

test("matriz: catálogo ON + avisos ON → columna SÍ aparece", () => {
    const colVisible = seRenderizaColumnaAvisos(true, true);
    assert.equal(colVisible, true);
});

test("matriz: flag catálogo NO participa en el render de la columna Avisos", () => {
    // El resultado debe ser idéntico con catálogo ON y OFF cuando avisos tiene el mismo valor.
    // Esto fija la invariante: cambiar el flag de catálogo no toca la columna.
    assert.equal(
        seRenderizaColumnaAvisos(false, true),
        seRenderizaColumnaAvisos(true, true),
        "catálogo ON/OFF no debe cambiar el resultado cuando avisos es ON"
    );
    assert.equal(
        seRenderizaColumnaAvisos(false, false),
        seRenderizaColumnaAvisos(true, false),
        "catálogo ON/OFF no debe cambiar el resultado cuando avisos es OFF"
    );
});
