/**
 * Lógica pura de la Tanda P3 ("circuito proveedor", spec
 * docs/ux/2026-07-22-p3-confirmar-costo-debajo-de-lo-pagado.md, decisión D2): al editar un
 * servicio y bajar el costo del operador por debajo de lo que ya se le pagó, el motor NO
 * bloquea (puede ser un descuento real del operador) pero exige confirmación explícita.
 * El backend responde 409 con `code: "COST_BELOW_PAID_CONFIRMATION_REQUIRED"` y un
 * `message` en es-AR con el monto exacto de la diferencia.
 *
 * Esta función SOLO decide si un error de guardado es ESE caso puntual (para pintar el
 * cartel ÁMBAR de aviso en `ServiceInlineCard` en vez del cartel ROJO genérico de error).
 * El texto que se muestra al vendedor SIEMPRE es `error.payload.message` tal cual — acá
 * nunca se inventa, recorta ni recalcula un mensaje ni un monto.
 *
 * Vive en un .js sin JSX (mismo patrón que serviceCancellationGuard.js) para poder
 * testearlo con Node puro, sin bundler: node --test .../costConfirmationGuard.test.mjs
 */

// Código estable que manda el backend en el 409 de PUT de los 5 tipos de servicio
// (mismo nombre literal de cada lado para que no puedan divergir con el tiempo).
export const CODIGO_CONFIRMAR_COSTO_MENOR_A_PAGADO = "COST_BELOW_PAID_CONFIRMATION_REQUIRED";

/**
 * ¿Este error de guardado es el 409 de "confirmá que el costo nuevo queda por debajo de
 * lo ya pagado al operador"? Se lee SOLO `error.payload.code` (nunca se adivina comparando
 * el texto del mensaje, que es libre y en es-AR).
 *
 * @param {{ payload?: { code?: string } }|null|undefined} error - el error que lanza
 *   `api.js` (tiene `.payload` con el body JSON de la respuesta 409).
 * @returns {boolean}
 */
export function esRechazoCostoMenorAPagado(error) {
  return error?.payload?.code === CODIGO_CONFIRMAR_COSTO_MENOR_A_PAGADO;
}

/**
 * Suma la marca de confirmación al payload que YA armó `buildPayload()` para el tipo de
 * servicio activo. No reconstruye nada: el reenvío tras "Sí, confirmar" tiene que ser
 * EXACTAMENTE el mismo guardado que el que rechazó el 409, más este único campo extra
 * (contrato ya construido y testeado del lado del motor).
 *
 * @param {object} payload - el payload armado por el builder de ServiceInlineCard para
 *   el tipo activo (Hotel/Aereo/Traslado/Paquete/Asistencia).
 * @returns {object} un objeto nuevo (no muta `payload`) con `confirmCostBelowPaid: true`.
 */
export function agregarConfirmacionCostoMenorAPagado(payload) {
  return { ...(payload || {}), confirmCostBelowPaid: true };
}
