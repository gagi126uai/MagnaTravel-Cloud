import { Link } from "react-router-dom";
import { AlertCircle, CheckCircle2, ExternalLink, Loader2, ShieldAlert } from "lucide-react";
import { formatCurrency, formatDate } from "../lib/financeUtils";
import { StatusChip } from "../../../components/ui/badge";

function SegmentedTabs({ options, value, onChange }) {
  return (
    <div className="inline-flex rounded-[10px] border border-slate-200 bg-slate-50 p-1 dark:border-slate-800 dark:bg-slate-900">
      {options.map((option) => (
        <button
          key={option.value}
          type="button"
          onClick={() => onChange(option.value)}
          className={`rounded-[10px] px-3 py-2 text-sm font-medium transition-colors ${value === option.value ? "bg-white text-slate-900 shadow-sm dark:bg-slate-800 dark:text-white" : "text-slate-500 hover:text-slate-900 dark:text-slate-400 dark:hover:text-white"}`}
        >
          {option.label}
        </button>
      ))}
    </div>
  );
}

function FilterInput({ label, value, onChange, placeholder, type = "text" }) {
  return (
    <div className="space-y-1.5">
      <label className="text-[11px] font-black uppercase tracking-[0.24em] text-slate-400">{label}</label>
      <input type={type} value={value} onChange={(event) => onChange(event.target.value)} placeholder={placeholder} className="w-full rounded-[10px] border border-slate-200 bg-white px-3 py-2.5 text-sm dark:border-slate-700 dark:bg-slate-900 dark:text-white" />
    </div>
  );
}

function Metric({ label, value, highlight = false }) {
  return (
    <div>
      <div className="text-[11px] font-semibold uppercase tracking-wider text-slate-400">{label}</div>
      {/* "highlight" resalta el importe que TODAVIA falta facturar: usamos ambar (B.1,
          "te pide algo") en vez del indigo viejo — ese color quedaba pegado al azul de
          los botones de accion y competia con ellos. */}
      <div className={`text-sm font-semibold ${highlight ? "text-amber-700 dark:text-amber-400" : "text-slate-900 dark:text-white"}`}>{value}</div>
    </div>
  );
}

/**
 * Lista de reservas que todavia no tienen la factura emitida (bandeja "Pendientes de emitir").
 * Se usa en la pestaña de Cobranzas → Pendientes de facturar (PaymentsPendingPage).
 */
export function WorkItemSection({ status, onStatusChange, items, searchTerm, onSearchTermChange, customerFilter, onCustomerFilterChange, reservationFilter, onReservationFilterChange, pagination }) {
  return (
    <div className="overflow-hidden rounded-[14px] border border-slate-200 bg-white shadow-sm dark:border-slate-800 dark:bg-slate-900">
      <div className="flex flex-col gap-4 border-b border-slate-100 px-6 py-5 dark:border-slate-800 lg:flex-row lg:items-center lg:justify-between">
        <div>
          <div className="font-semibold text-slate-900 dark:text-white">Pendientes de emitir</div>
          <div className="mt-1 text-sm text-slate-500 dark:text-slate-400">Reservas que todavia no tienen la factura emitida.</div>
        </div>
        <SegmentedTabs
          value={status}
          onChange={onStatusChange}
          options={[
            { value: "ready", label: "Listas para emitir" },
            { value: "override", label: "Requieren autorizacion" },
            { value: "blocked", label: "Bloqueadas" },
          ]}
        />
      </div>

      <div className="grid gap-3 border-b border-slate-100 bg-slate-50/70 px-6 py-5 dark:border-slate-800 dark:bg-slate-950/20 md:grid-cols-3">
        <FilterInput label="Busqueda" value={searchTerm} onChange={onSearchTermChange} placeholder="Reserva o cliente..." />
        <FilterInput label="Cliente" value={customerFilter} onChange={onCustomerFilterChange} placeholder="Nombre del cliente" />
        <FilterInput label="Reserva" value={reservationFilter} onChange={onReservationFilterChange} placeholder="Numero de reserva" />
      </div>

      {items.length === 0 ? (
        <div className="px-6 py-10 text-sm text-slate-500 dark:text-slate-400">No hay reservas pendientes de facturar.</div>
      ) : (
        <div className="divide-y divide-slate-100 dark:divide-slate-800">
          {/* Spec firmada 2026-08-06 (§4.4, P14=A): lista PASIVA — la fila entera lleva a la
              ficha de la reserva, donde emitir la factura ya vive en línea (EmitirFacturaInline,
              2026-06-13). Ya no hay botón "Emitir" acá ni ventana de facturar (CreateInvoiceModal
              murió: sin consumidores, se borró del proyecto). */}
          {items.map((item) => (
            <Link
              key={item.reservaPublicId}
              to={`/reservas/${item.reservaPublicId}`}
              className="flex flex-col gap-4 px-6 py-5 hover:bg-slate-50 dark:hover:bg-slate-800/40 xl:flex-row xl:items-center xl:justify-between"
            >
              <div className="space-y-1">
                <div className="flex items-center gap-2">
                  <span className="font-semibold text-slate-900 dark:text-white">{item.numeroReserva}</span>
                  {/* Chip de estado fiscal (B.5): verde = lista para emitir, ambar = pide
                      autorizacion o esta en curso, rojo = bloqueada. El TEXTO sigue viniendo
                      del backend (fiscalStatusLabel), el chip solo pinta el tono. */}
                  <StatusChip
                    tone={item.fiscalStatus === "ready" ? "verde" : item.fiscalStatus === "override" || item.fiscalStatus === "in_progress" ? "ambar" : "rojo"}
                    role={item.fiscalStatus === "in_progress" ? "status" : undefined}
                    aria-live={item.fiscalStatus === "in_progress" ? "polite" : undefined}
                  >
                    {item.fiscalStatus === "ready" ? <CheckCircle2 className="h-3 w-3" aria-hidden="true" /> : item.fiscalStatus === "in_progress" ? <Loader2 className="h-3 w-3 animate-spin" aria-hidden="true" /> : item.fiscalStatus === "override" ? <ShieldAlert className="h-3 w-3" aria-hidden="true" /> : <AlertCircle className="h-3 w-3" aria-hidden="true" />}
                    {item.fiscalStatus === "in_progress" ? "Facturando…" : item.fiscalStatusLabel}
                  </StatusChip>
                </div>
                <div className="text-sm text-slate-500 dark:text-slate-400">{item.customerName}</div>
                {item.economicBlockReason && <div className="text-xs text-slate-400">{item.economicBlockReason}</div>}
              </div>

              <div className="grid grid-cols-2 gap-4 xl:min-w-[460px] xl:grid-cols-4">
                <Metric label="Salida" value={formatDate(item.startDate)} />
                <Metric label="Venta total" value={formatCurrency(item.totalSale)} />
                <Metric label="Ya facturado" value={formatCurrency(item.alreadyInvoiced)} />
                <Metric label="Pendiente fiscal" value={formatCurrency(item.pendingFiscalAmount)} highlight />
              </div>

              <ExternalLink className="hidden h-4 w-4 shrink-0 text-slate-300 xl:block" aria-hidden="true" />
            </Link>
          ))}
        </div>
      )}
      {pagination ? <div className="border-t border-slate-100 dark:border-slate-800">{pagination}</div> : null}
    </div>
  );
}
