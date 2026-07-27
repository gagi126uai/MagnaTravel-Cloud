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
