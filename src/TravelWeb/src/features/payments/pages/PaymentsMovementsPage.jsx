import { useState, useCallback } from "react";
import { Search } from "lucide-react";
import { useDebounce } from "../../../hooks/useDebounce";
import { useMovements } from "../../movements/hooks/useMovements";
import MovementsTimeline from "../../movements/components/MovementsTimeline";
import { PaginationFooter } from "../../../components/ui/PaginationFooter";
import { MonthNavigator, monthToBounds } from "../../../components/ui/MonthNavigator";
import { useFinanceActions } from "../hooks/useFinanceActions";
import { useInvoicePolling } from "../hooks/useInvoicePolling";
import RequestApprovalModal from "../../approvals/components/RequestApprovalModal";

// B1.15 Fase D'.B (2026-05-11): pestaña "Movimientos" — timeline cronologico
// global con filtros. Sirve para "ver todo lo que paso hoy" o conciliar.
//
// Filtro de mes: patron canonico del modulo financiero (igual que Facturacion).
// Default: mes actual. El mes se traduce a dateFrom/dateTo ISO para el backend.
// Si activeMonth es null, no se envian filtros de fecha (historial completo).
//
// Acciones contextuales (D'.B prio-alta recuperadas tras redesign):
//   invoice Approved → Ver PDF · Descargar · Anular
//   invoice Rejected → Reintentar
//   credit_note Approved → Ver PDF · Descargar
//   credit_note Rejected → Reintentar
//   Resto → sin acciones
//
// Cuando "Anular" recibe 409 requiresApproval (Vendedor sin permiso), abre
// RequestApprovalModal con requestType="InvoiceAnnulment".

const KIND_OPTIONS = [
  { value: "payment", label: "Cobros" },
  { value: "invoice", label: "Facturas" },
  { value: "credit_note", label: "Notas de credito" },
  { value: "credit_note_reversal", label: "Reversiones NC" },
];


