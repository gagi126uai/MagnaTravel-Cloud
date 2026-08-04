/**
 * Mapeo pestaña → clave de contador para el listado de reservas (ReservasPage).
 *
 * Cada pestaña manda un "view" al backend (GET /reservas?view=...) y lee su número
 * desde el resumen (summary) que devuelve la misma respuesta. Desde la Tanda 3 (2026-07-24)
 * el valor de la pestaña y la clave del "view" son el mismo texto para casi todas las
 * pestañas (confirmed, traveling, closed, cancelled, lost, archived, quotation, budget) —
 * la única excepción es "in-management", que en la URL va con guion pero en el resumen
 * el campo es inManagementCount (camelCase, como lo manda el backend en JSON).
 */
export function tabCountKey(tabValue) {
  if (tabValue === "in-management") return "inManagement";
  return tabValue;
}

/**
 * H20 (barrido E2E 2026-07-25, decisión firmada 12): contador de la pestaña "Todas".
 *
 * El motor no manda un "AllCount" propio en el resumen (ReservaListSummaryDto) — la
 * pestaña "Todas" (view=all) ya existía sin filtro de estado, solo faltaba la pestaña.
 * En vez de agregar un campo nuevo al backend para un simple total, sumamos acá los 9
 * contadores que YA vienen y que son estados MUTUAMENTE EXCLUYENTES entre sí (cada
 * reserva está en un solo estado a la vez): Cotizaciones, Presupuestos, En gestión,
 * Confirmadas, En viaje, Finalizadas, Anuladas, Perdidas, Archivadas. Sumados, dan el
 * total exacto de reservas — el MISMO número que devolvería `totalCount` si se pidiera
 * la pestaña "Todas" al backend.
 *
 * OJO: no se suma `activeCount` acá — no es un estado propio, es "En gestión + Confirmadas"
 * combinado (sumarlo también contaría esas reservas dos veces).
 *
 * @param {object} summary - el mismo objeto que ya arma tabCounts en useReservas.js
 * @returns {number}
 */
export function calcularContadorTodas(summary) {
  const s = summary || {};
  return (
    (s.quotationCount || 0) +
    (s.budgetCount || 0) +
    (s.inManagementCount || 0) +
    (s.reservedCount || 0) +
    (s.operativeCount || 0) +
    (s.closedCount || 0) +
    (s.cancelledCount || 0) +
    (s.lostCount || 0) +
    (s.archivedCount || 0)
  );
}

// ─── Tanda 1 rediseño listado (2026-08-04, B2/B3) ──────────────────────────────

/**
 * B2: una solapa con contador en 0 queda VISIBLE pero apagada y no se puede
 * tocar — "cero" también es información, no se esconde la solapa (spec firmada).
 * "Todas" es la excepción: aunque su contador dé 0 (ej. un mes sin ninguna
 * reserva), sigue siendo la solapa por defecto y se puede tocar siempre.
 */
export function esSolapaApagada(tabValue, count) {
  if (tabValue === "all") return false;
  return (count || 0) === 0;
}

/**
 * B3: mientras el usuario está escribiendo en el buscador, el buscador ignora la
 * solapa y el mes (lo resuelve el motor con `globalSearch=true`, ver useReservas.js).
 * Para que la pantalla no mienta, la solapa que se ve MARCADA como activa pasa a
 * ser "Todas" — pero el estado real (`viewFilter`) no se toca acá, así que al
 * borrar el texto de búsqueda la solapa anterior se restaura sola.
 */
export function resolverSolapaVisible(viewFilter, estaBuscando) {
  return estaBuscando ? "all" : viewFilter;
}

/**
 * B2: si la solapa activa se queda sin resultados (ej. se archivaron todas las
 * reservas "En gestión" y ese contador bajó a 0), hay que saltar a "Todas" para
 * no dejar al usuario mirando una pantalla vacía sin ninguna salida (P-11⭐).
 *
 * No aplica mientras se está buscando: ahí la solapa real queda "congelada" tal
 * cual estaba y se restaura cuando se borra el texto (ver resolverSolapaVisible)
 * — cambiarla de verdad acá rompería esa restauración.
 */
export function debeSaltarATodas(viewFilter, tabCounts, estaBuscando) {
  if (estaBuscando) return false;
  if (viewFilter === "all") return false;
  const count = (tabCounts || {})[tabCountKey(viewFilter)] || 0;
  return count === 0;
}
