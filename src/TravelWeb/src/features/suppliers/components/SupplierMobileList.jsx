import React from "react";
import { Building2, Mail, Pencil, Power, Wallet } from "lucide-react";
import { StatusChip } from "../../../components/ui/badge";
import { Button } from "../../../components/ui/button";
import { ListEmptyState } from "../../../components/ui/ListEmptyState";
import { MobileRecordCard, MobileRecordList } from "../../../components/ui/MobileRecordCard";
import { getPublicId } from "../../../lib/publicIds";
import { formatCurrency } from "../../../lib/utils";
import { supplierBalanceLines } from "../lib/supplierBalanceView";

export function SupplierMobileList({ suppliers, onEdit, onToggleStatus, onAccountClick }) {
  const getInitials = (name) => {
    return name?.split(" ").map((part) => part[0]).join("").toUpperCase().slice(0, 2) || "PV";
  };

  // Iniciales en círculo neutro (firmado por Gastón 17/08, mismo criterio que
  // Clientes): los colores quedan reservados para significados, no decoración.
  const avatarTone = (isActive) =>
    isActive
      ? "bg-slate-100 text-slate-700 dark:bg-slate-800 dark:text-slate-200"
      : "bg-slate-200 text-slate-500 dark:bg-slate-800 dark:text-slate-400";

  if (suppliers.length === 0) {
    return (
      <ListEmptyState
        title="No se encontraron operadores"
        description="Ajustá los filtros o creá un operador nuevo."
        className="md:hidden rounded-[14px] border border-dashed border-slate-300 dark:border-slate-700"
      />
    );
  }

  return (
    <MobileRecordList>
      {suppliers.map((supplier) => {
        const balanceLines = supplierBalanceLines(supplier);
        return (
        <MobileRecordCard
          key={getPublicId(supplier)}
          inactive={!supplier.isActive}
          accentSlot={
            <div
              className={`flex h-10 w-10 items-center justify-center rounded-full text-xs font-bold shadow-sm ${avatarTone(supplier.isActive)}`}
            >
              {getInitials(supplier.name)}
            </div>
          }
          statusSlot={
            <StatusChip tone={supplier.isActive ? "verde" : "neutro"}>
              {supplier.isActive ? "Activo" : "Inactivo"}
            </StatusChip>
          }
          title={supplier.name}
          subtitle={supplier.taxId || "Sin CUIT"}
          meta={
            <>
              {/* Hallazgo #9 del barrido (2026-07-24): unificado a "—" (em dash), mismo símbolo
                  que ya usa el footer de esta tarjeta para "sin dato" (más abajo) — antes esta
                  tarjeta mezclaba dos símbolos distintos para lo mismo. */}
              <div className="flex items-center gap-2 text-slate-600 dark:text-slate-400">
                <Building2 className="h-3.5 w-3.5 opacity-70" />
                <span className="truncate">{supplier.contactName || "—"}</span>
              </div>
              <div className="flex items-center gap-2 text-slate-600 dark:text-slate-400">
                <Mail className="h-3.5 w-3.5 opacity-70" />
                <span className="truncate">{supplier.email || "—"}</span>
              </div>
            </>
          }
          footer={
            supplier.amountsVisible === false ? <span className="text-slate-400">—</span> : (
              <div className="space-y-0.5 font-mono font-medium">
                {balanceLines.length === 0 ? <span className="text-slate-500">Sin saldo</span> : balanceLines.map((line) => (
                  <div key={line.currency} className={line.balance > 0
                    ? "text-rose-600 dark:text-rose-400"
                    : "text-emerald-600 dark:text-emerald-400"}
                  >
                    {formatCurrency(line.balance, line.currency)}
                  </div>
                ))}
              </div>
            )
          }
          footerActions={
            <>
              <Button
                type="button"
                variant="outline"
                size="sm"
                onClick={() => onAccountClick(supplier)}
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
                aria-label={supplier.isActive ? "Desactivar operador" : "Activar operador"}
                className={`h-8 w-8 p-0 ${
                  supplier.isActive
                    ? "text-slate-400 hover:border-rose-200 hover:bg-rose-50 hover:text-rose-600 dark:hover:bg-rose-950/30"
                    : "border-emerald-300 bg-emerald-50 text-emerald-700 hover:bg-emerald-100 dark:border-emerald-800 dark:bg-emerald-900/20 dark:text-emerald-300"
                }`}
              >
                <Power className="h-4 w-4" aria-hidden="true" />
              </Button>
            </>
          }
        />
        );
      })}
    </MobileRecordList>
  );
}
