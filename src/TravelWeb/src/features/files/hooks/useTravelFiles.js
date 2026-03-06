import { useState, useEffect, useCallback } from 'react';
import { api } from "../../../api";
import { showError, showSuccess } from "../../../alerts";

/**
 * Hook to manage the Travel Files list, including filtering, searching, and archiving.
 */
export function useTravelFiles() {
    const [files, setFiles] = useState([]);
    const [loading, setLoading] = useState(true);
    const [searchTerm, setSearchTerm] = useState("");
    const [viewFilter, setViewFilter] = useState("all"); // all, Presupuesto, Reservado, Operativo, archived

    const loadFiles = useCallback(async () => {
        setLoading(true);
        try {
            const data = await api.get("/travelfiles");
            setFiles(data);
        } catch (error) {
            console.error(error);
            showError("Error cargando files: " + (error.response?.data?.Error || error.message));
            setFiles([]);
        } finally {
            setLoading(false);
        }
    }, []);

    useEffect(() => {
        loadFiles();
    }, [loadFiles]);

    const handleArchive = async (id) => {
        if (!confirm("¿Archivar este expediente? Desaparecerá de la lista principal.")) return;

        try {
            await api.put(`/travelfiles/${id}/archive`);
            showSuccess("Expediente archivado");
            await loadFiles();
            return true;
        } catch (error) {
            showError(error.message || "Error al archivar");
            return false;
        }
    };

    const filteredFiles = files.filter(f => {
        // Search Filter
        const searchMatch =
            f.name?.toLowerCase().includes(searchTerm.toLowerCase()) ||
            f.fileNumber?.toLowerCase().includes(searchTerm.toLowerCase()) ||
            f.customerName?.toLowerCase().includes(searchTerm.toLowerCase());

        // View Filter
        let viewMatch = true;
        if (viewFilter === 'all') {
            viewMatch = !['Cerrado', 'Cancelado', 'Archived'].includes(f.status);
        } else if (viewFilter === 'archived') {
            viewMatch = ['Cerrado', 'Cancelado', 'Archived'].includes(f.status);
        } else {
            viewMatch = f.status === viewFilter;
        }

        return searchMatch && viewMatch;
    });

    const sortedFiles = [...filteredFiles].sort((a, b) => {
        if (!a.startDate && !b.startDate) return new Date(b.createdAt) - new Date(a.createdAt);
        if (!a.startDate) return 1;
        if (!b.startDate) return -1;
        return new Date(a.startDate) - new Date(b.startDate);
    });

    const tabCounts = {
        all: files.filter(f => !['Cerrado', 'Cancelado', 'Archived'].includes(f.status)).length,
        Presupuesto: files.filter(f => f.status === 'Presupuesto').length,
        Reservado: files.filter(f => f.status === 'Reservado').length,
        Operativo: files.filter(f => f.status === 'Operativo').length,
        archived: files.filter(f => ['Cerrado', 'Cancelado', 'Archived'].includes(f.status)).length,
    };

    // KPIs
    const activeFiles = files.filter(f => !['Cerrado', 'Cancelado', 'Archived'].includes(f.status));
    const stats = {
        activeCount: activeFiles.length,
        operativeCount: files.filter(f => f.status === 'Operativo').length,
        totalSaleActive: activeFiles.reduce((sum, f) => sum + (f.totalSale || 0), 0),
        totalCostActive: activeFiles.reduce((sum, f) => sum + (f.totalCost || 0), 0),
        totalPendingBalance: activeFiles.reduce((sum, f) => sum + (f.balance > 0 ? f.balance : 0), 0),
    };
    stats.grossProfit = stats.totalSaleActive - stats.totalCostActive;

    return {
        files,
        loading,
        searchTerm,
        setSearchTerm,
        viewFilter,
        setViewFilter,
        loadFiles,
        handleArchive,
        sortedFiles,
        tabCounts,
        stats
    };
}
