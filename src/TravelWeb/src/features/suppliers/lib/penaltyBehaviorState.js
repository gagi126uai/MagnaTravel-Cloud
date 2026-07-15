/**
 * Lógica PURA del estado de carga de "¿Suele cobrar multa cuando se anula?" en la
 * ficha del operador (spec `docs/ux/2026-07-14-config-multas-proveedor.md`, Pieza 1).
 *
 * Molde EXACTO del campo hermano (`treasuryFxOverrideState.js`, fix F2 del gate de
 * frontend 2026-07-10): el valor real de este campo se busca en GET /suppliers/{id} (NO
 * está en el overview de la cuenta) y, si esa carga falla, el submit del formulario
 * ENTERO tiene que quedar BLOQUEADO hasta reintentar — guardar sin conocer el valor real
 * arriesgaría pisar una excepción real con el default "no se sabe" sin que nadie se
 * entere (mismo bug de fondo que F2, aplicado acá al campo nuevo).
 *
 * `SupplierAccountPage.jsx` carga ESTE campo y `treasuryFxAssumedByOverride` con el
 * MISMO fetch a GET /suppliers/{id} (los dos viven en la misma respuesta) — se mantienen
 * en módulos separados, uno por campo, para que un cambio futuro en un campo no arrastre
 * al otro por accidente, aunque hoy compartan la llamada de red.
 */

/**
 * Deriva el nuevo estado del campo a partir del resultado del fetch compartido.
 *
 * Mismo contrato que `siguienteEstadoTreasuryFxOverride`: en éxito, apaga
 * `cargandoPenaltyBehavior` y aplica el valor nuevo; en error, `cargandoPenaltyBehavior`
 * se queda en `true` (bloquea el submit) y el resultado NO incluye
 * `penaltyBehaviorSelect` a propósito — el llamador no debe tocar ese estado ahí, se
 * queda con el valor que ya tenía en pantalla.
 *
 * @param {{ exito: boolean, selectValueNuevo?: number, errorMessage?: string }} resultado
 * @returns {{ cargandoPenaltyBehavior: boolean, errorCargaPenaltyBehavior: string|null, penaltyBehaviorSelect?: number }}
 */
export function siguienteEstadoPenaltyBehavior({ exito, selectValueNuevo, errorMessage }) {
  if (exito) {
    return {
      cargandoPenaltyBehavior: false,
      errorCargaPenaltyBehavior: null,
      penaltyBehaviorSelect: selectValueNuevo,
    };
  }

  return {
    cargandoPenaltyBehavior: true,
    errorCargaPenaltyBehavior:
      errorMessage || "No se pudo cargar el comportamiento con multas de este operador. Reintentá antes de guardar.",
    // Sin penaltyBehaviorSelect: el llamador no debe pisarlo en este camino.
  };
}

/**
 * True si el formulario puede enviarse en lo que respecta a este campo puntual — mismo
 * criterio que `puedeGuardarConTreasuryFxOverride`.
 *
 * @param {boolean} cargandoPenaltyBehavior
 * @returns {boolean}
 */
export function puedeGuardarConPenaltyBehavior(cargandoPenaltyBehavior) {
  return !cargandoPenaltyBehavior;
}
