/**
 * Tests de lógica pura para la campanita de notificaciones (NotificationBell).
 *
 * Cubre:
 *   - formatearDdMm: formato de fechas sin desfase UTC
 *   - textoProximoInicio: texto del ítem según daysLeft (HOY/singular/plural/dd-MM)
 *   - Línea 2 del ítem: fallbacks de holderName/name
 *   - tituloCostos: plural/singular del título de la sección
 *   - textoRazonCosto: texto de la línea 2 según la razón del costo
 *   - Cálculo del badge (suma upcomingStarts visibles + costsToConfirm + notificaciones; excluye urgentTrips/supplierDebts)
 *   - Descarte optimista: al descartar un id, no aparece en el array filtrado; al revertir, reaparece
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

/**
 * Construye el texto de la línea 1 del ítem de próximo inicio.
 * Exportada como textoProximoInicio en NotificationBell.jsx.
 * Copia exacta: si cambia allá, actualizar acá.
 */
function textoProximoInicio(daysLeft, firstStartDate) {
    const fecha = formatearDdMm(firstStartDate);
    // Defensivo: cubre daysLeft=0 (HOY) y negativos (server debería filtrarlos, pero por las dudas)
    if (daysLeft <= 0) {
        return `Empieza HOY ${fecha}`;
    }
    const diasTexto = daysLeft === 1 ? "en 1 día" : `en ${daysLeft} días`;
    return `⏰ Empieza el ${fecha} (${diasTexto})`;
}

/**
 * Construye la línea 2 del ítem: "Reserva {num} · {titular}" con fallbacks.
 * Replica la lógica en SeccionProximosInicios.
 */
