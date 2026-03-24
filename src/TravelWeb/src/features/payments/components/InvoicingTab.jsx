import { Link } from "react-router-dom";
import { AlertCircle, CheckCircle2, ExternalLink, Receipt, ShieldAlert } from "lucide-react";
import { formatCurrency, formatDate, getInvoiceLabel } from "../lib/financeUtils";
import { getPublicId } from "../../../lib/publicIds";

function WorkItemSection({ title, caption, items, emptyText, onInvoice }) {
  return (
    <div className="rounded-2xl border border-slate-200 dark:border-slate-800 bg-white dark:bg-slate-900 shadow-sm overflow-hidden">
      <div className="px-6 py-5 border-b border-slate-100 dark:border-slate-800">
        <div className="font-semibold text-slate-900 dark:text-white">{title}</div>
        <div className="text-sm text-slate-500 dark:text-slate-400 mt-1">{caption}</div>
      </div>

      {items.length === 0 ? (
        <div className="px-6 py-10 text-sm text-slate-500 dark:text-slate-400">{emptyText}</div>
      ) : (
        <div className="divide-y divide-slate-100 dark:divide-slate-800">
          {items.map((item) => (
            <div key={`${title}-${item.reservaPublicId || item.reservaId}`} className="px-6 py-5 flex flex-col xl:flex-row xl:items-center justify-between gap-4">
              <div className="space-y-1">
                <div className="flex items-center gap-2">
                  <Link
                    to={`/reservas/${item.reservaPublicId || item.reservaId}`}
                    className="font-semibold text-slate-900 dark:text-white hover:text-indigo-600 dark:hover:text-indigo-300"
                  >
                    {item.numeroReserva}
                  </Link>
                  <span
                    className={`inline-flex items-center gap-1 rounded-full px-2.5 py-1 text-[10px] font-semibold uppercase tracking-wider ${
                      item.fiscalStatus === "ready"
                        ? "bg-emerald-50 dark:bg-emerald-900/20 text-emerald-600 dark:text-emerald-300"
                        : item.fiscalStatus === "override"
                          ? "bg-amber-50 dark:bg-amber-900/20 text-amber-600 dark:text-amber-300"
                          : "bg-rose-50 dark:bg-rose-900/20 text-rose-600 dark:text-rose-300"
                    }`}
                  >
                    {item.fiscalStatus === "ready" ? (
                      <CheckCircle2 className="w-3 h-3" />
                    ) : item.fiscalStatus === "override" ? (
                      <ShieldAlert className="w-3 h-3" />
                    ) : (
                      <AlertCircle className="w-3 h-3" />
                    )}
                    {item.fiscalStatusLabel}
                  </span>
                </div>
                <div className="text-sm text-slate-500 dark:text-slate-400">{item.customerName}</div>
                {item.economicBlockReason && (
                  <div className="text-xs text-slate-400">{item.economicBlockReason}</div>
                )}
              </div>

              <div className="grid grid-cols-2 xl:grid-cols-3 gap-4 xl:min-w-[460px]">
                <div>
                  <div className="text-[11px] uppercase tracking-wider text-slate-400 font-semibold">Venta total</div>
                  <div className="text-sm font-semibold text-slate-900 dark:text-white">{formatCurrency(item.totalSale)}</div>
                </div>
                <div>
                  <div className="text-[11px] uppercase tracking-wider text-slate-400 font-semibold">Ya facturado</div>
                  <div className="text-sm font-semibold text-slate-500 dark:text-slate-400">{formatCurrency(item.alreadyInvoiced)}</div>
                </div>
                <div>
                  <div className="text-[11px] uppercase tracking-wider text-slate-400 font-semibold">Pendiente fiscal</div>
                  <div className="text-sm font-semibold text-indigo-600 dark:text-indigo-300">{formatCurrency(item.pendingFiscalAmount)}</div>
                </div>
              </div>

              <div className="flex justify-end">
                <button
                  type="button"
                  onClick={() => onInvoice(item)}
                  className={`inline-flex items-center gap-2 px-4 py-2 rounded-lg text-sm font-medium transition-colors ${
                    item.fiscalStatus === "override"
                      ? "bg-amber-500 text-white hover:bg-amber-600"
                      : item.fiscalStatus === "ready"
                        ? "bg-indigo-600 text-white hover:bg-indigo-700"
                        : "bg-slate-100 text-slate-400 cursor-not-allowed"
                  }`}
                  disabled={item.fiscalStatus === "blocked"}
                >
                  <Receipt className="w-4 h-4" />
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

function InvoiceSection({ title, caption, items, emptyText, onDownloadPdf, onViewPdf, onRetryInvoice, onAnnulInvoice }) {
  return (
    <div className="rounded-2xl border border-slate-200 dark:border-slate-800 bg-white dark:bg-slate-900 shadow-sm overflow-hidden">
      <div className="px-6 py-5 border-b border-slate-100 dark:border-slate-800">
        <div className="font-semibold text-slate-900 dark:text-white">{title}</div>
        <div className="text-sm text-slate-500 dark:text-slate-400 mt-1">{caption}</div>
      </div>

      {items.length === 0 ? (
        <div className="px-6 py-10 text-sm text-slate-500 dark:text-slate-400">{emptyText}</div>
      ) : (
        <div className="divide-y divide-slate-100 dark:divide-slate-800">
          {items.map((invoice) => (
            <div key={`${title}-${getPublicId(invoice)}`} className="px-6 py-5 flex flex-col xl:flex-row xl:items-center justify-between gap-4">
              <div className="space-y-1">
                <div className="flex items-center gap-2">
                  <span className="font-semibold text-slate-900 dark:text-white">{getInvoiceLabel(invoice.tipoComprobante)}</span>
                  {invoice.wasForced && (
                    <span className="inline-flex items-center rounded-full bg-amber-50 dark:bg-amber-900/20 px-2.5 py-1 text-[10px] font-semibold uppercase tracking-wider text-amber-600 dark:text-amber-300">
                      Excepcion
                    </span>
                  )}
                  <span
                    className={`inline-flex items-center rounded-full px-2.5 py-1 text-[10px] font-semibold uppercase tracking-wider ${
                      invoice.resultado === "A"
                        ? "bg-emerald-50 dark:bg-emerald-900/20 text-emerald-600 dark:text-emerald-300"
                        : invoice.resultado === "R"
                          ? "bg-rose-50 dark:bg-rose-900/20 text-rose-600 dark:text-rose-300"
                          : "bg-slate-100 dark:bg-slate-800 text-slate-500"
                    }`}
                  >
                    {invoice.resultado === "A" ? "Aprobado" : invoice.resultado === "R" ? "Rechazado" : "En proceso"}
                  </span>
                </div>
                <div className="text-sm text-slate-500 dark:text-slate-400">
                  {invoice.reserva?.numeroReserva || "Sin reserva"} · {invoice.reserva?.customerName || "Consumidor Final"}
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
                      className="px-3 py-1.5 rounded-lg border border-slate-200 dark:border-slate-700 text-xs font-semibold text-slate-600 dark:text-slate-300 hover:bg-slate-50 dark:hover:bg-slate-800"
                    >
                      Ver PDF
                    </button>
                    <button
                      type="button"
                      onClick={() => onDownloadPdf(invoice)}
                      className="px-3 py-1.5 rounded-lg border border-slate-200 dark:border-slate-700 text-xs font-semibold text-slate-600 dark:text-slate-300 hover:bg-slate-50 dark:hover:bg-slate-800"
                    >
                      Descargar
                    </button>
                    {![2, 3, 7, 8, 12, 13, 52, 53].includes(invoice.tipoComprobante) && (
                      <button
                        type="button"
                        onClick={() => onAnnulInvoice(invoice)}
                        className="px-3 py-1.5 rounded-lg bg-slate-900 text-white text-xs font-semibold hover:bg-slate-800"
                      >
                        Anular
                      </button>
                    )}
                  </>
                ) : (
                  <button
                    type="button"
                    onClick={() => onRetryInvoice(invoice)}
                    className="px-3 py-1.5 rounded-lg bg-indigo-600 text-white text-xs font-semibold hover:bg-indigo-700"
                  >
                    Reintentar
                  </button>
                )}

                {(invoice.reservaPublicId || invoice.reservaId) && (
                  <Link
                    to={`/reservas/${invoice.reservaPublicId || invoice.reservaId}`}
                    className="p-2 rounded-lg hover:bg-slate-100 dark:hover:bg-slate-800 text-slate-500 hover:text-indigo-600"
                    title="Ver reserva"
                  >
                    <ExternalLink className="w-4 h-4" />
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
  issuedInvoices,
  creditNotes,
  onInvoice,
  onDownloadPdf,
  onViewPdf,
  onRetryInvoice,
  onAnnulInvoice,
}) {
  const readyItems = items.filter((item) => item.fiscalStatus === "ready");
  const blockedItems = items.filter((item) => item.fiscalStatus === "blocked" || item.fiscalStatus === "override");

  return (
    <div className="space-y-6">
      <WorkItemSection
        title="Para facturar"
        caption="Reservas canceladas economicamente con saldo fiscal pendiente."
        items={readyItems}
        emptyText="No hay reservas listas para emitir en AFIP."
        onInvoice={onInvoice}
      />

      <WorkItemSection
        title="Bloqueadas"
        caption="Reservas con deuda. Si la configuracion lo permite, el agente puede emitir por excepcion."
        items={blockedItems}
        emptyText="No hay reservas bloqueadas por deuda."
        onInvoice={onInvoice}
      />

      <InvoiceSection
        title="Emitidas"
        caption="Comprobantes fiscales del modulo, con acciones de consulta, descarga y anulacion."
        items={issuedInvoices}
        emptyText="Todavia no hay comprobantes para mostrar."
        onDownloadPdf={onDownloadPdf}
        onViewPdf={onViewPdf}
        onRetryInvoice={onRetryInvoice}
        onAnnulInvoice={onAnnulInvoice}
      />

      <InvoiceSection
        title="Notas de credito"
        caption="Notas de credito y documentos relacionados con anulaciones o ajustes fiscales."
        items={creditNotes}
        emptyText="No hay notas de credito registradas."
        onDownloadPdf={onDownloadPdf}
        onViewPdf={onViewPdf}
        onRetryInvoice={onRetryInvoice}
        onAnnulInvoice={onAnnulInvoice}
      />
    </div>
  );
}
