/**
 * Helpers de lógica pura para el PanelAsignarPasajeros (ADR-031 v2.1).
 *
 * Separados del componente (.jsx) para que los tests de Node puro puedan
 * importarlos directamente sin transpiler de JSX.
 *
 * Reglas:
 *   - Sin imports de React.
 *   - Sin JSX.
 *   - Lógica pura: misma entrada → misma salida, sin efectos secundarios.
 */

/**
 * Inicializa el set de tildes a partir del coverage que ya tenemos.
 *
 * Usamos el coverage (que ya llegó vía GET nominal-coverage del padre) en vez de hacer
 * otro GET /assignments al abrir el panel. Esto evita el bug donde servicePublicId
 * podía venir null en las asignaciones y romper el match.
 *
 * @param {object|null} coverage - ServiceNominalCoverageDto
 * @param {Array} pasajeros      - lista de pasajeros con nombre
 * @returns {Set<string>}        - set de publicIds en lowercase listos para tildar
 */
export function inicializarTildados(coverage, pasajeros) {
    const todosLosIds = new Set(
        pasajeros.map(p => String(p.publicId || p.PassengerPublicId || "").toLowerCase())
    );

    if (!coverage || !coverage.hasExplicitAssignments) {
        // Sin asignaciones explícitas = "Para: Todos" → tildar a todos
        return todosLosIds;
    }

    // Hay asignaciones explícitas: solo tildar los que el backend incluye en el set
    const idsDelSet = new Set(
        (coverage.serviceSet || []).map(p => String(p.passengerPublicId || "").toLowerCase())
    );

    // Intersectamos con los pasajeros disponibles en el panel (por si el coverage
    // incluye algún id que no está en la lista local — raro pero defensivo)
    const interseccion = new Set();
    for (const id of idsDelSet) {
        if (todosLosIds.has(id)) {
            interseccion.add(id);
        }
    }

    // Si la intersección quedó vacía (caso extremo: set del backend sin match), fallback a todos
    return interseccion.size > 0 ? interseccion : todosLosIds;
}

/**
 * Construye el payload para el PUT de asignaciones.
 *
 * Si el usuario tildó a TODOS los pasajeros, el backend espera lista vacía
 * (lo interpreta como "Para: Todos", sin asignaciones explícitas).
 * Si es un subconjunto, se mandan los ids específicos.
 *
 * @param {Set<string>} tildados  - set de publicIds en lowercase que el usuario eligió
 * @param {Array} pasajeros       - todos los pasajeros disponibles del panel
 * @returns {string[]}            - array de ids a enviar (vacío = Para: Todos)
 */
export function armarPayloadPut(tildados, pasajeros) {
    const todosLosIds = pasajeros.map(p => String(p.publicId || p.PassengerPublicId || "").toLowerCase());
    const esTodos = todosLosIds.every(id => tildados.has(id));
    // Lista vacía = "Para: Todos"; ids específicos = subconjunto explícito
    return esTodos ? [] : Array.from(tildados);
}
