import React from "react";
import { Mail, Pencil, Phone, Power, Wallet } from "lucide-react";
import { Button } from "../../../components/ui/button";
import { StatusChip } from "../../../components/ui/badge";
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
import { resolverBadgeSaldoCliente } from "../lib/balanceCompositionLogic";

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
          customers.map((customer) => {
            const saldoBadge = resolverBadgeSaldoCliente(
              customer.balancesByCurrency,
              customer.unappliedCreditsByCurrency
            );
            return (
            <DataGridRow key={getPublicId(customer)} inactive={!customer.isActive}>
              <DataGridCell className="text-slate-900 dark:text-slate-100">
                <div className="flex items-center gap-3">
                  {/* Iniciales del cliente: circulo neutro (gris dato / tinta), sin color
                      de accion — el azul boleto queda reservado solo para botones (B.1). */}
                  <div
                    className={`flex h-10 w-10 items-center justify-center rounded-full text-sm font-bold shadow-sm ${
                      customer.isActive
                        ? "bg-slate-100 text-slate-700 dark:bg-slate-800 dark:text-slate-200"
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
                {saldoBadge.estado === "debe" && saldoBadge.montos.map((monto) => (
                  <div key={monto.currency} className="font-mono font-medium text-rose-600 dark:text-rose-400">
                    {formatCurrency(monto.amount, monto.currency)}
                  </div>
                ))}
                {saldoBadge.estado === "aFavor" && saldoBadge.montos.map((monto) => (
                  <div key={monto.currency} className="font-mono font-medium text-emerald-600 dark:text-emerald-400">
                    {formatCurrency(monto.amount, monto.currency)}
                  </div>
                ))}
                {saldoBadge.estado === "alDia" && (
                  <div className="font-mono font-medium text-emerald-600 dark:text-emerald-400">Al día</div>
                )}
                {saldoBadge.estado === "debe" && (
                  <StatusChip tone="rojo" className="mt-1">Deuda</StatusChip>
                )}
                {saldoBadge.estado === "aFavor" && (
                  <StatusChip tone="verde" className="mt-1">A favor</StatusChip>
                )}
              </DataGridCell>
              <DataGridCell align="center">
                <StatusChip tone={customer.isActive ? "verde" : "neutro"}>
                  {customer.isActive ? "Activo" : "Inactivo"}
                </StatusChip>
              </DataGridCell>
              {/* Tres acciones por fila (ver cuenta / editar / activar-desactivar): son
                  huesos existentes, no se tocan. Lo que cambia es la piel: salen del
                  molde de boton compartido (outline 32px, B.3) en vez de un <button>
                  a mano con hover indigo suelto. aria-label nuevo (sin cambiar el
                  title visible) para que el lector de pantalla tenga nombre accesible,
                  ya que el icono solo no alcanza. */}
              <DataGridActionCell>
                <Button
                  type="button"
                  variant="outline"
                  size="sm"
                  onClick={() => onAccountClick(customer)}
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
                  onClick={() => onEdit(customer)}
                  title="Editar cliente"
                  aria-label="Editar cliente"
                  className="h-8 w-8 p-0"
                >
                  <Pencil className="h-4 w-4" aria-hidden="true" />
                </Button>
                <Button
                  type="button"
                  variant="outline"
                  size="sm"
                  onClick={() => onToggleStatus(customer)}
                  title={customer.isActive ? "Desactivar" : "Activar"}
                  aria-label={customer.isActive ? "Desactivar cliente" : "Activar cliente"}
                  className={`h-8 w-8 p-0 ${
                    customer.isActive
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
