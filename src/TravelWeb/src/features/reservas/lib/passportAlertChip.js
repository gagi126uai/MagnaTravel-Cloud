/**
 * F11 (D2, 2026-07-31, mockup firmado): arma los datos visuales del chip fijo de vencimiento
 * de pasaporte que se muestra en cada fila de la solapa Pasajeros.
 *
 * T-13 (el front NUNCA calcula fechas de vencimiento): el nivel (`passportAlertLevel`) y el
 * texto largo (`passportAlertText`) ya vienen CALCULADOS del backend en el PassengerDto — acá
 * solo se traduce ese nivel a la forma visual del chip (texto corto, color, tooltip). La ÚNICA
 * decisión que toma el front es si la reserva tiene fechas de viaje cargadas o no (un dato que
 * ya tiene a mano, reserva.startDate/endDate), para elegir entre los dos textos cortos del
 * mockup — eso no es "calcular una fecha", es leer un campo que ya existe.
 */

/**
 * @param {{ passportAlertLevel?: string|null, passportAlertText?: string|null }} pasajero
 * @param {{ startDate?: string|null, endDate?: string|null }} reserva
 * @returns {{ key: string, label: string, className: string, title: string }|null}
 *   null cuando el motor no mandó ninguna alerta (passportAlertLevel es null/undefined).
 */
export function construirChipPasaporte(pasajero, reserva) {
    const nivel = pasajero?.passportAlertLevel;
    if (!nivel) return null;

    // Mismo criterio que el motor (PassportExpiryRules, B8): "sin fechas de viaje" = ni
    // endDate ni startDate cargados en la reserva. Esto NO es recalcular el vencimiento, es
    // leer un campo de la reserva que el front ya tiene disponible en otros lados (ej.
    // ReservaHeader).
    const tieneFechasDeViaje = Boolean(reserva?.endDate || reserva?.startDate);

    if (nivel === "Expired") {
        return {
            key: "pasaporte-vencido",
            label: tieneFechasDeViaje ? "Pasaporte vencido para el viaje" : "Pasaporte vencido",
            className: "bg-rose-100 text-rose-700 border-rose-200 dark:bg-rose-900/30 dark:text-rose-300 dark:border-rose-800",
            // El texto largo del motor va como tooltip (title). Si por algún motivo no llegó,
            // el mensaje corto alcanza igual como respaldo (nunca dejamos el tooltip vacío).
            title: pasajero?.passportAlertText || "El pasaporte de este pasajero está vencido.",
        };
    }

    if (nivel === "Tight") {
        return {
            key: "pasaporte-vence-justo",
            label: "Pasaporte vence justo",
            className: "bg-amber-100 text-amber-700 border-amber-200 dark:bg-amber-900/30 dark:text-amber-300 dark:border-amber-800",
            title: pasajero?.passportAlertText || "Al pasaporte le quedan menos de 6 meses después del viaje.",
        };
    }

    // Nivel desconocido (ni "Expired" ni "Tight"): tratamiento conservador, sin chip — un
    // valor raro del backend nunca debe llegar a la pantalla como texto crudo (T-5).
    return null;
}
