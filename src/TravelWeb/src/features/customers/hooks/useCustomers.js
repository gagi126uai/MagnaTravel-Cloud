import { useState, useEffect, useCallback, useMemo } from 'react';
import { api } from "../../../api";
import { showError, showSuccess } from "../../../alerts";
import Swal from "sweetalert2";
import { getPublicId } from "../../../lib/publicIds";

export function useCustomers() {
    const [customers, setCustomers] = useState([]);
    const [loading, setLoading] = useState(true);
    const [searchTerm, setSearchTerm] = useState("");
    const [showInactive, setShowInactive] = useState(false);

    const fetchCustomers = useCallback(async () => {
        try {
            setLoading(true);
            const data = await api.get(`/customers?includeInactive=${showInactive}`);
            setCustomers(data);
        } catch (error) {
            console.error("Error fetching customers:", error);
            showError("No se pudieron cargar los clientes");
        } finally {
            setLoading(false);
        }
    }, [showInactive]);

    useEffect(() => {
        fetchCustomers();
    }, [fetchCustomers]);

    const handleSaveCustomer = async (formData, customerId = null) => {
        try {
            if (customerId) {
                await api.put(`/customers/${customerId}`, formData);
                showSuccess("Cliente actualizado correctamente");
            } else {
                await api.post("/customers", formData);
                showSuccess("Cliente creado exitosamente");
            }
            await fetchCustomers();
            return true;
        } catch (error) {
            console.error("Error saving customer:", error);
            showError("No se pudo guardar el cliente");
            return false;
        }
    };

    const handleToggleStatus = async (customer) => {
        const action = customer.isActive ? "desactivar" : "activar";
        const result = await Swal.fire({
            title: `¿${action.charAt(0).toUpperCase() + action.slice(1)} cliente?`,
            text: customer.isActive
                ? "El cliente no aparecerá en las búsquedas de nuevos expedientes."
                : "El cliente volverá a estar disponible.",
            icon: "warning",
            showCancelButton: true,
            confirmButtonColor: customer.isActive ? "#ef4444" : "#10b981",
            cancelButtonColor: "#3085d6",
            confirmButtonText: `Sí, ${action}`,
            cancelButtonText: "Cancelar"
        });

        if (result.isConfirmed) {
            try {
                const customerPublicId = getPublicId(customer);
                await api.put(`/customers/${customerPublicId}`, {
                    ...customer,
                    isActive: !customer.isActive
                });
                await fetchCustomers();
                showSuccess(`Cliente ${action === "activar" ? "activado" : "desactivado"}.`);
                return true;
            } catch (error) {
                showError("No se pudo cambiar el estado");
                return false;
            }
        }
        return false;
    };

    const filteredCustomers = useMemo(() => {
        return customers.filter(c =>
            c.fullName.toLowerCase().includes(searchTerm.toLowerCase()) ||
            c.documentNumber?.includes(searchTerm) ||
            c.taxId?.includes(searchTerm)
        );
    }, [customers, searchTerm]);

    return {
        customers,
        loading,
        searchTerm,
        setSearchTerm,
        showInactive,
        setShowInactive,
        fetchCustomers,
        handleSaveCustomer,
        handleToggleStatus,
        filteredCustomers
    };
}
