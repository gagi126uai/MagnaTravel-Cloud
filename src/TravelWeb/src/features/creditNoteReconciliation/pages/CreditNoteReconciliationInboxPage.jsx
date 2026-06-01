import { useState } from "react";
import { FileWarning, RefreshCw } from "lucide-react";
import { MonthNavigator } from "../../../components/ui/MonthNavigator";
import { PaginationFooter } from "../../../components/ui/PaginationFooter";
import { useCreditNoteReconciliation } from "../hooks/useCreditNoteReconciliation";
import ReconciliationRow from "../components/ReconciliationRow";
import RequestApprovalModal from "../../approvals/components/RequestApprovalModal";

/**
 * FC1.3 Fase 3 (ADR-010, 2026-05-29): bandeja de reconciliacion de NC parciales con recibos vivos.
 *
 * Permite al back-office ver los casos pendientes donde se emitio una NC parcial pero los
 * recibos de pago de esa factura siguen "vivos" (no anulados). Desde aca se pueden anular
 * los recibos individualmente y cerrar el caso manualmente.
 *
 * La devolucion de plata real NO se hace aca: se gestiona en Caja/Cuenta Corriente.
 *
 * Permisos requeridos: approvals.review (igual que la inbox de aprobaciones).
 *
 * Patron visual clonado de ApprovalsInboxPage.
 */
