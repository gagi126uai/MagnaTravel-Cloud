import { useState, useEffect, useCallback } from "react";
import { api } from "../api";
import { Activity } from "lucide-react";
import { camelize } from "../lib/utils";
import { getApiErrorMessage } from "../lib/errors";
import { ListLoadErrorState } from "./ui/ListLoadErrorState";
import {
    agruparEventosPorDia,
    horaDeEvento,
    describirEventoHistorial,
} from "../lib/reservaTimelineText";

/**
 * Solapa "Historial" de la ficha de una reserva: línea de tiempo de todo lo que
 * pasó, agrupada por día y contada en criollo — no con jerga de programador.
 *
 * Tanda 4 (rediseño de fichas, 2026-08-04, maqueta docs/ux/maquetas/2026-08-03-
 * reservas-rediseno.html sección 8). Gastón la resumió así: "no es muy clara,
 * parece más de programador que de usuario de agencia de viajes". Los eventos
 * siguen siendo EXACTAMENTE los que manda el backend (GET .../timeline) — lo
 * que cambió es cómo se leen: agrupados por día ("Hoy", "Ayer", el nombre del
 * día), con hora + punto de color + una oración humana en vez de un título
 * técnico ("Cambio en una Factura") seguido de una lista de bullets cruda.
 */
export default function ReservaTimeline({ reservaId }) {
    const [events, setEvents] = useState([]);
    const [loading, setLoading] = useState(true);
    // Antes un error de red caía en un console.error silencioso y la pantalla
    // mostraba el mismo cartel de "todavía no pasó nada" que un historial
    // genuinamente vacío — un error disfrazado de vacío. Ahora se distinguen:
    // error propio (con reintentar) vs. vacío real (maqueta, nota "Cuando no
    // hay nada / cuando falla").
    const [error, setError] = useState(null);

    const fetchTimeline = useCallback(async () => {
        if (!reservaId) return;
        setLoading(true);
        setError(null);
        try {
            const rawRes = await api.get(`/reservas/${reservaId}/timeline`);
            setEvents(camelize(rawRes) || []);
        } catch (err) {
            setError(getApiErrorMessage(err, "No pudimos traer el historial de esta reserva."));
        } finally {
            setLoading(false);
        }
    }, [reservaId]);

    useEffect(() => {
        fetchTimeline();
    }, [fetchTimeline]);

    if (loading) {
        return <div className="p-4 text-center text-slate-500">Cargando el historial…</div>;
    }

    if (error) {
        return <ListLoadErrorState message={error} onRetry={fetchTimeline} />;
    }

    if (events.length === 0) {
        return (
            <div className="rounded-lg border border-dashed border-slate-300 bg-slate-50 p-8 text-center dark:border-slate-700 dark:bg-slate-800">
                <Activity className="mx-auto mb-2 h-8 w-8 text-slate-400" />
                <p className="text-slate-500 dark:text-slate-400">Todavía no pasó nada en esta reserva.</p>
            </div>
        );
    }

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
 * izquierda, igual que la maqueta (".dia"). Reemplaza la fecha repetida en
 * cada renglón que tenía la versión vieja.
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
// describirEventoHistorial ya calculó (rojo=anulación, verde=cobro,
// indigo=factura, neutro=el resto).
const COLOR_PUNTO = {
    rojo: "bg-rose-500",
    verde: "bg-emerald-500",
    indigo: "bg-indigo-500",
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
