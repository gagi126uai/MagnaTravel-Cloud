import { useState, useEffect, useCallback } from "react";
import { api } from "../../../api";
import { showConfirm, showError, showSuccess, showWarning } from "../../../alerts";
import { useDebounce } from "../../../hooks/useDebounce";
import { getApiErrorMessage, isDatabaseUnavailableError } from "../../../lib/errors";
import { getPublicId } from "../../../lib/publicIds";
import { getReservaArchiveBlockReason } from "../archiveRules";
import { calcularContadorTodas } from "../lib/reservaTabsMapping";

const emptyPage = {
  items: [],
  page: 1,
  pageSize: 25,
  totalCount: 0,
  totalPages: 0,
  hasPreviousPage: false,
  hasNextPage: false,
  summary: {
    // ADR-020: ciclo unico. Cada campo corresponde a un estado del backend.
    // ADR-036: toSettleCount eliminado — "A liquidar" ya no existe como estado visible.
    quotationCount: 0,
    budgetCount: 0,
    inManagementCount: 0,
    activeCount: 0,
    reservedCount: 0,
    operativeCount: 0,
    closedCount: 0,
    lostCount: 0,
    // Tanda 3 (2026-07-24, #38/#40): cancelledCount y archivedCount son campos nuevos del
    // backend (pestaña "Anuladas" separada de "Finalizadas" + contador de Archivadas). El
    // backend se termina en paralelo — si todavia no vienen en la respuesta, quedan en 0
    // (fallback: comportamiento actual, sin romper si el campo no esta presente).
    cancelledCount: 0,
    archivedCount: 0,
    // Tanda 1 rediseño listado (2026-08-04, P-3⭐): reemplazan a los viejos escalares
    // totalSaleActive/totalCostActive/totalPendingBalance/grossProfit, que mezclaban
    // pesos y dólares en un solo número (la regla más dura del producto es que las
    // monedas NUNCA se suman). Cada uno es una lista [{ currency, amount }].
    vendidoPorMoneda: [],
    porCobrarPorMoneda: [],
  },
};

