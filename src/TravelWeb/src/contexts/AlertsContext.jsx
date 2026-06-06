import { createContext, useContext, useState, useEffect } from "react";
import { api } from "../api";

const AlertsContext = createContext();

export function AlertsProvider({ children }) {
    // La API /alerts responde camelCase (AlertsResponse.cs serializa por defecto así).
    // F3: quitamos el gate isAdmin() — ahora los vendedores también consultan /alerts.
    // El servidor filtra por vendedor/permiso y con flags OFF devuelve vacío.
    // UrgentTrips/SupplierDebts siguen siendo solo-admin en el backend (sin cambio).
    const [alerts, setAlerts] = useState({
        urgentTrips: [],
        supplierDebts: [],
        serviceDeadlines: [],
        costsToConfirm: [],
        totalCount: 0,
    });
    const [notifications, setNotifications] = useState([]);
    const [loading, setLoading] = useState(true);

    const fetchAlerts = async () => {
        try {
            // Todos los usuarios autenticados pueden consultar /alerts (controller: [Authorize]).
            // El servidor decide qué buckets incluir según rol y permisos del caller.
            const res = await api.get("/alerts");
            setAlerts(res || {
                urgentTrips: [],
                supplierDebts: [],
                serviceDeadlines: [],
                costsToConfirm: [],
                totalCount: 0,
            });
        } catch (error) {
            console.error("Error al cargar alertas:", error);
        }
    };

    const fetchNotifications = async () => {
        try {
            const res = await api.get("/notifications");
            setNotifications(res || []);
        } catch (error) {
            console.error("Error al cargar notificaciones:", error);
        }
    };

    const markAsRead = async (id) => {
        try {
            await api.post(`/notifications/${id}/read`);
            setNotifications(prev => prev.filter(n => n.id !== id));
        } catch (error) {
            console.error("Error al marcar como leída:", error);
        }
    };

    const refreshAll = () => {
        setLoading(true);
        Promise.all([fetchAlerts(), fetchNotifications()]).finally(() => setLoading(false));
    };

    // useEffect con deps vacías: carga inicial + polling cada 30 seg.
    // El poll también llama a fetchAlerts sin gate de rol (mismo criterio que el mount).
    useEffect(() => {
        refreshAll();
        const interval = setInterval(() => {
            fetchNotifications();
            fetchAlerts();
        }, 30 * 1000);
        return () => clearInterval(interval);
    // eslint-disable-next-line react-hooks/exhaustive-deps
    }, []);

    return (
        <AlertsContext.Provider value={{ alerts, notifications, loading, refreshAlerts: refreshAll, markAsRead }}>
            {children}
        </AlertsContext.Provider>
    );
}

export function useAlerts() {
    return useContext(AlertsContext);
}