export default function CreditNoteReconciliationInboxPage() {
  // ─── Modal de solicitud de aprobacion (cuatro ojos) ───────────────────────────
  // Cuando anular un recibo devuelve 409 requiresApproval, la fila llama a
  // handleApprovalRequired y guardamos los datos necesarios para abrir el modal.
  // Mismo patron que PaymentsHistoryPage.
  const [approvalContext, setApprovalContext] = useState(null);

  const handleApprovalRequired = ({ requestType, entityType, entityId }) => {
    setApprovalContext({
      requestType,
      entityType,
      entityId,
      // Etiqueta legible para mostrar en el modal. "Comprobante de pago" es
      // suficiente aqui porque el recibo no tiene un numero disponible en este
      // callback (solo se pasa el payload del 409).
      entityLabel: "Comprobante de pago",
    });
  };

  // ─── Filtros ──────────────────────────────────────────────────────────────────

  // Filtro de estado: "pending" por defecto (los casos sin resolver son los urgentes).
  const [statusFilter, setStatusFilter] = useState("pending");

  // Filtro mensual: arrancamos en el mes actual (como MonthNavigator recomienda).
  // Guardamos un Date (primer dia del mes) o null (todo el historial).
  const [selectedMonth, setSelectedMonth] = useState(() => {
    const now = new Date();
    return new Date(now.getFullYear(), now.getMonth(), 1);
  });

  // Paginacion
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(25);

  // ─── Conversion del mes a year/month para el backend ─────────────────────────
  // Este backend usa year + month enteros, NO un rango from/to.
  // Por eso NO usamos monthToBounds — convertimos directamente.
  const year = selectedMonth ? selectedMonth.getFullYear() : null;
  const monthNumber = selectedMonth ? selectedMonth.getMonth() + 1 : null;

  // Cuando el usuario cambia el filtro mensual, volvemos a la pagina 1.
  const handleMonthChange = (newMonth) => {
    setSelectedMonth(newMonth);
    setPage(1);
  };

  // Cuando cambia el filtro de estado, volvemos a la pagina 1.
  const handleStatusChange = (event) => {
    setStatusFilter(event.target.value);
    setPage(1);
  };

  const handlePageSizeChange = (newPageSize) => {
    setPageSize(newPageSize);
    setPage(1);
  };

  // ─── Datos ────────────────────────────────────────────────────────────────────

  const { items, totalCount, totalPages, hasNextPage, hasPreviousPage, loading, error, reload } =
    useCreditNoteReconciliation({
      status: statusFilter,
      year,
      month: monthNumber,
      page,
      pageSize,
    });

  return (
    <div className="space-y-6">
      {/* ─ Header de la pantalla ─────────────────────────────────────────────── */}
      <div className="flex items-center gap-3">
        <div className="rounded-lg bg-orange-100 p-2 text-orange-700 dark:bg-orange-900/30 dark:text-orange-300">
          <FileWarning className="h-5 w-5" />
        </div>
        <div>
          <h1 className="text-2xl font-bold text-slate-900 dark:text-white">
            Notas de crédito por revisar
          </h1>
          <p className="text-sm text-slate-500 dark:text-slate-400">
            Reservas a las que les hiciste una nota de crédito por una parte del importe y todavía
            tienen recibos de pago sin anular. Acá los dejás al día; la devolución de la plata se
            hace en Caja.
          </p>
        </div>
      </div>

      {/* ─ Panel de filtros + lista ───────────────────────────────────────────── */}
      <div className="rounded-2xl border border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900">
        {/* Toolbar de filtros */}
        <div className="flex flex-col gap-3 border-b border-slate-100 px-6 py-4 dark:border-slate-800 sm:flex-row sm:items-center sm:justify-between">
          <div className="flex flex-wrap items-center gap-3">
            {/* Filtro de estado del caso */}
            <div className="flex items-center gap-2">
              <span className="text-xs font-bold uppercase tracking-wider text-slate-400">
                Estado
              </span>
              <select
                value={statusFilter}
                onChange={handleStatusChange}
                className="rounded-lg border border-slate-300 dark:border-slate-600 dark:bg-slate-800 dark:text-white px-3 py-1.5 text-sm"
                data-testid="status-filter"
              >
                <option value="pending">Pendientes</option>
                <option value="resolved">Resueltos</option>
                <option value="all">Todos</option>
              </select>
            </div>

            {/* Filtro mensual canonico del sistema */}
            <MonthNavigator
              month={selectedMonth}
              onChange={handleMonthChange}
              disabled={loading}
            />
          </div>

          <button
            type="button"
            onClick={reload}
            disabled={loading}
            className="inline-flex items-center gap-1.5 rounded-lg border border-slate-300 dark:border-slate-600 px-3 py-1.5 text-xs font-semibold text-slate-700 dark:text-slate-200 hover:bg-slate-50 dark:hover:bg-slate-800 disabled:opacity-50"
            aria-label="Refrescar bandeja"
            data-testid="refresh-button"
          >
            <RefreshCw className={`h-3.5 w-3.5 ${loading ? "animate-spin" : ""}`} />
            Refrescar
          </button>
        </div>

        {/* ─ Estados: loading / error / vacio / lista ────────────────────────── */}
        {loading ? (
          <div className="px-6 py-10 text-center text-sm text-slate-500">
            Cargando bandeja…
          </div>
        ) : error ? (
          <div className="px-6 py-10 text-center space-y-2">
            <p className="text-sm text-rose-600">No se pudo cargar la bandeja de reconciliacion.</p>
            <button
              type="button"
              onClick={reload}
              className="text-xs text-indigo-600 hover:underline dark:text-indigo-400"
            >
              Reintentar
            </button>
          </div>
        ) : items.length === 0 ? (
          <div
            className="px-6 py-10 text-center text-sm text-slate-500"
            data-testid="empty-state"
          >
            {statusFilter === "pending"
              ? "No hay casos pendientes en el periodo seleccionado."
              : "No hay casos en el periodo seleccionado."}
          </div>
        ) : (
          <div className="divide-y divide-slate-100 dark:divide-slate-800">
            {items.map((caso) => (
              <ReconciliationRow
                key={caso.publicId}
                caso={caso}
                onResolved={reload}
                onApprovalRequired={handleApprovalRequired}
              />
            ))}
          </div>
        )}

        {/* ─ Footer de paginacion (solo si hay items) ─────────────────────────── */}
        {!loading && !error && items.length > 0 && (
          <div className="border-t border-slate-100 dark:border-slate-800 px-6 py-4">
            <PaginationFooter
              page={page}
              pageSize={pageSize}
              totalCount={totalCount}
              totalPages={totalPages}
              hasPreviousPage={hasPreviousPage}
              hasNextPage={hasNextPage}
              onPageChange={setPage}
              onPageSizeChange={handlePageSizeChange}
            />
          </div>
        )}
      </div>

      {/* Modal de solicitud de aprobacion — se abre cuando anular un recibo requiere cuatro ojos.
          Vive en la pagina (no en la fila) para evitar instanciar N modales simultaneos.
          onCreated cierra el modal; no necesitamos reload porque el recibo no cambio de estado
          (la anulacion quedara pendiente hasta que el aprobador la resuelva). */}
      <RequestApprovalModal
        isOpen={Boolean(approvalContext)}
        onClose={() => setApprovalContext(null)}
        onCreated={() => setApprovalContext(null)}
        requestType={approvalContext?.requestType}
        entityType={approvalContext?.entityType}
        entityId={approvalContext?.entityId}
        entityLabel={approvalContext?.entityLabel}
      />
    </div>
  );
}
