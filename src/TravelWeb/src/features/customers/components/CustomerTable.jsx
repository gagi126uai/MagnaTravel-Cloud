import React from 'react';
import { Mail, Phone, Wallet, Pencil, Power } from "lucide-react";
import { formatCurrency } from "../../../lib/utils";
import { Badge } from "../../../components/ui/badge";

export function CustomerTable({ customers, onEdit, onToggleStatus, onAccountClick }) {
    const getInitials = (name) => {
        return name?.split(" ").map(n => n[0]).join("").toUpperCase().slice(0, 2) || "??";
    };

    return (
        <div className="hidden md:block rounded-xl border bg-card shadow-sm overflow-hidden">
            <div className="relative w-full overflow-auto">
                <table className="w-full caption-bottom text-sm text-left">
                    <thead className="bg-slate-50 dark:bg-slate-900/50">
                        <tr className="border-b transition-colors border-slate-200 dark:border-slate-800">
                            <th className="h-12 px-4 align-middle font-medium text-slate-500 dark:text-slate-400">Cliente</th>
                            <th className="h-12 px-4 align-middle font-medium text-slate-500 dark:text-slate-400">Contacto</th>
                            <th className="h-12 px-4 align-middle font-medium text-slate-500 dark:text-slate-400 text-right">Saldo Actual</th>
                            <th className="h-12 px-4 align-middle font-medium text-slate-500 dark:text-slate-400 text-center">Estado</th>
                            <th className="h-12 px-4 align-middle font-medium text-slate-500 dark:text-slate-400 text-right">Acciones</th>
                        </tr>
                    </thead>
                    <tbody className="[&_tr:last-child]:border-0 divide-y divide-slate-100 dark:divide-slate-800">
                        {customers.map((customer) => (
                            <tr key={customer.id} className={`transition-colors hover:bg-slate-50/50 dark:hover:bg-slate-900/50 ${!customer.isActive ? 'opacity-60 bg-slate-50/30' : ''}`}>
                                <td className="p-4 align-middle">
                                    <div className="flex items-center gap-3">
                                        <div className={`h-10 w-10 rounded-full flex items-center justify-center text-sm font-bold shadow-sm ${customer.isActive ? 'bg-indigo-100 text-indigo-700 dark:bg-indigo-900/30 dark:text-indigo-300' : 'bg-slate-200 text-slate-500 dark:bg-slate-800 dark:text-slate-400'}`}>
                                            {getInitials(customer.fullName)}
                                        </div>
                                        <div className="flex flex-col">
                                            <span className="font-semibold text-slate-900 dark:text-slate-100">{customer.fullName}</span>
                                            <span className="text-xs text-muted-foreground">{customer.taxId || customer.documentNumber || "S/D"}</span>
                                        </div>
                                    </div>
                                </td>
                                <td className="p-4 align-middle text-muted-foreground">
                                    <div className="flex flex-col gap-0.5">
                                        <div className="flex items-center gap-1.5 text-xs">
                                            <Mail className="h-3 w-3" />
                                            {customer.email || "-"}
                                        </div>
                                        <div className="flex items-center gap-1.5 text-xs">
                                            <Phone className="h-3 w-3" />
                                            {customer.phone || "-"}
                                        </div>
                                    </div>
                                </td>
                                <td className="p-4 align-middle text-right">
                                    <div className={`font-mono font-medium ${(customer.currentBalance || 0) > 0 ? "text-rose-600 dark:text-rose-400" : "text-emerald-600 dark:text-emerald-400"}`}>
                                        {formatCurrency(customer.currentBalance || 0)}
                                    </div>
                                    {(customer.currentBalance || 0) > 0 && (
                                        <span className="text-[10px] text-rose-500 font-semibold uppercase">Deuda</span>
                                    )}
                                </td>
                                <td className="p-4 align-middle text-center">
                                    <Badge variant={customer.isActive ? "success" : "secondary"} className={customer.isActive ? "bg-emerald-100 text-emerald-700 border-transparent" : "bg-slate-100 text-slate-500"}>
                                        {customer.isActive ? "Activo" : "Inactivo"}
                                    </Badge>
                                </td>
                                <td className="p-4 align-middle text-right">
                                    <div className="flex items-center justify-end gap-1">
                                        <button
                                            onClick={() => onAccountClick(customer.id)}
                                            title="Ver Cuenta Corriente"
                                            className="inline-flex h-8 w-8 items-center justify-center rounded-md border border-slate-200 bg-white text-slate-600 hover:bg-slate-50 hover:text-indigo-600 dark:border-slate-800 dark:bg-slate-950 dark:text-slate-400 dark:hover:bg-slate-900 transition-colors shadow-sm"
                                        >
                                            <Wallet className="h-4 w-4" />
                                        </button>
                                        <button
                                            onClick={() => onEdit(customer)}
                                            title="Editar Cliente"
                                            className="inline-flex h-8 w-8 items-center justify-center rounded-md border border-slate-200 bg-white text-slate-600 hover:bg-slate-50 hover:text-indigo-600 dark:border-slate-800 dark:bg-slate-950 dark:text-slate-400 dark:hover:bg-slate-900 transition-colors shadow-sm"
                                        >
                                            <Pencil className="h-4 w-4" />
                                        </button>
                                        <button
                                            onClick={() => onToggleStatus(customer)}
                                            title={customer.isActive ? "Desactivar" : "Activar"}
                                            className={`inline-flex h-8 w-8 items-center justify-center rounded-md border shadow-sm transition-colors ${customer.isActive ? 'border-slate-200 bg-white text-slate-400 hover:bg-rose-50 hover:text-rose-600 hover:border-rose-200 dark:border-slate-800 dark:bg-slate-950' : 'border-emerald-200 bg-emerald-50 text-emerald-600 hover:bg-emerald-100'}`}
                                        >
                                            <Power className="h-4 w-4" />
                                        </button>
                                    </div>
                                </td>
                            </tr>
                        ))}
                    </tbody>
                </table>
            </div>
        </div>
    );
}
