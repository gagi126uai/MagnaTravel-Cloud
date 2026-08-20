import { useState, useEffect, useCallback } from "react";
import { Clock } from "lucide-react";
import { api } from "../../../api";
import { camelize } from "../../../lib/utils";
import { getApiErrorMessage } from "../../../lib/errors";
import { ListLoadErrorState } from "../../../components/ui/ListLoadErrorState";
import { TimelineEventsList } from "../../../components/TimelineEventsList";

/**
 * Solapa "Historial" de la ficha del operador: línea de tiempo de TODO lo que pasó con ese
 * proveedor — compras confirmadas, anulaciones de reservas, multas (confirmadas o
 * perdonadas), reembolsos, pagos y facturas — ordenada del más nuevo al más viejo.
 *
 * Guía firmada 2026-08-19 #2 (spec docs/ux/2026-08-20-operador-con-rastro-e-historial.md §3):
 * es la ÚNICA pantalla de la ficha donde una decisión SIN plata ("cerró la multa sin cobrar
 * nada") queda visible. Mismo esqueleto visual que el Historial de la reserva
 * (`ReservaTimeline.jsx`), compartido vía `TimelineEventsList.jsx` — acá solo cambian el
 * endpoint y los textos de carga/error/vacío.
 *
 * Los montos respetan F-14 (enmascarado sin `cobranzas.see_cost`): el backend YA arma cada
 * frase con o sin número, este componente nunca decide eso — se limita a mostrar `event.title`
 * tal cual llega (ver `describirEventoHistorial`).
 */
export function SupplierHistorialSection({ supplierPublicId }) {
    const [events, setEvents] = useState([]);
    const [loading, setLoading] = useState(true);
    const [error, setError] = useState(null);

    const fetchTimeline = useCallback(async () => {
        if (!supplierPublicId) return;
        setLoading(true);
        setError(null);
        try {
            const rawRes = await api.get(`/suppliers/${supplierPublicId}/timeline`);
            // El endpoint devuelve { amountsVisible, events } (SupplierTimelineDto) — la
            // pantalla no necesita amountsVisible por su cuenta: el backend ya arma cada
            // Title con o sin monto según el permiso, así que acá solo se leen los eventos.
            setEvents(camelize(rawRes)?.events || []);
        } catch (err) {
            setError(getApiErrorMessage(err, "No pudimos traer el historial de este operador."));
        } finally {
            setLoading(false);
        }
    }, [supplierPublicId]);

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
                <Clock className="mx-auto mb-2 h-8 w-8 text-slate-400" />
                <p className="text-slate-500 dark:text-slate-400">Todavía no pasó nada con este operador.</p>
            </div>
        );
    }

    return <TimelineEventsList events={events} />;
}
