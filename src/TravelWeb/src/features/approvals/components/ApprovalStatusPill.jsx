import { STATUS_LABELS } from "../api/approvalsApi";

const COLOR_CLASSES = {
  amber: "bg-amber-100 text-amber-700 dark:bg-amber-900/30 dark:text-amber-300",
  emerald: "bg-emerald-100 text-emerald-700 dark:bg-emerald-900/30 dark:text-emerald-300",
  rose: "bg-rose-100 text-rose-700 dark:bg-rose-900/30 dark:text-rose-300",
  slate: "bg-slate-100 text-slate-600 dark:bg-slate-800 dark:text-slate-300",
};

export default function ApprovalStatusPill({ status }) {
  const entry = STATUS_LABELS[status] || { label: status, color: "slate" };
  const className = COLOR_CLASSES[entry.color] || COLOR_CLASSES.slate;
  return (
    <span className={`inline-flex items-center rounded-full px-2.5 py-0.5 text-[10px] font-black uppercase tracking-wider ${className}`}>
      {entry.label}
    </span>
  );
}
