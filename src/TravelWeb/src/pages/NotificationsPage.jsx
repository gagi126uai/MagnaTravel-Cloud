import { useEffect } from "react";
import { Link } from "react-router-dom";
import { useAlerts } from "../contexts/AlertsContext";
import { format } from "date-fns";
import { es } from "date-fns/locale";
import { CheckCircle, AlertCircle, Info, Bell, Check } from "lucide-react";
import { aHoraArgentina } from "../lib/utils";
import { StatusChip } from "../components/ui/badge";
import { Button } from "../components/ui/button";

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

    // Cierre de hueco (PR-7, 2026-08-19): mismo tratamiento que NotificationBell.jsx — el
    // botón de marcar leída puede vivir DENTRO de la fila clickeable (Link), así que su click
    // tiene que frenar acá antes de burbujear al Link y disparar una navegación no deseada.
    const handleMarkAsRead = (id, e) => {
        if (e) {
            e.preventDefault();
            e.stopPropagation();
        }
        markAsRead(id);
    };

    return (
        <div className="p-6 max-w-4xl mx-auto">
            <div className="flex items-center justify-between mb-6">
                <h1 className="text-2xl font-bold flex items-center gap-2">
                    <Bell className="h-6 w-6" />
                    Notificaciones
                </h1>
                <StatusChip tone="azul">{notifications.length} sin leer</StatusChip>
            </div>

            {notifications.length === 0 ? (
                <div className="text-center py-12 bg-white rounded-[14px] shadow-sm border border-slate-200">
                    <Inbox className="h-12 w-12 text-slate-300 mx-auto mb-3" />
                    <p className="text-slate-500">No tienes notificaciones nuevas</p>
                </div>
            ) : (
                <div className="space-y-4">
                    {notifications.map((notif) => {
                        // Cierre de hueco (PR-7): la mayoría de los avisos NO traen targetUrl
                        // (avisos viejos, otros tipos de notificación) — esos se comportan EXACTO
                        // igual que antes, sin volverse clickeables.
                        const puedeNavegar = Boolean(notif.targetUrl);

                        // Icono + texto es el mismo contenido navegue o no — se arma una vez y se
                        // reusa en las dos ramas de abajo (mismo patrón que NotificationBell.jsx).
                        const contenido = (
                            <>
                                <div className="mt-1 flex-shrink-0">
                                    {getIcon(notif.type)}
                                </div>
                                <div className="flex-1 min-w-0">
                                    <p className="text-slate-800 font-medium">{notif.message}</p>
                                    <p className="text-xs text-slate-400 mt-1">
                                        {format(aHoraArgentina(notif.createdAt), "dd 'de' MMMM, HH:mm", { locale: es })}
                                    </p>
                                </div>
                            </>
                        );

                        return (
                            <div
                                key={notif.id}
                                className="bg-white p-4 rounded-[14px] shadow-sm border border-slate-200 flex gap-4 transition-all hover:shadow-md"
                            >
                                {/* Zona clickeable (Link) + botón de marcar leída SEPARADOS: un
                                    <button> dentro de un <a> es HTML inválido y rompe el click en
                                    algunos navegadores (mismo patrón que NotificationBell.jsx). */}
                                {puedeNavegar ? (
                                    <Link to={notif.targetUrl} className="flex flex-1 gap-4 min-w-0">
                                        {contenido}
                                    </Link>
                                ) : (
                                    <div className="flex flex-1 gap-4 min-w-0">
                                        {contenido}
                                    </div>
                                )}
                                <Button
                                    type="button"
                                    variant="ghost"
                                    size="icon"
                                    onClick={(e) => handleMarkAsRead(notif.id, e)}
                                    className="self-start"
                                    title="Marcar como leída"
                                    aria-label="Marcar como leída"
                                >
                                    <Check className="h-5 w-5" aria-hidden="true" />
                                </Button>
                            </div>
                        );
                    })}
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
