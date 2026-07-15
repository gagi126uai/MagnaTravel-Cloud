/**
 * Lógica PURA de "¿Suele cobrar multa cuando se anula?" — configuración de multas de
 * cancelación en la ficha del operador (2026-07-14, spec
 * `docs/ux/2026-07-14-config-multas-proveedor.md`, Pieza 1).
 *
 * Contexto de negocio: algunos operadores casi siempre cobran multa al anular, otros
 * casi nunca, y otros "depende de la tarifa" (Gastón dixit, 2026-07-14: sus operadores
 * son "mitad y mitad"). Este campo es solo una PISTA para el vendedor — el sistema NUNCA
 * decide solo ni completa montos: en el paso de la multa de una anulación, esta config
 * solo resalta un camino sugerido (ver `sugerenciaCaminoMulta` en
 * `features/cancellations/operatorPenaltyBanner.js`).
 *
 * Molde EXACTO del campo hermano `treasuryFxAssumedByOverride` (ver
 * `lib/treasuryFxAssumedBy.js`): mismo criterio de serialización — el backend
 * (`SupplierPenaltyBehavior.cs`) NO tiene JsonStringEnumConverter configurado, así que el
 * enum viaja como el INT crudo (Unknown=0, RarelyCharges=1, UsuallyCharges=2), nunca como
 * el nombre en texto. A diferencia del campo hermano, acá NO hay un valor "hereda la
 * config general" — Unknown ("no se sabe") es en sí mismo un valor de negocio válido y es
 * el default con el que arranca todo operador nuevo.
 */

export const SUPPLIER_PENALTY_BEHAVIOR = {
  Unknown: 0,
  RarelyCharges: 1,
  UsuallyCharges: 2,
};

/**
 * Las 3 opciones del desplegable de la ficha del operador, EN EL ORDEN EXACTO de la
 * spec (2026-07-14, "Textos finales"): primero las dos opciones "de excepción" (casi
 * nunca / casi siempre), y al final el default invisible ("no se sabe"), que es el valor
 * con el que arranca todo operador mientras nadie lo toque.
 */
export const OPCIONES_COMPORTAMIENTO_MULTA_OPERADOR = [
  { value: SUPPLIER_PENALTY_BEHAVIOR.RarelyCharges, label: "Casi nunca cobra multa" },
  { value: SUPPLIER_PENALTY_BEHAVIOR.UsuallyCharges, label: "Casi siempre cobra multa" },
  { value: SUPPLIER_PENALTY_BEHAVIOR.Unknown, label: "No se sabe / depende de la tarifa" },
];

/**
 * Traduce el valor crudo que vino del backend (INT del enum, o null/undefined en un DTO
 * viejo que todavía no traía este campo) al valor que espera el <select> de la ficha.
 * Cualquier valor no reconocido cae al default seguro "no se sabe" — nunca se inventa una
 * excepción que el operador no tiene configurada.
 *
 * @param {number|null|undefined} penaltyBehavior
 * @returns {number}
 */
export function valorSelectDesdePenaltyBehavior(penaltyBehavior) {
  if (penaltyBehavior === SUPPLIER_PENALTY_BEHAVIOR.RarelyCharges) return SUPPLIER_PENALTY_BEHAVIOR.RarelyCharges;
  if (penaltyBehavior === SUPPLIER_PENALTY_BEHAVIOR.UsuallyCharges) return SUPPLIER_PENALTY_BEHAVIOR.UsuallyCharges;
  return SUPPLIER_PENALTY_BEHAVIOR.Unknown;
}

/**
 * Traduce la selección del <select> (el DOM siempre entrega el `value` como STRING, aunque
 * las `option` se hayan armado con un número) al INT que espera el payload del PUT del
 * operador (`SupplierUpsertRequest.penaltyBehavior`).
 *
 * @param {string|number} valorSelect
 * @returns {number}
 */
export function penaltyBehaviorDesdeValorSelect(valorSelect) {
  const numero = Number(valorSelect);
  if (numero === SUPPLIER_PENALTY_BEHAVIOR.RarelyCharges) return SUPPLIER_PENALTY_BEHAVIOR.RarelyCharges;
  if (numero === SUPPLIER_PENALTY_BEHAVIOR.UsuallyCharges) return SUPPLIER_PENALTY_BEHAVIOR.UsuallyCharges;
  return SUPPLIER_PENALTY_BEHAVIOR.Unknown;
}
