import React from "react";
import { Info, Pencil, Power, Wallet } from "lucide-react";
import { StatusChip } from "../../../components/ui/badge";
import { Button } from "../../../components/ui/button";
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
import { supplierBalanceLines } from "../lib/supplierBalanceView";

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
          <DataGridHeaderCell className="w-[30%]">Operador</DataGridHeaderCell>
          <DataGridHeaderCell className="w-[25%]">Contacto</DataGridHeaderCell>
          <DataGridHeaderCell align="right" className="w-[18%]">
            <div className="group relative flex items-center justify-end gap-1 cursor-help">
              Saldo (deuda)
              <Info className="h-3 w-3 text-slate-400" />
              <div className="pointer-events-none absolute bottom-full right-0 z-10 mb-2 w-64 rounded-[10px] bg-slate-800 p-2 text-xs text-white opacity-0 shadow-lg transition-opacity group-hover:opacity-100">
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
            title="No se encontraron operadores"
            description="Ajustá los filtros o creá un operador nuevo para empezar."
          />
        ) : (
          suppliers.map((supplier) => {
            const balanceLines = supplierBalanceLines(supplier);
            return (
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
                  {/* Hallazgo #9 del barrido (2026-07-24): esta misma tabla mezclaba "-" acá y
                      "—" en la columna de Saldo (unas líneas más abajo) para el mismo concepto
                      ("no hay dato"). Unificado a "—" (em dash), el símbolo que ya domina en el
                      resto del sistema para celdas sin dato. */}
                  <span className="font-medium text-slate-600 dark:text-slate-300">{supplier.contactName || "—"}</span>
                  {supplier.email ? <span className="truncate text-slate-400">{supplier.email}</span> : null}
                </div>
              </DataGridCell>
              <DataGridCell align="right">
                {supplier.amountsVisible === false ? (
                  <span className="text-slate-400">—</span>
                ) : balanceLines.length === 0 ? (
                  <span className="text-slate-500">Sin saldo</span>
                ) : (
                  <div className="space-y-0.5 font-mono font-medium">
                    {balanceLines.map((line) => (
                      <div key={line.currency} className={line.balance > 0
                        ? "text-rose-600 dark:text-rose-400"
                        : "text-emerald-600 dark:text-emerald-400"}
                      >
                        {formatCurrency(line.balance, line.currency)}
                      </div>
                    ))}
                  </div>
                )}
              </DataGridCell>
              <DataGridCell align="center">
                <StatusChip tone={supplier.isActive ? "verde" : "neutro"}>
                  {supplier.isActive ? "Activo" : "Inactivo"}
                </StatusChip>
              </DataGridCell>
              {/* Tres acciones por fila (cuenta / editar / activar-desactivar): mismo
                  criterio que CustomerTable — salen del molde de boton compartido
                  (outline 32px, B.3) en vez de un <button> a mano con hover indigo suelto. */}
              <DataGridActionCell>
                <Button
                  type="button"
                  variant="outline"
                  size="sm"
                  onClick={() => onAccountClick(supplier)}
                  title="Ver cuenta corriente"
                  aria-label="Ver cuenta corriente"
                  className="h-8 w-8 p-0"
                >
                  <Wallet className="h-4 w-4" aria-hidden="true" />
                </Button>
                <Button
                  type="button"
                  variant="outline"
                  size="sm"
                  onClick={() => onEdit(supplier)}
                  title="Editar operador"
                  aria-label="Editar operador"
                  className="h-8 w-8 p-0"
                >
                  <Pencil className="h-4 w-4" aria-hidden="true" />
                </Button>
                <Button
                  type="button"
                  variant="outline"
                  size="sm"
                  onClick={() => onToggleStatus(supplier)}
                  title={supplier.isActive ? "Desactivar" : "Activar"}
                  aria-label={supplier.isActive ? "Desactivar operador" : "Activar operador"}
                  className={`h-8 w-8 p-0 ${
                    supplier.isActive
                      ? "text-slate-400 hover:border-rose-200 hover:bg-rose-50 hover:text-rose-600 dark:hover:bg-rose-950/30"
                      : "border-emerald-300 bg-emerald-50 text-emerald-700 hover:bg-emerald-100 dark:border-emerald-800 dark:bg-emerald-900/20 dark:text-emerald-300"
                  }`}
                >
                  <Power className="h-4 w-4" aria-hidden="true" />
                </Button>
              </DataGridActionCell>
            </DataGridRow>
            );
          })
        )}
      </DataGridBody>
    </DataGrid>
  );
}
