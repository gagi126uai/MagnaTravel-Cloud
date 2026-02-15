import { createContext, useContext, useState, useEffect } from "react";
import { api } from "../api";
import { isAdmin } from "../auth";

const AlertsContext = createContext();

export function AlertsProvider({ children }) {
    const [alerts, setAlerts] = useState({ UrgentTrips: [], SupplierDebts: [], TotalCount: 0 });
    const [loading, setLoading] = useState(true);

    const fetchAlerts = async () => {
        if (!isAdmin()) return; // Only admins see alerts
        try {
            const res = await api.get("/alerts");
            setAlerts(res || { UrgentTrips: [], SupplierDebts: [], TotalCount: 0 });
        } catch (error) {
            console.error("Error al cargar alertas:", error);
        } finally {
            setLoading(false);
        }
    };

    useEffect(() => {
        fetchAlerts();
        // Optional: Poll every 5 minutes
        const interval = setInterval(fetchAlerts, 5 * 60 * 1000);
        return () => clearInterval(interval);
    }, []);

    return (
        <AlertsContext.Provider value={{ alerts, loading, refreshAlerts: fetchAlerts }}>
            {children}
        </AlertsContext.Provider>
    );
}

export function useAlerts() {
    return useContext(AlertsContext);
}
