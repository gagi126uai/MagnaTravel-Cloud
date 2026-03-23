import { Link } from "react-router-dom";
import {
  ArrowDownLeft,
  ArrowUpRight,
  Download,
  Eye,
  FileText,
  Loader2,
  Receipt,
  RotateCw,
  Search,
  Wallet,
} from "lucide-react";
import { useFinanceHistory } from "../hooks/useFinanceHistory";
import { formatCurrency, formatDateTime } from "../lib/financeUtils";

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
    const payment = item.entity;
    const canIssueReceipt = payment.entryType === "Payment" && Number(payment.amount) > 0 && !payment.receipt;

    return (
      <div className="flex items-center gap-2">
        {payment.receipt && payment.receipt.status !== "Voided" && (
          <button
            type="button"
            onClick={() => onDownloadReceiptPdf(payment)}
            className="p-2 rounded-lg hover:bg-slate-100 dark:hover:bg-slate-800 text-slate-500 hover:text-indigo-600"
            title="Ver comprobante interno"
          >
            <Receipt className="w-4 h-4" />
          </button>
        )}
        {canIssueReceipt && (
          <button
            type="button"
            onClick={() => onIssueReceipt(payment)}
            className="px-3 py-1.5 rounded-lg bg-slate-900 text-white text-xs font-semibold hover:bg-slate-800"
          >
            Emitir comprobante
          </button>
        )}
      </div>
    );
  }

  if (item.entityType === "invoice") {
    const invoice = item.entity;
    return (
      <div className="flex items-center gap-2">
        {invoice.resultado === "A" ? (
          <>
            <button
              type="button"
              onClick={() => onViewPdf(invoice)}
              className="p-2 rounded-lg hover:bg-slate-100 dark:hover:bg-slate-800 text-slate-500 hover:text-indigo-600"
              title="Ver PDF"
            >
              <Eye className="w-4 h-4" />
            </button>
            <button
              type="button"
              onClick={() => onDownloadPdf(invoice)}
              className="p-2 rounded-lg hover:bg-slate-100 dark:hover:bg-slate-800 text-slate-500 hover:text-slate-900 dark:hover:text-white"
              title="Descargar PDF"
            >
              <Download className="w-4 h-4" />
            </button>
            {![2, 3, 7, 8, 12, 13, 52, 53].includes(invoice.tipoComprobante) && (
              <button
                type="button"
                onClick={() => onAnnulInvoice(invoice)}
                className="px-3 py-1.5 rounded-lg border border-slate-200 dark:border-slate-700 text-xs font-semibold text-slate-600 dark:text-slate-300 hover:bg-slate-50 dark:hover:bg-slate-800"
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
      </div>
    );
  }

  return <span className="text-xs text-slate-400 uppercase tracking-wider">Manual</span>;
}

export default function PaymentsHistoryPage() {
  const {
    loading,
    timeline,
    searchTerm,
    setSearchTerm,
    handleDownloadPdf,
    handleViewPdf,
    handleRetryInvoice,
    handleAnnulInvoice,
    handleDownloadReceiptPdf,
    handleIssueReceipt,
  } = useFinanceHistory();

  if (loading && timeline.length === 0) {
    return (
      <div className="flex justify-center items-center h-64 text-slate-400">
        <Loader2 className="w-8 h-8 animate-spin" />
      </div>
    );
  }

  return (
    <div className="space-y-6">
      <div className="flex justify-end">
        <div className="relative min-w-[260px]">
          <Search className="absolute left-3 top-1/2 -translate-y-1/2 h-4 w-4 text-slate-400" />
          <input
            type="text"
            placeholder="Buscar por reserva, referencia o tipo..."
            value={searchTerm}
            onChange={(event) => setSearchTerm(event.target.value)}
            className="pl-9 pr-4 py-2 w-full text-sm bg-slate-50 dark:bg-slate-900 border border-slate-200 dark:border-slate-800 rounded-xl focus:ring-2 focus:ring-slate-200 transition-shadow dark:text-white"
          />
        </div>
      </div>

      <div className="space-y-4">
        {timeline.length === 0 ? (
          <div className="py-20 text-center bg-slate-50/50 dark:bg-slate-800/20 rounded-3xl border-2 border-dashed border-slate-200 dark:border-slate-800">
            <div className="text-lg font-semibold text-slate-900 dark:text-white mb-1">Sin historial</div>
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

            return (
              <div
                key={item.id}
                className="rounded-2xl border border-slate-200 dark:border-slate-800 bg-white dark:bg-slate-900 shadow-sm p-5"
              >
                <div className="flex flex-col lg:flex-row lg:items-start justify-between gap-4">
                  <div className="flex items-start gap-4">
                    <div
                      className={`p-3 rounded-2xl ${
                        item.entityType === "invoice"
                          ? "bg-indigo-50 text-indigo-600 dark:bg-indigo-950/30 dark:text-indigo-300"
                          : isNegative
                            ? "bg-rose-50 text-rose-600 dark:bg-rose-950/30 dark:text-rose-300"
                            : "bg-emerald-50 text-emerald-600 dark:bg-emerald-950/30 dark:text-emerald-300"
                      }`}
                    >
                      <Icon className="w-5 h-5" />
                    </div>

                    <div className="space-y-1">
                      <div className="flex flex-wrap items-center gap-2">
                        <span className="text-sm font-semibold text-slate-900 dark:text-white">{item.title}</span>
                        <span className="inline-flex items-center rounded-full bg-slate-100 dark:bg-slate-800 px-2.5 py-1 text-[10px] font-semibold uppercase tracking-wider text-slate-500">
                          {item.kind}
                        </span>
                        {item.entityType === "invoice" && item.entity.wasForced && (
                          <span className="inline-flex items-center rounded-full bg-amber-50 dark:bg-amber-900/20 px-2.5 py-1 text-[10px] font-semibold uppercase tracking-wider text-amber-600 dark:text-amber-300">
                            Excepcion
                          </span>
                        )}
                      </div>
                      <div className="text-sm text-slate-500 dark:text-slate-400">{item.subtitle}</div>
                      <div className="text-xs text-slate-400">{formatDateTime(item.date)}</div>
                      {item.entityType === "payment" && item.entity.receipt && (
                        <div className="text-xs text-slate-500 dark:text-slate-400">
                          Comprobante interno: {item.entity.receipt.receiptNumber} ({item.entity.receipt.status})
                        </div>
                      )}
                      {item.entityType === "invoice" && item.entity.forceReason && (
                        <div className="text-xs text-slate-500 dark:text-slate-400">
                          Motivo del agente: {item.entity.forceReason}
                        </div>
                      )}
                    </div>
                  </div>

                  <div className="flex flex-col items-start lg:items-end gap-3">
                    <div
                      className={`text-lg font-semibold ${
                        item.entityType === "invoice"
                          ? "text-indigo-600 dark:text-indigo-300"
                          : isNegative
                            ? "text-rose-600 dark:text-rose-300"
                            : "text-emerald-600 dark:text-emerald-300"
                      }`}
                    >
                      {isNegative ? "-" : item.entityType === "payment" ? "+" : ""}
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

                    {(item.entityType === "payment" || item.entityType === "invoice") && item.entity?.reservaId && (
                      <Link
                        to={`/reservas/${item.entity.reservaId}`}
                        className="text-xs font-medium text-indigo-600 hover:text-indigo-700"
                      >
                        Ver reserva
                      </Link>
                    )}
                    {item.entityType === "movement" && item.entity?.reservaId && (
                      <Link
                        to={`/reservas/${item.entity.reservaId}`}
                        className="text-xs font-medium text-indigo-600 hover:text-indigo-700"
                      >
                        Ver reserva vinculada
                      </Link>
                    )}
                  </div>
                </div>
              </div>
            );
          })
        )}
      </div>
    </div>
  );
}
