import { AlertTriangle, CalendarClock, ShieldAlert, User, Wallet } from "lucide-react";
import { Link } from "react-router-dom";
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
import { ListEmptyState } from "../../../components/ui/ListEmptyState";
import { MobileRecordCard, MobileRecordList } from "../../../components/ui/MobileRecordCard";
import { formatCurrency, formatDate } from "../lib/financeUtils";

function StatusBadge({ item }) {
  if (item.urgencyStatus === "Urgente") {
    return (
      <span className="inline-flex items-center gap-1 rounded-full bg-rose-50 px-2.5 py-1 text-[10px] font-semibold uppercase tracking-wider text-rose-600 dark:bg-rose-900/20 dark:text-rose-300">
        <AlertTriangle className="w-3 h-3" />
        Urgente
      </span>
    );
  }

  if (item.collectionStatus === "Parcial") {
    return (
      <span className="inline-flex items-center rounded-full bg-amber-50 px-2.5 py-1 text-[10px] font-semibold uppercase tracking-wider text-amber-600 dark:bg-amber-900/20 dark:text-amber-300">
        Parcial
      </span>
    );
  }

  return (
    <span className="inline-flex items-center rounded-full bg-slate-100 px-2.5 py-1 text-[10px] font-semibold uppercase tracking-wider text-slate-500 dark:bg-slate-800">
      Pendiente
    </span>
  );
}

function BlockTags({ item }) {
  return (
    <div className="flex flex-wrap gap-2">
      {item.blocksOperational ? (
        <span className="inline-flex items-center gap-1 rounded-full bg-slate-100 px-2.5 py-1 text-[10px] font-semibold uppercase tracking-wider text-slate-500 dark:bg-slate-800">
          Bloquea operativo
        </span>
      ) : null}
      {item.blocksVoucher ? (
        <span className="inline-flex items-center gap-1 rounded-full bg-indigo-50 px-2.5 py-1 text-[10px] font-semibold uppercase tracking-wider text-indigo-600 dark:bg-indigo-900/20 dark:text-indigo-300">
          <ShieldAlert className="w-3 h-3" />
          Bloquea voucher
        </span>
      ) : null}
    </div>
  );
}

