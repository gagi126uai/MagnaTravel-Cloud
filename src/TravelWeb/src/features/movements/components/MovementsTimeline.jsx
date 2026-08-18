import { Link } from "react-router-dom";
import { ArrowDown, ArrowUp, FileText, FilePlus, Loader2, Receipt, RotateCcw } from "lucide-react";
import { KIND_COLORS, KIND_LABELS, STATUS_LABELS } from "../api/movementsApi";
import { getMovementActions } from "../lib/movementActions";
import { formatDate } from "../../../lib/utils";
import { Button } from "../../../components/ui/button";
import { StatusChip } from "../../../components/ui/badge";

// B1.15 Fase D' (2026-05-11): timeline cronologico de movimientos.
// Reutilizable en pantalla principal, CustomerAccountPage y ReservaDetailPage.
//
// Props base (sin cambio de firma):
//   items: array de MovementDto (puede estar vacio).
//   loading: bool.
//   emptyText: string opcional, default "No hay movimientos.".
//   showReservaColumn: bool, default true. Oculta la columna en vistas
//     prefiltradas por reserva (ej. ReservaDetailPage).
//   onItemClick?: handler opcional al click de una fila (default link a reserva).
//
// Props de acciones contextuales (opcionales — si no se pasan, no se renderizan
// botones de accion):
//   onViewPdf(item)      — abrir PDF en nueva pestana.
//   onDownloadPdf(item)  — descargar PDF.
//   onAnnulInvoice(item) — anular factura (genera NC).
//   onRetryInvoice(item) — reintentar emision AFIP.
//   busyItems: Set<string> — publicIds de items con operacion en curso.
//     Los botones de esa row se deshabilitan y se muestra spinner.
export default function MovementsTimeline({
  items = [],
  loading = false,
  emptyText = "No hay movimientos.",
  showReservaColumn = true,
  onItemClick,
  onViewPdf,
  onDownloadPdf,
  onAnnulInvoice,
  onRetryInvoice,
  onVoidReceipt,
  busyItems,
}) {
  if (loading) {
    return <div className="px-6 py-10 text-center text-sm text-slate-400">Cargando movimientos…</div>;
  }

  if (!items || items.length === 0) {
    return <div className="px-6 py-10 text-center text-sm text-slate-500">{emptyText}</div>;
  }

  const hasActions = Boolean(onViewPdf || onDownloadPdf || onAnnulInvoice || onRetryInvoice || onVoidReceipt);

  return (
    <div className="divide-y divide-slate-100 dark:divide-slate-800">
      {items.map((item) => (
        <MovementRow
          key={`${item.kind}-${item.legacyId}`}
          item={item}
          showReservaColumn={showReservaColumn}
          onClick={onItemClick}
          hasActions={hasActions}
          onViewPdf={onViewPdf}
          onDownloadPdf={onDownloadPdf}
          onAnnulInvoice={onAnnulInvoice}
          onRetryInvoice={onRetryInvoice}
          onVoidReceipt={onVoidReceipt}
          busy={busyItems instanceof Set ? busyItems.has(String(item.publicId).toLowerCase()) : false}
        />
      ))}
    </div>
  );
}

// Map estatico de clases para que Tailwind no las purgue (la generacion dinamica
// bg-${color}-100 no es safe sin safelist).
const KIND_BUBBLE_CLASS = {
  payment: "bg-emerald-100 text-emerald-700 dark:bg-emerald-900/30 dark:text-emerald-300",
  invoice: "bg-blue-100 text-blue-700 dark:bg-blue-900/30 dark:text-blue-300",
  credit_note: "bg-amber-100 text-amber-700 dark:bg-amber-900/30 dark:text-amber-300",
  // debit_note: naranja para distinguirla de NC (amber) y de factura (indigo).
  debit_note: "bg-orange-100 text-orange-700 dark:bg-orange-900/30 dark:text-orange-300",
  credit_note_reversal: "bg-slate-100 text-slate-600 dark:bg-slate-800 dark:text-slate-300",
};

