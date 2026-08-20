import {
    agruparEventosPorDia,
    horaDeEvento,
    describirEventoHistorial,
} from "../lib/reservaTimelineText";

/**
 * Esqueleto visual de una línea de tiempo: agrupado por día, con hora + punto de color +
 * frase humana por renglón — SIN el fetch ni los estados de carga/error/vacío (esos los arma
 * cada pantalla dueña, porque cada una tiene su propio endpoint y sus propios textos).
 *
 * Se extrajo de `ReservaTimeline.jsx` en la obra "la ficha del operador no borra la historia"
 * (2026-08-20, spec docs/ux/2026-08-20-operador-con-rastro-e-historial.md §3.3) para que la
 * solapa "Historial" de la reserva Y la solapa "Historial" del operador
 * (`SupplierHistorialSection.jsx`) compartan el MISMO molde en vez de tener dos copias del
 * mismo JSX que se puedan desincronizar con el tiempo.
 *
 * @param {{ events: object[] }} props - `events` YA ordenados del más nuevo al más viejo
 *   (como los mandan los dos endpoints de historial); este componente solo agrupa por día,
 *   nunca reordena nada.
 */
export function TimelineEventsList({ events }) {
    // El backend ya manda los eventos del más nuevo al más viejo — acá solo se
    // agrupan por día calendario de Argentina, sin reordenar nada.
    const grupos = agruparEventosPorDia(events);

    return (
        <div>
            {grupos.map((grupo) => (
                <div key={grupo.etiqueta}>
                    <SeparadorDeDia etiqueta={grupo.etiqueta} />
                    {grupo.eventos.map((event, idx) => (
                        <Hito
                            key={`${event.timestamp}-${idx}`}
                            event={event}
                            esUltimoDelDia={idx === grupo.eventos.length - 1}
                        />
                    ))}
                </div>
            ))}
        </div>
    );
}

/**
 * Separador de día ("Hoy — 03/08/2026"): una línea fina con la etiqueta a la
 * izquierda, igual que la maqueta (".dia").
 */
function SeparadorDeDia({ etiqueta }) {
    return (
        <div className="mb-2 mt-5 flex items-center gap-3 first:mt-0">
            <span className="whitespace-nowrap text-xs font-bold text-slate-500 dark:text-slate-400">
                {etiqueta}
            </span>
            <span className="h-px flex-1 bg-slate-200 dark:bg-slate-700" aria-hidden="true" />
        </div>
    );
}

// Color del punto de la línea de tiempo según qué pasó — mismo criterio que
// describirEventoHistorial ya calculó (rojo=anulación/reversa, verde=entra plata,
// indigo=documento fiscal, ambar=decisión notable sin plata, neutro=el resto).
const COLOR_PUNTO = {
    rojo: "bg-rose-500",
    verde: "bg-emerald-500",
    indigo: "bg-blue-500",
    ambar: "bg-amber-500",
    neutro: "bg-slate-400 dark:bg-slate-600",
};

/**
 * Un renglón de la línea de tiempo: hora, punto de color, y la frase humana
 * (con el actor en negrita cuando lo hizo una persona). `esUltimoDelDia`
 * decide si se dibuja la línea vertical que conecta con el próximo renglón
 * del MISMO día (no cruza el separador hacia el día anterior).
 */
function Hito({ event, esUltimoDelDia }) {
    const descripcion = describirEventoHistorial(event);
    const hora = horaDeEvento(event.timestamp);

    return (
        <div className="grid grid-cols-[44px_16px_1fr] items-start gap-2.5 py-1.5" data-testid="hito-historial">
            <div className="pt-0.5 text-xs text-slate-400 dark:text-slate-500">{hora}</div>

            <div className="relative flex justify-center pt-1.5">
                <span
                    className={`h-2 w-2 rounded-full ${COLOR_PUNTO[descripcion.colorPunto] || COLOR_PUNTO.neutro}`}
                    aria-hidden="true"
                />
                {!esUltimoDelDia && (
                    <span
                        className="absolute top-3.5 bottom-[-22px] w-px bg-slate-200 dark:bg-slate-700"
                        aria-hidden="true"
                    />
                )}
            </div>

            <div className="min-w-0 pb-1">
                <p className="text-[13.5px] text-slate-700 dark:text-slate-300">
                    {descripcion.esCobro ? (
                        <FraseCobro actor={descripcion.actor} montoTexto={descripcion.montoTexto} />
                    ) : (
                        <>
                            {descripcion.actor && (
                                <span className="font-bold text-slate-900 dark:text-white">{descripcion.actor} </span>
                            )}
                            {descripcion.frase}
                        </>
                    )}
                </p>
                {descripcion.detalle && (
                    <p className="mt-0.5 text-xs text-slate-400 dark:text-slate-500">{descripcion.detalle}</p>
                )}
            </div>
        </div>
    );
}

/**
 * Frase de un cobro: "Actor cobró $X." con el monto en verde y negrita — nunca
 * en negativo (regla firmada de la maqueta: "un cobro entra plata"). Si no hay
 * actor humano (caso raro, cobro registrado por un proceso sin usuario), queda
 * "Se cobró $X." en vez de nombrar a un actor que no existe.
 */
function FraseCobro({ actor, montoTexto }) {
    return (
        <>
            {actor ? (
                <span className="font-bold text-slate-900 dark:text-white">{actor} </span>
            ) : null}
            {actor ? "cobró " : "Se cobró "}
            {montoTexto ? (
                <span className="font-bold text-emerald-600 dark:text-emerald-400">{montoTexto}</span>
            ) : null}
            .
        </>
    );
}