export function CollectionsTab({ items, onPay }) {
  return (
    <div className="space-y-6">
      <DataGrid minWidth="980px">
        <DataGridHeader>
          <DataGridHeaderRow>
            <DataGridHeaderCell>Reserva / cliente</DataGridHeaderCell>
            <DataGridHeaderCell>Salida / responsable</DataGridHeaderCell>
            <DataGridHeaderCell align="right">Venta total</DataGridHeaderCell>
            <DataGridHeaderCell align="right">Cobrado</DataGridHeaderCell>
            <DataGridHeaderCell align="right">Saldo</DataGridHeaderCell>
            <DataGridHeaderCell>Estado</DataGridHeaderCell>
            <DataGridHeaderCell align="right">Accion</DataGridHeaderCell>
          </DataGridHeaderRow>
        </DataGridHeader>
        <DataGridBody>
          {items.length === 0 ? (
            <DataGridEmptyState
              colSpan={7}
              icon={Wallet}
              title="Todo al dia"
              description="No hay reservas con deuda comercial pendiente."
            />
          ) : (
            items.map((item) => (
              <DataGridRow key={item.reservaPublicId}>
                <DataGridCell>
                  <div className="flex flex-col">
                    <Link
                      to={`/reservas/${item.reservaPublicId}`}
                      className="font-bold text-slate-900 transition-colors hover:text-indigo-600 dark:text-white dark:hover:text-indigo-400"
                    >
                      {item.numeroReserva}
                    </Link>
                    <span className="mt-0.5 flex items-center gap-1.5 text-sm text-slate-500 dark:text-slate-400">
                      <User className="w-3 h-3 opacity-40" />
                      {item.customerName || "Consumidor Final"}
                    </span>
                  </div>
                </DataGridCell>
                <DataGridCell>
                  <div className="space-y-2">
                    <div className="flex items-center gap-1.5 text-sm text-slate-600 dark:text-slate-400">
                      <CalendarClock className="w-3.5 h-3.5 opacity-50" />
                      {formatDate(item.startDate)}
                    </div>
                    <div className="text-xs text-slate-500 dark:text-slate-400">
                      {item.responsibleUserName || "Sin responsable asignado"}
                    </div>
                  </div>
                </DataGridCell>
                <DataGridCell align="right" className="font-medium">
                  {formatCurrency(item.totalSale)}
                </DataGridCell>
                <DataGridCell align="right" className="font-medium text-emerald-600 dark:text-emerald-400">
                  {formatCurrency(item.totalPaid)}
                </DataGridCell>
                <DataGridCell align="right">
                  <div className="text-base font-bold text-rose-600 dark:text-rose-400">{formatCurrency(item.balance)}</div>
                </DataGridCell>
                <DataGridCell>
                  <div className="space-y-2">
                    <StatusBadge item={item} />
                    <BlockTags item={item} />
                  </div>
                </DataGridCell>
                <DataGridActionCell>
                  <button
                    type="button"
                    onClick={() => onPay(item)}
                    className="inline-flex items-center gap-2 rounded-lg bg-slate-900 px-4 py-2 text-sm font-medium text-white shadow-sm transition-colors hover:bg-slate-800 dark:bg-white dark:text-slate-900"
                  >
                    <Wallet className="w-4 h-4" />
                    Registrar pago
                  </button>
                </DataGridActionCell>
              </DataGridRow>
            ))
          )}
        </DataGridBody>
      </DataGrid>

      {items.length === 0 ? (
        <ListEmptyState
          icon={Wallet}
          title="Todo al dia"
          description="No hay reservas con deuda comercial pendiente."
          className="md:hidden rounded-xl border border-dashed border-slate-200 bg-slate-50/50 dark:border-slate-800 dark:bg-slate-800/20"
        />
      ) : (
        <MobileRecordList>
          {items.map((item) => (
            <MobileRecordCard
              key={item.reservaPublicId}
              statusSlot={<StatusBadge item={item} />}
              title={item.customerName || "Consumidor Final"}
              subtitle={`Reserva ${item.numeroReserva}`}
              meta={
                <>
                  <div className="text-xs text-slate-500 dark:text-slate-400">Salida {formatDate(item.startDate)}</div>
                  <div className="text-xs text-slate-500 dark:text-slate-400">{item.responsibleUserName || "Sin responsable asignado"}</div>
                  <BlockTags item={item} />
                </>
              }
              footer={
                <div className="grid grid-cols-3 gap-3 text-xs">
                  <div>
                    <div className="font-bold uppercase tracking-tight text-slate-400">Venta</div>
                    <div className="mt-1 text-sm font-semibold text-slate-700 dark:text-slate-200">{formatCurrency(item.totalSale)}</div>
                  </div>
                  <div>
                    <div className="font-bold uppercase tracking-tight text-slate-400">Cobrado</div>
                    <div className="mt-1 text-sm font-semibold text-emerald-600">{formatCurrency(item.totalPaid)}</div>
                  </div>
                  <div>
                    <div className="font-bold uppercase tracking-tight text-slate-400">Saldo</div>
                    <div className="mt-1 text-sm font-semibold text-rose-600">{formatCurrency(item.balance)}</div>
                  </div>
                </div>
              }
              footerActions={
                <button
                  type="button"
                  onClick={() => onPay(item)}
                  className="flex items-center justify-center gap-2 rounded-lg bg-slate-900 px-3 py-2 text-xs font-medium text-white transition-colors hover:bg-slate-800 dark:bg-white dark:text-slate-900"
                >
                  <Wallet className="w-4 h-4" />
                  Pagar
                </button>
              }
            />
          ))}
        </MobileRecordList>
      )}
    </div>
  );
}
