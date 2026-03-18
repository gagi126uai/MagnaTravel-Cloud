import { useState, useEffect, useCallback } from 'react';
import { api } from "../../../api";
import { showError, showSuccess } from "../../../alerts";

/**
 * Hook to manage the Reservas list, including filtering, searching, and archiving.
 */
export function useReservas() {
    const [reservas, setReservas] = useState([]);
    const [loading, setLoading] = useState(true);
    const [searchTerm, setSearchTerm] = useState("");
    const [viewFilter, setViewFilter] = useState("all"); // all, Presupuesto, Reservado, Operativo, archived

    const loadReservas = useCallback(async () => {
        setLoading(true);
        try {
            const data = await api.get("/reservas");
            setReservas(data);
        } catch (error) {
            console.error(error);
            showError("Error cargando reservas: " + (error.response?.data?.Error || error.message));
            setReservas([]);
        } finally {
            setLoading(false);
        }
    }, []);

    useEffect(() => {
        loadReservas();
    }, [loadReservas]);

    const handleArchive = async (id) => {
        if (!confirm("¿Archivar esta reserva? Desaparecerá de la lista principal.")) return;

        try {
            await api.put(`/reservas/${id}/archive`);
            showSuccess("Reserva archivada");
            await loadReservas();
            return true;
        } catch (error) {
            showError(error.message || "Error al archivar");
            return false;
        }
    };

    const filteredReservas = reservas.filter(f => {
        // Search Filter
        const searchMatch =
            f.name?.toLowerCase().includes(searchTerm.toLowerCase()) ||
            f.numeroReserva?.toLowerCase().includes(searchTerm.toLowerCase()) ||
            f.customerName?.toLowerCase().includes(searchTerm.toLowerCase());

        // View Filter
        let viewMatch = true;
        if (viewFilter === 'all') {
            viewMatch = !['Cerrado', 'Cancelado', 'Archived'].includes(f.status);
        } else if (viewFilter === 'Cerrado') {
            viewMatch = ['Cerrado', 'Cancelado', 'Archived'].includes(f.status);
        } else {
            viewMatch = f.status === viewFilter;
        }

        return searchMatch && viewMatch;
    });

    const sortedReservas = [...filteredReservas].sort((a, b) => {
        if (!a.startDate && !b.startDate) return new Date(b.createdAt) - new Date(a.createdAt);
        if (!a.startDate) return 1;
        if (!b.startDate) return -1;
        return new Date(a.startDate) - new Date(b.startDate);
    });

    const tabCounts = {
        all: reservas.filter(f => !['Cerrado', 'Cancelado', 'Archived'].includes(f.status)).length,
        Reservado: reservas.filter(f => f.status === 'Reservado').length,
        Operativo: reservas.filter(f => f.status === 'Operativo').length,
        Cerrado: reservas.filter(f => ['Cerrado', 'Cancelado', 'Archived'].includes(f.status)).length,
    };

    // KPIs
    const activeReservas = reservas.filter(f => !['Cerrado', 'Cancelado', 'Archived'].includes(f.status));
    const stats = {
        activeCount: activeReservas.length,
        operativeCount: reservas.filter(f => f.status === 'Operativo').length,
        totalSaleActive: activeReservas.reduce((sum, f) => sum + (f.totalSale || 0), 0),
        totalCostActive: activeReservas.reduce((sum, f) => sum + (f.totalCost || 0), 0),
        totalPendingBalance: activeReservas.reduce((sum, f) => sum + (f.balance > 0 ? f.balance : 0), 0),
    };
    stats.grossProfit = stats.totalSaleActive - stats.totalCostActive;

    return {
        reservas,
        loading,
        searchTerm,
        setSearchTerm,
        viewFilter,
        setViewFilter,
        loadReservas,
        handleArchive,
        sortedReservas,
        tabCounts,
        stats
    };
}
