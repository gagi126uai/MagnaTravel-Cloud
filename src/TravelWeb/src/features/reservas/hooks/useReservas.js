import { useState, useEffect, useCallback } from "react";
import { api } from "../../../api";
import { showConfirm, showError, showSuccess, showWarning } from "../../../alerts";
import { useDebounce } from "../../../hooks/useDebounce";
import { getApiErrorMessage, isDatabaseUnavailableError } from "../../../lib/errors";
import { getPublicId } from "../../../lib/publicIds";
import { getReservaArchiveBlockReason } from "../archiveRules";

const emptyPage = {
  items: [],
  page: 1,
  pageSize: 25,
  totalCount: 0,
  totalPages: 0,
  hasPreviousPage: false,
  hasNextPage: false,
  summary: {
    budgetCount: 0,
    activeCount: 0,
    reservedCount: 0,
    operativeCount: 0,
    closedCount: 0,
    totalSaleActive: 0,
    totalCostActive: 0,
    totalPendingBalance: 0,
    grossProfit: 0,
  },
};

export function useReservas() {
  const [reservasPage, setReservasPage] = useState(emptyPage);
  const [loading, setLoading] = useState(true);
  const [searchTerm, setSearchTerm] = useState("");
  const [viewFilter, setViewFilter] = useState("active");
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(25);
  const [databaseUnavailable, setDatabaseUnavailable] = useState(false);
  
  // Filtro de período por defecto: Últimos 90 días
  const today = new Date();
  const [dateRange, setDateRange] = useState({
    from: "",
    to: "", // vacío = hasta hoy
    preset: "month" // all, 90days, 365days, custom, month — default: mes en curso
  });

  const [currentMonth, setCurrentMonth] = useState(new Date(today.getFullYear(), today.getMonth(), 1));

  const debouncedSearch = useDebounce(searchTerm, 300);

  const loadReservas = useCallback(async () => {
    setLoading(true);
    try {
      const params = new URLSearchParams({
        page: String(page),
        pageSize: String(pageSize),
        view: viewFilter,
      });

      if (dateRange.preset === "month") {
        const from = new Date(currentMonth.getFullYear(), currentMonth.getMonth(), 1);
        const to = new Date(currentMonth.getFullYear(), currentMonth.getMonth() + 1, 0, 23, 59, 59);
        params.set("createdFrom", from.toISOString());
        params.set("createdTo", to.toISOString());
      } else if (dateRange.preset !== "all") {
        if (dateRange.from) {
          params.set("createdFrom", new Date(`${dateRange.from}T00:00:00Z`).toISOString());
        }
        if (dateRange.to) {
          params.set("createdTo", new Date(`${dateRange.to}T23:59:59Z`).toISOString());
        }
      }

      if (debouncedSearch.trim()) {
        params.set("search", debouncedSearch.trim());
      }

      const data = await api.get(`/reservas?${params.toString()}`);
      setReservasPage({ ...emptyPage, ...(data || {}) });
      setDatabaseUnavailable(false);
    } catch (error) {
      console.error(error);
      setReservasPage(emptyPage);
      setDatabaseUnavailable(isDatabaseUnavailableError(error));
      showError(`Error cargando reservas: ${getApiErrorMessage(error, "Error desconocido")}`);
    } finally {
      setLoading(false);
    }
  }, [debouncedSearch, page, pageSize, viewFilter, dateRange, currentMonth]);

  useEffect(() => {
    loadReservas();
  }, [loadReservas]);

  useEffect(() => {
    setPage(1);
  }, [debouncedSearch, viewFilter, pageSize]);

  const handleArchive = async (reservaOrPublicId) => {
    const reserva = typeof reservaOrPublicId === "object" ? reservaOrPublicId : null;
    const blockReason = getReservaArchiveBlockReason(reserva);
    if (blockReason) {
      showWarning(blockReason, "No se puede archivar");
      return false;
    }

    const publicId =
      typeof reservaOrPublicId === "string" ? reservaOrPublicId : getPublicId(reservaOrPublicId);
    const numeroReserva =
      typeof reservaOrPublicId === "object" && reservaOrPublicId?.numeroReserva
        ? `#${reservaOrPublicId.numeroReserva}`
        : "esta reserva";

    const confirmed = await showConfirm({
      title: "Archivar reserva",
      eyebrow: "Archivo",
      text: `La reserva ${numeroReserva} pasara a archivo y quedara solo para consulta.`,
      details: "No se elimina informacion. El historial, las cobranzas y los documentos se conservan.",
      confirmText: "Si, archivar",
      cancelText: "Seguir viendo",
      confirmColor: "amber",
    });

    if (!confirmed) return false;

    try {
      await api.put(`/reservas/${publicId}/archive`);
      showSuccess("Reserva archivada");
      await loadReservas();
      return true;
    } catch (error) {
      showError(getApiErrorMessage(error, "Error al archivar"));
      return false;
    }
  };

  const summary = reservasPage.summary || emptyPage.summary;

  return {
    reservas: reservasPage.items || [],
    loading,
    searchTerm,
    setSearchTerm,
    viewFilter,
    setViewFilter,
    page: reservasPage.page || page,
    pageSize: reservasPage.pageSize || pageSize,
    totalCount: reservasPage.totalCount || 0,
    totalPages: reservasPage.totalPages || 0,
    hasPreviousPage: Boolean(reservasPage.hasPreviousPage),
    setPage,
    setPageSize,
    dateRange,
    setDateRange,
    currentMonth,
    setCurrentMonth,
    loadReservas,
    handleArchive,
    databaseUnavailable,
    tabCounts: {
      budget: summary.budgetCount || 0,
      active: summary.activeCount || 0,
      reserved: summary.reservedCount || 0,
      operative: summary.operativeCount || 0,
      closed: summary.closedCount || 0,
    },
    stats: {
      budgetCount: summary.budgetCount || 0,
      activeCount: summary.activeCount || 0,
      operativeCount: summary.operativeCount || 0,
      totalSaleActive: summary.totalSaleActive || 0,
      totalCostActive: summary.totalCostActive || 0,
      totalPendingBalance: summary.totalPendingBalance || 0,
      grossProfit: summary.grossProfit || 0,
    },
  };
}
