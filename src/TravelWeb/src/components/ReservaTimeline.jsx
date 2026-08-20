import { useState, useEffect, useCallback } from "react";
import { api } from "../api";
import { Activity } from "lucide-react";
import { camelize } from "../lib/utils";
import { getApiErrorMessage } from "../lib/errors";
import { ListLoadErrorState } from "./ui/ListLoadErrorState";
import { TimelineEventsList } from "./TimelineEventsList";

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
 *
 * Obra "la ficha del operador no borra la historia" (2026-08-20): el timeline
 * ahora también trae la anulación de la reserva, la multa del operador y sus
 * notas de crédito (antes ausentes) — ver `describirEventoHistorial` en
 * `lib/reservaTimelineText.js`. El esqueleto visual (agrupado + renglón) vive
 * en `TimelineEventsList.jsx`, compartido con la solapa gemela del operador
 * (`SupplierHistorialSection.jsx`) para no duplicar el mismo JSX dos veces.
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
            <div className="rounded-[10px] border border-dashed border-slate-300 bg-slate-50 p-8 text-center dark:border-slate-700 dark:bg-slate-800">
                <Activity className="mx-auto mb-2 h-8 w-8 text-slate-400" />
                <p className="text-slate-500 dark:text-slate-400">Todavía no pasó nada en esta reserva.</p>
            </div>
        );
    }

    return <TimelineEventsList events={events} />;
}
