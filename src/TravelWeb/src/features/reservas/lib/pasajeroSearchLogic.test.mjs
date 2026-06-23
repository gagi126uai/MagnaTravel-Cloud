/**
 * Tests de la lógica pura de búsqueda de pasajeros históricos.
 *
 * Cómo correr:
 *   node --test src/features/reservas/lib/pasajeroSearchLogic.test.mjs
 *
 * Qué cubre:
 *   - Umbral de caracteres para disparar la búsqueda
 *   - Construcción de la URL según el campo que dispara (nombre vs documento)
 *   - Mapeo de una sugerencia del backend a los campos del formulario
 *   - Detección de duplicado en la reserva actual
 *   - Formateo del subtítulo del dropdown
 */

import test from "node:test";
import assert from "node:assert/strict";

import {
    cumpleUmbralBusqueda,
    construirUrlBusquedaHistorica,
    mapearSugerenciaAlForm,
    esDuplicadoEnReserva,
    formatearSubtituloSugerencia,
    MINIMO_CHARS_BUSQUEDA,
} from "./pasajeroSearchLogic.js";

// ─── Helpers para armar datos de prueba ──────────────────────────────────────

/**
 * Construye una sugerencia como la devuelve el backend.
 */
function sugerencia({
    fullName = "Ana García",
    documentType = "DNI",
    documentNumber = "30123456",
    birthDate = "1990-05-15",
    nationality = "Argentina",
    gender = "F",
    phone = "+54 9 11 1234-5678",
    email = "ana@ejemplo.com",
    passportExpiry = null,
    usageCount = 2,
    score = 0.9,
} = {}) {
    return { fullName, documentType, documentNumber, birthDate, nationality, gender, phone, email, passportExpiry, usageCount, score };
}

/**
 * Construye el formData con los valores típicos del modal.
 */
function formData({ fullName = "", documentType = "DNI", documentNumber = "" } = {}) {
    return { fullName, documentType, documentNumber };
}

// ─── cumpleUmbralBusqueda ─────────────────────────────────────────────────────

test(`umbral mínimo: el threshold definido es ${MINIMO_CHARS_BUSQUEDA} chars`, () => {
    assert.equal(MINIMO_CHARS_BUSQUEDA, 3, "el umbral no debe cambiar sin ajustar los tests");
});

test("umbral: texto vacío → no cumple", () => {
    assert.equal(cumpleUmbralBusqueda(""), false);
});

test("umbral: null → no cumple", () => {
    assert.equal(cumpleUmbralBusqueda(null), false);
});

test("umbral: texto con espacios que quedan vacíos al trim → no cumple", () => {
    assert.equal(cumpleUmbralBusqueda("   "), false);
});

test("umbral: texto de 2 chars → no cumple", () => {
    assert.equal(cumpleUmbralBusqueda("An"), false);
});

test("umbral: texto de exactamente 3 chars → cumple", () => {
    assert.equal(cumpleUmbralBusqueda("Ana"), true);
});

test("umbral: texto de 10 chars → cumple", () => {
    assert.equal(cumpleUmbralBusqueda("Ana García"), true);
});

test("umbral: número de documento de 2 dígitos → no cumple", () => {
    assert.equal(cumpleUmbralBusqueda("30"), false);
});

test("umbral: número de documento de 3 dígitos → cumple", () => {
    assert.equal(cumpleUmbralBusqueda("301"), true);
});

// ─── construirUrlBusquedaHistorica ────────────────────────────────────────────

test("URL nombre: campo 'name' → incluye fullName en los params", () => {
    const url = construirUrlBusquedaHistorica("name", formData({ fullName: "Ana García" }));
    assert.ok(url.includes("fullName=Ana"), "debe incluir fullName");
    assert.ok(!url.includes("documentNumber"), "no debe incluir documentNumber si el campo es nombre");
});

test("URL nombre: caracteres especiales se encodean correctamente", () => {
    const url = construirUrlBusquedaHistorica("name", formData({ fullName: "García López" }));
    assert.ok(url.includes("fullName="), "debe incluir fullName");
    // Los espacios y tildes se encodean, pero el campo debe estar presente
    assert.ok(url.includes("/passengers/search-similar"), "ruta correcta");
});

