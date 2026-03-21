import { useState } from "react";
import { AlertCircle, CheckCircle2, FilePlus, ShieldAlert, User, Receipt } from "lucide-react";

const currency = new Intl.NumberFormat("es-AR", {
  style: "currency",
  currency: "ARS",
  minimumFractionDigits: 2,
});

const getStatusMeta = (reserva) => {
  switch (reserva.afipStatus) {
    case "enabled":
      return {
        label: "Habilitada",
        caption: "Cancelada económicamente",
        className: "text-emerald-600 dark:text-emerald-400",
      };
    case "override":
      return {
        label: "Bloqueada por deuda",
        caption: "El agente puede emitir por excepción",
        className: "text-amber-600 dark:text-amber-400",
      };
    case "blocked":
      return {
        label: "Bloqueada por deuda",
        caption: reserva.economicBlockReason || "No puede emitirse aún",
        className: "text-rose-600 dark:text-rose-400",
      };
    default:
      return {
        label: "Sin pendiente",
        caption: "No queda saldo fiscal por emitir",
        className: "text-slate-400",
      };
  }
};

export function InvoicingTab({ reservas, onInvoice }) {
  const [visibleCount, setVisibleCount] = useState(25);
  const filtered = reservas.filter((reserva) => reserva.pendingAfipAmount > 0);
  const visibleReservas = filtered.slice(0, visibleCount);

  return (
    <div className="space-y-6">
      <div className="hidden md:block overflow-hidden rounded-xl border border-slate-200 dark:border-slate-800 bg-white dark:bg-slate-900 shadow-sm">
        <table className="w-full text-left border-collapse">
          <thead>
            <tr className="bg-slate-50 dark:bg-slate-800/50 border-b border-slate-200 dark:border-slate-800">
              <th className="px-6 py-4 text-[11px] uppercase tracking-wider text-slate-500 font-semibold">Reserva / Cliente</th>
              <th className="px-6 py-4 text-[11px] uppercase tracking-wider text-slate-500 font-semibold text-right">Venta total</th>
              <th className="px-6 py-4 text-[11px] uppercase tracking-wider text-slate-500 font-semibold text-right">Ya facturado</th>
              <th className="px-6 py-4 text-[11px] uppercase tracking-wider text-slate-500 font-semibold text-right">Pendiente AFIP</th>
              <th className="px-6 py-4 text-[11px] uppercase tracking-wider text-slate-500 font-semibold">Estado</th>
              <th className="px-6 py-4 text-[11px] uppercase tracking-wider text-slate-500 font-semibold text-right">Acción</th>
            </tr>
          </thead>
          <tbody className="divide-y divide-slate-100 dark:divide-slate-800">
            {visibleReservas.map((reserva) => {
              const statusMeta = getStatusMeta(reserva);
              const canOpen = reserva.afipStatus === "enabled" || reserva.afipStatus === "override";

              return (
                <tr key={reserva.id} className="group hover:bg-slate-50 dark:hover:bg-slate-800/40 transition-colors">
                  <td className="px-6 py-4">
                    <div className="flex flex-col">
                      <a href={`/reservas/${reserva.id}`} className="font-bold text-slate-900 dark:text-white hover:text-indigo-600 dark:hover:text-indigo-400 transition-colors uppercase tracking-tight">
                        {reserva.numeroReserva}
                      </a>
                      <span className="text-sm text-slate-500 dark:text-slate-400 flex items-center gap-1.5 mt-0.5">
                        <User className="w-3 h-3 opacity-40" />
                        {reserva.customerName || "Consumidor Final"}
                      </span>
                    </div>
                  </td>
                  <td className="px-6 py-4 text-right text-sm font-medium text-slate-600 dark:text-slate-400">
                    {currency.format(reserva.totalSaleAmount || 0)}
                  </td>
                  <td className="px-6 py-4 text-right text-sm font-medium text-slate-400">
                    {currency.format(reserva.computedInvoiced || 0)}
                  </td>
                  <td className="px-6 py-4 text-right">
                    <div className="text-base font-bold text-indigo-600 dark:text-indigo-400">
                      {currency.format(reserva.pendingAfipAmount || 0)}
                    </div>
                  </td>
                  <td className="px-6 py-4">
                    <div className={`text-sm font-bold ${statusMeta.className}`}>{statusMeta.label}</div>
                    <div className="text-xs text-slate-500 dark:text-slate-400 mt-1">{statusMeta.caption}</div>
                  </td>
                  <td className="px-6 py-4 text-right">
                    <button
                      type="button"
                      onClick={() => onInvoice(reserva)}
                      disabled={!canOpen}
                      className={`inline-flex items-center gap-2 px-4 py-2 rounded-lg text-sm font-medium transition-colors shadow-sm ${
                        canOpen
                          ? reserva.afipStatus === "override"
                            ? "bg-amber-500 text-white hover:bg-amber-600"
                            : "bg-indigo-600 text-white hover:bg-indigo-700"
                          : "bg-slate-100 text-slate-400 cursor-not-allowed"
                      }`}
                    >
                      <Receipt className="w-4 h-4" />
                      {reserva.afipStatus === "override" ? "Emitir por excepción" : "Emitir AFIP"}
                    </button>
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>

      <div className="md:hidden space-y-4">
        {visibleReservas.map((reserva) => {
          const statusMeta = getStatusMeta(reserva);
          const canOpen = reserva.afipStatus === "enabled" || reserva.afipStatus === "override";

          return (
            <div key={reserva.id} className="bg-white dark:bg-slate-900 rounded-2xl p-5 border border-slate-200 dark:border-slate-800 shadow-sm">
              <div className="flex justify-between items-start mb-4">
                <div>
                  <div className="text-[10px] font-bold uppercase tracking-widest text-slate-400 mb-1">{reserva.numeroReserva}</div>
                  <h3 className="font-bold text-slate-900 dark:text-white line-clamp-1">
                    {reserva.customerName || "Consumidor Final"}
                  </h3>
                </div>
                <div className={statusMeta.className}>
                  {reserva.afipStatus === "override" ? <ShieldAlert className="w-5 h-5" /> : <AlertCircle className="w-5 h-5" />}
                </div>
              </div>

              <div className="space-y-3 py-3 border-t border-slate-50 dark:border-slate-800/50 mb-4">
                <div className="flex justify-between items-center text-sm">
                  <span className="text-slate-500">Venta total:</span>
                  <span className="font-semibold text-slate-900 dark:text-white">{currency.format(reserva.totalSaleAmount || 0)}</span>
                </div>
                <div className="flex justify-between items-center text-sm">
                  <span className="text-slate-500">Ya facturado:</span>
                  <span className="font-semibold text-slate-500">{currency.format(reserva.computedInvoiced || 0)}</span>
                </div>
                <div className="flex justify-between items-center text-sm">
                  <span className="text-slate-500">Pendiente AFIP:</span>
                  <span className="font-semibold text-indigo-600 dark:text-indigo-400">{currency.format(reserva.pendingAfipAmount || 0)}</span>
                </div>
                <div className={`text-xs font-bold ${statusMeta.className}`}>{statusMeta.label}</div>
                <div className="text-xs text-slate-500 dark:text-slate-400">{statusMeta.caption}</div>
              </div>

              <button
                type="button"
                onClick={() => onInvoice(reserva)}
                disabled={!canOpen}
                className={`w-full flex items-center justify-center gap-3 py-3.5 rounded-xl text-sm font-medium transition-colors shadow-sm ${
                  canOpen
                    ? reserva.afipStatus === "override"
                      ? "bg-amber-500 text-white hover:bg-amber-600"
                      : "bg-indigo-600 text-white hover:bg-indigo-700"
                    : "bg-slate-100 text-slate-400 cursor-not-allowed"
                }`}
              >
                <FilePlus className="w-5 h-5" />
                {reserva.afipStatus === "override" ? "Emitir por excepción" : "Emitir comprobante"}
              </button>
            </div>
          );
        })}
      </div>

      {filtered.length > visibleCount && (
        <div className="p-4 text-center">
          <button
            type="button"
            onClick={() => setVisibleCount((current) => current + 25)}
            className="px-6 py-2 text-sm font-medium text-slate-600 dark:text-slate-400 hover:text-slate-900 dark:hover:text-white transition-colors"
          >
            Cargar más reservas ({filtered.length - visibleCount} restantes)
          </button>
        </div>
      )}

      {filtered.length === 0 && (
        <div className="py-20 text-center bg-slate-50/50 dark:bg-slate-800/20 rounded-3xl border-2 border-dashed border-slate-200 dark:border-slate-800">
          <div className="w-16 h-16 bg-white dark:bg-slate-900 rounded-full flex items-center justify-center mx-auto mb-4 shadow-sm border border-slate-100 dark:border-slate-800">
            <CheckCircle2 className="w-8 h-8 text-emerald-400" />
          </div>
          <h3 className="text-lg font-bold text-slate-900 dark:text-white mb-1">AFIP al día</h3>
          <p className="text-slate-500 dark:text-slate-400 text-sm">
            No quedan reservas con saldo fiscal pendiente de emitir.
          </p>
        </div>
      )}
    </div>
  );
}
