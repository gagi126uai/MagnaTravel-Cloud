import { useState, useEffect, useCallback } from "react";
import { api } from "../../../api";
import { showError, showSuccess } from "../../../alerts";
import Swal from "sweetalert2";
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

export function useCustomers() {
  const [customersPage, setCustomersPage] = useState(emptyPage);
  const [loading, setLoading] = useState(true);
  const [searchTerm, setSearchTerm] = useState("");
  const [showInactive, setShowInactive] = useState(false);
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(25);
  const [databaseUnavailable, setDatabaseUnavailable] = useState(false);
  const debouncedSearch = useDebounce(searchTerm, 300);

  const fetchCustomers = useCallback(async () => {
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

      const data = await api.get(`/customers?${params.toString()}`);
      setCustomersPage({ ...emptyPage, ...(data || {}) });
      setDatabaseUnavailable(false);
    } catch (error) {
      console.error("Error fetching customers:", error);
      setCustomersPage(emptyPage);
      setDatabaseUnavailable(isDatabaseUnavailableError(error));
      showError("No se pudieron cargar los clientes");
    } finally {
      setLoading(false);
    }
  }, [debouncedSearch, page, pageSize, showInactive]);

  useEffect(() => {
    fetchCustomers();
  }, [fetchCustomers]);

  useEffect(() => {
    setPage(1);
  }, [debouncedSearch, showInactive, pageSize]);

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
      title: `${action.charAt(0).toUpperCase() + action.slice(1)} cliente?`,
      text: customer.isActive
        ? "El cliente no aparecera en las busquedas de nuevos expedientes."
        : "El cliente volvera a estar disponible.",
      icon: "warning",
      showCancelButton: true,
      confirmButtonColor: customer.isActive ? "#ef4444" : "#10b981",
      cancelButtonColor: "#3085d6",
      confirmButtonText: `Si, ${action}`,
      cancelButtonText: "Cancelar",
    });

    if (result.isConfirmed) {
      try {
        const customerPublicId = getPublicId(customer);
        await api.put(`/customers/${customerPublicId}`, {
          ...customer,
          isActive: !customer.isActive,
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

  return {
    customers: customersPage.items || [],
    loading,
    searchTerm,
    setSearchTerm,
    showInactive,
    setShowInactive,
    page: customersPage.page || page,
    pageSize: customersPage.pageSize || pageSize,
    totalCount: customersPage.totalCount || 0,
    totalPages: customersPage.totalPages || 0,
    hasPreviousPage: Boolean(customersPage.hasPreviousPage),
    hasNextPage: Boolean(customersPage.hasNextPage),
    setPage,
    setPageSize,
    fetchCustomers,
    handleSaveCustomer,
    handleToggleStatus,
    databaseUnavailable,
  };
}
