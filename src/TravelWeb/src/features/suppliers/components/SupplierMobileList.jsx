import React from "react";
import { Building2, Mail, Wallet, Pencil, Power } from "lucide-react";
import { formatCurrency } from "../../../lib/utils";
import { Badge } from "../../../components/ui/badge";
import { getPublicId } from "../../../lib/publicIds";

export function SupplierMobileList({ suppliers, onEdit, onToggleStatus, onAccountClick }) {
    const getInitials = (name) => {
        return name?.split(" ").map((part) => part[0]).join("").toUpperCase().slice(0, 2) || "PV";
    };

    const getRandomColor = (name) => {
        const colors = ["bg-blue-500", "bg-emerald-500", "bg-violet-500", "bg-amber-500", "bg-rose-500", "bg-indigo-500"];
        let hash = 0;
        for (let index = 0; index < name.length; index += 1) {
            hash = name.charCodeAt(index) + ((hash << 5) - hash);
        }
        return colors[Math.abs(hash) % colors.length];
    };

    if (suppliers.length === 0) {
        return (
            <div className="p-8 text-center text-muted-foreground border border-dashed rounded-xl border-slate-300 dark:border-slate-700">
                <p>No se encontraron proveedores</p>
            </div>
        );
    }

    return (
        <div className="md:hidden space-y-3">
            {suppliers.map((supplier) => (
                <div
                    key={getPublicId(supplier)}
                    className={`bg-white dark:bg-slate-900 rounded-xl p-4 border shadow-sm ${
                        !supplier.isActive ? "opacity-70 border-slate-200 dark:border-slate-800" : "border-slate-200 dark:border-slate-800"
                    }`}
                >
                    <div className="flex justify-between items-start mb-3">
                        <div className="flex items-center gap-3">
                            <div className={`flex h-10 w-10 items-center justify-center rounded-full text-xs font-bold text-white shadow-sm shrink-0 ${getRandomColor(supplier.name || "PV")}`}>
                                {getInitials(supplier.name)}
                            </div>
                            <div>
                                <div className="font-semibold text-slate-900 dark:text-white leading-tight">{supplier.name}</div>
                                <div className="text-xs text-slate-500 mt-0.5">{supplier.taxId || "Sin CUIT"}</div>
                            </div>
                        </div>
                        <Badge variant={supplier.isActive ? "success" : "secondary"} className="text-[10px] px-1.5 py-0.5">
                            {supplier.isActive ? "Activo" : "Inactivo"}
                        </Badge>
                    </div>

                    <div className="grid grid-cols-1 gap-2 text-sm mb-3">
                        <div className="flex items-center gap-2 text-slate-600 dark:text-slate-400">
                            <Building2 className="h-3.5 w-3.5 opacity-70" />
                            <span className="truncate">{supplier.contactName || "-"}</span>
                        </div>
                        <div className="flex items-center gap-2 text-slate-600 dark:text-slate-400">
                            <Mail className="h-3.5 w-3.5 opacity-70" />
                            <span className="truncate">{supplier.email || "-"}</span>
                        </div>
                    </div>

                    <div className="flex justify-between items-center pt-3 border-t border-slate-100 dark:border-slate-800">
                        <div className={`font-mono font-medium ${(supplier.currentBalance || 0) > 0 ? "text-rose-600 dark:text-rose-400" : "text-emerald-600 dark:text-emerald-400"}`}>
                            {formatCurrency(supplier.currentBalance || 0)}
                        </div>

                        <div className="flex items-center gap-1">
                            <button
                                onClick={() => onAccountClick(supplier)}
                                className="h-8 w-8 flex items-center justify-center rounded-md border border-slate-200 bg-white text-slate-600 hover:bg-indigo-50 hover:text-indigo-600 dark:border-slate-700 dark:bg-slate-800 dark:text-slate-400 transition-colors"
                            >
                                <Wallet className="h-4 w-4" />
                            </button>
                            <button
                                onClick={() => onEdit(supplier)}
                                className="h-8 w-8 flex items-center justify-center rounded-md border border-slate-200 bg-white text-slate-600 hover:bg-indigo-50 hover:text-indigo-600 dark:border-slate-700 dark:bg-slate-800 dark:text-slate-400 transition-colors"
                            >
                                <Pencil className="h-4 w-4" />
                            </button>
                            <button
                                onClick={() => onToggleStatus(supplier)}
                                className={`h-8 w-8 flex items-center justify-center rounded-md border transition-colors ${
                                    supplier.isActive
                                        ? "border-slate-200 bg-white text-slate-500 hover:bg-rose-50 hover:text-rose-600 dark:border-slate-700 dark:bg-slate-800"
                                        : "border-slate-200 bg-white text-slate-400 hover:bg-emerald-50 hover:text-emerald-600 dark:border-slate-700 dark:bg-slate-800"
                                }`}
                            >
                                <Power className="h-4 w-4" />
                            </button>
                        </div>
                    </div>
                </div>
            ))}
        </div>
    );
}
