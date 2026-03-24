import { useState } from "react";
import { AlertTriangle, CalendarClock, ShieldAlert, User, Wallet } from "lucide-react";
import { Link } from "react-router-dom";
import { formatCurrency, formatDate } from "../lib/financeUtils";

function StatusBadge({ item }) {
  if (item.urgencyStatus === "Urgente") {
    return (
      <span className="inline-flex items-center gap-1 rounded-full bg-rose-50 dark:bg-rose-900/20 px-2.5 py-1 text-[10px] font-semibold uppercase tracking-wider text-rose-600 dark:text-rose-300">
        <AlertTriangle className="w-3 h-3" />
        Urgente
      </span>
    );
  }

  if (item.collectionStatus === "Parcial") {
    return (
      <span className="inline-flex items-center rounded-full bg-amber-50 dark:bg-amber-900/20 px-2.5 py-1 text-[10px] font-semibold uppercase tracking-wider text-amber-600 dark:text-amber-300">
        Parcial
      </span>
    );
  }

  return (
    <span className="inline-flex items-center rounded-full bg-slate-100 dark:bg-slate-800 px-2.5 py-1 text-[10px] font-semibold uppercase tracking-wider text-slate-500">
      Pendiente
    </span>
  );
}

function BlockTags({ item }) {
  return (
    <div className="flex flex-wrap gap-2">
      {item.blocksOperational && (
        <span className="inline-flex items-center gap-1 rounded-full bg-slate-100 dark:bg-slate-800 px-2.5 py-1 text-[10px] font-semibold uppercase tracking-wider text-slate-500">
          Bloquea operativo
        </span>
      )}
      {item.blocksVoucher && (
        <span className="inline-flex items-center gap-1 rounded-full bg-indigo-50 dark:bg-indigo-900/20 px-2.5 py-1 text-[10px] font-semibold uppercase tracking-wider text-indigo-600 dark:text-indigo-300">
          <ShieldAlert className="w-3 h-3" />
          Bloquea voucher
        </span>
      )}
    </div>
  );
}

