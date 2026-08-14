import { Calendar } from 'lucide-react';
import { formatTripDate } from '../lib/tripDateFormat';
import { PromisedDatesBlock } from './PromisedDatesBlock';

/**
 * Renglón de fechas del viaje en la cabecera de la ficha (ADR-053, spec UX
 * 2026-08-13). Reemplaza al viejo botón "Editar fechas" + su modal: Salida/Regreso
 * pasaron a ser CALCULADAS por el motor desde los servicios vigentes (los anulados
 * ya no cuentan) y son de solo lectura acá — no hay casillero, no hay lápiz.
 *
 * Debajo, un bloque aparte y opcional para la "fecha prometida al cliente"
 * (ver PromisedDatesBlock.jsx) — esa sí la carga el vendedor a mano.
 */
export function TripDatesRow({ reserva, canEditPromisedDates, candadoDeEdicionActivo, onRequestEdit, onPromisedDatesChanged }) {
    const startLabel = formatTripDate(reserva.startDate);
    const endLabel = formatTripDate(reserva.endDate);

    // P1/P2 (spec UX, respuestas firmadas del dueño 2026-08-13): tres casos
    // posibles para el texto del renglón, según lo que trae el cálculo.
    let textoFechas;
    if (startLabel && endLabel) {
        textoFechas = `Del ${startLabel} al ${endLabel}`;
    } else if (startLabel) {
        // Solo hay salida (ej. un vuelo suelto de ida): el "al ..." NO se muestra vacío.
        textoFechas = `Del ${startLabel}`;
    } else if (endLabel) {
        // Caso borde no descripto en la spec (en teoría no debería pasar: el motor
        // calcula Start/End con el MISMO conjunto de servicios vigentes — si hay
        // fecha de regreso, tiene que haber fecha de salida). Defensivo, para no
        // mostrar "Sin fechas todavía" mintiendo cuando en realidad SÍ hay una.
        textoFechas = `Hasta el ${endLabel}`;
    } else {
        // P2: ningún servicio vivo con fecha (reserva recién creada, o todos anulados).
        textoFechas = 'Sin fechas todavía — se arman al cargar los servicios';
    }

    return (
        <div className="flex flex-col gap-1.5">
            <div className="inline-flex w-fit flex-col gap-0.5 rounded-xl border border-slate-200 bg-white px-3 py-1.5 text-sm dark:border-slate-800 dark:bg-slate-900">
                <span className="inline-flex items-center gap-2">
                    <Calendar className="h-4 w-4 flex-shrink-0 text-slate-400 dark:text-slate-500" aria-hidden="true" />
                    <span className="font-bold text-slate-900 dark:text-white" data-testid="reserva-fechas-calculadas">
                        {textoFechas}
                    </span>
                </span>
                {/* P1: aclaración chiquita gris debajo. Solo tiene sentido cuando SÍ hay
                    alguna fecha calculada — el texto de "Sin fechas todavía" ya explica
                    de dónde salen, repetirlo sería el mismo dato dos veces (P-16). */}
                {(startLabel || endLabel) && (
                    <span className="pl-6 text-[11px] text-slate-400 dark:text-slate-500">
                        según los servicios cargados
                    </span>
                )}
            </div>

            <PromisedDatesBlock
                reserva={reserva}
                publicId={reserva.publicId}
                canEdit={canEditPromisedDates}
                candadoActivo={candadoDeEdicionActivo}
                onRequestEdit={onRequestEdit}
                onSaved={onPromisedDatesChanged}
            />
        </div>
    );
}
