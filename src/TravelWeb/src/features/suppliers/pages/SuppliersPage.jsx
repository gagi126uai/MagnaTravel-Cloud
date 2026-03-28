import { useState } from "react";
import { useNavigate } from "react-router-dom";
import { Plus, Search } from "lucide-react";
import { SupplierFormModal } from "../components/SupplierFormModal";
import { SupplierMobileList } from "../components/SupplierMobileList";
import { SupplierTable } from "../components/SupplierTable";
import { useSuppliers } from "../hooks/useSuppliers";
import { DatabaseUnavailableState } from "../../../components/ui/DatabaseUnavailableState";
import { Button } from "../../../components/ui/button";
import { ListPageHeader } from "../../../components/ui/ListPageHeader";
import { ListToolbar } from "../../../components/ui/ListToolbar";
import { PaginationFooter } from "../../../components/ui/PaginationFooter";
import { getPublicId } from "../../../lib/publicIds";

export default function SuppliersPage() {
  const navigate = useNavigate();
  const [isModalOpen, setIsModalOpen] = useState(false);
  const [currentSupplier, setCurrentSupplier] = useState(null);

  const {
    loading,
    suppliers,
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
    handleSaveSupplier,
    handleToggleStatus,
    databaseUnavailable,
  } = useSuppliers();

  const handleOpenModal = (supplier = null) => {
    setCurrentSupplier(supplier);
    setIsModalOpen(true);
  };

  const onSave = async (formData, supplierId) => {
    const success = await handleSaveSupplier(formData, supplierId);
    if (success) {
      setIsModalOpen(false);
    }
  };

  return (
    <div className="animate-in fade-in space-y-6 duration-500">
      <ListPageHeader
        title="Proveedores"
        subtitle="Gestion comercial y cuentas corrientes"
        actions={
          <Button
            onClick={() => handleOpenModal()}
            className="gap-2 bg-indigo-600 text-white shadow-sm shadow-indigo-500/20 hover:bg-indigo-700"
          >
            <Plus className="h-4 w-4" />
            Nuevo Proveedor
          </Button>
        }
      />

      <ListToolbar
        searchSlot={
          <div className="relative max-w-sm flex-1">
            <Search className="absolute left-3 top-2.5 h-4 w-4 text-slate-400" />
            <input
              type="text"
              placeholder="Buscar por nombre o CUIT..."
              value={searchTerm}
              onChange={(event) => setSearchTerm(event.target.value)}
              className="w-full rounded-lg border border-slate-200 bg-slate-50 py-2 pl-9 pr-4 text-sm outline-none focus:border-indigo-500 focus:ring-1 focus:ring-indigo-500 dark:border-slate-700 dark:bg-slate-800 dark:text-white"
            />
          </div>
        }
        filterSlot={
          <label className="flex cursor-pointer select-none items-center gap-2 rounded-lg px-3 py-2 text-sm text-slate-600 transition-colors hover:bg-slate-100 dark:text-slate-400 dark:hover:bg-slate-800">
            <input
              type="checkbox"
              checked={showInactive}
              onChange={(event) => setShowInactive(event.target.checked)}
              className="rounded border-slate-300 text-indigo-600 focus:ring-indigo-500"
            />
            Mostrar inactivos
          </label>
        }
      />

      {loading && suppliers.length === 0 ? (
        <div className="p-12 text-center text-slate-500">Cargando proveedores...</div>
      ) : databaseUnavailable ? (
        <DatabaseUnavailableState />
      ) : (
        <>
          <SupplierTable
            suppliers={suppliers}
            onEdit={handleOpenModal}
            onToggleStatus={handleToggleStatus}
            onAccountClick={(supplier) => navigate(`/suppliers/${getPublicId(supplier)}/account`)}
          />
          <SupplierMobileList
            suppliers={suppliers}
            onEdit={handleOpenModal}
            onToggleStatus={handleToggleStatus}
            onAccountClick={(supplier) => navigate(`/suppliers/${getPublicId(supplier)}/account`)}
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

      <SupplierFormModal
        isOpen={isModalOpen}
        onClose={() => setIsModalOpen(false)}
        supplier={currentSupplier}
        onSave={onSave}
      />
    </div>
  );
}
