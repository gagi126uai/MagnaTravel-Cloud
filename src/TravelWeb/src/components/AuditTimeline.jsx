import { useState, useEffect } from "react";
import { api } from "../api";
import { format } from "date-fns";
import { es } from "date-fns/locale";
import { Clock, User, Activity, ArrowRight } from "lucide-react";

export default function AuditTimeline({ entityName, entityId }) {
    const [logs, setLogs] = useState([]);
    const [loading, setLoading] = useState(true);

    useEffect(() => {
        if (!entityName || !entityId) return;

        const fetchLogs = async () => {
            try {
                setLoading(true);
                const res = await api.get(`/auditlogs?entityName=${entityName}&entityId=${entityId}`);
                setLogs(res || []);
            } catch (error) {
                console.error("Error fetching audit logs:", error);
            } finally {
                setLoading(false);
            }
        };

        fetchLogs();
    }, [entityName, entityId]);

    if (loading) {
        return <div className="p-4 text-center text-gray-500">Cargando historial...</div>;
    }

    if (logs.length === 0) {
        return (
            <div className="p-8 text-center bg-gray-50 dark:bg-slate-800 rounded-lg border border-dashed border-gray-300 dark:border-slate-700">
                <Activity className="w-8 h-8 text-gray-400 mx-auto mb-2" />
                <p className="text-gray-500 dark:text-slate-400">No hay movimientos registrados.</p>
            </div>
        );
    }

    return (
        <div className="flow-root">
            <ul role="list" className="-mb-8">
                {logs.map((log, logIdx) => {
                    let changes = {};
                    try {
                        changes = JSON.parse(log.changes || "{}");
                    } catch (e) { }

                    return (
                        <li key={log.id}>
                            <div className="relative pb-8">
                                {logIdx !== logs.length - 1 ? (
                                    <span className="absolute top-4 left-4 -ml-px h-full w-0.5 bg-gray-200 dark:bg-slate-700" aria-hidden="true" />
                                ) : null}
                                <div className="relative flex space-x-3">
                                    <div>
                                        <span className={`h-8 w-8 rounded-full flex items-center justify-center ring-8 ring-white dark:ring-slate-900 
                      ${log.action === 'Create' ? 'bg-green-500' :
                                                log.action === 'Update' ? 'bg-blue-500' :
                                                    log.action === 'Delete' || log.action === 'SoftDelete' ? 'bg-red-500' : 'bg-gray-500'
                                            }`}>
                                            {log.action === 'Create' && <PlusIcon className="h-5 w-5 text-white" />}
                                            {log.action === 'Update' && <EditIcon className="h-5 w-5 text-white" />}
                                            {(log.action === 'Delete' || log.action === 'SoftDelete') && <TrashIcon className="h-5 w-5 text-white" />}
                                        </span>
                                    </div>
                                    <div className="min-w-0 flex-1 pt-1.5 flex justify-between space-x-4">
                                        <div>
                                            <p className="text-sm text-gray-500 dark:text-slate-400">
                                                <span className="font-medium text-gray-900 dark:text-white mr-1">{getActionLabel(log.action)}</span>
                                                por <span className="font-medium text-gray-900 dark:text-white">{log.userName}</span>
                                            </p>

                                            {/* Changes Detail */}
                                            {Object.keys(changes).length > 0 && (
                                                <div className="mt-2 text-sm text-gray-700 dark:text-slate-300 bg-gray-50 dark:bg-slate-800 p-2 rounded border border-gray-100 dark:border-slate-700">
                                                    {Object.entries(changes).map(([key, value]) => (
                                                        <div key={key} className="flex flex-wrap gap-1 mb-1 last:mb-0">
                                                            <span className="font-medium text-gray-500 dark:text-slate-400">{key}:</span>
                                                            {renderChangeValue(value)}
                                                        </div>
                                                    ))}
                                                </div>
                                            )}
                                        </div>
                                        <div className="text-right text-sm whitespace-nowrap text-gray-500 dark:text-slate-400">
                                            <time dateTime={log.timestamp}>{format(new Date(log.timestamp), "d MMM HH:mm", { locale: es })}</time>
                                        </div>
                                    </div>
                                </div>
                            </div>
                        </li>
                    );
                })}
            </ul>
        </div>
    );
}

function getActionLabel(action) {
    switch (action) {
        case 'Create': return 'Creado';
        case 'Update': return 'Modificado';
        case 'Delete': return 'Eliminado';
        case 'SoftDelete': return 'Enviado a Papelera';
        default: return action;
    }
}

function renderChangeValue(value) {
    if (typeof value === 'object' && value !== null && 'Old' in value && 'New' in value) {
        // It's a change pair
        return (
            <div className="flex items-center gap-2 text-xs">
                <span className="line-through text-red-400">{String(value.Old)}</span>
                <ArrowRight className="w-3 h-3 text-gray-400" />
                <span className="text-green-600 dark:text-green-400 font-medium">{String(value.New)}</span>
            </div>
        );
    }
    // Simple value (Create/Delete)
    return <span className="ml-1">{String(value)}</span>;
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
