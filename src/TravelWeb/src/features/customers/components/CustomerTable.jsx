import React from "react";
import { Mail, Pencil, Phone, Power, Wallet } from "lucide-react";
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

export function CustomerTable({ customers, onEdit, onToggleStatus, onAccountClick }) {
  const getInitials = (name) => {
    return name?.split(" ").map((part) => part[0]).join("").toUpperCase().slice(0, 2) || "??";
  };

  return (
    <DataGrid minWidth="820px">
      <DataGridHeader>
        <DataGridHeaderRow>
          <DataGridHeaderCell>Cliente</DataGridHeaderCell>
          <DataGridHeaderCell>Contacto</DataGridHeaderCell>
          <DataGridHeaderCell align="right">Saldo actual</DataGridHeaderCell>
          <DataGridHeaderCell align="center">Estado</DataGridHeaderCell>
          <DataGridHeaderCell align="right">Acciones</DataGridHeaderCell>
        </DataGridHeaderRow>
      </DataGridHeader>
      <DataGridBody>
        {customers.length === 0 ? (
          <DataGridEmptyState
            colSpan={5}
            title="No se encontraron clientes"
            description="Ajusta la busqueda o crea un nuevo cliente para empezar."
          />
        ) : (
          customers.map((customer) => (
            <DataGridRow key={getPublicId(customer)} inactive={!customer.isActive}>
              <DataGridCell className="text-slate-900 dark:text-slate-100">
                <div className="flex items-center gap-3">
                  <div
                    className={`flex h-10 w-10 items-center justify-center rounded-full text-sm font-bold shadow-sm ${
                      customer.isActive
                        ? "bg-indigo-100 text-indigo-700 dark:bg-indigo-900/30 dark:text-indigo-300"
                        : "bg-slate-200 text-slate-500 dark:bg-slate-800 dark:text-slate-400"
                    }`}
                  >
                    {getInitials(customer.fullName)}
                  </div>
                  <div className="flex flex-col">
                    <span className="font-semibold text-slate-900 dark:text-slate-100">{customer.fullName}</span>
                    <span className="text-xs text-slate-500 dark:text-slate-400">
                      {customer.taxId || customer.documentNumber || "S/D"}
                    </span>
                  </div>
                </div>
              </DataGridCell>
              <DataGridCell className="text-muted-foreground">
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
              </DataGridCell>
              <DataGridCell align="right">
                <div
                  className={`font-mono font-medium ${
                    (customer.currentBalance || 0) > 0
                      ? "text-rose-600 dark:text-rose-400"
                      : "text-emerald-600 dark:text-emerald-400"
                  }`}
                >
                  {formatCurrency(customer.currentBalance || 0)}
                </div>
                {(customer.currentBalance || 0) > 0 ? (
                  <span className="text-[10px] font-semibold uppercase text-rose-500">Deuda</span>
                ) : null}
              </DataGridCell>
              <DataGridCell align="center">
                <Badge
                  variant={customer.isActive ? "success" : "secondary"}
                  className={customer.isActive ? "border-transparent bg-emerald-100 text-emerald-700" : "bg-slate-100 text-slate-500"}
                >
                  {customer.isActive ? "Activo" : "Inactivo"}
                </Badge>
              </DataGridCell>
              <DataGridActionCell>
                <button
                  onClick={() => onAccountClick(customer)}
                  title="Ver cuenta corriente"
                  className="inline-flex h-8 w-8 items-center justify-center rounded-lg border border-slate-200 bg-white text-slate-600 shadow-sm transition-colors hover:bg-slate-50 hover:text-indigo-600 dark:border-slate-800 dark:bg-slate-950 dark:text-slate-400 dark:hover:bg-slate-900"
                >
                  <Wallet className="h-4 w-4" />
                </button>
                <button
                  onClick={() => onEdit(customer)}
                  title="Editar cliente"
                  className="inline-flex h-8 w-8 items-center justify-center rounded-lg border border-slate-200 bg-white text-slate-600 shadow-sm transition-colors hover:bg-slate-50 hover:text-indigo-600 dark:border-slate-800 dark:bg-slate-950 dark:text-slate-400 dark:hover:bg-slate-900"
                >
                  <Pencil className="h-4 w-4" />
                </button>
                <button
                  onClick={() => onToggleStatus(customer)}
                  title={customer.isActive ? "Desactivar" : "Activar"}
                  className={`inline-flex h-8 w-8 items-center justify-center rounded-lg border shadow-sm transition-colors ${
                    customer.isActive
                      ? "border-slate-200 bg-white text-slate-400 hover:border-rose-200 hover:bg-rose-50 hover:text-rose-600 dark:border-slate-800 dark:bg-slate-950"
                      : "border-emerald-200 bg-emerald-50 text-emerald-600 hover:bg-emerald-100"
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
