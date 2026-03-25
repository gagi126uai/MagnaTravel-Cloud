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
          className={`rounded-lg px-3 py-2 text-sm font-medium transition-colors ${
            value === option.value
              ? "bg-white text-slate-900 shadow-sm dark:bg-slate-800 dark:text-white"
              : "text-slate-500 hover:text-slate-900 dark:text-slate-400 dark:hover:text-white"
          }`}
        >
          {option.label}
        </button>
      ))}
    </div>
  );
}

function WorkItemSection({ status, onStatusChange, items, onInvoice }) {
  const isReadyTab = status === "ready";

  return (
    <div className="overflow-hidden rounded-2xl border border-slate-200 bg-white shadow-sm dark:border-slate-800 dark:bg-slate-900">
      <div className="flex flex-col gap-4 border-b border-slate-100 px-6 py-5 dark:border-slate-800 lg:flex-row lg:items-center lg:justify-between">
        <div>
          <div className="font-semibold text-slate-900 dark:text-white">Worklist AFIP</div>
          <div className="mt-1 text-sm text-slate-500 dark:text-slate-400">
            Reservas con saldo fiscal pendiente, filtradas desde el servidor.
          </div>
        </div>
        <SegmentedTabs
          value={status}
          onChange={onStatusChange}
          options={[
            { value: "ready", label: "Listas para facturar" },
            { value: "blocked", label: "Bloqueadas" },
          ]}
        />
      </div>

      {items.length === 0 ? (
        <div className="px-6 py-10 text-sm text-slate-500 dark:text-slate-400">
          {isReadyTab
            ? "No hay reservas listas para emitir en AFIP en esta pagina."
            : "No hay reservas bloqueadas por deuda en esta pagina."}
        </div>
      ) : (
        <div className="divide-y divide-slate-100 dark:divide-slate-800">
          {items.map((item) => (
            <div key={item.reservaPublicId} className="flex flex-col justify-between gap-4 px-6 py-5 xl:flex-row xl:items-center">
              <div className="space-y-1">
                <div className="flex items-center gap-2">
                  <Link
                    to={`/reservas/${item.reservaPublicId}`}
                    className="font-semibold text-slate-900 hover:text-indigo-600 dark:text-white dark:hover:text-indigo-300"
                  >
                    {item.numeroReserva}
                  </Link>
                  <span
                    className={`inline-flex items-center gap-1 rounded-full px-2.5 py-1 text-[10px] font-semibold uppercase tracking-wider ${
                      item.fiscalStatus === "ready"
                        ? "bg-emerald-50 text-emerald-600 dark:bg-emerald-900/20 dark:text-emerald-300"
                        : item.fiscalStatus === "override"
                          ? "bg-amber-50 text-amber-600 dark:bg-amber-900/20 dark:text-amber-300"
                          : "bg-rose-50 text-rose-600 dark:bg-rose-900/20 dark:text-rose-300"
                    }`}
                  >
                    {item.fiscalStatus === "ready" ? (
                      <CheckCircle2 className="h-3 w-3" />
                    ) : item.fiscalStatus === "override" ? (
                      <ShieldAlert className="h-3 w-3" />
                    ) : (
                      <AlertCircle className="h-3 w-3" />
                    )}
                    {item.fiscalStatusLabel}
                  </span>
                </div>
                <div className="text-sm text-slate-500 dark:text-slate-400">{item.customerName}</div>
                {item.economicBlockReason && (
                  <div className="text-xs text-slate-400">{item.economicBlockReason}</div>
                )}
              </div>

              <div className="grid grid-cols-2 gap-4 xl:min-w-[460px] xl:grid-cols-4">
                <div>
                  <div className="text-[11px] font-semibold uppercase tracking-wider text-slate-400">Salida</div>
                  <div className="text-sm font-semibold text-slate-900 dark:text-white">{formatDate(item.startDate)}</div>
                </div>
                <div>
                  <div className="text-[11px] font-semibold uppercase tracking-wider text-slate-400">Venta total</div>
                  <div className="text-sm font-semibold text-slate-900 dark:text-white">{formatCurrency(item.totalSale)}</div>
                </div>
                <div>
                  <div className="text-[11px] font-semibold uppercase tracking-wider text-slate-400">Ya facturado</div>
                  <div className="text-sm font-semibold text-slate-500 dark:text-slate-400">{formatCurrency(item.alreadyInvoiced)}</div>
                </div>
                <div>
                  <div className="text-[11px] font-semibold uppercase tracking-wider text-slate-400">Pendiente fiscal</div>
                  <div className="text-sm font-semibold text-indigo-600 dark:text-indigo-300">{formatCurrency(item.pendingFiscalAmount)}</div>
                </div>
              </div>

              <div className="flex justify-end">
                <button
                  type="button"
                  onClick={() => onInvoice(item)}
                  className={`inline-flex items-center gap-2 rounded-lg px-4 py-2 text-sm font-medium transition-colors ${
                    item.fiscalStatus === "override"
                      ? "bg-amber-500 text-white hover:bg-amber-600"
                      : item.fiscalStatus === "ready"
                        ? "bg-indigo-600 text-white hover:bg-indigo-700"
                        : "cursor-not-allowed bg-slate-100 text-slate-400"
                  }`}
                  disabled={item.fiscalStatus === "blocked"}
                >
                  <Receipt className="h-4 w-4" />
                  {item.fiscalStatus === "override" ? "Emitir por excepcion" : "Emitir AFIP"}
                </button>
              </div>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}

function InvoiceSection({
  invoiceKind,
  onInvoiceKindChange,
  items,
  onDownloadPdf,
  onViewPdf,
  onRetryInvoice,
  onAnnulInvoice,
}) {
  const isIssuedTab = invoiceKind === "issued";

  return (
    <div className="overflow-hidden rounded-2xl border border-slate-200 bg-white shadow-sm dark:border-slate-800 dark:bg-slate-900">
      <div className="flex flex-col gap-4 border-b border-slate-100 px-6 py-5 dark:border-slate-800 lg:flex-row lg:items-center lg:justify-between">
        <div>
          <div className="font-semibold text-slate-900 dark:text-white">Comprobantes AFIP</div>
          <div className="mt-1 text-sm text-slate-500 dark:text-slate-400">
            La pestaña activa define el filtro server-side de la pagina actual.
          </div>
        </div>
        <SegmentedTabs
          value={invoiceKind}
          onChange={onInvoiceKindChange}
          options={[
            { value: "issued", label: "Emitidas" },
            { value: "creditNote", label: "Notas de credito" },
          ]}
        />
      </div>

      {items.length === 0 ? (
        <div className="px-6 py-10 text-sm text-slate-500 dark:text-slate-400">
          {isIssuedTab
            ? "No hay comprobantes emitidos en esta pagina."
            : "No hay notas de credito en esta pagina."}
        </div>
      ) : (
        <div className="divide-y divide-slate-100 dark:divide-slate-800">
          {items.map((invoice) => (
            <div key={getPublicId(invoice)} className="flex flex-col justify-between gap-4 px-6 py-5 xl:flex-row xl:items-center">
              <div className="space-y-1">
                <div className="flex items-center gap-2">
                  <span className="font-semibold text-slate-900 dark:text-white">{getInvoiceLabel(invoice.tipoComprobante)}</span>
                  {invoice.wasForced && (
                    <span className="inline-flex items-center rounded-full bg-amber-50 px-2.5 py-1 text-[10px] font-semibold uppercase tracking-wider text-amber-600 dark:bg-amber-900/20 dark:text-amber-300">
                      Excepcion
                    </span>
                  )}
                  <span
                    className={`inline-flex items-center rounded-full px-2.5 py-1 text-[10px] font-semibold uppercase tracking-wider ${
                      invoice.resultado === "A"
                        ? "bg-emerald-50 text-emerald-600 dark:bg-emerald-900/20 dark:text-emerald-300"
                        : invoice.resultado === "R"
                          ? "bg-rose-50 text-rose-600 dark:bg-rose-900/20 dark:text-rose-300"
                          : "bg-slate-100 text-slate-500 dark:bg-slate-800"
                    }`}
                  >
                    {invoice.resultado === "A" ? "Aprobado" : invoice.resultado === "R" ? "Rechazado" : "En proceso"}
                  </span>
                </div>
                <div className="text-sm text-slate-500 dark:text-slate-400">
                  {invoice.numeroReserva || "Sin reserva"} · {invoice.customerName || "Consumidor Final"}
                </div>
                <div className="text-xs text-slate-400">
                  {formatDate(invoice.createdAt)} · #{invoice.numeroComprobante?.toString().padStart(8, "0") || "--------"}
                </div>
                {invoice.forceReason && (
                  <div className="text-xs text-slate-400">Motivo del agente: {invoice.forceReason}</div>
                )}
              </div>

              <div className="text-right">
                <div className="text-sm font-semibold text-slate-900 dark:text-white">
                  {formatCurrency(invoice.importeTotal)}
                </div>
              </div>

              <div className="flex items-center justify-end gap-2">
                {invoice.resultado === "A" ? (
                  <>
                    <button
                      type="button"
                      onClick={() => onViewPdf(invoice)}
                      className="rounded-lg border border-slate-200 px-3 py-1.5 text-xs font-semibold text-slate-600 hover:bg-slate-50 dark:border-slate-700 dark:text-slate-300 dark:hover:bg-slate-800"
                    >
                      Ver PDF
                    </button>
                    <button
                      type="button"
                      onClick={() => onDownloadPdf(invoice)}
                      className="rounded-lg border border-slate-200 px-3 py-1.5 text-xs font-semibold text-slate-600 hover:bg-slate-50 dark:border-slate-700 dark:text-slate-300 dark:hover:bg-slate-800"
                    >
                      Descargar
                    </button>
                    {![2, 3, 7, 8, 12, 13, 52, 53].includes(invoice.tipoComprobante) && (
                      <button
                        type="button"
                        onClick={() => onAnnulInvoice(invoice)}
                        className="rounded-lg bg-slate-900 px-3 py-1.5 text-xs font-semibold text-white hover:bg-slate-800"
                      >
                        Anular
                      </button>
                    )}
                  </>
                ) : (
                  <button
                    type="button"
                    onClick={() => onRetryInvoice(invoice)}
                    className="rounded-lg bg-indigo-600 px-3 py-1.5 text-xs font-semibold text-white hover:bg-indigo-700"
                  >
                    Reintentar
                  </button>
                )}

                {invoice.reservaPublicId && (
                  <Link
                    to={`/reservas/${invoice.reservaPublicId}`}
                    className="rounded-lg p-2 text-slate-500 hover:bg-slate-100 hover:text-indigo-600 dark:hover:bg-slate-800"
                    title="Ver reserva"
                  >
                    <ExternalLink className="h-4 w-4" />
                  </Link>
                )}
              </div>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}

export function InvoicingTab({
  items,
  invoices,
  worklistStatus,
  onWorklistStatusChange,
  invoiceKind,
  onInvoiceKindChange,
  onInvoice,
  onDownloadPdf,
  onViewPdf,
  onRetryInvoice,
  onAnnulInvoice,
}) {
  return (
    <div className="space-y-6">
      <WorkItemSection
        status={worklistStatus}
        onStatusChange={onWorklistStatusChange}
        items={items}
        onInvoice={onInvoice}
      />

      <InvoiceSection
        invoiceKind={invoiceKind}
        onInvoiceKindChange={onInvoiceKindChange}
        items={invoices}
        onDownloadPdf={onDownloadPdf}
        onViewPdf={onViewPdf}
        onRetryInvoice={onRetryInvoice}
        onAnnulInvoice={onAnnulInvoice}
      />
    </div>
  );
}