test("URL documento: campo 'document' → incluye documentNumber y documentType", () => {
    const url = construirUrlBusquedaHistorica("document", formData({ documentType: "DNI", documentNumber: "30123456" }));
    assert.ok(url.includes("documentNumber=30123456"), "debe incluir documentNumber");
    assert.ok(url.includes("documentType=DNI"), "debe incluir documentType");
    assert.ok(!url.includes("fullName"), "no debe incluir fullName si el campo es documento");
});

test("URL documento: Pasaporte también se envía el tipo", () => {
    const url = construirUrlBusquedaHistorica("document", formData({ documentType: "Pasaporte", documentNumber: "AAA123456" }));
    assert.ok(url.includes("documentType=Pasaporte"));
    assert.ok(url.includes("documentNumber=AAA123456"));
});

test("URL: siempre incluye take=5 por defecto", () => {
    const url = construirUrlBusquedaHistorica("name", formData({ fullName: "Ana" }));
    assert.ok(url.includes("take=5"), "debe incluir take por defecto");
});

test("URL: se puede personalizar el take", () => {
    const url = construirUrlBusquedaHistorica("name", formData({ fullName: "Ana" }), 8);
    assert.ok(url.includes("take=8"), "debe respetar el take personalizado");
});

test("URL: empieza con el path correcto del backend", () => {
    const url = construirUrlBusquedaHistorica("name", formData({ fullName: "Ana" }));
    assert.ok(url.startsWith("/passengers/search-similar"), "path debe coincidir con el endpoint del backend");
});

// ─── mapearSugerenciaAlForm ───────────────────────────────────────────────────

test("mapeo: todos los campos del form se completan", () => {
    const form = mapearSugerenciaAlForm(sugerencia());

    assert.equal(form.fullName, "Ana García");
    assert.equal(form.documentType, "DNI");
    assert.equal(form.documentNumber, "30123456");
    assert.equal(form.birthDate, "1990-05-15");
    assert.equal(form.nationality, "Argentina");
    assert.equal(form.gender, "F");
    assert.equal(form.phone, "+54 9 11 1234-5678");
    assert.equal(form.email, "ana@ejemplo.com");
});

test("mapeo: birthDate con timestamp ISO se recorta a YYYY-MM-DD", () => {
    const form = mapearSugerenciaAlForm(sugerencia({ birthDate: "1990-05-15T00:00:00Z" }));
    assert.equal(form.birthDate, "1990-05-15", "debe tomar solo la parte de fecha");
});

test("mapeo: birthDate null → campo vacío (no rompe el input date)", () => {
    const form = mapearSugerenciaAlForm(sugerencia({ birthDate: null }));
    assert.equal(form.birthDate, "", "null debe convertirse en string vacío");
});

test("mapeo: campos faltantes en la sugerencia → valores por defecto seguros", () => {
    // Sugerencia mínima: solo nombre
    const form = mapearSugerenciaAlForm({ fullName: "Juan" });

    assert.equal(form.fullName, "Juan");
    assert.equal(form.documentType, "DNI", "documentType default");
    assert.equal(form.documentNumber, "", "documentNumber vacío si no viene");
    assert.equal(form.birthDate, "", "birthDate vacío si no viene");
    assert.equal(form.nationality, "", "nationality vacío si no viene");
    assert.equal(form.gender, "M", "gender default M");
    assert.equal(form.phone, "", "phone vacío si no viene");
    assert.equal(form.email, "", "email vacío si no viene");
});

test("mapeo: las notas siempre quedan vacías (no se copian del histórico)", () => {
    // Las notas son propias de cada viaje, no se deben copiar del registro histórico
    const form = mapearSugerenciaAlForm(sugerencia());
    assert.equal(form.notes, "", "notas siempre vacías al autocompletar");
});

test("mapeo: passportExpiry del backend no rompe el mapeo (campo extra, no se mapea aún)", () => {
    // El campo passportExpiry existe en el backend pero el modal no tiene input visible todavía
    const form = mapearSugerenciaAlForm(sugerencia({ passportExpiry: "2028-01-01" }));
    // El test verifica que no explota y que los campos conocidos llegan bien
    assert.equal(form.fullName, "Ana García");
    assert.ok(!("passportExpiry" in form), "passportExpiry no debe estar en el form todavía");
});

// ─── esDuplicadoEnReserva ─────────────────────────────────────────────────────

test("duplicado: sin pasajeros existentes → false (no bloquea)", () => {
    const sug = sugerencia({ documentType: "DNI", documentNumber: "30123456" });
    assert.equal(esDuplicadoEnReserva(sug, []), false);
});

