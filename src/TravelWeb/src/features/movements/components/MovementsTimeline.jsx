import { Link } from "react-router-dom";
import { ArrowDown, ArrowUp, FileText, Receipt, RotateCcw } from "lucide-react";
import { KIND_COLORS, KIND_LABELS, STATUS_LABELS } from "../api/movementsApi";

// B1.15 Fase D' (2026-05-11): timeline cronologico de movimientos.
// Reutilizable en pantalla principal, CustomerAccountPage y ReservaDetailPage.
//
// Props:
//   items: array de MovementDto (puede estar vacio).
//   loading: bool.
//   emptyText: string opcional, default "No hay movimientos.".
//   showReservaColumn: bool, default true. Oculta la columna en vistas
//     prefiltradas por reserva (ej. ReservaDetailPage).
//   onItemClick?: handler opcional al click de una fila (default link a reserva).
export default function MovementsTimeline({
  items = [],
  loading = false,
  emptyText = "No hay movimientos.",
  showReservaColumn = true,
  onItemClick,
}) {
  if (loading) {
    return <div className="px-6 py-10 text-center text-sm text-slate-400">Cargando movimientos…</div>;
  }

  if (!items || items.length === 0) {
    return <div className="px-6 py-10 text-center text-sm text-slate-500">{emptyText}</div>;
  }

  return (
    <div className="divide-y divide-slate-100 dark:divide-slate-800">
      {items.map((item) => (
        <MovementRow
          key={`${item.kind}-${item.legacyId}`}
          item={item}
          showReservaColumn={showReservaColumn}
          onClick={onItemClick}
        />
      ))}
    </div>
  );
}

// Map estatico de clases para que Tailwind no las purgue (la generacion dinamica
// bg-${color}-100 no es safe sin safelist).
const KIND_BUBBLE_CLASS = {
  payment: "bg-emerald-100 text-emerald-700 dark:bg-emerald-900/30 dark:text-emerald-300",
  invoice: "bg-indigo-100 text-indigo-700 dark:bg-indigo-900/30 dark:text-indigo-300",
  credit_note: "bg-amber-100 text-amber-700 dark:bg-amber-900/30 dark:text-amber-300",
  credit_note_reversal: "bg-slate-100 text-slate-600 dark:bg-slate-800 dark:text-slate-300",
};

function MovementRow({ item, showReservaColumn, onClick }) {
  const status = STATUS_LABELS[item.status] || { label: item.status, color: "slate" };
  const amountClass =
    item.amount < 0
      ? "text-rose-600 dark:text-rose-400"
      : item.kind === "payment"
      ? "text-emerald-700 dark:text-emerald-300"
      : "text-slate-900 dark:text-white";
  const Icon = iconFor(item.kind);
  const bubbleClass = KIND_BUBBLE_CLASS[item.kind] || KIND_BUBBLE_CLASS.credit_note_reversal;

  const dateFmt = new Date(item.date).toLocaleDateString("es-AR", { day: "2-digit", month: "2-digit", year: "numeric" });
  const timeFmt = new Date(item.date).toLocaleTimeString("es-AR", { hour: "2-digit", minute: "2-digit" });

  return (
    <div
      className={`flex flex-col gap-3 px-6 py-4 lg:flex-row lg:items-center lg:justify-between ${onClick ? "cursor-pointer hover:bg-slate-50 dark:hover:bg-slate-800/50" : ""}`}
      onClick={onClick ? () => onClick(item) : undefined}
    >
      <div className="flex items-start gap-3 flex-1 min-w-0">
        <div className={`flex h-9 w-9 flex-shrink-0 items-center justify-center rounded-lg ${bubbleClass}`}>
          <Icon className="h-4 w-4" />
        </div>
        <div className="space-y-0.5 min-w-0 flex-1">
          <div className="flex flex-wrap items-center gap-2">
            <span className="text-[10px] font-bold uppercase tracking-wider text-slate-400">
              {KIND_LABELS[item.kind] || item.kind}
            </span>
            <StatusPill status={status} />
          </div>
          <div className="text-sm font-semibold text-slate-900 dark:text-white truncate">{item.reference}</div>
          {item.relatedTo ? (
            <div className="text-xs text-slate-500 dark:text-slate-400">
              {item.kind === "credit_note" ? "Anula" : "Sobre"}{" "}
              <span className="font-medium">{item.relatedTo.label}</span>
            </div>
          ) : null}
          {showReservaColumn && item.numeroReserva ? (
            <div className="text-xs text-slate-500 dark:text-slate-400">
              <Link
                to={`/reservas/${item.reservaPublicId}`}
                className="hover:text-indigo-600 dark:hover:text-indigo-300"
                onClick={(event) => event.stopPropagation()}
              >
                Reserva {item.numeroReserva}
              </Link>
              {item.customerName ? <span className="ml-1.5 text-slate-400">· {item.customerName}</span> : null}
            </div>
          ) : null}
          {item.notes ? (
            <div className="text-xs italic text-slate-400 truncate">{item.notes}</div>
          ) : null}
        </div>
      </div>
      <div className="flex items-center gap-6 justify-between lg:justify-end lg:gap-8">
        <div className="text-right">
          <div className={`text-sm font-bold ${amountClass}`}>
            {item.amount.toLocaleString("es-AR", { style: "currency", currency: "ARS", minimumFractionDigits: 2 })}
          </div>
          <div className="text-[10px] text-slate-400">{dateFmt} · {timeFmt}</div>
        </div>
      </div>
    </div>
  );
}

function StatusPill({ status }) {
  const colorMap = {
    emerald: "bg-emerald-100 text-emerald-700 dark:bg-emerald-900/30 dark:text-emerald-300",
    rose: "bg-rose-100 text-rose-700 dark:bg-rose-900/30 dark:text-rose-300",
    amber: "bg-amber-100 text-amber-700 dark:bg-amber-900/30 dark:text-amber-300",
    slate: "bg-slate-100 text-slate-600 dark:bg-slate-800 dark:text-slate-300",
  };
  const cls = colorMap[status.color] || colorMap.slate;
  return (
    <span className={`inline-flex items-center rounded-full px-2 py-0.5 text-[10px] font-black uppercase tracking-wider ${cls}`}>
      {status.label}
    </span>
  );
}

function iconFor(kind) {
  switch (kind) {
    case "payment": return ArrowDown;
    case "invoice": return FileText;
    case "credit_note": return Receipt;
    case "credit_note_reversal": return RotateCcw;
    default: return ArrowUp;
  }
}
