/**
 * Pantalla "Comisiones" — visible SOLO para el dueño/admin.
 *
 * Muestra cuánto ganó cada vendedor en un mes, calculado como un % de la ganancia
 * de cada reserva. El admin navega por mes con el MonthNavigator y, al tocar un
 * vendedor de la lista, ve el detalle reserva por reserva.
 *
 * Ruta: /commissions
 * Permiso: isAdmin() — mismo patrón que /admin en App.jsx.
 *
 * Regla de negocio: nunca sumar pesos con dólares. Cada vendedor muestra
 * su total por moneda en líneas separadas.
 */

import { useState } from "react";
import { useNavigate } from "react-router-dom";
import { TrendingUp, ChevronRight, AlertCircle, RefreshCw, ArrowLeft, ExternalLink } from "lucide-react";
import { MonthNavigator, monthToBounds } from "../../../components/ui/MonthNavigator";
import { useCommissionsSummary } from "../hooks/useCommissionsSummary";
import { useCommissionsAccruals } from "../hooks/useCommissionsAccruals";
import { formatCurrency } from "../../../lib/utils";

// ─── Helpers ─────────────────────────────────────────────────────────────────

/**
 * Formatea una fecha ISO como "03/06/2026".
 * Usa la zona horaria local para evitar que el timezone la corra un día.
 */
function formatFechaCorta(isoString) {
  if (!isoString) return "—";
  const d = new Date(isoString);
  if (isNaN(d.getTime())) return "—";
  return d.toLocaleDateString("es-AR", { day: "2-digit", month: "2-digit", year: "numeric" });
}

/**
 * Etiqueta del estado de una acumulación de comisión.
 * Los valores vienen del backend como strings (ej. "Earned", "Pending", "Cancelled").
 */
function EstadoComisionBadge({ status }) {
  const estilos = {
    Earned:    "bg-emerald-100 text-emerald-700 dark:bg-emerald-900/30 dark:text-emerald-300",
    Pending:   "bg-amber-100 text-amber-700 dark:bg-amber-900/20 dark:text-amber-300",
    Cancelled: "bg-slate-100 text-slate-500 dark:bg-slate-800 dark:text-slate-400",
  };
  const etiquetas = {
    Earned:    "Ganada",
    Pending:   "Pendiente",
    Cancelled: "Cancelada",
  };
  const clases = estilos[status] ?? "bg-slate-100 text-slate-500";
  const texto  = etiquetas[status] ?? status ?? "—";
  return (
    <span className={`rounded-full px-2 py-0.5 text-[10px] font-black uppercase ${clases}`}>
      {texto}
    </span>
  );
}

// ─── Sub-componente: lista de vendedores del mes ──────────────────────────────

/**
 * Lista de tarjetas de vendedores con su total por moneda del mes.
 * Al hacer clic en una fila se abre el detalle de ese vendedor.
 */
function ListaVendedores({ sellers, sellerIdActivo, onSeleccionarVendedor }) {
  if (sellers.length === 0) {
    return (
      <div
        className="flex flex-col items-center justify-center py-16 text-center text-slate-400 dark:text-slate-500"
        data-testid="empty-commissions"
      >
        <TrendingUp className="mb-3 h-10 w-10 opacity-30" aria-hidden="true" />
        <p className="text-sm font-medium">Sin comisiones este mes.</p>
        <p className="mt-1 text-xs">
          Cuando se registren cobros en reservas con comisión de vendedor, aparecen acá.
        </p>
      </div>
    );
  }

  return (
    <ul className="divide-y divide-slate-100 dark:divide-slate-800" data-testid="seller-list">
      {sellers.map((seller) => {
        const isActive = seller.sellerUserId === sellerIdActivo;
        return (
          <li key={seller.sellerUserId}>
            <button
              type="button"
              onClick={() => onSeleccionarVendedor(seller)}
              data-testid={`seller-row-${seller.sellerUserId}`}
              className={[
                "flex w-full items-center justify-between px-6 py-4 text-left transition-colors",
                isActive
                  ? "bg-indigo-50 dark:bg-indigo-900/20"
                  : "hover:bg-slate-50 dark:hover:bg-slate-800/50",
              ].join(" ")}
              aria-pressed={isActive}
            >
              {/* Nombre del vendedor */}
              <div className="min-w-0">
                <span className="block text-sm font-semibold text-slate-900 dark:text-white truncate">
                  {seller.sellerName ?? "Vendedor desconocido"}
                </span>
                {/* Total por moneda — nunca sumamos pesos con dólares */}
                <div className="mt-1 flex flex-wrap gap-2">
                  {(seller.totalsByCurrency ?? []).map(({ currency, amount }) => (
                    <span
                      key={currency}
                      className="text-xs font-medium text-emerald-700 dark:text-emerald-400"
                      data-testid={`seller-total-${seller.sellerUserId}-${currency}`}
                    >
                      {formatCurrency(amount, currency)}
                    </span>
                  ))}
                  {(seller.totalsByCurrency ?? []).length === 0 && (
                    <span className="text-xs text-slate-400">sin monto</span>
                  )}
                </div>
              </div>

              {/* Flecha de expansión */}
              <ChevronRight
                className={[
                  "h-4 w-4 flex-shrink-0 transition-colors",
                  isActive ? "text-indigo-600" : "text-slate-300 dark:text-slate-600",
                ].join(" ")}
                aria-hidden="true"
              />
            </button>
          </li>
        );
      })}
    </ul>
  );
}

