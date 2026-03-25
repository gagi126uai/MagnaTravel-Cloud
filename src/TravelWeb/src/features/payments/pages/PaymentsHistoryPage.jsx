import { Link } from "react-router-dom";
import {
  ArrowDownLeft,
  ArrowUpRight,
  Download,
  Eye,
  FileText,
  Loader2,
  Receipt,
  Search,
  Wallet,
} from "lucide-react";
import { PaginationFooter } from "../../../components/ui/PaginationFooter";
import { DatabaseUnavailableState } from "../../../components/ui/DatabaseUnavailableState";
import { useFinanceHistory } from "../hooks/useFinanceHistory";
import {
  formatCurrency,
  formatDateTime,
  getInvoiceLabel,
  isCreditNote,
} from "../lib/financeUtils";

function HistoryActions({
  item,
  onDownloadPdf,
  onViewPdf,
  onRetryInvoice,
  onAnnulInvoice,
  onDownloadReceiptPdf,
  onIssueReceipt,
}) {
  if (item.entityType === "payment") {
    const canIssueReceipt =
      item.paymentEntryType === "Payment" &&
      Number(item.amount) > 0 &&
      !item.receiptPublicId;

    return (
      <div className="flex items-center gap-2">
        {item.receiptPublicId && item.receiptStatus !== "Voided" && (
          <button
            type="button"
            onClick={() => onDownloadReceiptPdf(item)}
            className="rounded-lg p-2 text-slate-500 hover:bg-slate-100 hover:text-indigo-600 dark:hover:bg-slate-800"
            title="Ver comprobante interno"
          >
            <Receipt className="h-4 w-4" />
          </button>
        )}
        {canIssueReceipt && (
          <button
            type="button"
            onClick={() => onIssueReceipt(item)}
            className="rounded-lg bg-slate-900 px-3 py-1.5 text-xs font-semibold text-white hover:bg-slate-800"
          >
            Emitir comprobante
          </button>
        )}
      </div>
    );
  }

  if (item.entityType === "invoice") {
    return (
      <div className="flex items-center gap-2">
        {item.invoiceResultado === "A" ? (
          <>
            <button
              type="button"
              onClick={() => onViewPdf(item)}
              className="rounded-lg p-2 text-slate-500 hover:bg-slate-100 hover:text-indigo-600 dark:hover:bg-slate-800"
              title="Ver PDF"
            >
              <Eye className="h-4 w-4" />
            </button>
            <button
              type="button"
              onClick={() => onDownloadPdf(item)}
              className="rounded-lg p-2 text-slate-500 hover:bg-slate-100 hover:text-slate-900 dark:hover:bg-slate-800 dark:hover:text-white"
              title="Descargar PDF"
            >
              <Download className="h-4 w-4" />
            </button>
            {![2, 3, 7, 8, 12, 13, 52, 53].includes(item.invoiceTipoComprobante) && (
              <button
                type="button"
                onClick={() => onAnnulInvoice(item)}
                className="rounded-lg border border-slate-200 px-3 py-1.5 text-xs font-semibold text-slate-600 hover:bg-slate-50 dark:border-slate-700 dark:text-slate-300 dark:hover:bg-slate-800"
              >
                Anular
              </button>
            )}
          </>
        ) : (
          <button
            type="button"
            onClick={() => onRetryInvoice(item)}
            className="rounded-lg bg-indigo-600 px-3 py-1.5 text-xs font-semibold text-white hover:bg-indigo-700"
          >
            Reintentar
          </button>
        )}
      </div>
    );
  }

  return <span className="text-xs uppercase tracking-wider text-slate-400">Manual</span>;
}

