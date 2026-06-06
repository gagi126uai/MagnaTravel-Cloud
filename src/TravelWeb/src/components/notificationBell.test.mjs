/**
 * Tests de lógica pura para la campanita de notificaciones (NotificationBell).
 *
 * Cubre:
 *   - formatearDdMm: formato de fechas sin desfase UTC
 *   - textoDeadline: texto por tipo (OperatorPayment/Ticketing) y si está vencida
 *   - tituloCostos: plural/singular del título de la sección
 *   - textoRazonCosto: texto de la línea 2 según la razón del costo
 *   - Cálculo del badge (suma deadlines + costsToConfirm + notificaciones; excluye urgentTrips/supplierDebts)
 *   - Orden de deadlines (vencidas arriba)
 *
 * Por qué lógica pura: las reglas de texto y cálculo no dependen del DOM ni del contexto.
 * Mismo patrón que serviceListCostConfirm.test.mjs y alertsContract.test.mjs.
 *
 * Correr: node --test src/components/notificationBell.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

// ─── Lógica copiada de NotificationBell.jsx ──────────────────────────────────
// Si cambia allá, actualizar acá.

function formatearDdMm(fechaIso) {
    const soloFecha = (fechaIso || "").split("T")[0];
    if (!soloFecha) return "";
    const [, mes, dia] = soloFecha.split("-");
    if (!mes || !dia) return "";
    return `${dia}/${mes}`;
}

function textoDeadline(deadlineKind, fechaIso, isOverdue) {
    const fecha = formatearDdMm(fechaIso);
    if (deadlineKind === "OperatorPayment") {
        return isOverdue ? `Venció señar el ${fecha}` : `⏰ Señar antes del ${fecha}`;
    }
    return isOverdue ? `Venció emitir el ${fecha}` : `⏰ Emitir antes del ${fecha}`;
}

function tituloCostos(cantidad) {
    if (cantidad === 1) return "Tenés 1 costo a confirmar";
    return `Tenés ${cantidad} costos a confirmar`;
}

function textoRazonCosto(reason) {
    if (reason === "NoKnownCost") return "Producto nuevo, sin costo conocido";
    if (reason === "StaleReference") return "El costo viene de una venta vieja";
    return null;
}

/**
 * Calcula el número del badge.
 * Decisión del dueño: urgentTrips y supplierDebts NO se suman (viven en Cobranzas).
 */
function calcularBadge(serviceDeadlines, costsToConfirm, notificacionesSinLeer) {
    return serviceDeadlines.length + costsToConfirm.length + notificacionesSinLeer;
}

/**
 * Ordena los deadlines por fecha ascendente (vencidas arriba).
 * Mismo criterio que SeccionFechasLimite en NotificationBell.jsx.
 */
function ordenarDeadlines(deadlines) {
    return [...deadlines].sort((a, b) => {
        const fa = (a.deadline || "").split("T")[0];
        const fb = (b.deadline || "").split("T")[0];
        if (fa < fb) return -1;
        if (fa > fb) return 1;
        return 0;
    });
}

// ─── Tests: formatearDdMm ─────────────────────────────────────────────────────

test("formatearDdMm: formatea fecha ISO sin año", () => {
    assert.equal(formatearDdMm("2025-11-30"), "30/11");
    assert.equal(formatearDdMm("2026-01-05"), "05/01");
    assert.equal(formatearDdMm("2026-12-31"), "31/12");
});

test("formatearDdMm: ignora la parte de hora (T...) para evitar desfase UTC", () => {
    // Si hubiera conversión UTC, "2025-11-30T03:00:00Z" en ART sería "30/11",
    // pero con new Date().toLocaleDateString() en UTC-3 podría salir "29/11".
    // Este helper toma solo la parte YYYY-MM-DD, así que siempre devuelve "30/11".
    assert.equal(formatearDdMm("2025-11-30T03:00:00Z"), "30/11");
    assert.equal(formatearDdMm("2026-06-15T00:00:00"), "15/06");
});

test("formatearDdMm: devuelve vacío con entrada vacía o nula", () => {
    assert.equal(formatearDdMm(""), "");
    assert.equal(formatearDdMm(null), "");
    assert.equal(formatearDdMm(undefined), "");
});

// ─── Tests: textoDeadline ─────────────────────────────────────────────────────

test("textoDeadline: OperatorPayment vigente muestra emoji y texto de señar", () => {
    const texto = textoDeadline("OperatorPayment", "2026-12-30", false);
    assert.equal(texto, "⏰ Señar antes del 30/12");
});

test("textoDeadline: OperatorPayment vencida muestra texto de vencimiento sin emoji", () => {
    const texto = textoDeadline("OperatorPayment", "2026-01-10", true);
    assert.equal(texto, "Venció señar el 10/01");
    // Sin emoji
    assert.ok(!texto.includes("⏰"), "Las vencidas no llevan emoji");
});