export function CollectionsTab({ items, onPay }) {
  const [visibleCount, setVisibleCount] = useState(25);
  const visibleItems = items.slice(0, visibleCount);

  return (
    <div className="space-y-6">
      <div className="hidden md:block overflow-hidden rounded-xl border border-slate-200 dark:border-slate-800 bg-white dark:bg-slate-900 shadow-sm">
        <table className="w-full text-left border-collapse">
          <thead>
            <tr className="bg-slate-50 dark:bg-slate-800/50 border-b border-slate-200 dark:border-slate-800">
              <th className="px-6 py-4 text-[11px] uppercase tracking-wider text-slate-500 font-semibold">Reserva / Cliente</th>
              <th className="px-6 py-4 text-[11px] uppercase tracking-wider text-slate-500 font-semibold">Salida / Responsable</th>
              <th className="px-6 py-4 text-[11px] uppercase tracking-wider text-slate-500 font-semibold text-right">Venta total</th>
              <th className="px-6 py-4 text-[11px] uppercase tracking-wider text-slate-500 font-semibold text-right">Cobrado</th>
              <th className="px-6 py-4 text-[11px] uppercase tracking-wider text-slate-500 font-semibold text-right">Saldo</th>
              <th className="px-6 py-4 text-[11px] uppercase tracking-wider text-slate-500 font-semibold">Estado</th>
              <th className="px-6 py-4 text-[11px] uppercase tracking-wider text-slate-500 font-semibold text-right">Accion</th>
            </tr>
          </thead>
          <tbody className="divide-y divide-slate-100 dark:divide-slate-800">
            {visibleItems.map((item) => (
              <tr key={item.reservaPublicId || item.reservaId} className="group hover:bg-slate-50 dark:hover:bg-slate-800/40 transition-colors">
                <td className="px-6 py-4">
                  <div className="flex flex-col">
                    <Link
                      to={`/reservas/${item.reservaPublicId || item.reservaId}`}
                      className="font-bold text-slate-900 dark:text-white hover:text-indigo-600 dark:hover:text-indigo-400 transition-colors"
                    >
                      {item.numeroReserva}
                    </Link>
                    <span className="text-sm text-slate-500 dark:text-slate-400 flex items-center gap-1.5 mt-0.5">
                      <User className="w-3 h-3 opacity-40" />
                      {item.customerName || "Consumidor Final"}
                    </span>
                  </div>
                </td>
                <td className="px-6 py-4">
                  <div className="space-y-2">
                    <div className="text-sm text-slate-600 dark:text-slate-400 flex items-center gap-1.5">
                      <CalendarClock className="w-3.5 h-3.5 opacity-50" />
                      {formatDate(item.startDate)}
                    </div>
                    <div className="text-xs text-slate-500 dark:text-slate-400">
                      {item.responsibleUserName || "Sin responsable asignado"}
                    </div>
                  </div>
                </td>
                <td className="px-6 py-4 text-right text-sm font-medium text-slate-600 dark:text-slate-400">
                  {formatCurrency(item.totalSale)}
                </td>
                <td className="px-6 py-4 text-right text-sm font-medium text-emerald-600 dark:text-emerald-400">
                  {formatCurrency(item.totalPaid)}
                </td>
                <td className="px-6 py-4 text-right">
                  <div className="text-base font-bold text-rose-600 dark:text-rose-400">
                    {formatCurrency(item.balance)}
                  </div>
                </td>
                <td className="px-6 py-4">
                  <div className="space-y-2">
                    <StatusBadge item={item} />
                    <BlockTags item={item} />
                  </div>
                </td>
                <td className="px-6 py-4 text-right">
                  <button
                    type="button"
                    onClick={() => onPay(item)}
                    className="inline-flex items-center gap-2 px-4 py-2 rounded-lg bg-slate-900 dark:bg-white text-white dark:text-slate-900 text-sm font-medium hover:bg-slate-800 transition-colors shadow-sm"
                  >
                    <Wallet className="w-4 h-4" />
                    Registrar pago
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>

        {items.length > visibleCount && (
          <div className="p-4 border-t border-slate-100 dark:border-slate-800 text-center">
            <button
              type="button"
              onClick={() => setVisibleCount((current) => current + 25)}
              className="px-6 py-2 text-sm font-medium text-slate-600 dark:text-slate-400 hover:text-slate-900 dark:hover:text-white transition-colors"
            >
              Cargar mas reservas ({items.length - visibleCount} restantes)
            </button>
          </div>
        )}
      </div>

      <div className="md:hidden space-y-4">
        {visibleItems.map((item) => (
          <div key={item.reservaPublicId || item.reservaId} className="bg-white dark:bg-slate-900 rounded-2xl p-5 border border-slate-200 dark:border-slate-800 shadow-sm">
            <div className="flex justify-between items-start gap-4">
              <div>
                <div className="text-[10px] font-bold uppercase tracking-widest text-indigo-600 dark:text-indigo-400 mb-1">
                  {item.numeroReserva}
                </div>
                <div className="font-semibold text-slate-900 dark:text-white">{item.customerName}</div>
                <div className="text-xs text-slate-500 dark:text-slate-400 mt-1">
                  Salida {formatDate(item.startDate)}
                </div>
              </div>
              <StatusBadge item={item} />
            </div>

            <div className="grid grid-cols-3 gap-3 py-4 border-y border-slate-100 dark:border-slate-800 mt-4">
              <div>
                <div className="text-[10px] text-slate-400 uppercase font-bold tracking-tight">Venta</div>
                <div className="text-sm font-semibold text-slate-700 dark:text-slate-200">{formatCurrency(item.totalSale)}</div>
              </div>
              <div>
                <div className="text-[10px] text-slate-400 uppercase font-bold tracking-tight">Cobrado</div>
                <div className="text-sm font-semibold text-emerald-600">{formatCurrency(item.totalPaid)}</div>
              </div>
              <div>
                <div className="text-[10px] text-slate-400 uppercase font-bold tracking-tight">Saldo</div>
                <div className="text-sm font-semibold text-rose-600">{formatCurrency(item.balance)}</div>
              </div>
            </div>

            <div className="mt-4 space-y-3">
              <BlockTags item={item} />
              <button
                type="button"
                onClick={() => onPay(item)}
                className="w-full flex items-center justify-center gap-3 py-3 rounded-xl bg-slate-900 dark:bg-white text-white dark:text-slate-900 font-medium hover:bg-slate-800 transition-colors shadow-sm"
              >
                <Wallet className="w-5 h-5" />
                Registrar pago
              </button>
            </div>
          </div>
        ))}
      </div>

      {items.length === 0 && (
        <div className="py-20 text-center bg-slate-50/50 dark:bg-slate-800/20 rounded-3xl border-2 border-dashed border-slate-200 dark:border-slate-800">
          <div className="w-16 h-16 bg-white dark:bg-slate-900 rounded-full flex items-center justify-center mx-auto mb-4 shadow-sm border border-slate-100 dark:border-slate-800">
            <Wallet className="w-8 h-8 text-slate-300" />
          </div>
          <h3 className="text-lg font-bold text-slate-900 dark:text-white mb-1">Todo al dia</h3>
          <p className="text-slate-500 dark:text-slate-400 text-sm">
            No hay reservas con deuda comercial pendiente.
          </p>
        </div>
      )}
    </div>
  );
}
