import { createContext, useContext, useState, useEffect } from "react";
import { api } from "../api";
import { isAdmin } from "../auth";

const AlertsContext = createContext();

export function AlertsProvider({ children }) {
    const [alerts, setAlerts] = useState({ UrgentTrips: [], SupplierDebts: [], TotalCount: 0 });
    const [notifications, setNotifications] = useState([]);
    const [loading, setLoading] = useState(true);

    const fetchAlerts = async () => {
        if (isAdmin()) {
            try {
                const res = await api.get("/alerts");
                setAlerts(res || { UrgentTrips: [], SupplierDebts: [], TotalCount: 0 });
            } catch (error) {
                console.error("Error al cargar alertas:", error);
            }
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

    useEffect(() => {
        refreshAll();
        // Poll every 30 seconds for notifications (faster than alerts)
        const interval = setInterval(() => {
            fetchNotifications();
            if (isAdmin()) fetchAlerts();
        }, 30 * 1000);
        return () => clearInterval(interval);
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
