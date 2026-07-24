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
