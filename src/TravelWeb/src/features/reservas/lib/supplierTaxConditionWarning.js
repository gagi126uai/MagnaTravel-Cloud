/**
 * Aviso "falta condición fiscal del operador" al cancelar/borrar un servicio.
 *
 * Bug #22 (Tanda 4, 2026-07-24) — parte de pantalla. El motor ahora manda
 * `supplierTaxConditionUnknown: boolean` en cada fila de servicio (hotelBookings[],
 * flightSegments[], transferBookings[], packageBookings[], assistanceBookings[] y
 * servicios[] del GET de la reserva): true = el operador de ESE servicio todavía no
 * tiene la condición fiscal cargada en su ficha.
 *
 * Decisión del diagnóstico: esto es un AVISO TEMPRANO, no un bloqueo. El bloqueo real
 * sigue viviendo más adelante, en el circuito de nota de crédito (esa es la red de
 * seguridad de negocio) — acá solo le avisamos al vendedor ANTES de que cancele, para
 * que pueda cargar el dato del operador y evitarse la traba después.
 */

export const TEXTO_AVISO_CONDICION_FISCAL_OPERADOR_DESCONOCIDA =
    "Falta la condición fiscal del operador. Cargala en su ficha antes de cancelar, así la nota de crédito no se traba después.";

/**
 * Decide si hay que mostrar el aviso de condición fiscal desconocida para un servicio.
 * Default seguro: sin el campo (dato faltante o backend viejo), NO se avisa — "sin dato,
 * sin aviso" es el mismo criterio que ya usa el resto del proyecto para no generar falsos
 * positivos con reservas creadas antes de que este campo existiera.
 *
 * @param {{supplierTaxConditionUnknown?: boolean}|null|undefined} service
 * @returns {boolean}
 */
export function debeAvisarCondicionFiscalOperadorDesconocida(service) {
    return service?.supplierTaxConditionUnknown === true;
}
