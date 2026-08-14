/**
 * Lógica pura de "fecha prometida al cliente" (ADR-053, spec UX 2026-08-13).
 *
 * La fecha prometida es un par de fechas OPCIONAL que carga el vendedor a mano y que
 * el cálculo automático de Salida/Regreso JAMÁS pisa (backend: Reserva.PromisedStartDate/
 * PromisedEndDate). Sirve para anotar lo que se le dijo al cliente cuando todavía no
 * coincide con lo que arman los servicios cargados (ej.: "todavía no confirmó el hotel
 * pero le dijiste que sale el 12").
 *
 * Archivo `.js` PURO (sin JSX): se testea con `node --test` sin montar React.
 */

/**
 * Compara dos fechas ISO por SOLO EL DÍA CALENDARIO (yyyy-MM-dd), sin pasar por
 * `new Date()` — mismo motivo que `formatTripDate` (tripDateFormat.js): comparar
 * objetos `Date` corridos por zona horaria puede decir "son distintas" cuando en
 * realidad son el mismo día.
 */
function esMismoDia(valorA, valorB) {
    if (!valorA || !valorB) return false;
    return String(valorA).split("T")[0] === String(valorB).split("T")[0];
}

/**
 * P8 (spec UX 2026-08-13, respuesta FIRMADA del dueño — opción C, contra la
 * recomendación de "no marcar nada"): si lo prometido al cliente no coincide con
 * lo que dicen los servicios cargados, la ficha lo marca ÁMBAR.
 *
 * Solo compara los lados que están cargados DE LOS DOS extremos (prometida Y
 * calculada). Si el cálculo todavía no tiene fecha (reserva sin servicios vivos
 * todavía), no hay nada con qué comparar — no es un caso de "no coincide", es un
 * caso de "todavía no hay nada calculado" (Suposición propia, no pedida
 * explícitamente por el dueño: evita marcar ámbar apenas se carga la primera
 * fecha prometida de una reserva que recién arranca, antes de tener servicios).
 *
 * @param {{startDate?: string|null, endDate?: string|null, promisedStartDate?: string|null, promisedEndDate?: string|null}} params
 * @returns {boolean}
 */
export function hayDiscrepanciaFechaPrometida({ startDate, endDate, promisedStartDate, promisedEndDate }) {
    const difiereSalida = Boolean(promisedStartDate) && Boolean(startDate) && !esMismoDia(promisedStartDate, startDate);
    const difiereRegreso = Boolean(promisedEndDate) && Boolean(endDate) && !esMismoDia(promisedEndDate, endDate);
    return difiereSalida || difiereRegreso;
}

/**
 * True si hay AL MENOS una fecha prometida cargada (para decidir si se muestra el
 * renglón "Fecha prometida al cliente: ..." de solo lectura, o el enlace chiquito
 * "Fecha prometida al cliente +" de cuando todavía no se cargó nada — spec §3.1,
 * estados 1 y 3).
 *
 * @param {{promisedStartDate?: string|null, promisedEndDate?: string|null}} params
 * @returns {boolean}
 */
export function tieneFechaPrometidaCargada({ promisedStartDate, promisedEndDate }) {
    return Boolean(promisedStartDate) || Boolean(promisedEndDate);
}