// ─── Sub-componente: detalle de un vendedor ───────────────────────────────────

/**
 * Panel de detalle: lista de acumulaciones de comisión de un vendedor.
 * Una fila por reserva, con link para ir a la reserva.
 */
function DetalleVendedor({ sellerName, items, loading, error, onReload, onVolver, navigate }) {
  return (
    <div className="space-y-4" data-testid="seller-detail">
      {/* Encabezado del detalle */}
      <div className="flex items-center gap-3">
        <button
          type="button"
          onClick={onVolver}
          className="rounded-lg p-1.5 text-slate-500 hover:bg-slate-100 dark:hover:bg-slate-800 dark:text-slate-400"
          aria-label="Volver a la lista de vendedores"
          data-testid="btn-volver-lista"
        >
          <ArrowLeft className="h-4 w-4" aria-hidden="true" />
        </button>
        <h2 className="text-base font-bold text-slate-900 dark:text-white">
          {sellerName}
        </h2>
      </div>

      {/* Estado de carga */}
      {loading && (
        <div className="flex items-center justify-center py-10 text-slate-400" data-testid="detail-loading">
          <RefreshCw className="h-5 w-5 animate-spin mr-2" aria-hidden="true" />
          <span className="text-sm">Cargando detalle...</span>
        </div>
      )}

      {/* Error */}
      {!loading && error && (
        <div
          className="flex items-start gap-3 rounded-xl border border-red-200 bg-red-50 dark:border-red-900/40 dark:bg-red-900/20 px-4 py-3 text-sm text-red-700 dark:text-red-300"
          role="alert"
          data-testid="detail-error"
        >
          <AlertCircle className="h-4 w-4 flex-shrink-0 mt-0.5" aria-hidden="true" />
          <div className="flex-1">
            {error}
            <button
              type="button"
              onClick={onReload}
              className="ml-2 underline hover:no-underline"
            >
              Reintentar
            </button>
          </div>
        </div>
      )}

      {/* Tabla de acumulaciones */}
      {!loading && !error && items.length === 0 && (
        <div
          className="py-10 text-center text-sm text-slate-400"
          data-testid="detail-empty"
        >
          No hay comisiones detalladas para este vendedor en el período.
        </div>
      )}

      {!loading && !error && items.length > 0 && (
        <div className="overflow-x-auto rounded-xl border border-slate-200 dark:border-slate-800">
          <table className="w-full text-sm" aria-label={`Comisiones de ${sellerName}`}>
            <thead>
              <tr className="border-b border-slate-100 dark:border-slate-800 bg-slate-50 dark:bg-slate-800/50">
                <th className="px-4 py-2.5 text-left text-xs font-semibold uppercase tracking-wider text-slate-500 dark:text-slate-400">
                  Reserva
                </th>
                <th className="px-4 py-2.5 text-right text-xs font-semibold uppercase tracking-wider text-slate-500 dark:text-slate-400">
                  %
                </th>
                <th className="px-4 py-2.5 text-right text-xs font-semibold uppercase tracking-wider text-slate-500 dark:text-slate-400">
                  Comisión
                </th>
                <th className="px-4 py-2.5 text-center text-xs font-semibold uppercase tracking-wider text-slate-500 dark:text-slate-400">
                  Estado
                </th>
                <th className="px-4 py-2.5 text-left text-xs font-semibold uppercase tracking-wider text-slate-500 dark:text-slate-400">
                  Fecha
                </th>
                <th className="px-4 py-2.5" aria-label="Ir a la reserva" />
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-100 dark:divide-slate-800">
              {items.map((accrual) => (
                <tr
                  key={accrual.publicId}
                  className="hover:bg-slate-50 dark:hover:bg-slate-800/30"
                  data-testid={`accrual-row-${accrual.publicId}`}
                >
                  <td className="px-4 py-3 font-medium text-slate-900 dark:text-white">
                    {accrual.reservaNumber ?? accrual.reservaPublicId ?? "—"}
                  </td>
                  <td className="px-4 py-3 text-right text-slate-600 dark:text-slate-400">
                    {accrual.ratePercent != null ? `${accrual.ratePercent}%` : "—"}
                  </td>
                  <td className="px-4 py-3 text-right font-semibold text-emerald-700 dark:text-emerald-400">
                    {accrual.currency && accrual.amount != null
                      ? formatCurrency(accrual.amount, accrual.currency)
                      : "—"}
                  </td>
                  <td className="px-4 py-3 text-center">
                    <EstadoComisionBadge status={accrual.status} />
                  </td>
                  <td className="px-4 py-3 text-slate-500 dark:text-slate-400">
                    {formatFechaCorta(accrual.createdAt)}
                  </td>
                  <td className="px-4 py-3 text-right">
                    {accrual.reservaPublicId && (
                      <button
                        type="button"
                        onClick={() => navigate(`/reservas/${accrual.reservaPublicId}`)}
                        className="inline-flex items-center gap-1 rounded px-2 py-1 text-xs font-medium text-indigo-600 hover:bg-indigo-50 dark:text-indigo-300 dark:hover:bg-indigo-900/20"
                        title="Ir a la reserva"
                        data-testid={`btn-ir-reserva-${accrual.publicId}`}
                      >
                        <ExternalLink className="h-3.5 w-3.5" aria-hidden="true" />
                        Ver
                      </button>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}

// ─── Página principal ─────────────────────────────────────────────────────────

export default function CommissionsPage() {
  const navigate = useNavigate();

  // Mes activo: arrancamos en el mes actual.
  const [selectedMonth, setSelectedMonth] = useState(() => {
    const now = new Date();
    return new Date(now.getFullYear(), now.getMonth(), 1);
  });

  // Vendedor seleccionado para ver el detalle.
  const [selectedSeller, setSelectedSeller] = useState(null);

  // Convertimos el Date a year/month enteros para la API de resumen.
  const year = selectedMonth.getFullYear();
  const month = selectedMonth.getMonth() + 1; // getMonth() devuelve 0-11

  // Rango ISO para el endpoint de accruals (primer y último día del mes).
  const { from, to } = monthToBounds(selectedMonth);

  const { data: summaryData, loading: summaryLoading, error: summaryError, reload: reloadSummary } =
    useCommissionsSummary(year, month);

  const { items: accruals, loading: accrualsLoading, error: accrualsError, reload: reloadAccruals } =
    useCommissionsAccruals(selectedSeller?.sellerUserId ?? null, from, to);

  const sellers = summaryData?.sellers ?? [];

  // Cuando el usuario cambia el mes, volvemos a la lista (limpiamos el vendedor seleccionado).
  const handleMonthChange = (newMonth) => {
    setSelectedMonth(newMonth);
    setSelectedSeller(null);
  };

  const handleSeleccionarVendedor = (seller) => {
    // Si toca el mismo vendedor dos veces, lo deselecciona (toggle).
    setSelectedSeller((prev) => (prev?.sellerUserId === seller.sellerUserId ? null : seller));
  };

  const handleVolverALista = () => {
    setSelectedSeller(null);
  };

  return (
    <div className="space-y-6 max-w-5xl mx-auto pb-20 md:pb-0">
      {/* ─ Header de la pantalla ─────────────────────────────────────────── */}
      <div className="flex items-center gap-3">
        <div className="rounded-lg bg-indigo-100 p-2 text-indigo-700 dark:bg-indigo-900/30 dark:text-indigo-300">
          <TrendingUp className="h-5 w-5" aria-hidden="true" />
        </div>
        <div>
          <h1 className="text-2xl font-bold text-slate-900 dark:text-white">
            Comisiones
          </h1>
          <p className="text-sm text-slate-500 dark:text-slate-400">
            Comisiones de vendedor calculadas como un % de la ganancia de cada reserva.
            Se ganan al cobrar.
          </p>
        </div>
      </div>

      {/* ─ Panel principal ─────────────────────────────────────────────────── */}
      <div className="rounded-2xl border border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900">

        {/* Toolbar con el navegador de mes */}
        <div className="flex flex-col sm:flex-row sm:items-center gap-3 border-b border-slate-100 dark:border-slate-800 px-6 py-4">
          <MonthNavigator
            month={selectedMonth}
            onChange={handleMonthChange}
            disabled={summaryLoading}
            disableNext
          />
          {/* Botón de reload */}
          <button
            type="button"
            onClick={reloadSummary}
            disabled={summaryLoading}
            className="inline-flex items-center gap-1.5 rounded-lg border border-slate-300 dark:border-slate-600 px-3 py-1.5 text-xs font-semibold text-slate-700 dark:text-slate-200 hover:bg-slate-50 dark:hover:bg-slate-800 disabled:opacity-50"
            data-testid="btn-reload-summary"
          >
            <RefreshCw className={`h-3.5 w-3.5 ${summaryLoading ? "animate-spin" : ""}`} aria-hidden="true" />
            Actualizar
          </button>
        </div>

        {/* ─ Estado de carga del resumen ─────────────────────────────────── */}
        {summaryLoading && (
          <div
            className="flex items-center justify-center py-16 text-slate-400"
            data-testid="summary-loading"
          >
            <RefreshCw className="h-5 w-5 animate-spin mr-2" aria-hidden="true" />
            <span className="text-sm">Cargando comisiones...</span>
          </div>
        )}

        {/* ─ Error del resumen ───────────────────────────────────────────── */}
        {!summaryLoading && summaryError && (
          <div
            className="flex items-start gap-3 m-6 rounded-xl border border-red-200 bg-red-50 dark:border-red-900/40 dark:bg-red-900/20 px-4 py-3 text-sm text-red-700 dark:text-red-300"
            role="alert"
            data-testid="summary-error"
          >
            <AlertCircle className="h-4 w-4 flex-shrink-0 mt-0.5" aria-hidden="true" />
            <div className="flex-1">
              {summaryError}
              <button
                type="button"
                onClick={reloadSummary}
                className="ml-2 underline hover:no-underline"
              >
                Reintentar
              </button>
            </div>
          </div>
        )}

        {/* ─ Contenido principal (resumen + detalle) ─────────────────────── */}
        {!summaryLoading && !summaryError && (
          <div className="grid md:grid-cols-2 md:divide-x divide-slate-100 dark:divide-slate-800">

            {/* Columna izquierda: lista de vendedores */}
            <div>
              <ListaVendedores
                sellers={sellers}
                sellerIdActivo={selectedSeller?.sellerUserId ?? null}
                onSeleccionarVendedor={handleSeleccionarVendedor}
              />
            </div>

            {/* Columna derecha: detalle del vendedor seleccionado */}
            <div className="p-6">
              {selectedSeller ? (
                <DetalleVendedor
                  sellerName={selectedSeller.sellerName}
                  items={accruals}
                  loading={accrualsLoading}
                  error={accrualsError}
                  onReload={reloadAccruals}
                  onVolver={handleVolverALista}
                  navigate={navigate}
                />
              ) : (
                <div
                  className="flex h-full min-h-[12rem] items-center justify-center text-sm text-slate-400 dark:text-slate-500"
                  data-testid="detail-placeholder"
                >
                  {sellers.length > 0
                    ? "Seleccioná un vendedor para ver el detalle."
                    : "Sin comisiones en este mes."}
                </div>
              )}
            </div>

          </div>
        )}
      </div>
    </div>
  );
}
