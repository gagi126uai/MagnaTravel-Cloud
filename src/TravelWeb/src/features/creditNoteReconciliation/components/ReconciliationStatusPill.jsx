import { RECONCILIATION_STATUS_LABELS, RECEIPT_STATUS_LABELS } from "../api/creditNoteReconciliationApi";

/**
 * Pill de estado para un caso de reconciliacion (Pending / Resolved).
 * Sigue el mismo patron visual que ApprovalStatusPill.
 */

const COLOR_CLASSES = {
  amber: "bg-amber-100 text-amber-700 dark:bg-amber-900/30 dark:text-amber-300",
  emerald: "bg-emerald-100 text-emerald-700 dark:bg-emerald-900/30 dark:text-emerald-300",
  rose: "bg-rose-100 text-rose-700 dark:bg-rose-900/30 dark:text-rose-300",
  slate: "bg-slate-100 text-slate-600 dark:bg-slate-800 dark:text-slate-300",
};

export function ReconciliationStatusPill({ status }) {
  const entry = RECONCILIATION_STATUS_LABELS[status] || { label: status, color: "slate" };
  const colorClass = COLOR_CLASSES[entry.color] || COLOR_CLASSES.slate;
  return (
    <span
      className={`inline-flex items-center rounded-full px-2.5 py-0.5 text-[10px] font-black uppercase tracking-wider ${colorClass}`}
      data-testid="reconciliation-status-pill"
    >
      {entry.label}
    </span>
  );
}

/**
 * Pill de estado vigente de un recibo individual (Issued = vivo / Voided = anulado).
 */
export function ReceiptStatusPill({ status }) {
  const entry = RECEIPT_STATUS_LABELS[status] || { label: status, color: "slate" };
  const colorClass = COLOR_CLASSES[entry.color] || COLOR_CLASSES.slate;
  return (
    <span
      className={`inline-flex items-center rounded-full px-2 py-0.5 text-[10px] font-bold uppercase tracking-wider ${colorClass}`}
      data-testid="receipt-status-pill"
    >
      {entry.label}
    </span>
  );
}
