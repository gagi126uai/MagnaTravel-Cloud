import { useState } from "react";
import { useNavigate } from "react-router-dom";
import { Plus, Search } from "lucide-react";
import { NuevoOperadorInline } from "../components/NuevoOperadorInline";
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

  // La ficha de ALTA se abre en línea (dentro de la página, no como ventana flotante).
  const [mostrarNuevoOperador, setMostrarNuevoOperador] = useState(false);

  // La edición de un operador existente sigue usando el modal hasta que se
  // migre a la solapa "Datos" de SupplierAccountPage (spec sección 1, pendiente).
  const [currentSupplier, setCurrentSupplier] = useState(null);
  const [isEditModalOpen, setIsEditModalOpen] = useState(false);

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
    refresh,
    databaseUnavailable,
  } = useSuppliers();

  // Abre el modal de edición para un operador existente.
  // La creación ya no pasa por acá: usa NuevoOperadorInline.
  function handleOpenEditModal(supplier) {
    setCurrentSupplier(supplier);
    setIsEditModalOpen(true);
  }

  async function onSaveEdit(formData, supplierId) {
    const success = await handleSaveSupplier(formData, supplierId);
    if (success) {
      setIsEditModalOpen(false);
    }
  }

  // Cuando el alta inline termina con éxito, cerramos la ficha y refrescamos la lista.
  function handleOperadorCreado() {
    setMostrarNuevoOperador(false);
    refresh();
  }

  return (
    <div className="animate-in fade-in space-y-6 duration-500">
      <ListPageHeader
        title="Operadores"
        subtitle="Tus operadores y mayoristas, con la cuenta corriente de cada uno."
        actions={
          // Si la ficha está abierta, el botón no se muestra para no confundir.
          !mostrarNuevoOperador && (
            <Button
              onClick={() => setMostrarNuevoOperador(true)}
              className="gap-2 bg-indigo-600 text-white shadow-sm shadow-indigo-500/20 hover:bg-indigo-700"
              data-testid="btn-nuevo-operador"
            >
              <Plus className="h-4 w-4" />
              Nuevo operador
            </Button>
          )
        }
      />

      {/* Ficha de alta en línea: se despliega debajo del encabezado, antes de la lista.
          Sigue la regla del dueño: nada se abre como ventana encima de la página. */}
      {mostrarNuevoOperador && (
        <NuevoOperadorInline
          onCreado={handleOperadorCreado}
          onCancelar={() => setMostrarNuevoOperador(false)}
        />
      )}

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
        <div className="p-12 text-center text-slate-500">Cargando operadores…</div>
      ) : databaseUnavailable ? (
        <DatabaseUnavailableState />
      ) : (
        <>
          <SupplierTable
            suppliers={suppliers}
            onEdit={handleOpenEditModal}
            onToggleStatus={handleToggleStatus}
            onAccountClick={(supplier) => navigate(`/suppliers/${getPublicId(supplier)}/account`)}
          />
          <SupplierMobileList
            suppliers={suppliers}
            onEdit={handleOpenEditModal}
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

      {/* Modal de EDICIÓN de un operador existente.
          La edición migra a la solapa "Datos" de SupplierAccountPage en la spec sección 1. */}
      <SupplierFormModal
        isOpen={isEditModalOpen}
        onClose={() => setIsEditModalOpen(false)}
        supplier={currentSupplier}
        onSave={onSaveEdit}
      />
    </div>
  );
}