function MovementRow({ item, showReservaColumn, onClick, hasActions, onViewPdf, onDownloadPdf, onAnnulInvoice, onRetryInvoice, onVoidReceipt, busy }) {
  const status = STATUS_LABELS[item.status] || { label: item.status, color: "slate" };
  const amountClass =
    item.amount < 0
      ? "text-rose-600 dark:text-rose-400"
      : item.kind === "payment"
      ? "text-emerald-700 dark:text-emerald-300"
      : "text-slate-900 dark:text-white";
  const Icon = iconFor(item.kind);
  const bubbleClass = KIND_BUBBLE_CLASS[item.kind] || KIND_BUBBLE_CLASS.credit_note_reversal;

  // item.date mezcla semánticas según item.kind: para "payment" es una fecha de negocio
  // (día elegido por el cajero, medianoche UTC, sin hora real); para facturas/NC/ND puede
  // ser la fecha fiscal (día calendario de ARCA) o un instante real de emisión — la MISMA
  // ambigüedad que linea.date en los extractos. formatDate() central ya distingue ambos
  // casos (ver lib/utils.js), así que no hace falta separar por kind acá.
  const dateFmt = formatDate(item.date);
  // La hora se ancla a Argentina explícito (no la del navegador/servidor) — para los items
  // de fecha de negocio (sin hora real) esto va a mostrar 00:00, que es la ambigüedad
  // inherente del formato ya documentada en formatDate().
  //
  // hallazgo #19 del barrido (2026-07-24): sin `hourCycle: "h23"`, Intl arma la hora en
  // formato 12 horas con "a. m."/"p. m." aunque se pida `hour: "2-digit"` — mismo bug que
  // ya se corrigió en formatDateTime() (lib/utils.js). Todo el sistema muestra hora de 24hs.
  const timeFmt = new Date(item.date).toLocaleTimeString("es-AR", {
    hour: "2-digit",
    minute: "2-digit",
    timeZone: "America/Argentina/Buenos_Aires",
    hourCycle: "h23",
  });

  const actions = hasActions ? getMovementActions(item.kind, item.status, { receiptStatus: item.receiptStatus }) : [];

  // Si hay click handler Y hay acciones visibles, los botones deben detener
  // la propagacion para no activar el onClick del row.
  const rowClickable = Boolean(onClick) && actions.length === 0;

  return (
    <div
      className={`flex flex-col gap-3 px-6 py-4 lg:flex-row lg:items-center lg:justify-between ${rowClickable ? "cursor-pointer hover:bg-slate-50 dark:hover:bg-slate-800/50" : ""}`}
      onClick={rowClickable ? () => onClick(item) : undefined}
    >
      {/* Seccion izquierda: icono + info */}
      <div className="flex items-start gap-3 flex-1 min-w-0">
        <div className={`flex h-9 w-9 flex-shrink-0 items-center justify-center rounded-[10px] ${bubbleClass}`}>
          <Icon className="h-4 w-4" />
        </div>
        <div className="space-y-0.5 min-w-0 flex-1">
          <div className="flex flex-wrap items-center gap-2">
            <span className="text-[11px] font-bold uppercase tracking-wider text-slate-400">
              {KIND_LABELS[item.kind] || item.kind}
            </span>
            <StatusPill status={status} />
          </div>
          <div className="text-sm font-semibold text-slate-900 dark:text-white truncate">{item.reference}</div>
          {item.relatedTo ? (
            <div className="text-xs text-slate-500 dark:text-slate-400">
              {item.kind === "credit_note" || item.kind === "debit_note" ? "Referencia" : "Sobre"}{" "}
              <span className="font-medium">{item.relatedTo.label}</span>
            </div>
          ) : null}
          {showReservaColumn && item.numeroReserva ? (
            <div className="text-xs text-slate-500 dark:text-slate-400">
              <Link
                to={`/reservas/${item.reservaPublicId}`}
                className="hover:text-primary"
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

      {/* Seccion derecha: monto + fecha + acciones */}
      <div className="flex items-center gap-4 justify-between lg:justify-end lg:gap-6">
        <div className="text-right">
          <div className={`text-sm font-bold ${amountClass}`}>
            {item.amount.toLocaleString("es-AR", { style: "currency", currency: "ARS", minimumFractionDigits: 2 })}
          </div>
          <div className="text-[11px] text-slate-400">{dateFmt} · {timeFmt}</div>
        </div>

        {/* Acciones contextuales por row — solo se renderizan si el padre paso handlers */}
        {actions.length > 0 ? (
          <MovementActions
            actions={actions}
            item={item}
            busy={busy}
            onViewPdf={onViewPdf}
            onDownloadPdf={onDownloadPdf}
            onAnnulInvoice={onAnnulInvoice}
            onRetryInvoice={onRetryInvoice}
            onVoidReceipt={onVoidReceipt}
          />
        ) : null}
      </div>
    </div>
  );
}

// Botones de accion contextuales. Mismo molde que InvoicingTab (InvoiceSection):
// outline para las secundarias, destructive para Anular, default (azul boleto)
// para Reintentar (Button, componentes/ui/button.jsx).
function MovementActions({ actions, item, busy, onViewPdf, onDownloadPdf, onAnnulInvoice, onRetryInvoice, onVoidReceipt }) {
  if (busy) {
    return (
      <div role="status" aria-live="polite" aria-label="Operacion en curso" className="flex items-center gap-1.5">
        <Loader2 className="h-4 w-4 animate-spin text-slate-400" aria-hidden="true" />
        <span className="text-xs text-slate-400">Procesando…</span>
      </div>
    );
  }

  return (
    <div
      className="flex items-center gap-1.5"
      onClick={(event) => event.stopPropagation()}
    >
      {actions.includes("view_pdf") && onViewPdf ? (
        <Button
          type="button"
          variant="outline"
          size="sm"
          onClick={() => onViewPdf(item)}
          data-testid="movement-action-view-pdf"
          aria-label={`Ver PDF de ${item.reference}`}
        >
          Ver PDF
        </Button>
      ) : null}

      {actions.includes("download_pdf") && onDownloadPdf ? (
        <Button
          type="button"
          variant="outline"
          size="sm"
          onClick={() => onDownloadPdf(item)}
          data-testid="movement-action-download"
          aria-label={`Descargar PDF de ${item.reference}`}
        >
          Descargar
        </Button>
      ) : null}

      {actions.includes("annul") && onAnnulInvoice ? (
        <Button
          type="button"
          variant="destructive"
          size="sm"
          onClick={() => onAnnulInvoice(item)}
          data-testid="movement-action-annul"
          aria-label={`Anular ${item.reference}`}
        >
          Anular
        </Button>
      ) : null}

      {actions.includes("retry") && onRetryInvoice ? (
        <Button
          type="button"
          size="sm"
          onClick={() => onRetryInvoice(item)}
          data-testid="movement-action-retry"
          aria-label={`Reintentar emision de ${item.reference}`}
        >
          Reintentar
        </Button>
      ) : null}

      {actions.includes("void_receipt") && onVoidReceipt ? (
        <Button
          type="button"
          variant="destructive"
          size="sm"
          onClick={() => onVoidReceipt(item)}
          data-testid="movement-action-void-receipt"
          aria-label={`Anular comprobante de ${item.reference}`}
        >
          Anular comprobante
        </Button>
      ) : null}
    </div>
  );
}

// status.color viene de STATUS_LABELS (movementsApi.js) con los mismos nombres viejos
// (emerald/rose/amber/slate) — se traducen al tono equivalente de StatusChip.
const STATUS_TONE_BY_COLOR = {
  emerald: "verde",
  rose: "rojo",
  amber: "ambar",
  slate: "neutro",
};

function StatusPill({ status }) {
  const tone = STATUS_TONE_BY_COLOR[status.color] || "neutro";
  return <StatusChip tone={tone}>{status.label}</StatusChip>;
}

function iconFor(kind) {
  switch (kind) {
    case "payment": return ArrowDown;
    case "invoice": return FileText;
    case "credit_note": return Receipt;
    case "debit_note": return FilePlus;
    case "credit_note_reversal": return RotateCcw;
    default: return ArrowUp;
  }
}
