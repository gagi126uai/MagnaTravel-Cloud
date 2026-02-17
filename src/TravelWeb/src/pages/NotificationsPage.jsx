import { useEffect } from "react";
import { useAlerts } from "../contexts/AlertsContext";
import { format } from "date-fns";
import { es } from "date-fns/locale";
import { CheckCircle, AlertCircle, Info, Bell, Check } from "lucide-react";

export default function NotificationsPage() {
    const { notifications, markAsRead, refreshAlerts } = useAlerts();

    useEffect(() => {
        refreshAlerts();
    }, []);

    const getIcon = (type) => {
        switch (type) {
            case "Success": return <CheckCircle className="h-5 w-5 text-green-500" />;
            case "Error": return <AlertCircle className="h-5 w-5 text-red-500" />;
            case "Warning": return <AlertCircle className="h-5 w-5 text-amber-500" />;
            default: return <Info className="h-5 w-5 text-blue-500" />;
        }
    };

    return (
        <div className="p-6 max-w-4xl mx-auto">
            <div className="flex items-center justify-between mb-6">
                <h1 className="text-2xl font-bold flex items-center gap-2">
                    <Bell className="h-6 w-6" />
                    Notificaciones
                </h1>
                <span className="bg-indigo-100 text-indigo-700 px-3 py-1 rounded-full text-sm font-medium">
                    {notifications.length} sin leer
                </span>
            </div>

            {notifications.length === 0 ? (
                <div className="text-center py-12 bg-white rounded-xl shadow-sm border border-slate-200">
                    <Inbox className="h-12 w-12 text-slate-300 mx-auto mb-3" />
                    <p className="text-slate-500">No tienes notificaciones nuevas</p>
                </div>
            ) : (
                <div className="space-y-4">
                    {notifications.map((notif) => (
                        <div key={notif.id} className="bg-white p-4 rounded-xl shadow-sm border border-slate-200 flex gap-4 transition-all hover:shadow-md">
                            <div className="mt-1 flex-shrink-0">
                                {getIcon(notif.type)}
                            </div>
                            <div className="flex-1">
                                <p className="text-slate-800 font-medium">{notif.message}</p>
                                <p className="text-xs text-slate-400 mt-1">
                                    {format(new Date(notif.createdAt), "dd 'de' MMMM, HH:mm", { locale: es })}
                                </p>
                            </div>
                            <button
                                onClick={() => markAsRead(notif.id)}
                                className="self-start p-2 text-slate-400 hover:text-indigo-600 hover:bg-indigo-50 rounded-lg transition-colors"
                                title="Marcar como leída"
                            >
                                <Check className="h-5 w-5" />
                            </button>
                        </div>
                    ))}
                </div>
            )}
        </div>
    );
}

function Inbox({ className }) {
    return (
        <svg
            className={className}
            xmlns="http://www.w3.org/2000/svg"
            width="24"
            height="24"
            viewBox="0 0 24 24"
            fill="none"
            stroke="currentColor"
            strokeWidth="2"
            strokeLinecap="round"
            strokeLinejoin="round"
        >
            <polyline points="22 12 16 12 14 15 10 15 8 12 2 12" />
            <path d="M5.45 5.11L2 12v6a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2v-6l-3.45-6.89A2 2 0 0 0 16.76 4H7.24a2 2 0 0 0-1.79 1.11z" />
        </svg>
    );
}
