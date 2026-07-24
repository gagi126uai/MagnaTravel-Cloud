/**
 * Lógica pura del buscador global (SearchPalette) — Tanda 3, 2026-07-24, fix #39 "Buscador
 * global honesto".
 *
 * Antes, cualquier error de red/permiso quedaba atrapado en un `console.error` invisible
 * para el usuario: la pantalla se quedaba en el estado "Escribí un nombre..." como si no
 * hubiera pasado nada, indistinguible de "todavía no buscaste". Este archivo separa la
 * lógica de DECISIÓN (qué mensaje mostrar) del componente, para poder testearla sin montar
 * React — mismo criterio que cartelEmergenteLogic.js.
 */

// Texto único cuando la búsqueda falla por red/permiso — a propósito DISTINTO de "No se
// encontraron resultados" (ese es un resultado válido; esto es que la búsqueda ni corrió).
export const MENSAJE_ERROR_BUSQUEDA = "No se pudo buscar. Probá de nuevo.";

// Aviso al pie de una sección cuando el backend recortó los resultados a "lo del usuario"
// (sin el permiso *.view_all). Mismo texto en las dos secciones que lo pueden traer
// (reservas, pagos) — un único aviso en toda la app, sin jerga de permisos.
export const AVISO_ALCANCE_PROPIO = "Mostrando solo lo tuyo";

/**
 * Decide si corresponde mostrar el aviso "Mostrando solo lo tuyo" al pie de una sección
 * del buscador. Lee la señal ESTRUCTURADA que manda el backend (`SearchScopeInfo`,
 * fix #39) — nunca se adivina a partir de que la lista haya venido corta.
 *
 * @param {{reservasScopedToOwn?: boolean, paymentsScopedToOwn?: boolean}|null|undefined} scope
 * @param {"reservas"|"payments"} seccion
 * @returns {boolean}
 */
export function debeMostrarAvisoAlcancePropio(scope, seccion) {
    if (!scope) return false; // backend viejo sin el campo `scope` — no se inventa el aviso
    if (seccion === "reservas") return Boolean(scope.reservasScopedToOwn);
    if (seccion === "payments") return Boolean(scope.paymentsScopedToOwn);
    return false;
}

/**
 * Arma el estado visual del buscador a partir de lo que hay en cada momento. Un único
 * punto de decisión evita que loading/error/vacío/con-resultados se pisen entre sí (ej.
 * mostrar "Escribí algo" mientras en realidad la búsqueda reventó).
 *
 * @param {{ query: string, loading: boolean, results: object|null, errorMensaje: string|null }} params
 * @returns {"inicial"|"cargando"|"error"|"sin-resultados"|"con-resultados"}
 */
export function resolverEstadoBusqueda({ query, loading, results, errorMensaje }) {
    const hayQuery = Boolean(query && query.trim());

    if (errorMensaje) return "error";
    if (!hayQuery) return "inicial";
    if (loading && !results) return "cargando";

    const hayResultados = Boolean(
        results &&
        ((results.reservas?.length ?? 0) > 0 ||
            (results.files?.length ?? 0) > 0 ||
            (results.customers?.length ?? 0) > 0 ||
            (results.payments?.length ?? 0) > 0)
    );

    if (results && !hayResultados) return "sin-resultados";
    if (hayResultados) return "con-resultados";
    return "inicial";
}
