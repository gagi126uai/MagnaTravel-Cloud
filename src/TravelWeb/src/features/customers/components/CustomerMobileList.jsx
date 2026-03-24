import React, { useState } from 'react';
import { Mail, Phone, Wallet, Pencil } from "lucide-react";
import { formatCurrency } from "../../../lib/utils";
import { Badge } from "../../../components/ui/badge";
import { getPublicId } from "../../../lib/publicIds";

export function CustomerMobileList({ customers, onEdit, onAccountClick }) {
    const getInitials = (name) => {
        return name?.split(" ").map(n => n[0]).join("").toUpperCase().slice(0, 2) || "??";
    };

    const [visibleCount, setVisibleCount] = useState(50);
    const visibleCustomers = customers.slice(0, visibleCount);

    const handleLoadMore = () => {
        setVisibleCount(prev => prev + 50);
    };

    if (customers.length === 0) {
        return (
            <div className="p-8 text-center text-muted-foreground flex flex-col items-center border border-dashed rounded-xl border-slate-300 dark:border-slate-700">
                <p>No se encontraron clientes</p>
            </div>
        );
    }

    return (
        <div className="md:hidden space-y-3">
            {visibleCustomers.map((customer) => (
                <div key={getPublicId(customer)} className={`bg-white dark:bg-slate-900 rounded-xl p-4 border shadow-sm ${!customer.isActive ? 'opacity-70 border-slate-200 dark:border-slate-800' : 'border-slate-200 dark:border-slate-800'}`}>
                    <div className="flex justify-between items-start mb-3">
                        <div className="flex items-center gap-3">
                            <div className={`h-10 w-10 rounded-full flex items-center justify-center text-sm font-bold shadow-sm shrink-0 ${customer.isActive ? 'bg-indigo-100 text-indigo-700 dark:bg-indigo-900/30 dark:text-indigo-300' : 'bg-slate-200 text-slate-500 dark:bg-slate-800 dark:text-slate-400'}`}>
                                {getInitials(customer.fullName)}
                            </div>
                            <div>
                                <div className="font-semibold text-slate-900 dark:text-slate-100">{customer.fullName}</div>
                                <div className="text-xs text-muted-foreground">{customer.taxId || customer.documentNumber || "S/D"}</div>
                            </div>
                        </div>
                        <Badge variant={customer.isActive ? "success" : "secondary"} className={customer.isActive ? "bg-emerald-100 text-emerald-700 border-transparent text-[10px] px-1.5 py-0.5" : "bg-slate-100 text-slate-500 text-[10px] px-1.5 py-0.5"}>
                            {customer.isActive ? "Activo" : "Inactivo"}
                        </Badge>
                    </div>

                    <div className="grid grid-cols-1 gap-2 text-sm mb-3">
                        <div className="flex items-center gap-2 text-slate-600 dark:text-slate-400">
                            <Mail className="h-3.5 w-3.5 opacity-70" />
                            <span className="truncate">{customer.email || "-"}</span>
                        </div>
                        <div className="flex items-center gap-2 text-slate-600 dark:text-slate-400">
                            <Phone className="h-3.5 w-3.5 opacity-70" />
                            <span>{customer.phone || "-"}</span>
                        </div>
                    </div>

                    <div className="flex justify-between items-center pt-3 border-t border-slate-100 dark:border-slate-800">
                        <div className={`font-mono font-medium ${(customer.currentBalance || 0) > 0 ? "text-rose-600 dark:text-rose-400" : "text-emerald-600 dark:text-emerald-400"}`}>
                            {formatCurrency(customer.currentBalance || 0)}
                        </div>

                        <div className="flex items-center gap-1">
                            <button
                                onClick={() => onAccountClick(customer)}
                                className="h-8 w-8 flex items-center justify-center rounded-md border border-slate-200 bg-white text-slate-600 hover:bg-indigo-50 hover:text-indigo-600 dark:border-slate-700 dark:bg-slate-800 dark:text-slate-400"
                            >
                                <Wallet className="h-4 w-4" />
                            </button>
                            <button
                                onClick={() => onEdit(customer)}
                                className="h-8 w-8 flex items-center justify-center rounded-md border border-slate-200 bg-white text-slate-600 hover:bg-indigo-50 hover:text-indigo-600 dark:border-slate-700 dark:bg-slate-800 dark:text-slate-400"
                            >
                                <Pencil className="h-4 w-4" />
                            </button>
                        </div>
                    </div>
                </div>
            ))}
            {customers.length > visibleCount && (
                <div className="pt-2 pb-4 text-center">
                    <button 
                        onClick={handleLoadMore}
                        className="text-sm font-semibold text-slate-600 dark:text-slate-300 w-full px-4 py-2 border rounded-md hover:bg-slate-100 dark:hover:bg-slate-800 transition-colors"
                    >
                        Cargar más resultados ({customers.length - visibleCount} restantes)
                    </button>
                </div>
            )}
        </div>
    );
}