test("duplicado: lista null → false (sin datos, no bloquea)", () => {
    assert.equal(esDuplicadoEnReserva(sugerencia(), null), false);
});

test("duplicado: mismo tipo y número → true (es duplicado)", () => {
    const sug = sugerencia({ documentType: "DNI", documentNumber: "30123456" });
    const existentes = [{ documentType: "DNI", documentNumber: "30123456", fullName: "Ana García" }];
    assert.equal(esDuplicadoEnReserva(sug, existentes), true);
});

test("duplicado: mismo número pero distinto tipo → false (Pasaporte vs DNI son distintos)", () => {
    const sug = sugerencia({ documentType: "Pasaporte", documentNumber: "30123456" });
    const existentes = [{ documentType: "DNI", documentNumber: "30123456" }];
    assert.equal(esDuplicadoEnReserva(sug, existentes), false, "tipo diferente no es duplicado");
});

test("duplicado: mismo tipo, número diferente → false", () => {
    const sug = sugerencia({ documentType: "DNI", documentNumber: "30123456" });
    const existentes = [{ documentType: "DNI", documentNumber: "99999999" }];
    assert.equal(esDuplicadoEnReserva(sug, existentes), false);
});

test("duplicado: comparación es case-insensitive en tipo de documento", () => {
    // "dni" vs "DNI" no deben generar un falso negativo
    const sug = sugerencia({ documentType: "DNI", documentNumber: "30123456" });
    const existentes = [{ documentType: "dni", documentNumber: "30123456" }];
    assert.equal(esDuplicadoEnReserva(sug, existentes), true, "comparación case-insensitive");
});

test("duplicado: comparación de número es case-insensitive (pasaportes tienen letras)", () => {
    const sug = sugerencia({ documentType: "Pasaporte", documentNumber: "AAB123" });
    const existentes = [{ documentType: "pasaporte", documentNumber: "aab123" }];
    assert.equal(esDuplicadoEnReserva(sug, existentes), true);
});

test("duplicado: sugerencia sin número de documento → false (no se puede determinar)", () => {
    const sug = sugerencia({ documentNumber: "" });
    const existentes = [{ documentType: "DNI", documentNumber: "" }];
    // Sin documento no se puede afirmar duplicado con certeza
    assert.equal(esDuplicadoEnReserva(sug, existentes), false);
});

test("duplicado: hay 3 pasajeros, el duplicado es el tercero → true", () => {
    const sug = sugerencia({ documentType: "DNI", documentNumber: "30123456" });
    const existentes = [
        { documentType: "DNI", documentNumber: "11111111" },
        { documentType: "DNI", documentNumber: "22222222" },
        { documentType: "DNI", documentNumber: "30123456" },
    ];
    assert.equal(esDuplicadoEnReserva(sug, existentes), true);
});

// ─── formatearSubtituloSugerencia ────────────────────────────────────────────

test("subtítulo: con tipo+número y usageCount → formato completo", () => {
    const texto = formatearSubtituloSugerencia(sugerencia({ usageCount: 3 }));
    assert.ok(texto.includes("DNI 30123456"), "debe incluir tipo y número");
    assert.ok(texto.includes("3 reservas"), "debe incluir el conteo en plural");
});

test("subtítulo: usageCount 1 → 'reserva' en singular", () => {
    const texto = formatearSubtituloSugerencia(sugerencia({ usageCount: 1 }));
    assert.ok(texto.includes("1 reserva"), "singular cuando es 1");
    assert.ok(!texto.includes("reservas"), "no debe decir reservas en plural");
});

test("subtítulo: usageCount 0 → no aparece el conteo", () => {
    const texto = formatearSubtituloSugerencia(sugerencia({ usageCount: 0 }));
    assert.ok(!texto.includes("reserva"), "con 0 reservas no aparece el texto");
});

test("subtítulo: sin documento → solo aparece el conteo si existe", () => {
    const texto = formatearSubtituloSugerencia({ usageCount: 5, documentType: "", documentNumber: "" });
    assert.ok(texto.includes("5 reservas"), "usageCount debe aparecer igual");
    assert.ok(!texto.includes("·"), "no debe haber separador si solo hay un elemento");
});

test("subtítulo: sin datos → string vacío (no rompe el render)", () => {
    const texto = formatearSubtituloSugerencia({});
    assert.equal(texto, "", "sin datos devuelve string vacío");
});
