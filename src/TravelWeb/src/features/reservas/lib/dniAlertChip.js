/**
 * Semáforo de DNI vencido para cabotaje (2026-08-03, spec UX firmada): arma los datos visuales
 * del chip fijo de vencimiento de DNI que se muestra en cada fila de la solapa Pasajeros.
 *
 * Hermano gemelo de `passportAlertChip.js` — mismo lugar, mismo tamaño, mismo tratamiento.
 * Única diferencia real: acá hay un solo nivel ("Expired"), no hay versión ámbar "vence justo"
 * (el DNI no tiene ese matiz firmado, a diferencia del pasaporte).
 *
 * T-13 (el front NUNCA calcula fechas de vencimiento): el nivel (`dniAlertLevel`) y el texto
 * largo (`dniAlertText`) ya vienen CALCULADOS del backend en el PassengerDto, decidiendo adentro
 * con la llave de Configuración (enableDomesticDniExpiryAlert) y la marca Nacional del servicio.
 * Con la llave apagada, o sin servicio Nacional, o sin vencimiento cargado, el motor manda estos
 * campos en null y acá no se arma ningún chip (silencio firmado). La ÚNICA decisión que toma el
 * front es la misma que ya toma el chip de pasaporte: si la reserva tiene fechas de viaje
 * cargadas, para elegir entre los dos textos cortos firmados.
 */

/**
 * @param {{ dniAlertLevel?: string|null, dniAlertText?: string|null }} pasajero
 * @param {{ startDate?: string|null, endDate?: string|null }} reserva
 * @returns {{ key: string, label: string, className: string, title: string }|null}
 *   null cuando el motor no mandó ninguna alerta (dniAlertLevel es null/undefined).
 */
export function construirChipDni(pasajero, reserva) {
    const nivel = pasajero?.dniAlertLevel;
    if (!nivel) return null;

    // Mismo criterio que el chip de pasaporte (y que el motor, PassportExpiryRules/DniExpiryRules):
    // "sin fechas de viaje" = ni endDate ni startDate cargados en la reserva. Leer un campo que ya
    // existe en la reserva NO es recalcular un vencimiento.
    const tieneFechasDeViaje = Boolean(reserva?.endDate || reserva?.startDate);

    if (nivel === "Expired") {
        return {
            key: "dni-vencido",
            label: tieneFechasDeViaje ? "DNI vencido para el viaje" : "DNI vencido",
            // Mismo rojo (rose) que el chip "Pasaporte vencido" — un solo nivel, ningún color nuevo.
            className: "bg-rose-100 text-rose-700 border-rose-200 dark:bg-rose-900/30 dark:text-rose-300 dark:border-rose-800",
            // El texto largo del motor va como tooltip (title). Respaldo con el texto firmado si por
            // algún motivo no llegó (nunca dejamos el tooltip vacío).
            title: pasajero?.dniAlertText
                || "El DNI de este pasajero se vence antes del viaje. Para volar dentro del país piden DNI vigente (o pasaporte vigente).",
        };
    }

    // Nivel desconocido (no "Expired"): tratamiento conservador, sin chip — un valor raro del
    // backend nunca debe llegar a la pantalla como texto crudo (T-5).
    return null;
}
