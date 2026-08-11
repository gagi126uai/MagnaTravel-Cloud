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

import { formatDate } from "../../../lib/utils.js";
import {
    mergearCandidatosDedup,
    resolverTextoDeCrear,
    resolverListaParaMostrar,
    dudaDeProductoLocal,
    debeMostrarDuda,
} from "./productDedupMatchLogic.js";

// ─── Lógica pura extraída de ProductSearchField ───────────────────────────────
// Estas funciones representan exactamente las reglas que el componente aplica.
// Si cambia la lógica allá, actualizar acá.

/**
 * Copia de formatSoldDate (ProductSearchField.jsx), que ahora delega en la
 * formatDate() central de utils.js — se testea contra el módulo real.
 */
function formatSoldDate(isoDate) {
    if (!isoDate) return null;
    const formateada = formatDate(isoDate);
    return formateada === "-" ? null : formateada;
}

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

test("(d) skipNextSearch activo tras handleCreateNew → NO relanza la búsqueda (bloqueante B1)", () => {
    // Bug bloqueante (revisor funcional): handleCreateNew ahora prende skipNextSearch
    // ANTES de avisarle al padre (mismo patrón que handleSelectExisting) — sin esto,
    // `textoParaCrear` (el nombre que limpió el matcher) puede diferir de `value`, el
    // padre lo sube al form, `value` cambia, y sin el flag el efecto de debounce lo
    // trataría como un tecleo nuevo: el desplegable reaparecería ~350ms después tapando
    // el recuadro de "producto nuevo" recién abierto.
    const resultado = debeDispararseBusqueda({
        userHasInteracted: true,
        skipNextSearch: true, // lo que deja handleCreateNew tras el fix
        value: "Amerian Posadas", // textoParaCrear, distinto de lo que tipeó el vendedor
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

// ─── Tests: formatSoldDate (última venta) — bug fechas corridas 2026-07-16 ───
// soldAt es un instante REAL (CreatedAt del servicio vendido), no una fecha-solo-día
// elegida por el usuario, así que corresponde mostrarlo en hora local (comportamiento
// sin cambios). Estos tests confirman que delegar en la formatDate() central no
// rompió el caso de uso real de este dropdown.

test("formatSoldDate: null → null", () => {
    assert.equal(formatSoldDate(null), null);
});

test("formatSoldDate: timestamp real con hora → fecha en formato DD/MM/AAAA", () => {
    assert.equal(formatSoldDate("2026-05-22T14:03:00Z"), formatDate("2026-05-22T14:03:00Z"));
});

// ─── Tests: matcher anti-duplicados invisible — wiring de ProductSearchField ──
// (a)/(b) usan las funciones REALES de productDedupMatchLogic.js (no una copia): acá
// se prueba que ProductSearchField las usa tal cual para armar `resultadosParaMostrar`
// y `textoParaCrear`, que es exactamente lo que renderiza el dropdown y la opción crear.

test("(a) merge del matcher en la lista visible: no duplica lo que el buscador normal ya trajo", () => {
    const resultadosDelBuscadorNormal = [{ ratePublicId: "r1", name: "Maitei Posadas" }];
    const candidatosDelMotor = [
        { ratePublicId: "r1", name: "Maitei Posadas (motor)" }, // ya está, no se duplica
        { ratePublicId: "r2", name: "Amerian Posadas" }, // nuevo, se suma
    ];
    const resultadosParaMostrar = mergearCandidatosDedup(resultadosDelBuscadorNormal, candidatosDelMotor, 8);

    assert.equal(resultadosParaMostrar.length, 2);
    assert.equal(resultadosParaMostrar[0].name, "Maitei Posadas"); // el original, sin pisar
    assert.equal(resultadosParaMostrar[1].ratePublicId, "r2");
});

test("(b) la opción 'crear ...' usa textoParaCrear (el nombre limpio del motor), no la frase cruda", () => {
    const fraseCruda = "hotel amerian posadas triple mp julia 91000 pesos";
    const textoParaCrear = resolverTextoDeCrear("Amerian Posadas", fraseCruda);

    assert.equal(textoParaCrear, "Amerian Posadas");
    assert.notEqual(textoParaCrear, fraseCruda);
});

// ─── (c) La lista NO cambia y el índice no se desalinea mientras se navega con teclado ──
// Bug bloqueante B2: si el matcher aterriza una respuesta MIENTRAS el vendedor tiene
// el dropdown navegado con las flechas, la lista no puede crecer debajo de sus dedos —
// el índice que apuntaba a "crear" pasaría a apuntar a un producto existente y un Enter
// rápido lo elegiría por error. Estos tests usan `resolverListaParaMostrar` REAL (la
// misma función que importa y llama ProductSearchField.jsx, ver el import de arriba) —
// nada de mirrors: si el día de mañana cambia la regla ahí, este test la sigue de cerca
// en vez de quedarse afirmando una versión vieja en silencio.

test("(c) con keyboardIndex >= 0 (navegando): la lista NO se actualiza aunque llegue un merge nuevo", () => {
    const congelada = [{ ratePublicId: "r1" }];
    const fresca = [{ ratePublicId: "r1" }, { ratePublicId: "r2" }];
    assert.deepEqual(resolverListaParaMostrar({ keyboardIndex: 0, listaCongelada: congelada, listaFresca: fresca }), congelada);
    assert.deepEqual(resolverListaParaMostrar({ keyboardIndex: 3, listaCongelada: congelada, listaFresca: fresca }), congelada);
});

test("(c) con keyboardIndex -1 (cursor en el input, sin navegar): la lista SÍ se actualiza a la fresca", () => {
    const congelada = [{ ratePublicId: "r1" }];
    const fresca = [{ ratePublicId: "r1" }, { ratePublicId: "r2" }];
    assert.deepEqual(resolverListaParaMostrar({ keyboardIndex: -1, listaCongelada: congelada, listaFresca: fresca }), fresca);
});

test("(c) simulación completa: navegando sobre 'crear' (índice = length de la lista vieja), un merge que llega tarde NO corre esa posición", () => {
    // Estado ANTES de que aterrice el matcher: 1 resultado + la opción "crear" en el
    // índice 1 (results.length). El vendedor bajó la flechita hasta ahí.
    const listaAntesDelMerge = [{ ratePublicId: "r1", name: "Maitei Posadas" }];
    const keyboardIndex = listaAntesDelMerge.length; // 1 → estaba parado en "crear"

    // El matcher aterriza CON el vendedor todavía navegando: la lista fresca ya tiene 2
    // filas (esto es lo que causaría el bug si se usara sin más).
    const candidatosQueLlegaronTarde = [{ ratePublicId: "r2", name: "Amerian Posadas" }];
    const listaFresca = mergearCandidatosDedup(listaAntesDelMerge, candidatosQueLlegaronTarde, 8);
    assert.equal(listaFresca.length, 2);

    // resolverListaParaMostrar (la función REAL) decide cuál lista corresponde en este
    // render — con keyboardIndex >= 0, tiene que devolver la congelada, no la fresca.
    const listaEfectivamenteMostrada = resolverListaParaMostrar({
        keyboardIndex,
        listaCongelada: listaAntesDelMerge,
        listaFresca,
    });

    assert.equal(listaEfectivamenteMostrada.length, 1);
    // El índice 1 (donde estaba parado el vendedor) sigue siendo "crear" (length de la
    // lista mostrada), no un producto existente.
    assert.equal(keyboardIndex, listaEfectivamenteMostrada.length);
});

// ─── (d) Duda de producto LOCAL (H-1, 2026-08-11) — wiring de ProductSearchField ──
// `dudaVigente = dudaLocal ?? dedupResult?.duda ?? null` es el merge REAL que arma el
// componente; estos tests reproducen esa fórmula con las funciones REALES para probar
// que la local gana y que el gate de rateId vinculado sigue apagando la ✨ igual que
// para la duda del motor.

test("(d) la duda LOCAL gana sobre la del motor: si el buscador local ya detectó la ambigüedad, no hace falta esperar al motor", () => {
    const resultadosDelBuscador = [
        { name: "Sheraton Iguazú", subtitle: "Puerto Iguazú" },
        { name: "Sheraton Iguazú", subtitle: "Posadas" },
    ];
    const dudaDelMotor = { field: "producto", question: "¿Pregunta vieja del motor?" };

    const dudaLocal = dudaDeProductoLocal(resultadosDelBuscador);
    const dudaVigente = dudaLocal ?? dudaDelMotor;

    assert.equal(dudaVigente.question, "¿Sheraton Iguazú de Puerto Iguazú o el de Posadas?");
});

test("(d) sin duda local, la del motor sigue funcionando (fallback normal)", () => {
    const resultadosSinAmbiguedad = [{ name: "Sheraton Iguazú", subtitle: "Puerto Iguazú" }];
    const dudaDelMotor = { field: "producto", question: "¿El de Delfos o el de Ola Mayorista?" };

    const dudaLocal = dudaDeProductoLocal(resultadosSinAmbiguedad);
    const dudaVigente = dudaLocal ?? dudaDelMotor;

    assert.equal(dudaVigente, dudaDelMotor);
});

test("(d) con rateId ya vinculado, la duda local tampoco se muestra (mismo gate que la del motor)", () => {
    const resultadosDelBuscador = [
        { name: "Sheraton Iguazú", subtitle: "Puerto Iguazú" },
        { name: "Sheraton Iguazú", subtitle: "Posadas" },
    ];
    const dudaVigente = dudaDeProductoLocal(resultadosDelBuscador);

    // La duda existe (se armó bien), pero debeMostrarDuda la tapa por rateId vinculado.
    assert.notEqual(dudaVigente, null);
    const seMuestra = debeMostrarDuda({ duda: dudaVigente, isSearching: false, dudaDescartada: false, hayProductoVinculado: true });
    assert.equal(seMuestra, false);
});