test("textoDeadline: Ticketing vigente muestra emoji y texto de emisión", () => {
    const texto = textoDeadline("Ticketing", "2026-11-05", false);
    assert.equal(texto, "⏰ Emitir antes del 05/11");
});

test("textoDeadline: Ticketing vencida muestra texto de vencimiento sin emoji", () => {
    const texto = textoDeadline("Ticketing", "2026-03-22", true);
    assert.equal(texto, "Venció emitir el 22/03");
    assert.ok(!texto.includes("⏰"), "Las vencidas no llevan emoji");
});

// ─── Tests: tituloCostos (plural/singular) ────────────────────────────────────

test("tituloCostos: 1 item usa singular", () => {
    assert.equal(tituloCostos(1), "Tenés 1 costo a confirmar");
});

test("tituloCostos: más de 1 item usa plural", () => {
    assert.equal(tituloCostos(2), "Tenés 2 costos a confirmar");
    assert.equal(tituloCostos(5), "Tenés 5 costos a confirmar");
    assert.equal(tituloCostos(10), "Tenés 10 costos a confirmar");
});

// ─── Tests: textoRazonCosto ───────────────────────────────────────────────────

test("textoRazonCosto: NoKnownCost muestra texto de producto nuevo", () => {
    assert.equal(textoRazonCosto("NoKnownCost"), "Producto nuevo, sin costo conocido");
});

test("textoRazonCosto: StaleReference muestra texto de venta vieja", () => {
    assert.equal(textoRazonCosto("StaleReference"), "El costo viene de una venta vieja");
});

test("textoRazonCosto: null devuelve null (sin línea 2)", () => {
    assert.equal(textoRazonCosto(null), null);
});

test("textoRazonCosto: razón desconocida devuelve null (sin línea 2)", () => {
    assert.equal(textoRazonCosto("OtroValor"), null);
    assert.equal(textoRazonCosto(undefined), null);
    assert.equal(textoRazonCosto(""), null);
});

// ─── Tests: cálculo del badge ────────────────────────────────────────────────

test("badge: suma deadlines + costos + notificaciones sin leer", () => {
    const deadlines = [{ deadline: "2026-12-01" }, { deadline: "2026-12-02" }];
    const costos = [{ reason: "NoKnownCost" }];
    const notificaciones = 3;

    // 2 + 1 + 3 = 6
    assert.equal(calcularBadge(deadlines, costos, notificaciones), 6);
});

test("badge: sin avisos nuevos y sin notificaciones devuelve 0", () => {
    assert.equal(calcularBadge([], [], 0), 0);
});

test("badge: NO suma urgentTrips ni supplierDebts (no van al badge)", () => {
    // Decisión del dueño: esos buckets viven en las tarjetas de Cobranzas, no en el badge.
    // El badge solo toma lo que pasa como parámetro: deadlines + costos + notif.
    const deadlines = [];
    const costos = [];
    const notificaciones = 0;
    // urgentTrips y supplierDebts existen en el contexto pero NO se pasan al cálculo
    assert.equal(calcularBadge(deadlines, costos, notificaciones), 0);
});

test("badge: solo deadlines sin notificaciones ni costos", () => {
    const deadlines = [{ deadline: "2026-12-01" }, { deadline: "2026-12-02" }, { deadline: "2026-12-03" }];
    assert.equal(calcularBadge(deadlines, [], 0), 3);
});

test("badge: solo costos sin notificaciones ni deadlines", () => {
    const costos = [{ reason: null }, { reason: "NoKnownCost" }];
    assert.equal(calcularBadge([], costos, 0), 2);
});

test("badge: solo notificaciones sin avisos de flag", () => {
    assert.equal(calcularBadge([], [], 7), 7);
});

// ─── Tests: orden de deadlines ────────────────────────────────────────────────

test("ordenarDeadlines: ordena por fecha ascendente (vencidas quedan arriba)", () => {
    const items = [
        { deadline: "2026-12-15", isOverdue: false },
        { deadline: "2026-01-10", isOverdue: true },  // vencida = fecha menor = va arriba
        { deadline: "2026-06-30", isOverdue: false },
    ];

    const resultado = ordenarDeadlines(items);

    assert.equal(resultado[0].deadline, "2026-01-10", "La más antigua queda primero");
    assert.equal(resultado[1].deadline, "2026-06-30");
    assert.equal(resultado[2].deadline, "2026-12-15");
});

test("ordenarDeadlines: lista vacía devuelve lista vacía", () => {
    assert.deepEqual(ordenarDeadlines([]), []);
});

test("ordenarDeadlines: no muta el array original", () => {
    const original = [
        { deadline: "2026-12-01" },
        { deadline: "2026-01-01" },
    ];
    const copia = [...original];
    ordenarDeadlines(original);
    // El array original no debe haber cambiado de orden
    assert.deepEqual(original, copia, "ordenarDeadlines no debe mutar el original");
});