export function useReservas() {
  const [reservasPage, setReservasPage] = useState(emptyPage);
  const [loading, setLoading] = useState(true);
  const [searchTerm, setSearchTerm] = useState("");
  const [viewFilter, setViewFilter] = useState("all");
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(25);
  const [databaseUnavailable, setDatabaseUnavailable] = useState(false);
  // Tanda 1 (2026-08-04, B5): mensaje del cartel rojo "no pudimos traer las reservas"
  // con botón "Probar de nuevo" (mismo patrón que la solapa Copias de seguridad). Se
  // separa de databaseUnavailable porque ese caso ya tiene su propia pantalla (DB
  // caída es distinto de "el pedido falló por otra razón": timeout, error 500, etc).
  const [loadError, setLoadError] = useState(null);
  // Obra 4 (firma de Gastón 2026-07-27): al entrar a Reservas la pestaña "Todas" queda
  // seleccionada y la lista muestra todo — "lo que se ve es lo que dice la pestaña".
  // Antes arrancaba en "active" (En gestión + Confirmadas), un filtro invisible para
  // quien recién entra a la pantalla.

  // Filtro de período por defecto: Últimos 90 días
  const today = new Date();
  const [dateRange, setDateRange] = useState({
    from: "",
    to: "", // vacío = hasta hoy
    preset: "month", // all, 90days, 365days, custom, month — default: mes en curso
    field: "created" // created | travel — sobre qué fecha filtrar
  });

  const [currentMonth, setCurrentMonth] = useState(new Date(today.getFullYear(), today.getMonth(), 1));

  const debouncedSearch = useDebounce(searchTerm, 300);

  const loadReservas = useCallback(async () => {
    setLoading(true);
    setLoadError(null);
    try {
      const params = new URLSearchParams({
        page: String(page),
        pageSize: String(pageSize),
        view: viewFilter,
        // Tanda 1 (2026-08-04, plan B3/A3): SOLO este buscador (el del listado de
        // Reservas) manda esta bandera — es la que le dice al motor "ignorá la
        // pestaña y el mes, buscá en todo". El motor la respeta únicamente cuando
        // además hay texto en "search"; sin texto no cambia nada, así que es seguro
        // mandarla siempre. Ninguna otra pantalla que necesite que la pestaña SÍ
        // filtre debe copiar esta línea.
        globalSearch: "true",
      });

      const fromKey = dateRange.field === "travel" ? "travelFrom" : "createdFrom";
      const toKey = dateRange.field === "travel" ? "travelTo" : "createdTo";

      if (dateRange.preset === "month") {
        const from = new Date(currentMonth.getFullYear(), currentMonth.getMonth(), 1);
        const to = new Date(currentMonth.getFullYear(), currentMonth.getMonth() + 1, 0, 23, 59, 59);
        params.set(fromKey, from.toISOString());
        params.set(toKey, to.toISOString());
      } else if (dateRange.preset !== "all") {
        if (dateRange.from) {
          params.set(fromKey, new Date(`${dateRange.from}T00:00:00Z`).toISOString());
        }
        if (dateRange.to) {
          params.set(toKey, new Date(`${dateRange.to}T23:59:59Z`).toISOString());
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
      const esBaseDeDatosCaida = isDatabaseUnavailableError(error);
      setDatabaseUnavailable(esBaseDeDatosCaida);
      // B5 (P-11: ningún error deja al usuario sin salida): la base de datos caída ya
      // tiene su propia pantalla (DatabaseUnavailableState, sin retry — es un estado
      // de infraestructura). Para cualquier OTRO error (red, timeout, 500) mostramos
      // el cartel rojo con "Probar de nuevo" en el mismo lugar de la tabla, en vez de
      // un toast que desaparece solo.
      if (!esBaseDeDatosCaida) {
        setLoadError(getApiErrorMessage(error, "No pudimos traer las reservas."));
      }
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
      text: `La reserva ${numeroReserva} pasa a archivo y queda solo para consulta.`,
      details: "No se elimina información. El historial, las cobranzas y los documentos se conservan.",
      confirmText: "Sí, archivar",
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
    // Fix de review (2026-08-04): la pagina decide "estoy buscando" con el MISMO texto que viajo
    // al motor (el debounced), no con lo que se esta tipeando — si no, durante los ~300 ms de
    // debounce la UI marca "Todas" mientras la lista todavia obedece a la solapa vieja.
    debouncedSearch,
    setSearchTerm,
    viewFilter,
    setViewFilter,
    page: reservasPage.page || page,
    pageSize: reservasPage.pageSize || pageSize,
    totalCount: reservasPage.totalCount || 0,
    totalPages: reservasPage.totalPages || 0,
    hasPreviousPage: Boolean(reservasPage.hasPreviousPage),
    // Bug de arrastre (no forma parte del plan de esta tanda, pero está en la misma
    // línea que se está tocando): faltaba devolver hasNextPage, así que el botón
    // "siguiente" de la paginación quedaba SIEMPRE deshabilitado (undefined es falsy).
    hasNextPage: Boolean(reservasPage.hasNextPage),
    setPage,
    setPageSize,
    dateRange,
    setDateRange,
    currentMonth,
    setCurrentMonth,
    loadReservas,
    handleArchive,
    databaseUnavailable,
    loadError,
    tabCounts: {
      // ADR-020: todos los estados del ciclo unico, sin flags.
      // ADR-036: toSettle eliminado — "A liquidar" ya no existe como estado visible en la UI.
      // Tanda 3 (2026-07-24, #38/#40): las claves "confirmed"/"traveling" reemplazan a las
      // legacy "reserved"/"operative" para que coincidan 1 a 1 con el value de cada pestaña
      // (ver reservaTabsMapping.js). El campo del resumen (ReservedCount/OperativeCount) no
      // cambio de nombre en el backend, solo la clave que usa el frontend para leerlo.
      quotation: summary.quotationCount || 0,
      budget: summary.budgetCount || 0,
      inManagement: summary.inManagementCount || 0,
      active: summary.activeCount || 0,
      confirmed: summary.reservedCount || 0,
      traveling: summary.operativeCount || 0,
      closed: summary.closedCount || 0,
      // cancelledCount/archivedCount: campos nuevos del backend (fallback 0 si aun no vienen).
      cancelled: summary.cancelledCount || 0,
      lost: summary.lostCount || 0,
      archived: summary.archivedCount || 0,
      // H20 (2026-07-25): pestaña "Todas" — suma de los 9 estados excluyentes (ver
      // calcularContadorTodas). No hay campo "AllCount" propio en el backend.
      all: calcularContadorTodas(summary),
    },
    // Tanda 1 (2026-08-04, B1): la tira de KPIs de arriba del listado quedó en solo
    // 3 números (activas / por cobrar / vendido) — "Operativos" y "Rentabilidad
    // estimada" murieron (ver ReservaKPIs.jsx). Los importes viajan por moneda,
    // nunca como escalar mezclado (P-3⭐).
    stats: {
      activeCount: summary.activeCount || 0,
      vendidoPorMoneda: summary.vendidoPorMoneda || [],
      porCobrarPorMoneda: summary.porCobrarPorMoneda || [],
    },
  };
}
