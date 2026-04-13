import { Link } from "react-router-dom";
import { AlertCircle, CheckCircle2, ExternalLink, Receipt, ShieldAlert } from "lucide-react";
import { formatCurrency, formatDate, getInvoiceLabel } from "../lib/financeUtils";
import { getPublicId } from "../../../lib/publicIds";

function SegmentedTabs({ options, value, onChange }) {
  return (
    <div className="inline-flex rounded-xl border border-slate-200 bg-slate-50 p-1 dark:border-slate-800 dark:bg-slate-900">
      {options.map((option) => (
        <button
          key={option.value}
          type="button"
          onClick={() => onChange(option.value)}
          className={`rounded-lg px-3 py-2 text-sm font-medium transition-colors ${value === option.value ? "bg-white text-slate-900 shadow-sm dark:bg-slate-800 dark:text-white" : "text-slate-500 hover:text-slate-900 dark:text-slate-400 dark:hover:text-white"}`}
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
      <label className="text-[10px] font-black uppercase tracking-[0.24em] text-slate-400">{label}</label>
      <input type={type} value={value} onChange={(event) => onChange(event.target.value)} placeholder={placeholder} className="w-full rounded-xl border border-slate-200 bg-white px-3 py-2.5 text-sm dark:border-slate-700 dark:bg-slate-900 dark:text-white" />
    </div>
  );
}

function FilterSelect({ label, value, onChange, options }) {
  return (
    <div className="space-y-1.5">
      <label className="text-[10px] font-black uppercase tracking-[0.24em] text-slate-400">{label}</label>
      <select value={value} onChange={(event) => onChange(event.target.value)} className="w-full rounded-xl border border-slate-200 bg-white px-3 py-2.5 text-sm dark:border-slate-700 dark:bg-slate-900 dark:text-white">
        {options.map((option) => <option key={option.value} value={option.value}>{option.label}</option>)}
      </select>
    </div>
  );
}

export function WorkItemSection({ status, onStatusChange, items, onInvoice, searchTerm, onSearchTermChange, customerFilter, onCustomerFilterChange, reservationFilter, onReservationFilterChange }) {
  return (
    <div className="overflow-hidden rounded-2xl border border-slate-200 bg-white shadow-sm dark:border-slate-800 dark:bg-slate-900">
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
        <div className="px-6 py-10 text-sm text-slate-500 dark:text-slate-400">No hay reservas para esta vista.</div>
      ) : (
        <div className="divide-y divide-slate-100 dark:divide-slate-800">
          {items.map((item) => (
            <div key={item.reservaPublicId} className="flex flex-col gap-4 px-6 py-5 xl:flex-row xl:items-center xl:justify-between">
              <div className="space-y-1">
                <div className="flex items-center gap-2">
                  <Link to={`/reservas/${item.reservaPublicId}`} className="font-semibold text-slate-900 hover:text-indigo-600 dark:text-white dark:hover:text-indigo-300">{item.numeroReserva}</Link>
                  <span className={`inline-flex items-center gap-1 rounded-full px-2.5 py-1 text-[10px] font-semibold uppercase tracking-wider ${item.fiscalStatus === "ready" ? "bg-emerald-50 text-emerald-600 dark:bg-emerald-900/20 dark:text-emerald-300" : item.fiscalStatus === "override" ? "bg-amber-50 text-amber-600 dark:bg-amber-900/20 dark:text-amber-300" : "bg-rose-50 text-rose-600 dark:bg-rose-900/20 dark:text-rose-300"}`}>
                    {item.fiscalStatus === "ready" ? <CheckCircle2 className="h-3 w-3" /> : item.fiscalStatus === "override" ? <ShieldAlert className="h-3 w-3" /> : <AlertCircle className="h-3 w-3" />}
                    {item.fiscalStatusLabel}
                  </span>
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

              <div className="flex justify-end">
                <button type="button" onClick={() => onInvoice(item)} disabled={item.fiscalStatus === "blocked"} className={`inline-flex items-center gap-2 rounded-lg px-4 py-2 text-sm font-medium transition-colors ${item.fiscalStatus === "override" ? "bg-amber-500 text-white hover:bg-amber-600" : item.fiscalStatus === "ready" ? "bg-indigo-600 text-white hover:bg-indigo-700" : "cursor-not-allowed bg-slate-100 text-slate-400"}`}>
                  <Receipt className="h-4 w-4" />
                  {item.fiscalStatus === "override" ? "Emitir con autorizacion" : "Emitir en AFIP"}
                </button>
              </div>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}

export function InvoiceSection({ invoiceKind, onInvoiceKindChange, items, onDownloadPdf, onViewPdf, onRetryInvoice, onAnnulInvoice, searchTerm, onSearchTermChange, period, onPeriodChange, customerFilter, onCustomerFilterChange, reservationFilter, onReservationFilterChange, voucherNumberFilter, onVoucherNumberFilterChange, resultFilter, onResultFilterChange }) {
  return (
    <div className="overflow-hidden rounded-2xl border border-slate-200 bg-white shadow-sm dark:border-slate-800 dark:bg-slate-900">
      <div className="flex flex-col gap-4 border-b border-slate-100 px-6 py-5 dark:border-slate-800 lg:flex-row lg:items-center lg:justify-between">
        <div>
          <div className="font-semibold text-slate-900 dark:text-white">Facturas emitidas</div>
          <div className="mt-1 text-sm text-slate-500 dark:text-slate-400">Comprobantes emitidos y notas de credito con filtros por periodo y resultado.</div>
        </div>
        <SegmentedTabs value={invoiceKind} onChange={onInvoiceKindChange} options={[{ value: "issued", label: "Emitidas" }, { value: "creditNote", label: "Notas de credito" }]} />
      </div>

      <div className="grid gap-3 border-b border-slate-100 bg-slate-50/70 px-6 py-5 dark:border-slate-800 dark:bg-slate-950/20 md:grid-cols-2 xl:grid-cols-3">
        <FilterInput label="Mes" type="month" value={period} onChange={onPeriodChange} />
        <FilterInput label="Busqueda" value={searchTerm} onChange={onSearchTermChange} placeholder="Cliente, reserva o detalle..." />
        <FilterInput label="Cliente" value={customerFilter} onChange={onCustomerFilterChange} placeholder="Nombre del cliente" />
        <FilterInput label="Reserva" value={reservationFilter} onChange={onReservationFilterChange} placeholder="Numero de reserva" />
        <FilterInput label="Comprobante" value={voucherNumberFilter} onChange={onVoucherNumberFilterChange} placeholder="Numero de comprobante" />
        <FilterSelect label="Resultado" value={resultFilter} onChange={onResultFilterChange} options={[{ value: "all", label: "Todos" }, { value: "approved", label: "Aprobado" }, { value: "rejected", label: "Rechazado" }, { value: "pending", label: "En proceso" }]} />
      </div>

      {items.length === 0 ? (
        <div className="px-6 py-10 text-sm text-slate-500 dark:text-slate-400">No hay comprobantes para esta vista.</div>
      ) : (
        <div className="divide-y divide-slate-100 dark:divide-slate-800">
          {items.map((invoice) => (
            <div key={getPublicId(invoice)} className="flex flex-col gap-4 px-6 py-5 xl:flex-row xl:items-center xl:justify-between">
              <div className="space-y-1">
                <div className="flex items-center gap-2">
                  <span className="font-semibold text-slate-900 dark:text-white">{getInvoiceLabel(invoice.tipoComprobante)}</span>
                  {invoice.wasForced && <span className="inline-flex items-center rounded-full bg-amber-50 px-2.5 py-1 text-[10px] font-semibold uppercase tracking-wider text-amber-600 dark:bg-amber-900/20 dark:text-amber-300">Excepcion</span>}
                  <span className={`inline-flex items-center rounded-full px-2.5 py-1 text-[10px] font-semibold uppercase tracking-wider ${invoice.resultado === "A" ? "bg-emerald-50 text-emerald-600 dark:bg-emerald-900/20 dark:text-emerald-300" : invoice.resultado === "R" ? "bg-rose-50 text-rose-600 dark:bg-rose-900/20 dark:text-rose-300" : "bg-slate-100 text-slate-500 dark:bg-slate-800"}`}>
                    {invoice.resultado === "A" ? "Aprobado" : invoice.resultado === "R" ? "Rechazado" : "En proceso"}
                  </span>
                </div>
                <div className="text-sm text-slate-500 dark:text-slate-400">{invoice.numeroReserva || "Sin reserva"} · {invoice.customerName || "Consumidor Final"}</div>
                <div className="text-xs text-slate-400">{formatDate(invoice.createdAt)} · #{invoice.numeroComprobante?.toString().padStart(8, "0") || "--------"}</div>
                {invoice.forceReason && <div className="text-xs text-slate-400">Motivo: {invoice.forceReason}</div>}
              </div>

              <div className="text-right text-sm font-semibold text-slate-900 dark:text-white">{formatCurrency(invoice.importeTotal)}</div>

              <div className="flex items-center justify-end gap-2">
                {invoice.resultado === "A" ? (
                  <>
                    <button type="button" onClick={() => onViewPdf(invoice)} className="rounded-lg border border-slate-200 px-3 py-1.5 text-xs font-semibold text-slate-600 hover:bg-slate-50 dark:border-slate-700 dark:text-slate-300 dark:hover:bg-slate-800">Ver PDF</button>
                    <button type="button" onClick={() => onDownloadPdf(invoice)} className="rounded-lg border border-slate-200 px-3 py-1.5 text-xs font-semibold text-slate-600 hover:bg-slate-50 dark:border-slate-700 dark:text-slate-300 dark:hover:bg-slate-800">Descargar</button>
                    {![2, 3, 7, 8, 12, 13, 52, 53].includes(invoice.tipoComprobante) && <button type="button" onClick={() => onAnnulInvoice(invoice)} className="rounded-lg bg-slate-900 px-3 py-1.5 text-xs font-semibold text-white hover:bg-slate-800">Anular</button>}
                  </>
                ) : (
                  <button type="button" onClick={() => onRetryInvoice(invoice)} className="rounded-lg bg-indigo-600 px-3 py-1.5 text-xs font-semibold text-white hover:bg-indigo-700">Reintentar</button>
                )}
                {invoice.reservaPublicId && <Link to={`/reservas/${invoice.reservaPublicId}`} className="rounded-lg p-2 text-slate-500 hover:bg-slate-100 hover:text-indigo-600 dark:hover:bg-slate-800" title="Ver reserva"><ExternalLink className="h-4 w-4" /></Link>}
              </div>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}

function Metric({ label, value, highlight = false }) {
  return (
    <div>
      <div className="text-[11px] font-semibold uppercase tracking-wider text-slate-400">{label}</div>
      <div className={`text-sm font-semibold ${highlight ? "text-indigo-600 dark:text-indigo-300" : "text-slate-900 dark:text-white"}`}>{value}</div>
    </div>
  );
}

export function InvoicingTab(props) {
  return (
    <div className="space-y-6">
      <WorkItemSection
        status={props.worklistStatus}
        onStatusChange={props.onWorklistStatusChange}
        items={props.items}
        onInvoice={props.onInvoice}
        searchTerm={props.worklistSearchTerm}
        onSearchTermChange={props.onWorklistSearchTermChange}
        customerFilter={props.worklistCustomerFilter}
        onCustomerFilterChange={props.onWorklistCustomerFilterChange}
        reservationFilter={props.worklistReservationFilter}
        onReservationFilterChange={props.onWorklistReservationFilterChange}
      />

      <InvoiceSection
        invoiceKind={props.invoiceKind}
        onInvoiceKindChange={props.onInvoiceKindChange}
        items={props.invoices}
        onDownloadPdf={props.onDownloadPdf}
        onViewPdf={props.onViewPdf}
        onRetryInvoice={props.onRetryInvoice}
        onAnnulInvoice={props.onAnnulInvoice}
        searchTerm={props.invoiceSearchTerm}
        onSearchTermChange={props.onInvoiceSearchTermChange}
        period={props.invoicePeriod}
        onPeriodChange={props.onInvoicePeriodChange}
        customerFilter={props.invoiceCustomerFilter}
        onCustomerFilterChange={props.onInvoiceCustomerFilterChange}
        reservationFilter={props.invoiceReservationFilter}
        onReservationFilterChange={props.onInvoiceReservationFilterChange}
        voucherNumberFilter={props.invoiceVoucherNumberFilter}
        onVoucherNumberFilterChange={props.onInvoiceVoucherNumberFilterChange}
        resultFilter={props.invoiceResultFilter}
        onResultFilterChange={props.onInvoiceResultFilterChange}
      />
    </div>
  );
}
