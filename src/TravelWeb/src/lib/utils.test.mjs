/**
 * Tests de formatDate() — bug "fechas corridas un día" (reportado por el dueño 2026-07-16).
 *
 * Este archivo importa el módulo REAL (utils.js no tiene JSX, así que a diferencia de
 * otros tests .mjs del proyecto no hace falta copiar la lógica). Mismo patrón que
 * moneyStatus.test.mjs.
 *
 * Reporte del bug: el dueño cargó "23/05/2026" en un input de fecha y la pantalla le
 * mostró "22/05/2026". Causa raíz: el backend guarda las fechas-solo-día (sin hora,
 * ej. fecha de salida de un viaje) como medianoche UTC. formatDate() las pasaba por
 * new Date(...).toLocaleDateString() en hora LOCAL del navegador — en Argentina
 * (UTC-3) la medianoche UTC del día 23 cae a las 21:00 del día 22, así que se veía
 * el día anterior.
 *
 * Cómo correr: node --test src/lib/utils.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

import { formatDate, formatDateTime, hoyArgentina } from "./utils.js";

// ─── Caso 1: fecha-solo-día cruda del input (sin pasar por el backend) ───────

test("formatDate: fecha-solo-día 'YYYY-MM-DD' → mismo día, sin corrimiento", () => {
    assert.equal(formatDate("2026-05-23"), "23/05/2026");
});

// ─── Caso 2: medianoche UTC devuelta por el backend (distintas variantes) ────

test("formatDate: medianoche UTC con 'Z' → mismo día calendario del string", () => {
    assert.equal(formatDate("2026-05-23T00:00:00Z"), "23/05/2026");
});

test("formatDate: medianoche UTC con milisegundos '.000Z' → mismo día", () => {
    assert.equal(formatDate("2026-05-23T00:00:00.000Z"), "23/05/2026");
});

test("formatDate: medianoche UTC con 7 dígitos de fracción (round-trip .NET) → mismo día", () => {
    // System.Text.Json puede serializar con hasta 7 dígitos de fracción de segundo.
    assert.equal(formatDate("2026-05-23T00:00:00.0000000Z"), "23/05/2026");
});

test("formatDate: medianoche SIN sufijo Z (Kind=Unspecified) → mismo día", () => {
    assert.equal(formatDate("2026-05-23T00:00:00"), "23/05/2026");
});

// ─── Caso 3: instante real con hora (NO debe tocarse — sigue en hora local) ──

test("formatDate: timestamp real con hora → se sigue mostrando en hora local, sin cambios", () => {
    // createdAt de una factura, por ejemplo: tiene una hora real, no es medianoche.
    // El comportamiento acá es el de SIEMPRE (new Date().toLocaleDateString()).
    const esperado = new Date("2026-05-23T14:30:00Z").toLocaleDateString("es-AR", {
        day: "2-digit", month: "2-digit", year: "numeric",
    });
    assert.equal(formatDate("2026-05-23T14:30:00Z"), esperado);
});

test("formatDate: instante con offset explícito distinto de Z → comportamiento local (no es fecha-solo-día)", () => {
    const esperado = new Date("2026-05-23T10:00:00-03:00").toLocaleDateString("es-AR", {
        day: "2-digit", month: "2-digit", year: "numeric",
    });
    assert.equal(formatDate("2026-05-23T10:00:00-03:00"), esperado);
});

// ─── Casos vacíos / nulos ──────────────────────────────────────────────────

test("formatDate: null → '-'", () => {
    assert.equal(formatDate(null), "-");
});

test("formatDate: undefined → '-'", () => {
    assert.equal(formatDate(undefined), "-");
});

test("formatDate: cadena vacía → '-'", () => {
    assert.equal(formatDate(""), "-");
});

// ─── Gate data-exposure (2026-07-23): nunca mostrarle al usuario un texto técnico ───
// como "Invalid Date" — un no-programador no entiende eso, y es justo el tipo de fuga
// que el gate de exposición de datos internos existe para atrapar.

test("formatDate: string basura (dato sucio, no es fecha) → '-', nunca el texto 'Invalid Date'", () => {
    const resultado = formatDate("esto-no-es-una-fecha");
    assert.equal(resultado, "-");
    assert.ok(!resultado.includes("Invalid"), `No debe filtrar texto técnico, recibido: "${resultado}"`);
});

test("formatDateTime: string basura (dato sucio, no es fecha) → '-', nunca el texto 'Invalid Date'", () => {
    const resultado = formatDateTime("esto-no-es-una-fecha");
    assert.equal(resultado, "-");
    assert.ok(!resultado.includes("Invalid"), `No debe filtrar texto técnico, recibido: "${resultado}"`);
});

// ─── Casos borde de calendario pedidos explícitamente por el dueño ──────────
// (fin de mes, fin de año, 29/02 bisiesto — no solo el día, también mes y año)

test("formatDate: fin de mes (31 de mayo, medianoche UTC) → no salta a junio", () => {
    assert.equal(formatDate("2026-05-31T00:00:00Z"), "31/05/2026");
});

test("formatDate: fin de año (31 de diciembre, medianoche UTC) → no salta al año siguiente", () => {
    assert.equal(formatDate("2026-12-31T00:00:00Z"), "31/12/2026");
});

test("formatDate: 1 de enero (medianoche UTC) → no retrocede al 31 de diciembre anterior", () => {
    // Este es EXACTAMENTE el caso que rompía antes del fix: en UTC-3 el 1/1 00:00 UTC
    // cae el 31/12 a las 21:00 local — el bug original mostraba el año anterior.
    assert.equal(formatDate("2026-01-01T00:00:00Z"), "01/01/2026");
});

test("formatDate: 29 de febrero en año bisiesto (medianoche UTC) → se muestra correctamente", () => {
    assert.equal(formatDate("2028-02-29T00:00:00Z"), "29/02/2028");
});

// ─── Ida y vuelta: fecha del input → ISO simulado del backend → formatDate ──

test("formatDate: ida y vuelta completa — el día que el usuario tipeó es el día que ve", () => {
    // Simula: el usuario carga "23/05/2026" en el input (value="2026-05-23"),
    // el backend lo guarda y lo devuelve como medianoche UTC.
    const valorDelInput = "2026-05-23";
    const respuestaSimuladaDelBackend = `${valorDelInput}T00:00:00Z`;
    assert.equal(formatDate(respuestaSimuladaDelBackend), "23/05/2026");
});

// ─── Bug real 2026-07-22: cobro en el extracto mostraba un día menos ────────
//
// Repro exacto reportado por el dueño: reserva "F-2026-1112", cobro de Transferencia $150
// fechado 22/07/2026, el extracto mostraba "21/7/2026". Causa raíz: EstadoCuentaExtracto.jsx
// (y sus 2 copias paralelas: extracto del proveedor y extracto del cliente 360) llamaban
// directo a `new Date(linea.date).toLocaleDateString("es-AR")` en vez de reusar formatDate().
// El valor real que devuelve el backend para linea.date es exactamente el string de abajo
// (confirmado contra la fila real de Postgres de esa reserva).

test("formatDate: repro EXACTO del bug reportado 2026-07-22 (cobro 22/07 no debe verse 21/7)", () => {
    assert.equal(formatDate("2026-07-22T00:00:00Z"), "22/07/2026");
});

// ─── formatDateTime(): mismo criterio, para las pantallas que además muestran hora ──────
// (Movimientos de caja / Historial de cobros — MovementsTab.jsx, HistoryTab.jsx)

test("formatDateTime: fecha de negocio (medianoche UTC, sin hora real) → solo el día, sin hora inventada", () => {
    assert.equal(formatDateTime("2026-07-22T00:00:00Z"), "22/07/2026");
});

test("formatDateTime: null → '-'", () => {
    assert.equal(formatDateTime(null), "-");
});

// ─── Prueba de independencia de zona horaria (regla del dueño 2026-07-22: la fecha/hora que
// rige es SIEMPRE la de Argentina, nunca la del navegador/servidor) ──────────────────────
//
// Este caso usa un instante con hora REAL (01:00, no medianoche), a propósito para ejercitar
// la rama "instante real" de formatDate()/formatDateTime() (la rama de fecha-solo-día nunca
// necesita zona horaria: lee el string directo). A la 01:00 UTC del 22/07 ya es 22 de julio en
// UTC (o en cualquier proceso con TZ=UTC, que es lo más común en CI) — pero en Argentina
// (UTC-3) todavía son las 22:00 del 21 de julio. Si el resultado fuera "22/07/2026" acá,
// significaría que la función depende de la zona del proceso que corre el test, violando la
// regla del dueño. Con `timeZone: "America/Argentina/Buenos_Aires"` fijo, el resultado es
// SIEMPRE "21/07/2026", sin importar en qué zona horaria corra el runner (local, CI, VPS).
test("formatDate: instante real cerca de medianoche UTC → se ancla a Argentina, no a la zona del proceso que corre el test", () => {
    assert.equal(formatDate("2026-07-22T01:00:00Z"), "21/07/2026");
});

test("formatDateTime: mismo anclaje a Argentina para instantes reales (no depende de la zona del proceso)", () => {
    // Solo verificamos el DÍA (que es lo que corría antes del fix): a la 01:00 UTC del 22/07 ya
    // es 22 de julio en UTC, pero en Argentina (UTC-3) todavía es 21/07 a la noche. La hora exacta
    // que imprime Intl acá (formato 12hs sin aclarar am/pm, herencia de toLocaleString("es-AR") de
    // ANTES de este fix) es un tema aparte, no relacionado con el bug de zona horaria de esta tanda.
    assert.ok(
        formatDateTime("2026-07-22T01:00:00Z").startsWith("21/7/2026"),
        `Esperaba que empiece con "21/7/2026", recibido: "${formatDateTime("2026-07-22T01:00:00Z")}"`
    );
});

// ─── hoyArgentina(): bug real cazado en PROD 2026-07-22 21:50hs ART ─────────────────────
//
// "Registrar cobro" proponía por defecto el día 23/07 (mañana) en vez de 22/07 (hoy en
// Argentina). Causa: `new Date().toISOString().slice(0, 10)` da el día en UTC — a las 21:50
// ART (UTC-3) ya son las 00:50 UTC del día SIGUIENTE. El dato que se guardaba y mostraba
// después era fiel (por el fix de la tanda anterior); lo roto era el DEFAULT del campo.

test("hoyArgentina: instante donde en UTC ya es el día siguiente → devuelve el día de Argentina, no el de UTC", () => {
    // 2026-07-23T01:00:00Z: en UTC ya es 23 de julio. En Argentina (UTC-3) recién son las
    // 22:00 del 22 de julio — el caso EXACTO del bug reportado (repro con reloj simulado).
    const instanteSimulado = new Date("2026-07-23T01:00:00Z");
    assert.equal(hoyArgentina(instanteSimulado), "2026-07-22");
});

test("hoyArgentina: instante bien entrada la noche en Argentina, lejos de la medianoche UTC → mismo día en ambas zonas", () => {
    // Caso de control: a las 14:00 UTC (11:00 ART) es el mismo día calendario en las dos
    // zonas, así que no hay ambigüedad posible — sirve para confirmar que la función no
    // "adelanta" ni "atrasa" el día en el caso trivial.
    const instanteSimulado = new Date("2026-07-22T14:00:00Z");
    assert.equal(hoyArgentina(instanteSimulado), "2026-07-22");
});

test("hoyArgentina: instante justo antes de medianoche Argentina (23:59 ART) → todavía el día de hoy", () => {
    // 23:59 ART del 22/07 = 02:59 UTC del 23/07 (Argentina UTC-3). En UTC ya es "mañana",
    // pero en Argentina falta un minuto para que cambie el día.
    const instanteSimulado = new Date("2026-07-23T02:59:00Z");
    assert.equal(hoyArgentina(instanteSimulado), "2026-07-22");
});

test("hoyArgentina: sin argumento → usa el reloj real (no explota, devuelve formato YYYY-MM-DD)", () => {
    const resultado = hoyArgentina();
    assert.match(resultado, /^\d{4}-\d{2}-\d{2}$/);
});

test("hoyArgentina: fin de año — 31/12 23:00 ART no salta al 1/1 del año siguiente", () => {
    // 31/12 23:00 ART = 1/1 02:00 UTC del año siguiente. Caso borde de calendario, mismo
    // criterio que los tests de fin de año de formatDate() de más arriba.
    const instanteSimulado = new Date("2027-01-01T02:00:00Z");
    assert.equal(hoyArgentina(instanteSimulado), "2026-12-31");
});
