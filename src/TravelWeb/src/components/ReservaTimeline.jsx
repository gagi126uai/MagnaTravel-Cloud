import { useState, useEffect } from "react";
import { api } from "../api";
import { format } from "date-fns";
import { es } from "date-fns/locale";
import { Activity } from "lucide-react";

export default function ReservaTimeline({ reservaId }) {
    const [events, setEvents] = useState([]);
    const [loading, setLoading] = useState(true);

    useEffect(() => {
        if (!reservaId) return;

        const fetchTimeline = async () => {
            try {
                setLoading(true);
                const res = await api.get(`/reservas/${reservaId}/timeline`);
                setEvents(res || []);
            } catch (error) {
                console.error("Error fetching timeline:", error);
            } finally {
                setLoading(false);
            }
        };

        fetchTimeline();
    }, [reservaId]);

    if (loading) {
        return <div className="p-4 text-center text-slate-500">Cargando historial operativo...</div>;
    }

    if (events.length === 0) {
        return (
            <div className="rounded-lg border border-dashed border-slate-300 bg-slate-50 p-8 text-center dark:border-slate-700 dark:bg-slate-800">
                <Activity className="mx-auto mb-2 h-8 w-8 text-slate-400" />
                <p className="text-slate-500 dark:text-slate-400">No hay movimientos operativos registrados.</p>
            </div>
        );
    }

    return (
        <div className="flow-root">
            <ul role="list" className="-mb-8">
                {events.map((event, eventIdx) => (
                    <li key={eventIdx}>
                        <div className="relative pb-8">
                            {eventIdx !== events.length - 1 ? (
                                <span className="absolute left-4 top-4 -ml-px h-full w-0.5 bg-slate-200 dark:bg-slate-700" aria-hidden="true" />
                            ) : null}
                            <div className="relative flex space-x-3">
                                <div>
                                    <span className={`flex h-8 w-8 items-center justify-center rounded-full ring-8 ring-white dark:ring-slate-900 
                                        ${event.eventType === 'Create' ? 'bg-emerald-500' :
                                            event.eventType === 'Update' ? 'bg-indigo-500' :
                                                (event.eventType === 'Delete' || event.eventType === 'SoftDelete') ? 'bg-rose-500' : 'bg-slate-500'
                                        }`}>
                                        {event.eventType === 'Create' && <PlusIcon className="h-4 w-4 text-white" />}
                                        {event.eventType === 'Update' && <EditIcon className="h-4 w-4 text-white" />}
                                        {(event.eventType === 'Delete' || event.eventType === 'SoftDelete') && <TrashIcon className="h-4 w-4 text-white" />}
                                    </span>
                                </div>
                                <div className="flex min-w-0 flex-1 justify-between space-x-4 pt-1.5">
                                    <div>
                                        <p className="text-sm text-slate-500 dark:text-slate-400">
                                            <span className="mr-1 font-bold text-slate-900 dark:text-white">{event.title}</span>
                                            por <span className="font-medium text-slate-900 dark:text-white">{event.actor}</span>
                                        </p>

                                        {event.details && (
                                            <div className="mt-2 rounded border border-slate-100 bg-slate-50 p-3 text-sm text-slate-700 dark:border-slate-700 dark:bg-slate-800 dark:text-slate-300">
                                                {event.details.split('\n').map((line, i) => {
                                                    const isBoldSplit = line.includes('**');
                                                    if (isBoldSplit) {
                                                        const parts = line.split('**');
                                                        return (
                                                            <div key={i} className="mb-1 last:mb-0">
                                                                <span className="font-bold text-slate-600 dark:text-slate-400">{parts[1]}</span>
                                                                {parts[2]}
                                                            </div>
                                                        );
                                                    }
                                                    return <div key={i} className="mb-1 last:mb-0">{line}</div>;
                                                })}
                                            </div>
                                        )}
                                    </div>
                                    <div className="whitespace-nowrap text-right text-xs font-medium text-slate-500 dark:text-slate-400">
                                        <time dateTime={event.timestamp}>{format(new Date(event.timestamp), "d MMM HH:mm", { locale: es })}</time>
                                    </div>
                                </div>
                            </div>
                        </div>
                    </li>
                ))}
            </ul>
        </div>
    );
}

// Icons
function PlusIcon(props) {
    return <svg {...props} fill="none" viewBox="0 0 24 24" stroke="currentColor"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 4v16m8-8H4" /></svg>
}
function EditIcon(props) {
    return <svg {...props} fill="none" viewBox="0 0 24 24" stroke="currentColor"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M15.232 5.232l3.536 3.536m-2.036-5.036a2.5 2.5 0 113.536 3.536L6.5 21.036H3v-3.572L16.732 3.732z" /></svg>
}
function TrashIcon(props) {
    return <svg {...props} fill="none" viewBox="0 0 24 24" stroke="currentColor"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M19 7l-.867 12.142A2 2 0 0116.138 21H7.862a2 2 0 01-1.995-1.858L5 7m5 4v6m4-6v6m1-10V4a1 1 0 00-1-1h-4a1 1 0 00-1 1v3M4 7h16" /></svg>
}
