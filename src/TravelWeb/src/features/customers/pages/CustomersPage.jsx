import { useState } from "react";
import { useNavigate } from "react-router-dom";
import { Plus, Search } from "lucide-react";

import { useCustomers } from "../hooks/useCustomers";
import { CustomerTable } from "../components/CustomerTable";
import { CustomerMobileList } from "../components/CustomerMobileList";
import { CustomerFormModal } from "../components/CustomerFormModal";
import { getPublicId } from "../../../lib/publicIds";
import { PaginationFooter } from "../../../components/ui/PaginationFooter";
import { DatabaseUnavailableState } from "../../../components/ui/DatabaseUnavailableState";

export default function CustomersPage() {
  const navigate = useNavigate();
  const [isModalOpen, setIsModalOpen] = useState(false);
  const [currentCustomer, setCurrentCustomer] = useState(null);

  const {
    customers,
    loading,
    searchTerm,
    setSearchTerm,
    showInactive,
    setShowInactive,
    page,
    pageSize,
    totalCount,
    totalPages,
    hasPreviousPage,
    hasNextPage,
    setPage,
    setPageSize,
    handleSaveCustomer,
    handleToggleStatus,
    databaseUnavailable,
  } = useCustomers();

  const handleOpenModal = (customer = null) => {
    setCurrentCustomer(customer);
    setIsModalOpen(true);
  };

  const onSave = async (formData, customerId) => {
    const success = await handleSaveCustomer(formData, customerId);
    if (success) {
      setIsModalOpen(false);
    }
  };

  return (
    <div className="animate-in fade-in space-y-4 duration-500 md:space-y-6">
      <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
        <div>
          <h2 className="text-xl font-bold tracking-tight text-slate-900 dark:text-white md:text-2xl">Gestion de Clientes</h2>
          <p className="text-sm text-muted-foreground">Administra pasajeros y cuentas corporativas.</p>
        </div>
        <div className="flex gap-2">
          <button
            onClick={() => setShowInactive(!showInactive)}
            className={`rounded-md border px-3 py-2 text-xs transition-colors ${showInactive ? "border-slate-300 bg-slate-100 dark:border-slate-700 dark:bg-slate-800" : "border-transparent bg-transparent hover:bg-slate-50 dark:hover:bg-slate-900"}`}
          >
            {showInactive ? "Ocultar inactivos" : "Mostrar inactivos"}
          </button>
          <button
            onClick={() => handleOpenModal()}
            className="flex w-full items-center justify-center gap-2 rounded-lg bg-indigo-600 px-4 py-2.5 text-sm font-medium text-white shadow-md transition-all hover:bg-indigo-700 hover:shadow-lg sm:w-auto"
          >
            <Plus className="h-4 w-4" />
            Nuevo Cliente
          </button>
        </div>
      </div>

      <div className="flex items-center gap-2 rounded-lg border bg-card/50 px-3 py-2 shadow-sm transition-all focus-within:ring-2 focus-within:ring-indigo-500/20 backdrop-blur-sm dark:border-slate-800 dark:bg-slate-900/50">
        <Search className="h-4 w-4 text-muted-foreground" />
        <input
          type="text"
          placeholder="Buscar por nombre, documento o CUIT..."
          value={searchTerm}
          onChange={(event) => setSearchTerm(event.target.value)}
          className="flex-1 bg-transparent text-sm text-slate-900 outline-none placeholder:text-muted-foreground dark:text-white"
        />
      </div>

      {loading && customers.length === 0 ? (
        <div className="p-12 text-center text-slate-500">Cargando clientes...</div>
      ) : databaseUnavailable ? (
        <DatabaseUnavailableState />
      ) : (
        <>
          <CustomerTable
            customers={customers}
            onEdit={handleOpenModal}
            onToggleStatus={handleToggleStatus}
            onAccountClick={(customer) => navigate(`/customers/${getPublicId(customer)}/account`)}
          />
          <CustomerMobileList
            customers={customers}
            onEdit={handleOpenModal}
            onAccountClick={(customer) => navigate(`/customers/${getPublicId(customer)}/account`)}
          />
          <PaginationFooter
            page={page}
            pageSize={pageSize}
            totalCount={totalCount}
            totalPages={totalPages}
            hasPreviousPage={hasPreviousPage}
            hasNextPage={hasNextPage}
            onPageChange={setPage}
            onPageSizeChange={setPageSize}
          />
        </>
      )}

      {isModalOpen && (
        <CustomerFormModal
          isOpen={isModalOpen}
          onClose={() => setIsModalOpen(false)}
          customer={currentCustomer}
          onSave={onSave}
        />
      )}
    </div>
  );
}
