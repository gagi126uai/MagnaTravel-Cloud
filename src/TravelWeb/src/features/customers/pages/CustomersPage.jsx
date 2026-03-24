import { useState } from "react";
import { useNavigate } from "react-router-dom";
import { Plus, Search } from "lucide-react";

import { useCustomers } from "../hooks/useCustomers";
import { CustomerTable } from "../components/CustomerTable";
import { CustomerMobileList } from "../components/CustomerMobileList";
import { CustomerFormModal } from "../components/CustomerFormModal";
import { getPublicId } from "../../../lib/publicIds";

export default function CustomersPage() {
  const navigate = useNavigate();
  const [isModalOpen, setIsModalOpen] = useState(false);
  const [currentCustomer, setCurrentCustomer] = useState(null);

  const {
    loading,
    searchTerm,
    setSearchTerm,
    showInactive,
    setShowInactive,
    handleSaveCustomer,
    handleToggleStatus,
    filteredCustomers
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
    <div className="space-y-4 md:space-y-6 animate-in fade-in duration-500">
      <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
        <div>
          <h2 className="text-xl md:text-2xl font-bold tracking-tight text-slate-900 dark:text-white">Gestión de Clientes</h2>
          <p className="text-sm text-muted-foreground">Administra pasajeros y cuentas corporativas.</p>
        </div>
        <div className="flex gap-2">
          <button
            onClick={() => setShowInactive(!showInactive)}
            className={`text-xs px-3 py-2 rounded-md transition-colors border ${showInactive ? 'bg-slate-100 dark:bg-slate-800 border-slate-300 dark:border-slate-700' : 'bg-transparent border-transparent hover:bg-slate-50 dark:hover:bg-slate-900'}`}
          >
            {showInactive ? "Ocultar Inactivos" : "Mostrar Inactivos"}
          </button>
          <button
            onClick={() => handleOpenModal()}
            className="flex items-center justify-center gap-2 rounded-lg bg-indigo-600 px-4 py-2.5 text-sm font-medium text-white hover:bg-indigo-700 shadow-md transition-all hover:shadow-lg w-full sm:w-auto"
          >
            <Plus className="h-4 w-4" />
            Nuevo Cliente
          </button>
        </div>
      </div>

      <div className="flex items-center gap-2 rounded-lg border bg-card/50 px-3 py-2 backdrop-blur-sm shadow-sm focus-within:ring-2 focus-within:ring-indigo-500/20 transition-all dark:bg-slate-900/50 dark:border-slate-800">
        <Search className="h-4 w-4 text-muted-foreground" />
        <input
          type="text"
          placeholder="Buscar por nombre, documento o CUIT..."
          value={searchTerm}
          onChange={(e) => setSearchTerm(e.target.value)}
          className="flex-1 bg-transparent text-sm outline-none placeholder:text-muted-foreground text-slate-900 dark:text-white"
        />
      </div>

      {loading && filteredCustomers.length === 0 ? (
        <div className="p-12 text-center text-slate-500">Cargando clientes...</div>
      ) : (
        <>
          <CustomerTable
            customers={filteredCustomers}
            onEdit={handleOpenModal}
            onToggleStatus={handleToggleStatus}
            onAccountClick={(customer) => navigate(`/customers/${getPublicId(customer)}/account`)}
          />
          <CustomerMobileList
            customers={filteredCustomers}
            onEdit={handleOpenModal}
            onAccountClick={(customer) => navigate(`/customers/${getPublicId(customer)}/account`)}
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
