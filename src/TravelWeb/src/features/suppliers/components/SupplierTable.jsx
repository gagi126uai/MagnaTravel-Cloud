import React, { useState } from 'react';
import { Wallet, Pencil, Power, Info } from "lucide-react";
import { formatCurrency } from "../../../lib/utils";
import { Badge } from "../../../components/ui/badge";
import { getPublicId } from "../../../lib/publicIds";

export function SupplierTable({ suppliers, onEdit, onToggleStatus, onAccountClick }) {
    const getInitials = (name) => {
        return name?.split(" ").map(n => n[0]).join("").toUpperCase().slice(0, 2) || "PV";
    };

    const [visibleCount, setVisibleCount] = useState(50);
    const visibleSuppliers = suppliers.slice(0, visibleCount);

    const handleLoadMore = () => {
        setVisibleCount(prev => prev + 50);
    };

    const getRandomColor = (name) => {
        const colors = ["bg-blue-500", "bg-emerald-500", "bg-violet-500", "bg-amber-500", "bg-rose-500", "bg-indigo-500"];
        let hash = 0;
        for (let i = 0; i < name.length; i++) {
            hash = name.charCodeAt(i) + ((hash << 5) - hash);
        }
        return colors[Math.abs(hash) % colors.length];
    };

    return (
        <div className="hidden md:block rounded-xl border border-slate-200 bg-white shadow-sm dark:border-slate-800 dark:bg-slate-900">
            <div className="relative w-full overflow-auto">
                <table className="w-full table-fixed caption-bottom text-sm text-left">
                    <thead className="[&_tr]:border-b">
                        <tr className="border-b border-slate-100 dark:border-slate-800 transition-colors hover:bg-slate-50/50 dark:hover:bg-slate-800/50">
                            <th className="h-12 px-4 align-middle font-medium text-slate-500 dark:text-slate-400 w-[30%]">Proveedor</th>
                            <th className="h-12 px-4 align-middle font-medium text-slate-500 dark:text-slate-400 w-[25%]">Contacto</th>
                            <th className="h-12 px-4 align-middle font-medium text-slate-500 dark:text-slate-400 text-right w-[18%]">
                                <div className="flex items-center justify-end gap-1 cursor-help group relative">
                                    Saldo (Deuda)
                                    <Info className="h-3 w-3 text-slate-400" />
                                    <div className="absolute bottom-full mb-2 right-0 w-64 p-2 bg-slate-800 text-white text-xs rounded shadow-lg opacity-0 group-hover:opacity-100 transition-opacity pointer-events-none z-10">
                                        Solo incluye expedientes Reservados, Operativos o Cerrados. Los Presupuestos no suman deuda.
                                    </div>
                                </div>
                            </th>
                            <th className="h-12 px-4 align-middle font-medium text-slate-500 dark:text-slate-400 text-center w-[12%]">Estado</th>
                            <th className="h-12 px-4 align-middle font-medium text-slate-500 dark:text-slate-400 text-right w-[15%]">Acciones</th>
                        </tr>
                    </thead>
                    <tbody className="[&_tr:last-child]:border-0">
                        {suppliers.length === 0 ? (
                            <tr>
                                <td colSpan={5} className="p-8 text-center text-muted-foreground font-light">
                                    No se encontraron proveedores
                                </td>
                            </tr>
                        ) : (
                            visibleSuppliers.map((supplier) => (
                                <tr key={getPublicId(supplier)} className={`border-b border-slate-100 dark:border-slate-800 transition-colors hover:bg-slate-50 dark:hover:bg-slate-800/50 ${!supplier.isActive ? 'opacity-60 bg-slate-50/50 dark:bg-slate-900/50' : ''}`}>
                                    <td className="p-4 align-middle font-medium">
                                        <div className="flex items-center gap-3">
                                            <div className={`flex h-10 w-10 items-center justify-center rounded-full text-xs font-bold text-white shadow-sm ${getRandomColor(supplier.name)}`}>
                                                {getInitials(supplier.name)}
                                            </div>
                                            <div className="flex flex-col">
                                                <span className="text-slate-900 dark:text-white font-semibold">{supplier.name}</span>
                                                <span className="text-[11px] text-slate-500 mt-0.5">{supplier.taxId || "Sin CUIT"}</span>
                                            </div>
                                        </div>
                                    </td>
                                    <td className="p-4 align-middle">
                                        <div className="flex flex-col text-xs gap-1">
                                            <span className="text-slate-600 dark:text-slate-300 font-medium">{supplier.contactName || "-"}</span>
                                            {supplier.email && <span className="text-slate-400 truncate">{supplier.email}</span>}
                                        </div>
                                    </td>
                                    <td className="p-4 align-middle text-right">
                                        <div className={`font-mono font-medium ${supplier.currentBalance > 0 ? "text-rose-600 dark:text-rose-400" : "text-emerald-600 dark:text-emerald-400"}`}>
                                            {formatCurrency(supplier.currentBalance)}
                                        </div>
                                    </td>
                                    <td className="p-4 align-middle text-center">
                                        <Badge variant={supplier.isActive ? "success" : "secondary"} className="text-[10px] px-1.5 py-0.5">
                                            {supplier.isActive ? "Activo" : "Inactivo"}
                                        </Badge>
                                    </td>
                                    <td className="p-4 align-middle text-right pr-4">
                                        <div className="flex items-center justify-end gap-1">
                                            <button
                                                onClick={() => onAccountClick(supplier)}
                                                className="h-8 w-8 flex items-center justify-center rounded-md text-slate-500 hover:text-indigo-600 hover:bg-indigo-50 dark:hover:bg-indigo-900/30 transition-colors"
                                            >
                                                <Wallet className="h-4 w-4" />
                                            </button>
                                            <button
                                                onClick={() => onEdit(supplier)}
                                                className="h-8 w-8 flex items-center justify-center rounded-md text-slate-500 hover:text-indigo-600 hover:bg-indigo-50 dark:hover:bg-indigo-900/30 transition-colors"
                                            >
                                                <Pencil className="h-4 w-4" />
                                            </button>
                                            <button
                                                onClick={() => onToggleStatus(supplier)}
                                                className={`h-8 w-8 flex items-center justify-center rounded-md transition-colors ${supplier.isActive ? 'text-slate-500 hover:text-rose-600 hover:bg-rose-50' : 'text-slate-400 hover:text-emerald-600 hover:bg-emerald-50'}`}
                                            >
                                                <Power className="h-4 w-4" />
                                            </button>
                                        </div>
                                    </td>
                                </tr>
                            ))
                        )}
                    </tbody>
                </table>
            </div>
            {suppliers.length > visibleCount && (
                <div className="p-4 border-t border-slate-100 dark:border-slate-800 bg-slate-50/50 dark:bg-slate-800/20 text-center">
                    <button 
                        onClick={handleLoadMore}
                        className="text-sm font-semibold text-slate-600 dark:text-slate-300 w-full sm:w-auto px-4 py-2 border rounded-md hover:bg-slate-100 dark:hover:bg-slate-800 transition-colors"
                    >
                        Cargar más resultados ({suppliers.length - visibleCount} restantes)
                    </button>
                </div>
            )}
        </div>
    );
}
