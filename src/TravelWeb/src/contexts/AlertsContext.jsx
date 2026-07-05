import { createContext, useContext, useState, useEffect, useRef, useCallback } from "react";
import * as signalR from "@microsoft/signalr";
import { api, buildAppUrl } from "../api";

const AlertsContext = createContext();

/**
 * Fuente UNICA de alertas y notificaciones para toda la app (Tanda 5, 2026-07-05).
 *
 * Antes habia dos lectores independientes de /notifications: la campanita (fetch propio + su conexion SignalR) y
 * esta pagina (poll cada 30s SIN SignalR). Resultado: el badge de la campanita y la pagina de notificaciones se
 * desincronizaban. Ahora TODO vive aca:
 *   - `alerts`        — buckets calculados por el backend (/alerts): proximos inicios, costos a confirmar, etc.
 *   - `notifications` — avisos VIVOS del usuario (/notifications ya devuelve solo los vivos: sin resolver/leer).
 *   - conexion SignalR unica — un aviso nuevo entra en tiempo real a la MISMA lista que ven campanita y pagina.
 *   - `markAsRead` / `markAllAsRead` — marcan visto desde cualquier lado y actualizan el estado compartido.
 *
 * El servidor filtra por vendedor/permiso; el front no decide visibilidad.
 */
export function AlertsProvider({ children }) {
    const [alerts, setAlerts] = useState({
        urgentTrips: [],
        supplierDebts: [],
        upcomingStarts: [],
        upcomingStartsWindowDays: null,
        costsToConfirm: [],
        totalCount: 0,
        // Q9: lista de pre-ventas (presupuestos/cotizaciones) por caducar. Null = bucket no activo.
        expiringPreSales: null,
    });
    const [notifications, setNotifications] = useState([]);
    const [loading, setLoading] = useState(true);
    const connectionRef = useRef(null);

    const fetchAlerts = useCallback(async () => {
        try {
            // Todos los usuarios autenticados pueden consultar /alerts (controller: [Authorize]).
            // El servidor decide qué buckets incluir según rol y permisos del caller.
            const res = await api.get("/alerts");
            setAlerts(res || {
                urgentTrips: [],
                supplierDebts: [],
                upcomingStarts: [],
                upcomingStartsWindowDays: null,
                costsToConfirm: [],
                totalCount: 0,
                expiringPreSales: null,
            });
        } catch (error) {
            console.error("Error al cargar alertas:", error);
        }
    }, []);

    const fetchNotifications = useCallback(async () => {
        try {
            // /notifications devuelve solo los avisos VIVOS del usuario (sin resolver, sin leer, sin descartar).
            const res = await api.get("/notifications");
            setNotifications(res || []);
        } catch (error) {
            console.error("Error al cargar notificaciones:", error);
        }
    }, []);

    const markAsRead = useCallback(async (id) => {
        // Optimista: sacamos el aviso de la lista al instante. Si el POST falla, re-sincronizamos con el server.
        setNotifications((prev) => prev.filter((n) => n.id !== id));
        try {
            await api.post(`/notifications/${id}/read`);
        } catch (error) {
            console.error("Error al marcar como leída:", error);
            fetchNotifications();
        }
    }, [fetchNotifications]);

    const markAllAsRead = useCallback(async () => {
        // Snapshot de los ids antes de vaciar la lista (optimista).
        const ids = notifications.map((n) => n.id);
        setNotifications([]);
        try {
            await Promise.all(ids.map((id) => api.post(`/notifications/${id}/read`)));
        } catch (error) {
            console.error("Error al marcar todas como leídas:", error);
            fetchNotifications();
        }
    }, [notifications, fetchNotifications]);

    const refreshAll = useCallback(() => {
        setLoading(true);
        Promise.all([fetchAlerts(), fetchNotifications()]).finally(() => setLoading(false));
    }, [fetchAlerts, fetchNotifications]);

    // Carga inicial + polling cada 30 seg (mismo criterio de siempre: sin gate de rol, el server filtra).
    useEffect(() => {
        refreshAll();
        const interval = setInterval(() => {
            fetchNotifications();
            fetchAlerts();
        }, 30 * 1000);
        return () => clearInterval(interval);
    }, [refreshAll, fetchAlerts, fetchNotifications]);

    // Conexión SignalR única (movida desde la campanita): un aviso nuevo entra en tiempo real a la lista compartida.
    useEffect(() => {
        const url = buildAppUrl("/hubs/notifications");

        const connection = new signalR.HubConnectionBuilder()
            .withUrl(url, { withCredentials: true })
            .withAutomaticReconnect()
            .build();

        connection.on("ReceiveNotification", (notification) => {
            setNotifications((prev) => {
                // Evitamos duplicar si el mismo aviso ya está en la lista (p. ej. llegó por poll y por SignalR).
                if (notification?.id && prev.some((n) => n.id === notification.id)) {
                    return prev;
                }
                return [notification, ...prev];
            });
        });

        connection.start().catch((err) => console.error("SignalR Connection Error: ", err));
        connectionRef.current = connection;

        return () => {
            if (connectionRef.current) {
                connectionRef.current.stop();
            }
        };
    }, []);

    // La lista ya son solo los vivos, así que el contador de "sin leer" es su largo.
    const unreadCount = notifications.length;

    return (
        <AlertsContext.Provider
            value={{
                alerts,
                notifications,
                unreadCount,
                loading,
                refreshAlerts: refreshAll,
                markAsRead,
                markAllAsRead,
            }}
        >
            {children}
        </AlertsContext.Provider>
    );
}

export function useAlerts() {
    return useContext(AlertsContext);
}
