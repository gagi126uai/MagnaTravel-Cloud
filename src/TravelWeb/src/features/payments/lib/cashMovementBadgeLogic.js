/**
 * Lógica PURA del badge "Reemplazado"/"Anulado" del Libro de Caja (Obra 2, firma de
 * Gastón 2026-07-27): "Par de Caja por EDICIÓN se etiqueta 'Reemplazado', distinto del
 * par por anulación real que sigue diciendo 'Anulado'. El cajero distingue qué pasó sin
 * abrir nada." Se separa del JSX (`MovementsTab.jsx`) para poder testear el mapeo sin
 * montar React, mismo criterio que el resto de los helpers de `lib/`.
 *
 * CONTRATO CON EL MOTOR: `CashMovementDto` suma el campo `isReplaced` (bool), además del
 * `isAnnulled` que ya existía desde H14 (2026-07-25). Las filas viejas que todavía no
 * traen `isReplaced` (por ejemplo, una respuesta cacheada de una versión anterior del
 * motor) llegan con ese campo en `undefined` — el fallback de abajo hace que esas filas
 * se comporten EXACTAMENTE igual que hoy (solo miran `isAnnulled`), nunca rompen.
 */

/**
 * Calcula qué badge (si corresponde) hay que mostrar junto al origen del movimiento, y
 * el motivo en criollo que se ve al lado de los botones apagados (P-9: el motivo SIEMPRE
 * a la vista, nunca solo en un tooltip).
 *
 * Orden de prioridad: un movimiento reemplazado por una edición manda "Reemplazado" aunque
 * el motor también lo haya marcado `isAnnulled` internamente (el reemplazo se implementa
 * puertas adentro como "anular el viejo + crear el nuevo" — el cajero no necesita saber
 * eso, para él la palabra correcta es "Reemplazado").
 *
 * `estado` es un token ESTABLE en minúsculas ("reemplazado"/"anulado"), pensado para
 * `data-estado` en el DOM (fix del reviewer, 2026-07-27): antes QA tenía que buscar el
 * badge por un `data-testid` que cambiaba de nombre según el estado
 * (`movimiento-reemplazado-badge-{id}` vs `movimiento-anulado-badge-{id}`) — un selector
 * que cambia de nombre es frágil para automatizar. Ahora el testid es SIEMPRE el mismo
 * (`movimiento-estado-badge-{id}`) y `data-estado` es lo que varía.
 *
 * @param {{isReplaced?: boolean, isAnnulled?: boolean}|null|undefined} movement
 * @returns {{etiqueta: string, estado: string, motivoBotonesApagados: string}|null} null
 *   si el movimiento está en su estado normal (ni reemplazado ni anulado) — no se
 *   muestra ningún badge.
 */
export function obtenerEstadoBadgeMovimiento(movement) {
  if (movement?.isReplaced) {
    return {
      etiqueta: "Reemplazado",
      estado: "reemplazado",
      motivoBotonesApagados: "Fue reemplazado por una edición, no se puede editar ni anular.",
    };
  }

  if (movement?.isAnnulled) {
    return {
      etiqueta: "Anulado",
      estado: "anulado",
      motivoBotonesApagados: "Ya está anulado, no se puede editar ni anular de nuevo.",
    };
  }

  return null;
}

/**
 * True si los botones Editar/Anular de un movimiento manual deben quedar apagados
 * (P-9): tanto un movimiento reemplazado como uno anulado ya no se pueden volver a tocar.
 *
 * Bloque 3 "descalce devolución-caja" (2026-08-19): se suma una TERCERA causa — un
 * movimiento generado por un reembolso de operador (category === "OperatorRefund") tampoco
 * se puede tocar desde acá, aunque no esté reemplazado ni anulado. Ese movimiento se corrige
 * desde el circuito de la devolución (solapa Reembolsos del operador), no desde Caja: si se
 * editara/anulara acá, la caja y el extracto del operador quedarían desincronizados sin que
 * nadie se entere. Sin dependencia de backend: el dato (`category`) ya viaja hoy.
 *
 * @param {{isReplaced?: boolean, isAnnulled?: boolean, category?: string}|null|undefined} movement
 * @returns {boolean}
 */
export function debeApagarBotonesMovimiento(movement) {
  return Boolean(movement?.isReplaced || movement?.isAnnulled || esMovimientoDeReembolsoOperador(movement));
}

/**
 * True si este movimiento de Caja lo generó un reembolso de operador YA REGISTRADO
 * (solapa "Reembolsos" del operador, no un ajuste manual ni un cobro/pago normal).
 *
 * @param {{category?: string}|null|undefined} movement
 * @returns {boolean}
 */
export function esMovimientoDeReembolsoOperador(movement) {
  return movement?.category === "OperatorRefund";
}

/**
 * Motivo EXACTO (T-6, texto fijado) que se muestra cuando Editar/Anular están apagados
 * porque el movimiento viene de un reembolso de operador. Es el ÚNICO caso de esta obra
 * que NO agrega un chip nuevo (P-16: el ícono ya se ve gris, alcanza para distinguirlo) —
 * el motivo se muestra en un globito (escritorio) o texto fijo (mobile), nunca los dos con
 * el mismo criterio que Reemplazado/Anulado (ver MovementsTab.jsx).
 *
 * @param {{numeroReserva?: string}|null|undefined} movement
 * @returns {string}
 */
export function construirMotivoReembolsoOperadorApagado(movement) {
  const numeroReserva = movement?.numeroReserva ?? "";
  return (
    `Atado a la devolución recibida del operador de la reserva ${numeroReserva}. Se corrige desde ` +
    "el circuito de la devolución (solapa Reembolsos), no acá."
  );
}
