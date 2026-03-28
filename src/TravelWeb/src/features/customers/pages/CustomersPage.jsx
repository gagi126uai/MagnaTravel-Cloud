import { useState } from "react";
import { useNavigate } from "react-router-dom";
import { Plus, Search } from "lucide-react";
import { CustomerFormModal } from "../components/CustomerFormModal";
import { CustomerMobileList } from "../components/CustomerMobileList";
import { CustomerTable } from "../components/CustomerTable";
import { useCustomers } from "../hooks/useCustomers";
import { DatabaseUnavailableState } from "../../../components/ui/DatabaseUnavailableState";
import { Button } from "../../../components/ui/button";
import { ListPageHeader } from "../../../components/ui/ListPageHeader";
import { ListToolbar } from "../../../components/ui/ListToolbar";
import { PaginationFooter } from "../../../components/ui/PaginationFooter";
import { getPublicId } from "../../../lib/publicIds";

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
      <ListPageHeader
        title="Gestion de Clientes"
        subtitle="Administra pasajeros y cuentas corporativas."
        actions={
          <>
            <Button
              type="button"
              variant="outline"
              onClick={() => navigate("/quotes?create=1")}
              className="gap-2 border-indigo-200 text-indigo-700 hover:bg-indigo-50 hover:text-indigo-800 dark:border-indigo-900/60 dark:bg-slate-900 dark:text-indigo-300 dark:hover:bg-indigo-900/20"
            >
              <Plus className="h-4 w-4" />
              Nueva cotizacion
            </Button>
            <Button
              onClick={() => handleOpenModal()}
              className="gap-2 bg-indigo-600 text-white shadow-md hover:bg-indigo-700 hover:shadow-lg"
            >
              <Plus className="h-4 w-4" />
              Nuevo Cliente
            </Button>
          </>
        }
      />

      <ListToolbar
        searchSlot={
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
        }
        filterSlot={
          <Button
            type="button"
            variant={showInactive ? "secondary" : "ghost"}
            onClick={() => setShowInactive(!showInactive)}
            className={`text-xs ${showInactive ? "border border-slate-300 bg-slate-100 dark:border-slate-700 dark:bg-slate-800" : ""}`}
          >
            {showInactive ? "Ocultar inactivos" : "Mostrar inactivos"}
          </Button>
        }
      />

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