export default function PaymentsMovementsPage() {
  const [search, setSearch] = useState("");
  const [selectedKinds, setSelectedKinds] = useState(KIND_OPTIONS.map((k) => k.value));

  // Filtro de mes — default: mes actual.
  // null = sin filtro de fecha (historial completo).
  const [activeMonth, setActiveMonth] = useState(() => {
    const now = new Date();
    return new Date(now.getFullYear(), now.getMonth(), 1);
  });

  // B1.15 Fase D'.B: contexto del modal de aprobacion cuando /annul devuelve
  // 409 con requiresApproval. null = modal cerrado.
  const [approvalContext, setApprovalContext] = useState(null);

  // Items con operacion en curso (publicId como string). Deshabilita los
  // botones de esa row y muestra spinner inline sin bloquear la lista completa.
  const [busyItems, setBusyItems] = useState(new Set());

  const debouncedSearch = useDebounce(search, 300);

  // Cuando activeMonth es null no se envian filtros de fecha al backend.
  const dateBounds = activeMonth ? monthToBounds(activeMonth) : null;

  const {
    items, totalCount, totalPages, page, pageSize, setPage, setPageSize,
    loading, error, reload,
  } = useMovements({
    search: debouncedSearch.trim() || null,
    kinds: selectedKinds.length === KIND_OPTIONS.length ? null : selectedKinds,
    dateFrom: dateBounds?.from ?? null,
    dateTo: dateBounds?.to ?? null,
  });

  const {
    handleViewPdf,
    handleDownloadPdf,
    handleAnnulInvoice,
    handleRetryInvoice,
    handleVoidReceipt,
  } = useFinanceActions(reload, {
    onApprovalRequired: ({ requestType, entityType, entityId, invoice }) => {
      let entityLabel = null;
      if (requestType === "ReceiptVoidance") {
        entityLabel = "Comprobante de pago";
      } else {
        // La label se construye desde item.reference (ya formateada por el backend,
        // ej. "Factura B 00001-00000027") sin necesidad de campos adicionales del DTO.
        entityLabel = invoice?.reference ?? null;
      }
      setApprovalContext({ requestType, entityType, entityId, invoiceLabel: entityLabel });
    },
  });

  // Wrappers con tracking de busy-state por row. Evitan llamadas duplicadas
  // mientras la operacion esta en curso. Key normalizada a lowercase para
  // comparacion case-insensitive con MovementsTimeline.
  const withBusy = useCallback((handler) => async (item) => {
    const key = String(item.publicId).toLowerCase();
    setBusyItems((prev) => new Set(prev).add(key));
    try {
      await handler(item);
    } finally {
      setBusyItems((prev) => {
        const next = new Set(prev);
        next.delete(key);
        return next;
      });
    }
  }, []);

  const handleViewPdfForItem = (item) => withBusy(handleViewPdf)(item);
  const handleDownloadPdfForItem = (item) => withBusy(handleDownloadPdf)(item);
  const handleAnnulForItem = (item) => withBusy(handleAnnulInvoice)(item);
  const handleRetryForItem = (item) => withBusy(handleRetryInvoice)(item);
  const handleVoidReceiptForItem = (item) => withBusy(handleVoidReceipt)(item);

  // Polling adaptativo: activo solo cuando hay movimientos en estado transitorio.
  useInvoicePolling(items, reload);

  const toggleKind = (value) => {
    setSelectedKinds((current) =>
      current.includes(value) ? current.filter((k) => k !== value) : [...current, value]
    );
  };

  // Strip de conteo inferior.
  const monthLabel = activeMonth
    ? activeMonth.toLocaleDateString("es-AR", { month: "long", year: "numeric" })
    : null;

  const handleShowAll = () => setActiveMonth(null);
  const handleBackToCurrentMonth = () => {
    const now = new Date();
    setActiveMonth(new Date(now.getFullYear(), now.getMonth(), 1));
  };

  return (
    <div className="space-y-6">
      <div className="rounded-2xl border border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900">
        <div className="flex flex-col gap-3 border-b border-slate-100 px-6 py-4 dark:border-slate-800">
          <div className="flex flex-col gap-3 lg:flex-row lg:items-center lg:justify-between">
            <div className="relative w-full lg:max-w-md">
              <Search className="absolute left-3 top-1/2 -translate-y-1/2 h-4 w-4 text-slate-400" />
              <input
                type="text"
                value={search}
                onChange={(event) => setSearch(event.target.value)}
                placeholder="Buscar por reserva, cliente, referencia…"
                className="w-full rounded-lg border border-slate-300 dark:border-slate-600 dark:bg-slate-800 dark:text-white pl-9 pr-3 py-1.5 text-sm"
              />
            </div>
            <MonthNavigator
              month={activeMonth}
              onChange={setActiveMonth}
              disabled={loading}
            />
          </div>
          <div className="flex flex-wrap items-center gap-2">
            {KIND_OPTIONS.map((option) => {
              const active = selectedKinds.includes(option.value);
              return (
                <button
                  key={option.value}
                  type="button"
                  onClick={() => toggleKind(option.value)}
                  className={`rounded-full px-3 py-1 text-xs font-semibold transition-colors ${active ? "bg-indigo-600 text-white" : "bg-slate-100 text-slate-600 hover:bg-slate-200 dark:bg-slate-800 dark:text-slate-300 dark:hover:bg-slate-700"}`}
                >
                  {option.label}
                </button>
              );
            })}
          </div>
        </div>

        {error ? (
          <div className="m-6 rounded-xl border border-rose-200 bg-rose-50 px-4 py-3 text-sm text-rose-700 dark:border-rose-900/40 dark:bg-rose-900/20 dark:text-rose-300">
            No se pudieron cargar los movimientos.
          </div>
        ) : (
          <>
            <MovementsTimeline
              items={items}
              loading={loading}
              onViewPdf={handleViewPdfForItem}
              onDownloadPdf={handleDownloadPdfForItem}
              onAnnulInvoice={handleAnnulForItem}
              onRetryInvoice={handleRetryForItem}
              onVoidReceipt={handleVoidReceiptForItem}
              busyItems={busyItems}
            />
            <div className="border-t border-slate-100 dark:border-slate-800">
              <PaginationFooter
                page={page}
                pageSize={pageSize}
                totalCount={totalCount}
                totalPages={totalPages}
                hasPreviousPage={page > 1}
                hasNextPage={page < totalPages}
                onPageChange={setPage}
                onPageSizeChange={setPageSize}
              />
            </div>
            {/* Strip de conteo + accion historial completo */}
            {!loading && (
              <div className="flex flex-wrap items-center justify-between gap-2 border-t border-slate-100 px-6 py-2 dark:border-slate-800">
                <span className="text-xs text-slate-500 dark:text-slate-400">
                  {activeMonth
                    ? `Mostrando ${totalCount} movimiento${totalCount !== 1 ? "s" : ""} de ${monthLabel}`
                    : `Mostrando ${totalCount} movimiento${totalCount !== 1 ? "s" : ""} en total`}
                </span>
                {activeMonth ? (
                  <button
                    type="button"
                    onClick={handleShowAll}
                    className="text-xs text-indigo-600 hover:underline dark:text-indigo-400"
                    data-testid="movements-show-all"
                  >
                    Ver historial completo &rarr;
                  </button>
                ) : (
                  <button
                    type="button"
                    onClick={handleBackToCurrentMonth}
                    className="rounded-lg bg-indigo-600 px-3 py-1 text-xs font-semibold text-white hover:bg-indigo-700"
                    data-testid="movements-back-to-month"
                  >
                    Volver al mes actual
                  </button>
                )}
              </div>
            )}
          </>
        )}
      </div>

      {/* Modal de aprobacion para anulaciones que requieren autorizacion del Admin */}
      <RequestApprovalModal
        isOpen={Boolean(approvalContext)}
        onClose={() => setApprovalContext(null)}
        onCreated={() => {
          // El Vendedor recibe confirmacion en el modal (showSuccess interno).
          // Cerramos modal; el reintento de "Anular" lo hace el Vendedor cuando
          // el Admin apruebe — no es automatico.
          setApprovalContext(null);
        }}
        requestType={approvalContext?.requestType}
        entityType={approvalContext?.entityType}
        entityId={approvalContext?.entityId}
        entityLabel={approvalContext?.invoiceLabel}
      />
    </div>
  );
}
