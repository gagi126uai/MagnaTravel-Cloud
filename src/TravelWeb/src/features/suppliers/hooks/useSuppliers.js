import { useState, useEffect, useCallback } from 'react';
import { api } from "../../../api";
import { showError, showSuccess } from "../../../alerts";
import { getPublicId } from "../../../lib/publicIds";
import { useDebounce } from "../../../hooks/useDebounce";
import { isDatabaseUnavailableError } from "../../../lib/errors";

const emptyPage = {
    items: [],
    page: 1,
    pageSize: 25,
    totalCount: 0,
    totalPages: 0,
    hasPreviousPage: false,
    hasNextPage: false,
};

export function useSuppliers() {
    const [suppliersPage, setSuppliersPage] = useState(emptyPage);
    const [loading, setLoading] = useState(true);
    const [searchTerm, setSearchTerm] = useState("");
    const [showInactive, setShowInactive] = useState(false);
    const [page, setPage] = useState(1);
    const [pageSize, setPageSize] = useState(25);
    const [databaseUnavailable, setDatabaseUnavailable] = useState(false);
    const debouncedSearch = useDebounce(searchTerm, 300);

    const fetchSuppliers = useCallback(async () => {
        try {
            setLoading(true);
            const params = new URLSearchParams({
                page: String(page),
                pageSize: String(pageSize),
                includeInactive: String(showInactive),
            });

            if (debouncedSearch.trim()) {
                params.set("search", debouncedSearch.trim());
            }

            const data = await api.get(`/suppliers?${params.toString()}`);
            setSuppliersPage({ ...emptyPage, ...(data || {}) });
            setDatabaseUnavailable(false);
        } catch (error) {
            console.error("Error fetching suppliers:", error);
            setSuppliersPage(emptyPage);
            setDatabaseUnavailable(isDatabaseUnavailableError(error));
            showError("No se pudieron cargar los proveedores");
        } finally {
            setLoading(false);
        }
    }, [debouncedSearch, page, pageSize, showInactive]);

    useEffect(() => {
        fetchSuppliers();
    }, [fetchSuppliers]);

    useEffect(() => {
        setPage(1);
    }, [debouncedSearch, showInactive, pageSize]);

    const handleSaveSupplier = async (formData, supplierId = null) => {
        try {
            if (supplierId) {
                await api.put(`/suppliers/${supplierId}`, formData);
                showSuccess("Proveedor actualizado");
            } else {
                await api.post("/suppliers", formData);
                showSuccess("Proveedor creado");
            }
            fetchSuppliers();
            return true;
        } catch (error) {
            console.error("Error saving supplier:", error);
            showError("No se pudo guardar el proveedor");
            return false;
        }
    };

    const handleToggleStatus = async (supplier) => {
        try {
            const newStatus = !supplier.isActive;
            await api.put(`/suppliers/${getPublicId(supplier)}`, {
                ...supplier,
                isActive: newStatus
            });
            showSuccess(`Proveedor ${newStatus ? 'activado' : 'desactivado'}`);
            fetchSuppliers();
        } catch (error) {
            const message = error?.response?.data?.message || error?.message || "No se pudo cambiar el estado.";
            showError(message, "No se pudo cambiar el estado");
        }
    };

    return {
        suppliers: suppliersPage.items || [],
        loading,
        searchTerm,
        setSearchTerm,
        showInactive,
        setShowInactive,
        page: suppliersPage.page || page,
        pageSize: suppliersPage.pageSize || pageSize,
        totalCount: suppliersPage.totalCount || 0,
        totalPages: suppliersPage.totalPages || 0,
        hasPreviousPage: Boolean(suppliersPage.hasPreviousPage),
        hasNextPage: Boolean(suppliersPage.hasNextPage),
        setPage,
        setPageSize,
        handleSaveSupplier,
        handleToggleStatus,
        refresh: fetchSuppliers,
        databaseUnavailable,
    };
}
