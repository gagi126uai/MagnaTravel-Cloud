import React from "react";
import { Info, Pencil, Power, Wallet } from "lucide-react";
import { Badge } from "../../../components/ui/badge";
import {
  DataGrid,
  DataGridActionCell,
  DataGridBody,
  DataGridCell,
  DataGridEmptyState,
  DataGridHeader,
  DataGridHeaderCell,
  DataGridHeaderRow,
  DataGridRow,
} from "../../../components/ui/DataGrid";
import { getPublicId } from "../../../lib/publicIds";
import { formatCurrency } from "../../../lib/utils";

export function SupplierTable({ suppliers, onEdit, onToggleStatus, onAccountClick }) {
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

  return (
    <DataGrid minWidth="860px" tableClassName="table-fixed">
      <DataGridHeader>
        <DataGridHeaderRow>
          <DataGridHeaderCell className="w-[30%]">Proveedor</DataGridHeaderCell>
          <DataGridHeaderCell className="w-[25%]">Contacto</DataGridHeaderCell>
          <DataGridHeaderCell align="right" className="w-[18%]">
            <div className="group relative flex items-center justify-end gap-1 cursor-help">
              Saldo (deuda)
              <Info className="h-3 w-3 text-slate-400" />
              <div className="pointer-events-none absolute bottom-full right-0 z-10 mb-2 w-64 rounded-lg bg-slate-800 p-2 text-xs text-white opacity-0 shadow-lg transition-opacity group-hover:opacity-100">
                Solo incluye expedientes reservados, operativos o cerrados.
              </div>
            </div>
          </DataGridHeaderCell>
          <DataGridHeaderCell align="center" className="w-[12%]">Estado</DataGridHeaderCell>
          <DataGridHeaderCell align="right" className="w-[15%]">Acciones</DataGridHeaderCell>
        </DataGridHeaderRow>
      </DataGridHeader>
      <DataGridBody>
        {suppliers.length === 0 ? (
          <DataGridEmptyState
            colSpan={5}
            title="No se encontraron proveedores"
            description="Ajusta los filtros o crea un proveedor nuevo para empezar."
          />
        ) : (
          suppliers.map((supplier) => (
            <DataGridRow key={getPublicId(supplier)} inactive={!supplier.isActive}>
              <DataGridCell className="font-medium text-slate-900 dark:text-white">
                <div className="flex items-center gap-3">
                  <div
                    className={`flex h-10 w-10 items-center justify-center rounded-full text-xs font-bold text-white shadow-sm ${getRandomColor(
                      supplier.name || "PV"
                    )}`}
                  >
                    {getInitials(supplier.name)}
                  </div>
                  <div className="flex flex-col">
                    <span className="font-semibold text-slate-900 dark:text-white">{supplier.name}</span>
                    <span className="mt-0.5 text-[11px] text-slate-500">{supplier.taxId || "Sin CUIT"}</span>
                  </div>
                </div>
              </DataGridCell>
              <DataGridCell>
                <div className="flex flex-col gap-1 text-xs">
                  <span className="font-medium text-slate-600 dark:text-slate-300">{supplier.contactName || "-"}</span>
                  {supplier.email ? <span className="truncate text-slate-400">{supplier.email}</span> : null}
                </div>
              </DataGridCell>
              <DataGridCell align="right">
                <div
                  className={`font-mono font-medium ${
                    supplier.currentBalance > 0 ? "text-rose-600 dark:text-rose-400" : "text-emerald-600 dark:text-emerald-400"
                  }`}
                >
                  {formatCurrency(supplier.currentBalance)}
                </div>
              </DataGridCell>
              <DataGridCell align="center">
                <Badge variant={supplier.isActive ? "success" : "secondary"} className="text-[10px] px-1.5 py-0.5">
                  {supplier.isActive ? "Activo" : "Inactivo"}
                </Badge>
              </DataGridCell>
              <DataGridActionCell>
                <button
                  onClick={() => onAccountClick(supplier)}
                  className="flex h-8 w-8 items-center justify-center rounded-lg text-slate-500 transition-colors hover:bg-indigo-50 hover:text-indigo-600 dark:hover:bg-indigo-900/30"
                >
                  <Wallet className="h-4 w-4" />
                </button>
                <button
                  onClick={() => onEdit(supplier)}
                  className="flex h-8 w-8 items-center justify-center rounded-lg text-slate-500 transition-colors hover:bg-indigo-50 hover:text-indigo-600 dark:hover:bg-indigo-900/30"
                >
                  <Pencil className="h-4 w-4" />
                </button>
                <button
                  onClick={() => onToggleStatus(supplier)}
                  className={`flex h-8 w-8 items-center justify-center rounded-lg transition-colors ${
                    supplier.isActive
                      ? "text-slate-500 hover:bg-rose-50 hover:text-rose-600"
                      : "text-slate-400 hover:bg-emerald-50 hover:text-emerald-600"
                  }`}
                >
                  <Power className="h-4 w-4" />
                </button>
              </DataGridActionCell>
            </DataGridRow>
          ))
        )}
      </DataGridBody>
    </DataGrid>
  );
}
