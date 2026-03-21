import { Fragment, useState } from "react";
import {
  AlertCircle,
  ArrowDownLeft,
  ArrowUpRight,
  CheckCircle2,
  ChevronDown,
  ChevronUp,
  Clock,
  Download,
  ExternalLink,
  Eye,
  FileText,
  Loader2,
  Receipt,
  RotateCw,
  Wallet,
  XCircle,
} from "lucide-react";

const currency = new Intl.NumberFormat("es-AR", {
  style: "currency",
  currency: "ARS",
  minimumFractionDigits: 2,
});

export function HistoryTab({
  payments,
  invoices,
  onDownloadPdf,
  onViewPdf,
  onDownloadReceiptPdf,
  onIssueReceipt,
  onAnnulInvoice,
  onRetryInvoice,
}) {
  const [expandedInvoice, setExpandedInvoice] = useState(null);
  const [activeSubTab, setActiveSubTab] = useState("afip");
  const [visibleCountPayments, setVisibleCountPayments] = useState(25);
  const [visibleCountInvoices, setVisibleCountInvoices] = useState(25);

  const hasData = payments.length > 0 || invoices.length > 0;

  const getInvoiceLabel = (type) => {
    const labels = {
      1: "Factura A",
      6: "Factura B",
      11: "Factura C",
      3: "Nota de Crédito A",
      8: "Nota de Crédito B",
      13: "Nota de Crédito C",
      2: "Nota de Débito A",
      7: "Nota de Débito B",
      12: "Nota de Débito C",
      51: "Factura M",
      53: "Nota de Crédito M",
      52: "Nota de Débito M",
    };
    return labels[type] || `Comp. ${type}`;
  };

  if (!hasData) {
    return (
      <div className="py-20 text-center bg-slate-50/50 dark:bg-slate-800/20 rounded-3xl border-2 border-dashed border-slate-200 dark:border-slate-800">
        <div className="w-16 h-16 bg-white dark:bg-slate-900 rounded-full flex items-center justify-center mx-auto mb-4 shadow-sm border border-slate-100 dark:border-slate-800 text-slate-300">
          <Clock className="w-8 h-8" />
        </div>
        <h3 className="text-lg font-bold text-slate-900 dark:text-white mb-1">Sin historial</h3>
        <p className="text-slate-500 dark:text-slate-400 text-sm">
          No se registran cobranzas ni comprobantes todavía.
        </p>
      </div>
    );
  }

  return (
    <div className="space-y-6 mt-6">
      <div className="flex gap-4 border-b border-slate-200 dark:border-slate-800 pb-px">
        <button
          onClick={() => setActiveSubTab("afip")}
          className={`pb-3 text-sm font-semibold transition-colors flex items-center gap-2 relative ${
            activeSubTab === "afip"
              ? "text-indigo-600 dark:text-indigo-400"
              : "text-slate-500 hover:text-slate-700 dark:text-slate-400 dark:hover:text-slate-200"
          }`}
          type="button"
        >
          <Receipt className="w-4 h-4" />
          Registro fiscal AFIP
          {activeSubTab === "afip" && (
            <div className="absolute -bottom-px left-0 right-0 h-0.5 bg-indigo-600 dark:bg-indigo-400 rounded-t-full" />
          )}
        </button>
        <button
          onClick={() => setActiveSubTab("collections")}
          className={`pb-3 text-sm font-semibold transition-colors flex items-center gap-2 relative ${
            activeSubTab === "collections"
              ? "text-indigo-600 dark:text-indigo-400"
              : "text-slate-500 hover:text-slate-700 dark:text-slate-400 dark:hover:text-slate-200"
          }`}
          type="button"
        >
          <Wallet className="w-4 h-4" />
          Cobranzas y comprobantes
          {activeSubTab === "collections" && (
            <div className="absolute -bottom-px left-0 right-0 h-0.5 bg-indigo-600 dark:bg-indigo-400 rounded-t-full" />
          )}
        </button>
      </div>

      {activeSubTab === "collections" && (
        <div className="space-y-4">
          <div className="hidden md:block overflow-hidden rounded-xl border border-slate-200 dark:border-slate-800 bg-white dark:bg-slate-900/50 shadow-sm">
            <table className="w-full text-left border-collapse">
              <thead>
                <tr className="bg-slate-50/50 dark:bg-slate-800/50 border-b border-slate-200 dark:border-slate-800">
                  <th className="px-6 py-4 text-[11px] uppercase font-bold text-slate-500 tracking-wider">Fecha</th>
                  <th className="px-6 py-4 text-[11px] uppercase font-bold text-slate-500 tracking-wider">Detalle</th>
                  <th className="px-6 py-4 text-[11px] uppercase font-bold text-slate-500 tracking-wider">Método</th>
                  <th className="px-6 py-4 text-[11px] uppercase font-bold text-slate-500 tracking-wider">Comprobante interno</th>
                  <th className="px-6 py-4 text-[11px] uppercase font-bold text-slate-500 tracking-wider text-right">Importe</th>
                  <th className="px-6 py-4 text-[11px] uppercase font-bold text-slate-500 tracking-wider text-right">Acción</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-100 dark:divide-slate-800">
                {payments.slice(0, visibleCountPayments).map((payment) => {
                  const isPositive = Number(payment.amount) >= 0;
                  const canIssueReceipt = payment.entryType === "Payment" && Number(payment.amount) > 0 && !payment.receipt;

                  return (
                    <tr key={payment.id} className="hover:bg-slate-50/50 dark:hover:bg-slate-800/30 transition-colors">
                      <td className="px-6 py-4 text-sm text-slate-600 dark:text-slate-400">
                        {new Date(payment.paidAt).toLocaleString("es-AR")}
                      </td>
                      <td className="px-6 py-4">
                        <div className="flex items-center gap-3">
                          <div
                            className={`p-2 rounded-lg ${
                              isPositive
                                ? "bg-emerald-50 text-emerald-600 dark:bg-emerald-950/30 dark:text-emerald-400"
                                : "bg-rose-50 text-rose-600 dark:bg-rose-950/30 dark:text-rose-400"
                            }`}
                          >
                            {isPositive ? <ArrowDownLeft className="w-4 h-4" /> : <ArrowUpRight className="w-4 h-4" />}
                          </div>
                          <div>
                            <div className="text-sm font-bold text-slate-900 dark:text-white">
                              {payment.entryType === "CreditNoteReversal" ? "Reversión por nota de crédito" : "Cobranza recibida"}
                            </div>
                            <a href={`/reservas/${payment.reserva?.id}`} className="text-xs text-indigo-500 hover:underline font-mono uppercase tracking-tight">
                              RES: {payment.reserva?.numeroReserva || "---"}
                            </a>
                          </div>
                        </div>
                      </td>
                      <td className="px-6 py-4 text-sm text-slate-600 dark:text-slate-400">{payment.method}</td>
                      <td className="px-6 py-4">
                        {payment.receipt ? (
                          <div className="flex items-center gap-2">
                            <span className="text-xs font-semibold text-emerald-700 dark:text-emerald-300 bg-emerald-50 dark:bg-emerald-900/20 px-2 py-1 rounded-full">
                              {payment.receipt.status === "Voided" ? "Anulado" : payment.receipt.receiptNumber}
                            </span>
                            {payment.receipt.status !== "Voided" && (
                              <button
                                type="button"
                                onClick={() => onDownloadReceiptPdf(payment)}
                                className="text-xs font-medium text-indigo-600 hover:text-indigo-700"
                              >
                                Ver PDF
                              </button>
                            )}
                          </div>
                        ) : (
                          <span className="text-xs text-slate-400">Sin emitir</span>
                        )}
                      </td>
                      <td className="px-6 py-4 text-right">
                        <span className={`text-sm font-black ${isPositive ? "text-emerald-600 dark:text-emerald-500" : "text-rose-600 dark:text-rose-400"}`}>
                          {isPositive ? "+" : "-"}
                          {currency.format(payment.amount)}
                        </span>
                      </td>
                      <td className="px-6 py-4 text-right">
                        {canIssueReceipt ? (
                          <button
                            type="button"
                            onClick={() => onIssueReceipt(payment)}
                            className="px-3 py-1.5 rounded-lg bg-slate-900 text-white text-xs font-bold hover:bg-slate-800"
                          >
                            Emitir comprobante
                          </button>
                        ) : (
                          <span className="text-xs text-slate-400 uppercase tracking-wider">
                            {payment.entryType === "CreditNoteReversal" ? "Reversión" : "Registrado"}
                          </span>
                        )}
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>

          <div className="md:hidden space-y-3">
            {payments.slice(0, visibleCountPayments).map((payment) => {
              const isPositive = Number(payment.amount) >= 0;

              return (
                <div key={payment.id} className="bg-white dark:bg-slate-900 rounded-xl p-4 border border-slate-200 dark:border-slate-800">
                  <div className="flex justify-between items-start gap-3">
                    <div className="flex items-center gap-3">
                      <div
                        className={`p-2 rounded-lg ${
                          isPositive
                            ? "bg-emerald-50 text-emerald-600 dark:bg-emerald-950/30 dark:text-emerald-400"
                            : "bg-rose-50 text-rose-600 dark:bg-rose-950/30 dark:text-rose-400"
                        }`}
                      >
                        {isPositive ? <ArrowDownLeft className="w-4 h-4" /> : <ArrowUpRight className="w-4 h-4" />}
                      </div>
                      <div>
                        <div className="text-sm font-bold text-slate-900 dark:text-white">
                          {payment.reserva?.numeroReserva || "Cobranza"}
                        </div>
                        <div className="text-[10px] text-slate-400 uppercase font-black">
                          {new Date(payment.paidAt).toLocaleDateString("es-AR")} · {payment.method}
                        </div>
                      </div>
                    </div>
                    <div className={`text-sm font-black ${isPositive ? "text-emerald-600" : "text-rose-600"}`}>
                      {isPositive ? "+" : "-"}
                      {currency.format(payment.amount)}
                    </div>
                  </div>
                  <div className="mt-3 pt-3 border-t border-slate-100 dark:border-slate-800 text-xs text-slate-500 dark:text-slate-400">
                    {payment.receipt
                      ? `${payment.receipt.receiptNumber} · ${payment.receipt.status}`
                      : "Sin comprobante interno"}
                  </div>
                </div>
              );
            })}
          </div>

          {payments.length > visibleCountPayments && (
            <div className="p-4 text-center">
              <button
                type="button"
                onClick={() => setVisibleCountPayments((current) => current + 25)}
                className="px-6 py-2 text-sm font-medium text-slate-600 dark:text-slate-400 hover:text-slate-900 dark:hover:text-white transition-colors"
              >
                Cargar más cobranzas ({payments.length - visibleCountPayments} restantes)
              </button>
            </div>
          )}
        </div>
      )}

      {activeSubTab === "afip" && (
        <div className="space-y-4">
          <div className="hidden md:block overflow-hidden rounded-xl border border-slate-200 dark:border-slate-800 bg-white dark:bg-slate-900/50 shadow-sm">
            <table className="w-full text-left border-collapse">
              <thead>
                <tr className="bg-slate-50/50 dark:bg-slate-800/50 border-b border-slate-200 dark:border-slate-800">
                  <th className="px-6 py-4 text-[11px] uppercase font-bold text-slate-500 tracking-wider">Comprobante</th>
                  <th className="px-6 py-4 text-[11px] uppercase font-bold text-slate-500 tracking-wider">Fecha / Reserva</th>
                  <th className="px-6 py-4 text-[11px] uppercase font-bold text-slate-500 tracking-wider text-right">Importe</th>
                  <th className="px-6 py-4 text-[11px] uppercase font-bold text-slate-500 tracking-wider">Estado</th>
                  <th className="px-6 py-4 text-[11px] uppercase font-bold text-slate-500 tracking-wider text-right">Acciones</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-100 dark:divide-slate-800">
                {invoices.slice(0, visibleCountInvoices).map((invoice) => (
                  <Fragment key={invoice.id}>
                    <tr className="hover:bg-slate-50/50 dark:hover:bg-slate-800/30 transition-colors">
                      <td className="px-6 py-4">
                        <div className="flex flex-col">
                          <span className="text-xs font-black text-slate-800 dark:text-slate-200 uppercase">
                            {getInvoiceLabel(invoice.tipoComprobante)}
                          </span>
                          <span className="text-[10px] font-mono text-slate-400 font-bold">
                            {invoice.numeroComprobante?.toString().padStart(8, "0") || "--------"}
                          </span>
                          {invoice.wasForced && (
                            <span className="mt-2 inline-flex items-center gap-1 text-[10px] font-bold uppercase text-amber-700 dark:text-amber-300 bg-amber-50 dark:bg-amber-900/20 px-2 py-1 rounded-full">
                              <AlertCircle className="w-3 h-3" />
                              Emitida por excepción
                            </span>
                          )}
                        </div>
                      </td>
                      <td className="px-6 py-4">
                        <div className="text-sm font-medium text-slate-900 dark:text-white">
                          {new Date(invoice.createdAt).toLocaleDateString("es-AR")}
                        </div>
                        <a href={`/reservas/${invoice.reserva?.id}`} className="text-[10px] text-indigo-500 hover:underline font-bold uppercase tracking-tighter">
                          RES: {invoice.reserva?.numeroReserva || "---"}
                        </a>
                        {invoice.wasForced && (
                          <div className="text-xs text-slate-500 dark:text-slate-400 mt-1">
                            {invoice.forcedByUserName || "Agente"} · saldo {currency.format(invoice.outstandingBalanceAtIssuance || 0)}
                          </div>
                        )}
                      </td>
                      <td className="px-6 py-4 text-right">
                        <span className={`text-sm font-black ${invoice.resultado === "R" ? "text-rose-500" : "text-slate-900 dark:text-white"}`}>
                          {currency.format(invoice.importeTotal || 0)}
                        </span>
                      </td>
                      <td className="px-6 py-4">
                        {invoice.resultado === "A" ? (
                          <div className="flex items-center gap-1 text-emerald-600 dark:text-emerald-400 text-[10px] font-black uppercase">
                            <div className="w-1.5 h-1.5 rounded-full bg-emerald-500" />
                            Aprobado
                          </div>
                        ) : invoice.resultado === "R" ? (
                          <button
                            type="button"
                            onClick={() => setExpandedInvoice(expandedInvoice === invoice.id ? null : invoice.id)}
                            className="flex items-center gap-1 text-rose-600 dark:text-rose-400 text-[10px] font-black uppercase hover:underline"
                          >
                            <AlertCircle className="w-3.5 h-3.5" />
                            Rechazado
                            {expandedInvoice === invoice.id ? <ChevronUp className="w-3 h-3" /> : <ChevronDown className="w-3 h-3" />}
                          </button>
                        ) : (
                          <div className="flex items-center gap-1 text-slate-400 text-[10px] font-black uppercase italic">
                            <Loader2 className="w-3 h-3 animate-spin" />
                            En proceso
                          </div>
                        )}
                      </td>
                      <td className="px-6 py-4 text-right">
                        <div className="flex items-center justify-end gap-1.5">
                          {invoice.resultado === "A" && (
                            <>
                              <button
                                type="button"
                                onClick={() => onViewPdf(invoice)}
                                className="p-2 rounded-lg hover:bg-slate-100 dark:hover:bg-slate-800 text-slate-500 hover:text-indigo-600"
                                title="Ver PDF"
                              >
                                <Eye className="w-4.5 h-4.5" />
                              </button>
                              <button
                                type="button"
                                onClick={() => onDownloadPdf(invoice)}
                                className="p-2 rounded-lg hover:bg-slate-100 dark:hover:bg-slate-800 text-slate-500 hover:text-slate-900 dark:hover:text-white"
                                title="Descargar PDF"
                              >
                                <Download className="w-4.5 h-4.5" />
                              </button>
                              {![2, 3, 7, 8, 12, 13, 52, 53].includes(invoice.tipoComprobante) && (
                                <button
                                  type="button"
                                  onClick={() => onAnnulInvoice(invoice)}
                                  className="p-2 rounded-lg hover:bg-rose-50 dark:hover:bg-rose-900/20 text-slate-400 hover:text-rose-600"
                                  title="Anular"
                                >
                                  <XCircle className="w-4.5 h-4.5" />
                                </button>
                              )}
                            </>
                          )}
                          {invoice.resultado === "R" && (
                            <button
                              type="button"
                              onClick={() => onRetryInvoice(invoice)}
                              className="px-4 py-1.5 rounded-lg bg-indigo-600 text-white text-[10px] font-bold hover:bg-indigo-700"
                            >
                              Reintentar
                            </button>
                          )}
                          <a href={`/reservas/${invoice.reserva?.id}`} className="p-2 rounded-lg hover:bg-slate-100 dark:hover:bg-slate-800 text-slate-300 hover:text-indigo-600">
                            <ExternalLink className="w-4.5 h-4.5" />
                          </a>
                        </div>
                      </td>
                    </tr>
                    {expandedInvoice === invoice.id && (
                      <tr className="bg-rose-50/40 dark:bg-rose-950/20">
                        <td colSpan="5" className="px-8 py-4">
                          <div className="flex items-start gap-3 border-l-2 border-rose-500 pl-4 py-1">
                            <div className="p-1 rounded bg-rose-100 dark:bg-rose-900/30 text-rose-600">
                              <AlertCircle className="w-4 h-4" />
                            </div>
                            <div>
                              <div className="text-[10px] font-black text-rose-800 dark:text-rose-300 uppercase tracking-wider mb-1">
                                Motivo del rechazo
                              </div>
                              <p className="text-xs font-semibold text-rose-600 dark:text-rose-400 leading-relaxed italic">
                                "{invoice.observaciones || "AFIP rechazó el comprobante sin detalle adicional."}"
                              </p>
                              {invoice.forceReason && (
                                <p className="text-xs text-slate-600 dark:text-slate-400 mt-3">
                                  Motivo del agente: {invoice.forceReason}
                                </p>
                              )}
                            </div>
                          </div>
                        </td>
                      </tr>
                    )}
                  </Fragment>
                ))}
              </tbody>
            </table>
          </div>

          <div className="md:hidden space-y-4">
            {invoices.slice(0, visibleCountInvoices).map((invoice) => (
              <div key={invoice.id} className="bg-white dark:bg-slate-900 rounded-xl p-5 border border-slate-200 dark:border-slate-800">
                <div className="flex justify-between items-start mb-4">
                  <div>
                    <div className="text-[10px] font-black text-slate-400 uppercase mb-1">
                      {getInvoiceLabel(invoice.tipoComprobante)}
                    </div>
                    <div className="text-sm font-bold text-slate-900 dark:text-white">
                      {invoice.reserva?.numeroReserva || "Reserva"}
                    </div>
                    {invoice.wasForced && (
                      <div className="text-[10px] text-amber-600 dark:text-amber-300 font-bold uppercase mt-1">
                        Emitida por excepción
                      </div>
                    )}
                  </div>
                  <div className="text-right">
                    <div className="text-base font-black text-slate-900 dark:text-white">
                      {currency.format(invoice.importeTotal || 0)}
                    </div>
                    <div className="text-[9px] font-bold text-slate-400">
                      {new Date(invoice.createdAt).toLocaleDateString("es-AR")}
                    </div>
                  </div>
                </div>

                <div className="flex items-center gap-2 pt-4 border-t border-slate-50 dark:border-slate-800/50">
                  {invoice.resultado === "A" ? (
                    <button
                      type="button"
                      onClick={() => onViewPdf(invoice)}
                      className="flex-1 py-2 bg-slate-900 dark:bg-white text-white dark:text-slate-900 rounded-lg text-xs font-bold"
                    >
                      Ver PDF
                    </button>
                  ) : (
                    <button
                      type="button"
                      onClick={() => onRetryInvoice(invoice)}
                      className="flex-1 py-2 bg-indigo-600 text-white rounded-lg text-xs font-bold"
                    >
                      Reintentar
                    </button>
                  )}
                  <a href={`/reservas/${invoice.reserva?.id}`} className="p-2 bg-slate-50 dark:bg-slate-800 text-slate-400 rounded-lg">
                    <ExternalLink className="w-4 h-4" />
                  </a>
                </div>
              </div>
            ))}
          </div>
        </div>
      )}
    </div>
  );
}
