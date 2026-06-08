/**
 * Tests de lógica pura del buscador de productos (ProductSearchField).
 *
 * Por qué son lógica pura y no tests de componente:
 *   El bug crítico era de comportamiento del useEffect (abrir dropdown al montar
 *   con valor precargado). La decisión "cuándo buscar" es extractable como
 *   reglas puras sin DOM, igual que el resto de los tests de este directorio.
 *
 * Cómo correr: node --test src/features/reservas/inline-service/productSearchField.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

// ─── Lógica pura extraída de ProductSearchField ───────────────────────────────
// Estas funciones representan exactamente las reglas que el componente aplica.
// Si cambia la lógica allá, actualizar acá.

const MIN_QUERY_LENGTH = 2;

/**
 * Regla central del bug fix:
 * El debounce SOLO debe dispararse si el usuario interactuó (escribió en el campo).
 * En modo edición, el valor viene precargado pero userHasInteracted arranca en false.
 *
 * Simula la decisión que hace el useEffect en ProductSearchField.
 */
function debeDispararseBusqueda({ userHasInteracted, skipNextSearch, value }) {
    // Condición 1: si skipNextSearch está activo, consumir y NO buscar
    if (skipNextSearch) return false;
    // Condición 2: si el usuario nunca escribió (ej: edición con valor precargado), NO buscar
    if (!userHasInteracted) return false;
    // Condición 3: texto muy corto → NO buscar
    const query = value || "";
    if (query.trim().length < MIN_QUERY_LENGTH) return false;
    return true;
}

/**
 * Regla del handleFocus:
 * Re-abrir el dropdown al re-enfocar solo si el usuario ya interactuó.
 * En modo edición sin haber tipeado, el foco no debe abrir nada.
 */
function debeReabrirDropdownAlFoco({ userHasInteracted, value, hayResultados }) {
    return userHasInteracted && (value || "").trim().length >= MIN_QUERY_LENGTH && hayResultados;
}

// ─── Tests: regla debeDispararseBusqueda ─────────────────────────────────────

test("modo creación: usuario tipea → debe buscar", () => {
    const resultado = debeDispararseBusqueda({
        userHasInteracted: true,
        skipNextSearch: false,
        value: "HARD ROCK",
    });
    assert.equal(resultado, true);
});

test("modo edición al montar: valor precargado, usuario no tipeó → NO debe buscar", () => {
    // Este era exactamente el bug: value larga pero userHasInteracted=false (mount)
    const resultado = debeDispararseBusqueda({
        userHasInteracted: false,
        skipNextSearch: false,
        value: "HARD ROCK CAFE PUNTA CANA",
    });
    assert.equal(resultado, false);
});

test("modo edición: usuario borró y re-escribió → sí debe buscar", () => {
    // Después de que el usuario interactuó, las búsquedas vuelven a funcionar
    const resultado = debeDispararseBusqueda({
        userHasInteracted: true,
        skipNextSearch: false,
        value: "HARD",
    });
    assert.equal(resultado, true);
});

test("skipNextSearch activo (recién eligió resultado) → NO debe buscar", () => {
    // skipNextSearch se activa cuando handleSelectExisting sube el nombre al input
    const resultado = debeDispararseBusqueda({
        userHasInteracted: true,
        skipNextSearch: true,
        value: "HARD ROCK",
    });
    assert.equal(resultado, false);
});

test("texto demasiado corto (1 carácter) → NO debe buscar", () => {
    const resultado = debeDispararseBusqueda({
        userHasInteracted: true,
        skipNextSearch: false,
        value: "H",
    });
    assert.equal(resultado, false);
});

test("texto vacío → NO debe buscar", () => {
    const resultado = debeDispararseBusqueda({
        userHasInteracted: true,
        skipNextSearch: false,
        value: "",
    });
    assert.equal(resultado, false);
});

test("texto exactamente en el límite (2 caracteres) → sí debe buscar", () => {
    const resultado = debeDispararseBusqueda({
        userHasInteracted: true,
        skipNextSearch: false,
        value: "HA",
    });
    assert.equal(resultado, true);
});

test("texto solo espacios (length >= 2 pero trim < 2) → NO debe buscar", () => {
    const resultado = debeDispararseBusqueda({
        userHasInteracted: true,
        skipNextSearch: false,
        value: "   ",
    });
    assert.equal(resultado, false);
});

// ─── Tests: regla debeReabrirDropdownAlFoco ───────────────────────────────────

test("re-foco en modo creación con resultados en caché → debe re-abrir", () => {
    const resultado = debeReabrirDropdownAlFoco({
        userHasInteracted: true,
        value: "HARD ROCK",
        hayResultados: true,
    });
    assert.equal(resultado, true);
});

test("re-foco en modo edición sin haber tipeado → NO debe re-abrir", () => {
    // Bug secundario: el foco tampoco debe abrir el dropdown en edición sin interacción
    const resultado = debeReabrirDropdownAlFoco({
        userHasInteracted: false,
        value: "HARD ROCK CAFE PUNTA CANA",
        hayResultados: false, // en edición no hay resultados previos
    });
    assert.equal(resultado, false);
});

test("re-foco sin resultados en caché → NO debe re-abrir", () => {
    // Si el usuario borró todo y volvió a enfocar, no hay nada que mostrar
    const resultado = debeReabrirDropdownAlFoco({
        userHasInteracted: true,
        value: "HARD ROCK",
        hayResultados: false,
    });
    assert.equal(resultado, false);
});

test("re-foco con texto muy corto → NO debe re-abrir", () => {
    const resultado = debeReabrirDropdownAlFoco({
        userHasInteracted: true,
        value: "H",
        hayResultados: true,
    });
    assert.equal(resultado, false);
});

// ─── Tests: helper nombreTipoServicio (FIX 1: texto del botón "Crear nuevo") ──
// El botón "crear X como TIPO nuevo" usaba "hotel" hardcodeado para cualquier tipo.
// Ahora usa el mapa NOMBRE_TIPO_SERVICIO que mapea el serviceType al nombre correcto.

const NOMBRE_TIPO_SERVICIO = {
    Aereo: "aéreo",
    Hotel: "hotel",
    Traslado: "traslado",
    Paquete: "paquete",
    Asistencia: "asistencia",
};

function nombreTipoServicio(serviceType) {
    return NOMBRE_TIPO_SERVICIO[serviceType] || "servicio";
}

test("Aereo → 'aéreo'", () => {
    assert.equal(nombreTipoServicio("Aereo"), "aéreo");
});

test("Hotel → 'hotel'", () => {
    assert.equal(nombreTipoServicio("Hotel"), "hotel");
});

test("Traslado → 'traslado'", () => {
    assert.equal(nombreTipoServicio("Traslado"), "traslado");
});

test("Paquete → 'paquete'", () => {
    assert.equal(nombreTipoServicio("Paquete"), "paquete");
});

test("Asistencia → 'asistencia'", () => {
    assert.equal(nombreTipoServicio("Asistencia"), "asistencia");
});

test("tipo desconocido → 'servicio' (fallback genérico)", () => {
    assert.equal(nombreTipoServicio("Generico"), "servicio");
});

test("tipo null → 'servicio' (fallback genérico)", () => {
    assert.equal(nombreTipoServicio(null), "servicio");
});

test("tipo undefined → 'servicio' (fallback genérico)", () => {
    assert.equal(nombreTipoServicio(undefined), "servicio");
});
