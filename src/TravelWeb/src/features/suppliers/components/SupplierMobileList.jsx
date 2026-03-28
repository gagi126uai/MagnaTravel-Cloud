import React from "react";
import { Building2, Mail, Pencil, Power, Wallet } from "lucide-react";
import { Badge } from "../../../components/ui/badge";
import { ListEmptyState } from "../../../components/ui/ListEmptyState";
import { MobileRecordCard, MobileRecordList } from "../../../components/ui/MobileRecordCard";
import { getPublicId } from "../../../lib/publicIds";
import { formatCurrency } from "../../../lib/utils";

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
      <ListEmptyState
        title="No se encontraron proveedores"
        description="Ajusta los filtros o crea un proveedor nuevo."
        className="md:hidden rounded-xl border border-dashed border-slate-300 dark:border-slate-700"
      />
    );
  }

  return (
    <MobileRecordList>
      {suppliers.map((supplier) => (
        <MobileRecordCard
          key={getPublicId(supplier)}
          inactive={!supplier.isActive}
          accentSlot={
            <div
              className={`flex h-10 w-10 items-center justify-center rounded-full text-xs font-bold text-white shadow-sm ${getRandomColor(
                supplier.name || "PV"
              )}`}
            >
              {getInitials(supplier.name)}
            </div>
          }
          statusSlot={
            <Badge variant={supplier.isActive ? "success" : "secondary"} className="text-[10px] px-1.5 py-0.5">
              {supplier.isActive ? "Activo" : "Inactivo"}
            </Badge>
          }
          title={supplier.name}
          subtitle={supplier.taxId || "Sin CUIT"}
          meta={
            <>
              <div className="flex items-center gap-2 text-slate-600 dark:text-slate-400">
                <Building2 className="h-3.5 w-3.5 opacity-70" />
                <span className="truncate">{supplier.contactName || "-"}</span>
              </div>
              <div className="flex items-center gap-2 text-slate-600 dark:text-slate-400">
                <Mail className="h-3.5 w-3.5 opacity-70" />
                <span className="truncate">{supplier.email || "-"}</span>
              </div>
            </>
          }
          footer={
            <div
              className={`font-mono font-medium ${
                (supplier.currentBalance || 0) > 0
                  ? "text-rose-600 dark:text-rose-400"
                  : "text-emerald-600 dark:text-emerald-400"
              }`}
            >
              {formatCurrency(supplier.currentBalance || 0)}
            </div>
          }
          footerActions={
            <>
              <button
                onClick={() => onAccountClick(supplier)}
                className="flex h-8 w-8 items-center justify-center rounded-lg border border-slate-200 bg-white text-slate-600 transition-colors hover:bg-indigo-50 hover:text-indigo-600 dark:border-slate-700 dark:bg-slate-800 dark:text-slate-400"
              >
                <Wallet className="h-4 w-4" />
              </button>
              <button
                onClick={() => onEdit(supplier)}
                className="flex h-8 w-8 items-center justify-center rounded-lg border border-slate-200 bg-white text-slate-600 transition-colors hover:bg-indigo-50 hover:text-indigo-600 dark:border-slate-700 dark:bg-slate-800 dark:text-slate-400"
              >
                <Pencil className="h-4 w-4" />
              </button>
              <button
                onClick={() => onToggleStatus(supplier)}
                className={`flex h-8 w-8 items-center justify-center rounded-lg border transition-colors ${
                  supplier.isActive
                    ? "border-slate-200 bg-white text-slate-500 hover:bg-rose-50 hover:text-rose-600 dark:border-slate-700 dark:bg-slate-800"
                    : "border-slate-200 bg-white text-slate-400 hover:bg-emerald-50 hover:text-emerald-600 dark:border-slate-700 dark:bg-slate-800"
                }`}
              >
                <Power className="h-4 w-4" />
              </button>
            </>
          }
        />
      ))}
    </MobileRecordList>
  );
}
