import { useState, useEffect, useCallback, useMemo } from 'react';
import { api } from "../../../api";
import { showError, showSuccess } from "../../../alerts";

export function useSuppliers() {
    const [suppliers, setSuppliers] = useState([]);
    const [loading, setLoading] = useState(true);
    const [searchTerm, setSearchTerm] = useState("");
    const [showInactive, setShowInactive] = useState(false);

    const fetchSuppliers = useCallback(async () => {
        try {
            setLoading(true);
            const data = await api.get("/suppliers");
            setSuppliers(data);
        } catch (error) {
            console.error("Error fetching suppliers:", error);
            showError("No se pudieron cargar los proveedores");
        } finally {
            setLoading(false);
        }
    }, []);

    useEffect(() => {
        fetchSuppliers();
    }, [fetchSuppliers]);

    const handleSaveSupplier = async (formData, supplierId = null) => {
        try {
            if (supplierId) {
                await api.put(`/suppliers/${supplierId}`, { ...formData, id: supplierId });
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
            await api.put(`/suppliers/${supplier.id}`, {
                ...supplier,
                isActive: newStatus
            });
            showSuccess(`Proveedor ${newStatus ? 'activado' : 'desactivado'}`);
            fetchSuppliers();
        } catch (error) {
            showError("No se pudo cambiar el estado");
        }
    };

    const filteredSuppliers = useMemo(() => {
        return suppliers.filter(supplier => {
            const matchesSearch = supplier.name.toLowerCase().includes(searchTerm.toLowerCase()) ||
                supplier.taxId?.toLowerCase().includes(searchTerm.toLowerCase());
            const matchesStatus = showInactive ? true : supplier.isActive;
            return matchesSearch && matchesStatus;
        });
    }, [suppliers, searchTerm, showInactive]);

    return {
        loading,
        searchTerm,
        setSearchTerm,
        showInactive,
        setShowInactive,
        handleSaveSupplier,
        handleToggleStatus,
        filteredSuppliers,
        refresh: fetchSuppliers
    };
}
