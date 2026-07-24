import React from "react";
import { Mail, Pencil, Phone, Wallet } from "lucide-react";
import { Badge } from "../../../components/ui/badge";
import { ListEmptyState } from "../../../components/ui/ListEmptyState";
import { MobileRecordCard, MobileRecordList } from "../../../components/ui/MobileRecordCard";
import { getPublicId } from "../../../lib/publicIds";
import { formatCurrency } from "../../../lib/utils";
import { resolverBadgeSaldoCliente } from "../lib/balanceCompositionLogic";

export function CustomerMobileList({ customers, onEdit, onAccountClick }) {
  const getInitials = (name) => {
    return name?.split(" ").map((part) => part[0]).join("").toUpperCase().slice(0, 2) || "??";
  };

  if (customers.length === 0) {
    return (
      <ListEmptyState
        title="No se encontraron clientes"
        description="Ajusta la busqueda o crea un nuevo cliente."
        className="md:hidden rounded-xl border border-dashed border-slate-300 dark:border-slate-700"
      />
    );
  }

  return (
    <MobileRecordList>
      {customers.map((customer) => {
        const saldoBadge = resolverBadgeSaldoCliente(
          customer.balancesByCurrency,
          customer.unappliedCreditsByCurrency
        );
        return (
        <MobileRecordCard
          key={getPublicId(customer)}
          inactive={!customer.isActive}
          accentSlot={
            <div
              className={`flex h-10 w-10 items-center justify-center rounded-full text-sm font-bold shadow-sm ${
                customer.isActive
                  ? "bg-indigo-100 text-indigo-700 dark:bg-indigo-900/30 dark:text-indigo-300"
                  : "bg-slate-200 text-slate-500 dark:bg-slate-800 dark:text-slate-400"
              }`}
            >
              {getInitials(customer.fullName)}
            </div>
          }
          statusSlot={
            <Badge
              variant={customer.isActive ? "success" : "secondary"}
              className={
                customer.isActive
                  ? "border-transparent bg-emerald-100 px-1.5 py-0.5 text-[10px] text-emerald-700"
                  : "bg-slate-100 px-1.5 py-0.5 text-[10px] text-slate-500"
              }
            >
              {customer.isActive ? "Activo" : "Inactivo"}
            </Badge>
          }
          title={customer.fullName}
          subtitle={customer.taxId || customer.documentNumber || "S/D"}
          meta={
            <>
              <div className="flex items-center gap-2 text-slate-600 dark:text-slate-400">
                <Mail className="h-3.5 w-3.5 opacity-70" />
                <span className="truncate">{customer.email || "-"}</span>
              </div>
              <div className="flex items-center gap-2 text-slate-600 dark:text-slate-400">
                <Phone className="h-3.5 w-3.5 opacity-70" />
                <span>{customer.phone || "-"}</span>
              </div>
            </>
          }
          footer={
            <div className="font-mono font-medium">
              {saldoBadge.estado === "debe" && (
                <>
                  {saldoBadge.montos.map((monto) => (
                    <div key={monto.currency} className="text-rose-600 dark:text-rose-400">
                      {formatCurrency(monto.amount, monto.currency)}
                    </div>
                  ))}
                  <span className="text-[10px] font-semibold uppercase text-rose-500">Deuda</span>
                </>
              )}
              {saldoBadge.estado === "aFavor" && (
                <>
                  {saldoBadge.montos.map((monto) => (
                    <div key={monto.currency} className="text-emerald-600 dark:text-emerald-400">
                      {formatCurrency(monto.amount, monto.currency)}
                    </div>
                  ))}
                  <span className="text-[10px] font-semibold uppercase text-emerald-500">A favor</span>
                </>
              )}
              {saldoBadge.estado === "alDia" && (
                <span className="text-emerald-600 dark:text-emerald-400">Al día</span>
              )}
            </div>
          }
          footerActions={
            <>
              <button
                onClick={() => onAccountClick(customer)}
                className="flex h-8 w-8 items-center justify-center rounded-lg border border-slate-200 bg-white text-slate-600 transition-colors hover:bg-indigo-50 hover:text-indigo-600 dark:border-slate-700 dark:bg-slate-800 dark:text-slate-400"
              >
                <Wallet className="h-4 w-4" />
              </button>
              <button
                onClick={() => onEdit(customer)}
                className="flex h-8 w-8 items-center justify-center rounded-lg border border-slate-200 bg-white text-slate-600 transition-colors hover:bg-indigo-50 hover:text-indigo-600 dark:border-slate-700 dark:bg-slate-800 dark:text-slate-400"
              >
                <Pencil className="h-4 w-4" />
              </button>
            </>
          }
        />
        );
      })}
    </MobileRecordList>
  );
}
