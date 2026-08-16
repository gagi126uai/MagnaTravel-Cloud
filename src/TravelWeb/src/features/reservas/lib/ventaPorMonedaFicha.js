/**
 * Deriva "Total del viaje" y "Por persona" para la franja de números de la ficha
 * (ReservaSummaryStrip.jsx), SOLO en etapa Presupuesto (decisión firmada del dueño,
 * 2026-08-16). Es un archivo .js puro (sin JSX) a propósito, mismo criterio que
 * reservaDestinoFicha.js: así se puede testear con `node --test` y el componente
 * solo pinta lo que este módulo ya calculó.
 *
 * El backend manda `reserva.ventaPorMoneda`: una lista de
 * { currency, total, perPerson }. `perPerson` viene `null` cuando todavía no hay
 * pasajeros DECLARADOS (ADR-031, "somos 4") — sin esa cantidad no hay por cuántos
 * repartir el total.
 *
 * Regla P-3 (la más dura del producto, firmada): pesos y dólares NUNCA se suman.
 * Por eso esto siempre devuelve una LISTA — un renglón por moneda — y el
 * componente pinta cada renglón por separado, nunca los mezcla en un solo número.
 */

/**
 * Arma las líneas de venta por moneda para mostrar en la ficha.
 * Devuelve `null` cuando no corresponde mostrar nada:
 *   - la reserva no está en Presupuesto (Budget);
 *   - el backend todavía no manda `ventaPorMoneda` (API vieja cacheada, tolerancia
 *     pedida en la tarea);
 *   - `ventaPorMoneda` vino como lista vacía.
 *
 * @param {object} reserva - DTO de detalle de la reserva.
 * @returns {Array<{currency: string, total: number, perPerson: number|null}>|null}
 */
export function armarLineasVentaPorMoneda(reserva) {
  if (!reserva || reserva.status !== "Budget") return null;
  if (!Array.isArray(reserva.ventaPorMoneda) || reserva.ventaPorMoneda.length === 0) return null;

  return reserva.ventaPorMoneda.map((linea) => ({
    currency: linea.currency,
    total: Number(linea.total ?? 0),
    perPerson: linea.perPerson === null || linea.perPerson === undefined
      ? null
      : Number(linea.perPerson),
  }));
}

/**
 * Indica si corresponde mostrar el aviso "Cargá los pasajeros para ver el por
 * persona" — UNA sola vez, no repetido por moneda (P-16: un dato no se dice dos
 * veces). Aparece cuando NINGUNA línea trae `perPerson` (sin pasajeros declarados
 * es un dato de TODA la reserva, no de una moneda puntual: si una línea no lo
 * tiene, ninguna la tiene).
 *
 * Nota de convivencia con `armarAvisoPasajerosFaltantes` (reservaDestinoFicha.js):
 * ese aviso exige DECLARADO > 0 (se cargó "somos N" pero falta tipear algún
 * nombre). Este caso es DECLARADO = 0 (perPerson null). Son mutuamente
 * excluyentes por construcción — nunca van a convivir en pantalla, así que no
 * hace falta un chequeo cruzado extra acá.
 *
 * @param {Array<{perPerson: number|null}>|null} lineas
 * @returns {boolean}
 */
export function debeMostrarAvisoSinPasajerosDeclarados(lineas) {
  if (!Array.isArray(lineas) || lineas.length === 0) return false;
  return lineas.every((linea) => linea.perPerson === null);
}