export default function PaymentsHistoryPage() {
  const {
    loading,
    timeline,
    searchTerm,
    setSearchTerm,
    page,
    pageSize,
    totalCount,
    totalPages,
    hasPreviousPage,
    hasNextPage,
    setPage,
    setPageSize,
    handleDownloadPdf,
    handleViewPdf,
    handleRetryInvoice,
    handleAnnulInvoice,
    handleDownloadReceiptPdf,
    handleIssueReceipt,
    databaseUnavailable,
  } = useFinanceHistory();

  if (loading && timeline.length === 0) {
    return (
      <div className="flex h-64 items-center justify-center text-slate-400">
        <Loader2 className="h-8 w-8 animate-spin" />
      </div>
    );
  }

  return (
    <div className="space-y-6">
      <div className="flex justify-end">
        <div className="relative min-w-[260px]">
          <Search className="absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" />
          <input
            type="text"
            placeholder="Buscar por reserva, referencia o tipo..."
            value={searchTerm}
            onChange={(event) => setSearchTerm(event.target.value)}
            className="w-full rounded-xl border border-slate-200 bg-slate-50 py-2 pl-9 pr-4 text-sm transition-shadow focus:ring-2 focus:ring-slate-200 dark:border-slate-800 dark:bg-slate-900 dark:text-white"
          />
        </div>
      </div>

      {databaseUnavailable ? (
        <DatabaseUnavailableState />
      ) : (
        <>
          <div className="space-y-4">
            {timeline.length === 0 ? (
              <div className="rounded-3xl border-2 border-dashed border-slate-200 bg-slate-50/50 py-20 text-center dark:border-slate-800 dark:bg-slate-800/20">
                <div className="mb-1 text-lg font-semibold text-slate-900 dark:text-white">Sin historial</div>
                <div className="text-sm text-slate-500 dark:text-slate-400">
                  No hay movimientos todavia para mostrar.
                </div>
              </div>
            ) : (
              timeline.map((item) => {
                const isNegative = Number(item.amount) < 0;
                const Icon =
                  item.entityType === "payment"
                    ? isNegative
                      ? ArrowUpRight
                      : ArrowDownLeft
                    : item.entityType === "invoice"
                      ? FileText
                      : Wallet;

                const accentClass =
                  item.entityType === "invoice"
                    ? "bg-indigo-50 text-indigo-600 dark:bg-indigo-950/30 dark:text-indigo-300"
                    : isNegative
                      ? "bg-rose-50 text-rose-600 dark:bg-rose-950/30 dark:text-rose-300"
                      : "bg-emerald-50 text-emerald-600 dark:bg-emerald-950/30 dark:text-emerald-300";

                const amountClass =
                  item.entityType === "invoice"
                    ? "text-indigo-600 dark:text-indigo-300"
                    : isNegative
                      ? "text-rose-600 dark:text-rose-300"
                      : "text-emerald-600 dark:text-emerald-300";

                const amountPrefix =
                  item.entityType === "payment"
                    ? isNegative
                      ? "-"
                      : "+"
                    : isNegative
                      ? "-"
                      : "";

                const invoiceLabel =
                  item.entityType === "invoice" && item.invoiceTipoComprobante
                    ? getInvoiceLabel(item.invoiceTipoComprobante)
                    : null;

                const kindLabel =
                  item.entityType === "invoice" && item.invoiceTipoComprobante
                    ? isCreditNote({ tipoComprobante: item.invoiceTipoComprobante })
                      ? "Nota de credito"
                      : "Factura AFIP"
                    : item.kind;

                return (
                  <div
                    key={`${item.entityType}-${item.publicId}`}
                    className="rounded-2xl border border-slate-200 bg-white p-5 shadow-sm dark:border-slate-800 dark:bg-slate-900"
                  >
                    <div className="flex flex-col justify-between gap-4 lg:flex-row lg:items-start">
                      <div className="flex items-start gap-4">
                        <div className={`rounded-2xl p-3 ${accentClass}`}>
                          <Icon className="h-5 w-5" />
                        </div>

                        <div className="space-y-1">
                          <div className="flex flex-wrap items-center gap-2">
                            <span className="text-sm font-semibold text-slate-900 dark:text-white">
                              {invoiceLabel || item.title}
                            </span>
                            <span className="inline-flex items-center rounded-full bg-slate-100 px-2.5 py-1 text-[10px] font-semibold uppercase tracking-wider text-slate-500 dark:bg-slate-800">
                              {kindLabel}
                            </span>
                            {item.entityType === "invoice" && item.invoiceWasForced && (
                              <span className="inline-flex items-center rounded-full bg-amber-50 px-2.5 py-1 text-[10px] font-semibold uppercase tracking-wider text-amber-600 dark:bg-amber-900/20 dark:text-amber-300">
                                Excepcion
                              </span>
                            )}
                          </div>
                          <div className="text-sm text-slate-500 dark:text-slate-400">{item.subtitle || "Sin detalle"}</div>
                          <div className="text-xs text-slate-400">{formatDateTime(item.occurredAt)}</div>
                          {item.entityType === "payment" && item.receiptNumber && (
                            <div className="text-xs text-slate-500 dark:text-slate-400">
                              Comprobante interno: {item.receiptNumber} ({item.receiptStatus})
                            </div>
                          )}
                          {item.entityType === "invoice" && item.invoiceForceReason && (
                            <div className="text-xs text-slate-500 dark:text-slate-400">
                              Motivo del agente: {item.invoiceForceReason}
                            </div>
                          )}
                          {item.entityType === "movement" && item.reference && (
                            <div className="text-xs text-slate-500 dark:text-slate-400">
                              Ref. {item.reference}
                            </div>
                          )}
                        </div>
                      </div>

                      <div className="flex flex-col items-start gap-3 lg:items-end">
                        <div className={`text-lg font-semibold ${amountClass}`}>
                          {amountPrefix}
                          {formatCurrency(Math.abs(Number(item.amount || 0)))}
                        </div>

                        <HistoryActions
                          item={item}
                          onDownloadPdf={handleDownloadPdf}
                          onViewPdf={handleViewPdf}
                          onRetryInvoice={handleRetryInvoice}
                          onAnnulInvoice={handleAnnulInvoice}
                          onDownloadReceiptPdf={handleDownloadReceiptPdf}
                          onIssueReceipt={handleIssueReceipt}
                        />

                        {item.reservaPublicId && (
                          <Link
                            to={`/reservas/${item.reservaPublicId}`}
                            className="text-xs font-medium text-indigo-600 hover:text-indigo-700"
                          >
                            Ver reserva
                          </Link>
                        )}
                      </div>
                    </div>
                  </div>
                );
              })
            )}
          </div>

          <PaginationFooter
            page={page}
            pageSize={pageSize}
            totalCount={totalCount}
            totalPages={totalPages}
            hasPreviousPage={hasPreviousPage}
            hasNextPage={hasNextPage}
            onPageChange={setPage}
            onPageSizeChange={setPageSize}
          />
        </>
      )}
    </div>
  );
}
