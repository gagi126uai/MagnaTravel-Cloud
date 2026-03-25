import { useState } from "react";
import { useNavigate } from "react-router-dom";
import { Plus, Search } from "lucide-react";

import { useSuppliers } from "../hooks/useSuppliers";
import { SupplierTable } from "../components/SupplierTable";
import { SupplierMobileList } from "../components/SupplierMobileList";
import { SupplierFormModal } from "../components/SupplierFormModal";
import { PaginationFooter } from "../../../components/ui/PaginationFooter";
import { DatabaseUnavailableState } from "../../../components/ui/DatabaseUnavailableState";
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
        <div className="space-y-6 animate-in fade-in duration-500">
            {/* Header Section */}
            <div className="flex flex-col gap-4 md:flex-row md:items-center md:justify-between">
                <div>
                    <h2 className="text-2xl font-bold tracking-tight text-slate-900 dark:text-white">Proveedores</h2>
                    <p className="text-sm text-muted-foreground">Gestión comercial y cuentas corrientes</p>
                </div>
                <button
                    onClick={() => handleOpenModal()}
                    className="inline-flex items-center justify-center gap-2 rounded-lg bg-indigo-600 px-4 py-2 text-sm font-medium text-white hover:bg-indigo-700 shadow-sm shadow-indigo-500/20 transition-all hover:scale-105"
                >
                    <Plus className="h-4 w-4" />
                    Nuevo Proveedor
                </button>
            </div>

            {/* Filters & Toolbar */}
            <div className="flex flex-col gap-4 sm:flex-row sm:items-center justify-between rounded-xl border border-slate-200 bg-white p-4 shadow-sm dark:border-slate-800 dark:bg-slate-900/50">
                <div className="relative flex-1 max-w-sm">
                    <Search className="absolute left-3 top-2.5 h-4 w-4 text-slate-400" />
                    <input
                        type="text"
                        placeholder="Buscar por nombre o CUIT..."
                        value={searchTerm}
                        onChange={(e) => setSearchTerm(e.target.value)}
                        className="w-full rounded-lg border border-slate-200 bg-slate-50 pl-9 pr-4 py-2 text-sm outline-none focus:border-indigo-500 focus:ring-1 focus:ring-indigo-500 dark:border-slate-700 dark:bg-slate-800 dark:text-white"
                    />
                </div>

                <div className="flex items-center gap-2">
                    <label className="flex items-center gap-2 text-sm text-slate-600 dark:text-slate-400 cursor-pointer select-none px-3 py-2 rounded-lg hover:bg-slate-100 dark:hover:bg-slate-800 transition-colors">
                        <input
                            type="checkbox"
                            checked={showInactive}
                            onChange={(e) => setShowInactive(e.target.checked)}
                            className="rounded border-slate-300 text-indigo-600 focus:ring-indigo-500"
                        />
                        Mostrar inactivos
                    </label>
                </div>
            </div>

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