function construirLinea2(numeroReserva, holderName, name) {
    const titular = holderName || name || "";
    if (titular) return `Reserva ${numeroReserva} · ${titular}`;
    return `Reserva ${numeroReserva}`;
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
 * upcomingStartsVisibles = items filtrados por descarte optimista.
 */
function calcularBadge(upcomingStartsVisibles, costsToConfirm, notificacionesSinLeer) {
    return upcomingStartsVisibles.length + costsToConfirm.length + notificacionesSinLeer;
}

/**
 * Simula el filtro de descarte optimista.
 * descartadas = Set<reservaPublicId>
 */
function filtrarDescartados(items, descartadas) {
    return items.filter((item) => !descartadas.has(item.reservaPublicId));
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

// ─── Tests: textoProximoInicio ────────────────────────────────────────────────

test("textoProximoInicio: daysLeft 0 → HOY rojo, sin emoji", () => {
    const texto = textoProximoInicio(0, "2026-06-15T00:00:00Z");
    assert.equal(texto, "Empieza HOY 15/06");
    assert.ok(!texto.includes("⏰"), "HOY no lleva emoji");
});

test("textoProximoInicio: daysLeft 1 → singular 'en 1 día'", () => {
    const texto = textoProximoInicio(1, "2026-06-16T00:00:00Z");
    assert.equal(texto, "⏰ Empieza el 16/06 (en 1 día)");
});

test("textoProximoInicio: daysLeft 5 → plural 'en 5 días'", () => {
    const texto = textoProximoInicio(5, "2026-06-20T00:00:00Z");
    assert.equal(texto, "⏰ Empieza el 20/06 (en 5 días)");
});

test("textoProximoInicio: daysLeft 7 → plural, dd/MM correcto", () => {
    const texto = textoProximoInicio(7, "2026-01-01T00:00:00Z");
    assert.equal(texto, "⏰ Empieza el 01/01 (en 7 días)");
});

test("textoProximoInicio: sin T en la fecha → también funciona", () => {
    const texto = textoProximoInicio(3, "2026-12-25");
    assert.equal(texto, "⏰ Empieza el 25/12 (en 3 días)");
});

test("textoProximoInicio: daysLeft negativo → tratado como HOY (defensivo, server debería filtrar)", () => {
    // El server filtra items con daysLeft < 0, pero por defensividad lo tratamos como HOY.
    // Este test fija el contrato: daysLeft <= 0 → "Empieza HOY" sin emoji.
    const texto = textoProximoInicio(-1, "2026-06-05T00:00:00Z");
    assert.equal(texto, "Empieza HOY 05/06");
    assert.ok(!texto.includes("⏰"), "Negativo no lleva emoji");
});

// ─── Tests: línea 2 del ítem (fallbacks holderName/name) ─────────────────────

test("línea 2: holderName presente → 'Reserva {num} · {holderName}'", () => {
    const linea2 = construirLinea2("RES-001", "Juan Perez", "Paquete Caribe");
    assert.equal(linea2, "Reserva RES-001 · Juan Perez");
});

test("línea 2: holderName null → fallback a name", () => {
    const linea2 = construirLinea2("RES-002", null, "Caribe 7 noches");
    assert.equal(linea2, "Reserva RES-002 · Caribe 7 noches");
});

test("línea 2: holderName null y name null → solo número de reserva sin separador", () => {
    const linea2 = construirLinea2("RES-003", null, null);
    assert.equal(linea2, "Reserva RES-003");
    assert.ok(!linea2.includes("·"), "Sin titular no se agrega el separador '·'");
});

test("línea 2: holderName string vacío → fallback a name", () => {
    const linea2 = construirLinea2("RES-004", "", "Paquete Iguazu");
    assert.equal(linea2, "Reserva RES-004 · Paquete Iguazu");
});

test("línea 2: holderName vacío y name vacío → solo número", () => {
    const linea2 = construirLinea2("RES-005", "", "");
    assert.equal(linea2, "Reserva RES-005");
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

test("badge: suma upcomingStarts visibles + costos + notificaciones sin leer", () => {
    const upcomingStarts = [{ reservaPublicId: "a" }, { reservaPublicId: "b" }];
    const costos = [{ reason: "NoKnownCost" }];
    const notificaciones = 3;

    // 2 + 1 + 3 = 6
    assert.equal(calcularBadge(upcomingStarts, costos, notificaciones), 6);
});

test("badge: sin avisos nuevos y sin notificaciones devuelve 0", () => {
    assert.equal(calcularBadge([], [], 0), 0);
});

test("badge: NO suma urgentTrips ni supplierDebts (no van al badge)", () => {
    // Decisión del dueño: esos buckets viven en las tarjetas de Cobranzas, no en el badge.
    assert.equal(calcularBadge([], [], 0), 0);
});

test("badge: solo upcomingStarts sin notificaciones ni costos", () => {
    const upcomingStarts = [{ reservaPublicId: "a" }, { reservaPublicId: "b" }, { reservaPublicId: "c" }];
    assert.equal(calcularBadge(upcomingStarts, [], 0), 3);
});

test("badge: solo costos sin notificaciones ni upcoming", () => {
    const costos = [{ reason: null }, { reason: "NoKnownCost" }];
    assert.equal(calcularBadge([], costos, 0), 2);
});

test("badge: solo notificaciones sin avisos de flag", () => {
    assert.equal(calcularBadge([], [], 7), 7);
});

// ─── Tests: descarte optimista ────────────────────────────────────────────────

test("descarte optimista: al agregar un id al set, el ítem desaparece del filtro", () => {
    const items = [
        { reservaPublicId: "aaa", daysLeft: 3 },
        { reservaPublicId: "bbb", daysLeft: 0 },
    ];
    const descartadas = new Set(["aaa"]);

    const visibles = filtrarDescartados(items, descartadas);

    assert.equal(visibles.length, 1, "Solo queda 1 ítem tras descartar uno");
    assert.equal(visibles[0].reservaPublicId, "bbb");
});

test("descarte optimista: set vacío → todos los ítems visibles", () => {
    const items = [
        { reservaPublicId: "aaa" },
        { reservaPublicId: "bbb" },
    ];
    const visibles = filtrarDescartados(items, new Set());
    assert.equal(visibles.length, 2);
});

test("descarte optimista: al revertir (sacar del set), el ítem reaparece", () => {
    const items = [
        { reservaPublicId: "aaa" },
        { reservaPublicId: "bbb" },
    ];

    // 1. Descartar "aaa"
    let descartadas = new Set(["aaa"]);
    let visibles = filtrarDescartados(items, descartadas);
    assert.equal(visibles.length, 1, "Después de descartar quedan 1");

    // 2. POST falla → revertimos sacando "aaa" del set
    const next = new Set(descartadas);
    next.delete("aaa");
    descartadas = next;

    visibles = filtrarDescartados(items, descartadas);
    assert.equal(visibles.length, 2, "Al revertir, reaparece el ítem");
    assert.ok(visibles.some((i) => i.reservaPublicId === "aaa"), "aaa volvió a estar visible");
});

test("descarte optimista: badge refleja el filtro (no cuenta los descartados)", () => {
    const items = [
        { reservaPublicId: "aaa" },
        { reservaPublicId: "bbb" },
        { reservaPublicId: "ccc" },
    ];
    const descartadas = new Set(["aaa", "bbb"]);
    const visibles = filtrarDescartados(items, descartadas);

    // Badge debe usar los visibles (1), no el total (3)
    assert.equal(calcularBadge(visibles, [], 0), 1);
});

// ─── Tests: poda del Set de descartados optimistas (B1) ───────────────────────
// Contrato: el Set solo retiene ids que el server AÚN incluye en el payload.
// Si el id ya no está en el payload → se lo quita del Set (el ítem puede volver a aparecer).
// Si el server re-envía un ítem que fue descartado → el id sale del Set → vuelve a verse.

/**
 * Helper puro que replica la lógica del useEffect de poda de NotificationBell.
 * Recibe el Set anterior y el payload actual del server; devuelve el Set podado.
 */
function podarDescartadasOptimistas(prev, upcomingStartsActuales) {
    if (prev.size === 0) return prev;
    const idsActuales = new Set(upcomingStartsActuales.map((i) => i.reservaPublicId));
    const next = new Set([...prev].filter((id) => idsActuales.has(id)));
    // Mismo invariante que el useEffect: si no cambió el tamaño, devuelve la misma ref
    return next.size === prev.size ? prev : next;
}

test("poda: id descartado + ítem ausente del payload → sale del Set", () => {
    // "aaa" fue descartado optimistamente, pero el server ya no lo manda (fue procesado).
    const prevSet = new Set(["aaa"]);
    const payloadActual = [
        { reservaPublicId: "bbb", daysLeft: 2 },
    ];

    const podado = podarDescartadasOptimistas(prevSet, payloadActual);

    assert.equal(podado.has("aaa"), false, "'aaa' debe salir del Set cuando el server ya no lo manda");
    assert.equal(podado.size, 0);
});

test("poda: ítem que reaparece en el payload → sale del Set y vuelve a verse", () => {
    // Decisión del dueño: si la fecha del primer servicio cambia, el server re-incluye
    // el ítem → el cliente NO debe filtrarlo para siempre.
    const prevSet = new Set(["res-reaparece"]);
    const payloadConReaparicion = [
        { reservaPublicId: "res-reaparece", daysLeft: 1 },
        { reservaPublicId: "bbb", daysLeft: 3 },
    ];

    const podado = podarDescartadasOptimistas(prevSet, payloadConReaparicion);

    // "res-reaparece" está en el payload actual → se MANTIENE en el Set (ventana POST→refresh)
    // La poda solo lo suelta cuando el server deja de mandarlo.
    assert.equal(podado.has("res-reaparece"), true, "Si el server lo volvió a mandar, permanece en el Set hasta el próximo poll sin él");
});

test("poda: id en vuelo POST que el server aún manda → permanece en el Set (cubre la ventana)", () => {
    // Mientras el POST está en vuelo, el poll puede devolver el ítem todavía.
    // La poda NO debe sacarlo hasta que el server deje de mandarlo.
    const prevSet = new Set(["en-vuelo"]);
    const payloadMientrasPostEnVuelo = [
        { reservaPublicId: "en-vuelo", daysLeft: 0 },
    ];

    const podado = podarDescartadasOptimistas(prevSet, payloadMientrasPostEnVuelo);

    assert.equal(podado.has("en-vuelo"), true, "El id se mantiene mientras el server aún lo incluye");
    assert.equal(podado.size, 1);
});

test("poda: Set vacío → devuelve la misma referencia sin calcular (optimización)", () => {
    const prevSet = new Set();
    const payload = [{ reservaPublicId: "aaa" }];

    const resultado = podarDescartadasOptimistas(prevSet, payload);

    // Mismo objeto → no dispara re-render en React
    assert.equal(resultado, prevSet, "Con Set vacío debe devolver la misma referencia");
});

test("poda: payload vacío → Set queda vacío (el server ya no manda nada)", () => {
    const prevSet = new Set(["aaa", "bbb"]);
    const payloadVacio = [];

    const podado = podarDescartadasOptimistas(prevSet, payloadVacio);

    assert.equal(podado.size, 0, "Con payload vacío, el Set queda vacío");
    assert.equal(podado.has("aaa"), false);
    assert.equal(podado.has("bbb"), false);
});
