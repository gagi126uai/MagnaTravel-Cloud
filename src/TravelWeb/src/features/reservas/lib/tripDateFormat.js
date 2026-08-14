/**
 * Formateo de fechas de viaje (Salida/Regreso/Prometidas) para la ficha de reserva.
 *
 * Archivo `.js` PURO (sin JSX) a propósito, igual que `reservaHeaderTituloLogic.js`:
 * se puede testear con `node --test` sin montar React, y ahora que vive acá (antes
 * estaba definida DENTRO de ReservaHeader.jsx, que sí tiene JSX) el test ya no
 * necesita copiar la función a mano — la importa de verdad.
 *
 * Bug "fechas corridas un día" (2026-07-16, dueño): startDate/endDate (y ahora
 * PromisedStartDate/PromisedEndDate, ADR-053) son fechas-solo-día (el usuario elige
 * un día calendario, no una hora). El backend las guarda como medianoche UTC
 * ("...T00:00:00Z"). Si las pasamos por `new Date(value)` y pedimos el día en hora
 * LOCAL (UTC-3), la medianoche UTC del 23/05 cae a las 21:00 del 22/05 en Argentina
 * y el usuario ve "22/05/2026" en vez de "23/05/2026". Por eso las dos funciones de
 * acá abajo leen el día/mes/año directo del TEXTO (string-split), nunca pasan por
 * `new Date()` — mismo patrón que MonthNavigator y ReprogramarViajeModal.
 *
 * ADR-053 (2026-08-13): el par de fechas PROMETIDAS se pinta con el MISMO
 * `formatTripDate` que las calculadas — si alguna pantalla nueva arma su propia
 * versión "más simple", vuelve el mismo bug de siempre.
 */

/**
 * Fecha lista para mostrar en pantalla, formato dd/MM/aaaa (regla P-2).
 *
 * @param {string|null|undefined} value - fecha ISO del backend (con o sin hora).
 * @returns {string|null} - "23/05/2026", o null si no hay fecha o el texto no tiene forma de fecha.
 */
export function formatTripDate(value) {
    if (!value) return null;
    const soloFecha = String(value).split("T")[0];
    // Validacion numerica estricta (mismo criterio que la formatDate central):
    // un valor que no sea yyyy-MM-dd de verdad devuelve null, jamas texto basura.
    const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(soloFecha);
    if (!match) return null;
    const [, anio, mes, dia] = match;
    return `${dia}/${mes}/${anio}`;
}

/**
 * Fecha lista para PRE-RELLENAR un `<input type="date">` (formato yyyy-MM-dd que
 * pide el HTML), con la misma lectura por texto que `formatTripDate` — evita que el
 * casillero de "fecha prometida" muestre un día distinto al que tiene guardado.
 *
 * @param {string|null|undefined} value - fecha ISO del backend (con o sin hora).
 * @returns {string} - "2026-05-23", o cadena vacía si no hay fecha valida.
 */
export function toDateInputValue(value) {
    if (!value) return "";
    const soloFecha = String(value).split("T")[0];
    const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(soloFecha);
    if (!match) return "";
    const [, anio, mes, dia] = match;
    return `${anio}-${mes}-${dia}`;
}
