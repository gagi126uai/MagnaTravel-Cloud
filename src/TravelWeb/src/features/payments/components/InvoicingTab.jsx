import { Link } from "react-router-dom";
import { AlertCircle, CheckCircle2, ExternalLink, Loader2, ShieldAlert } from "lucide-react";
import { formatCurrency, formatDate, getInvoiceLabel } from "../lib/financeUtils";
import { getPublicId } from "../../../lib/publicIds";
import { StatusChip } from "../../../components/ui/badge";
import { Button } from "../../../components/ui/button";
import { resolverChipEstadoComprobante } from "../../customers/lib/facturacionFilters";

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

/**
 * Chip de estado del comprobante (fiscal ARCA + anulación).
 *
 * Spec 2026-08-18 (chip "Anulada"): usa el mismo criterio que ya vale en la
 * pantalla global de Facturación y en la solapa del cliente
 * (resolverChipEstadoComprobante), para que un comprobante anulado se vea
 * "Anulada" en rojo tachado acá también, en vez de seguir en verde "Aprobado".
 * El chip "Anulando…" conserva el ícono girando que ya tenía.
 */
function ChipEstadoComprobante({ invoice }) {
  const { tone, etiqueta, tachado } = resolverChipEstadoComprobante(invoice);
  const enCurso = invoice.annulmentStatus === "Pending";
  return (
    <StatusChip
      tone={tone}
      className={tachado ? "line-through" : undefined}
      role={enCurso ? "status" : undefined}
      aria-live={enCurso ? "polite" : undefined}
    >
      {enCurso && <Loader2 className="h-3 w-3 animate-spin" aria-hidden="true" />}
      {etiqueta}
    </StatusChip>
  );
}

function FilterSelect({ label, value, onChange, options }) {
  return (
    <div className="space-y-1.5">
      <label className="text-[11px] font-black uppercase tracking-[0.24em] text-slate-400">{label}</label>
      <select value={value} onChange={(event) => onChange(event.target.value)} className="w-full rounded-[10px] border border-slate-200 bg-white px-3 py-2.5 text-sm dark:border-slate-700 dark:bg-slate-900 dark:text-white">
        {options.map((option) => <option key={option.value} value={option.value}>{option.label}</option>)}
      </select>
    </div>
  );
}

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

export function InvoiceSection({ invoiceKind, onInvoiceKindChange, items, onDownloadPdf, onViewPdf, onRetryInvoice, onAnnulInvoice, searchTerm, onSearchTermChange, period, onPeriodChange, customerFilter, onCustomerFilterChange, reservationFilter, onReservationFilterChange, voucherNumberFilter, onVoucherNumberFilterChange, resultFilter, onResultFilterChange, pagination }) {
  return (
    <div className="overflow-hidden rounded-[14px] border border-slate-200 bg-white shadow-sm dark:border-slate-800 dark:bg-slate-900">
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
        {/* P2 de la spec 2026-08-18 (firmado): se agrega "Anulada", espejo de la
            opción que ya tiene el filtro de la pantalla global de Facturación —
            para poder buscar rápido "todas las anuladas" acá también. */}
        <FilterSelect label="Resultado" value={resultFilter} onChange={onResultFilterChange} options={[{ value: "all", label: "Todos" }, { value: "approved", label: "Aprobado" }, { value: "rejected", label: "Rechazado" }, { value: "pending", label: "En proceso" }, { value: "annulled", label: "Anulada" }]} />
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
                  {invoice.wasForced && <StatusChip tone="ambar">Excepcion</StatusChip>}
                  <ChipEstadoComprobante invoice={invoice} />
                </div>
                <div className="text-sm text-slate-500 dark:text-slate-400">{invoice.numeroReserva || "Sin reserva"} · {invoice.customerName || "Consumidor Final"}</div>
                <div className="text-xs text-slate-400">{formatDate(invoice.createdAt)} · #{invoice.numeroComprobante?.toString().padStart(8, "0") || "--------"}</div>
                {invoice.forceReason && <div className="text-xs text-slate-400">Motivo: {invoice.forceReason}</div>}
                {invoice.annulmentStatus === "Succeeded" && invoice.annulmentReason && (
                  <div className="text-xs text-slate-400">Anulada — Motivo: {invoice.annulmentReason}</div>
                )}
              </div>

              <div className="text-right text-sm font-semibold text-slate-900 dark:text-white">{formatCurrency(invoice.importeTotal)}</div>

              <div className="flex items-center justify-end gap-2">
                {invoice.resultado === "A" ? (
                  <>
                    <Button type="button" variant="outline" size="sm" onClick={() => onViewPdf(invoice)}>Ver PDF</Button>
                    <Button type="button" variant="outline" size="sm" onClick={() => onDownloadPdf(invoice)}>Descargar</Button>
                    {/* P1 de la spec 2026-08-18 (firmado): el botón se ESCONDE cuando la
                        factura YA está anulada (mismo criterio que Cuentas por pagar: la
                        acción no se repite una vez hecha) — antes solo se tapaba mientras
                        se estaba anulando (Pending), y quedaba visible después (Succeeded). */}
                    {![2, 3, 7, 8, 12, 13, 52, 53].includes(invoice.tipoComprobante) &&
                      invoice.annulmentStatus !== "Pending" &&
                      invoice.annulmentStatus !== "Succeeded" && (
                      <Button type="button" variant="destructive" size="sm" onClick={() => onAnnulInvoice(invoice)}>Anular</Button>
                    )}
                  </>
                ) : invoice.resultado !== "PENDING" ? (
                  <Button type="button" size="sm" onClick={() => onRetryInvoice(invoice)}>Reintentar</Button>
                ) : null}
                {invoice.reservaPublicId && <Link to={`/reservas/${invoice.reservaPublicId}`} className="rounded-lg p-2 text-slate-500 hover:bg-slate-100 hover:text-primary dark:hover:bg-slate-800" title="Ver reserva"><ExternalLink className="h-4 w-4" /></Link>}
              </div>
            </div>
          ))}
        </div>
      )}
      {pagination ? <div className="border-t border-slate-100 dark:border-slate-800">{pagination}</div> : null}
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
