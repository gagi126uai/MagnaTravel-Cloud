/**
 * Chip fijo de "menor en tramo internacional" (decision UX 2026-08-05 derivada de patrones firmados: P11=A ambar + spec DNI 2026-08-03; label a validar por Gaston): arma los datos
 * visuales del chip que se muestra en cada fila de la solapa Pasajeros.
 *
 * Hermano gemelo de `passportAlertChip.js` y `dniAlertChip.js` — mismo lugar, mismo tamaño,
 * mismo tratamiento. Única diferencia real: acá hay un solo nivel ("Notice") y NO hay texto
 * corto que cambie según si la reserva tiene fechas de viaje cargadas (a diferencia de los
 * chips de documento, este chip no depende de eso).
 *
 * T-13 (el front NUNCA calcula edades ni fechas): el nivel (`minorAlertLevel`) y el texto largo
 * (`minorAlertText`) ya vienen CALCULADOS del backend en el PassengerDto — acá solo se traduce
 * ese nivel a la forma visual del chip (texto corto, color, tooltip). El front no mira fecha de
 * nacimiento ni fechas de viaje para esto.
 */

// Label corto del chip. Recomendación UX (2026-08-05): dejar el texto en UN solo lugar para que
// Gaston lo pueda cambiar fácil por otra variante (ej. "Menor viaja al exterior") sin tocar el
// resto del archivo.
const LABEL_MENOR_INTERNACIONAL = "Menor: revisar autorización de salida";

/**
 * @param {{ minorAlertLevel?: string|null, minorAlertText?: string|null }} pasajero
 * @returns {{ key: string, label: string, className: string, title: string }|null}
 *   null cuando el motor no mandó ninguna alerta (minorAlertLevel es null/undefined).
 */
export function construirChipMenor(pasajero) {
    const nivel = pasajero?.minorAlertLevel;
    if (!nivel) return null;

    if (nivel === "Notice") {
        return {
            key: "menor-internacional",
            label: LABEL_MENOR_INTERNACIONAL,
            // Mismo ámbar que el chip "Pasaporte vence justo" — un solo nivel, ningún color nuevo.
            className: "bg-amber-100 text-amber-700 border-amber-200 dark:bg-amber-900/30 dark:text-amber-300 dark:border-amber-800",
            // El texto largo del motor va como tooltip (title). Respaldo con el texto firmado si por
            // algún motivo no llegó (nunca dejamos el tooltip vacío).
            title: pasajero?.minorAlertText
                || "Pasajero menor de edad en un tramo internacional. Revisá si necesita autorización para salir del país: el trámite varía según el destino y con quién viaja.",
        };
    }

    // Nivel desconocido (no "Notice"): tratamiento conservador, sin chip — un valor raro del
    // backend nunca debe llegar a la pantalla como texto crudo (T-5).
    return null;
}
