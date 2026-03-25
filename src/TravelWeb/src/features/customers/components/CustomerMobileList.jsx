import React from "react";
import { Mail, Phone, Wallet, Pencil } from "lucide-react";
import { formatCurrency } from "../../../lib/utils";
import { Badge } from "../../../components/ui/badge";
import { getPublicId } from "../../../lib/publicIds";

export function CustomerMobileList({ customers, onEdit, onAccountClick }) {
  const getInitials = (name) => {
    return name?.split(" ").map((part) => part[0]).join("").toUpperCase().slice(0, 2) || "??";
  };

  if (customers.length === 0) {
    return (
      <div className="flex flex-col items-center rounded-xl border border-dashed border-slate-300 p-8 text-center text-muted-foreground dark:border-slate-700">
        <p>No se encontraron clientes</p>
      </div>
    );
  }

  return (
    <div className="space-y-3 md:hidden">
      {customers.map((customer) => (
        <div key={getPublicId(customer)} className={`rounded-xl border p-4 shadow-sm ${!customer.isActive ? "border-slate-200 opacity-70 dark:border-slate-800" : "border-slate-200 dark:border-slate-800"} bg-white dark:bg-slate-900`}>
          <div className="mb-3 flex items-start justify-between">
            <div className="flex items-center gap-3">
              <div className={`flex h-10 w-10 shrink-0 items-center justify-center rounded-full text-sm font-bold shadow-sm ${customer.isActive ? "bg-indigo-100 text-indigo-700 dark:bg-indigo-900/30 dark:text-indigo-300" : "bg-slate-200 text-slate-500 dark:bg-slate-800 dark:text-slate-400"}`}>
                {getInitials(customer.fullName)}
              </div>
              <div>
                <div className="font-semibold text-slate-900 dark:text-slate-100">{customer.fullName}</div>
                <div className="text-xs text-muted-foreground">{customer.taxId || customer.documentNumber || "S/D"}</div>
              </div>
            </div>
            <Badge
              variant={customer.isActive ? "success" : "secondary"}
              className={customer.isActive ? "border-transparent bg-emerald-100 px-1.5 py-0.5 text-[10px] text-emerald-700" : "bg-slate-100 px-1.5 py-0.5 text-[10px] text-slate-500"}
            >
              {customer.isActive ? "Activo" : "Inactivo"}
            </Badge>
          </div>

          <div className="mb-3 grid grid-cols-1 gap-2 text-sm">
            <div className="flex items-center gap-2 text-slate-600 dark:text-slate-400">
              <Mail className="h-3.5 w-3.5 opacity-70" />
              <span className="truncate">{customer.email || "-"}</span>
            </div>
            <div className="flex items-center gap-2 text-slate-600 dark:text-slate-400">
              <Phone className="h-3.5 w-3.5 opacity-70" />
              <span>{customer.phone || "-"}</span>
            </div>
          </div>

          <div className="flex items-center justify-between border-t border-slate-100 pt-3 dark:border-slate-800">
            <div className={`font-mono font-medium ${(customer.currentBalance || 0) > 0 ? "text-rose-600 dark:text-rose-400" : "text-emerald-600 dark:text-emerald-400"}`}>
              {formatCurrency(customer.currentBalance || 0)}
            </div>

            <div className="flex items-center gap-1">
              <button
                onClick={() => onAccountClick(customer)}
                className="flex h-8 w-8 items-center justify-center rounded-md border border-slate-200 bg-white text-slate-600 hover:bg-indigo-50 hover:text-indigo-600 dark:border-slate-700 dark:bg-slate-800 dark:text-slate-400"
              >
                <Wallet className="h-4 w-4" />
              </button>
              <button
                onClick={() => onEdit(customer)}
                className="flex h-8 w-8 items-center justify-center rounded-md border border-slate-200 bg-white text-slate-600 hover:bg-indigo-50 hover:text-indigo-600 dark:border-slate-700 dark:bg-slate-800 dark:text-slate-400"
              >
                <Pencil className="h-4 w-4" />
              </button>
            </div>
          </div>
        </div>
      ))}
    </div>
  );
}
