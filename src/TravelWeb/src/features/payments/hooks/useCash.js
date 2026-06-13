import { useCallback, useEffect, useState } from "react";
import { api } from "../../../api";
import { showError } from "../../../alerts";
import { useFinanceActions } from "./useFinanceActions";
import { useDebounce } from "../../../hooks/useDebounce";
import { isDatabaseUnavailableError } from "../../../lib/errors";

const emptyPage = {
  items: [],
  page: 1,
  pageSize: 25,
  totalCount: 0,
  totalPages: 0,
  hasPreviousPage: false,
  hasNextPage: false,
};

/**
 * Devuelve true si el Date recibido corresponde al mes actual del cliente.
 * Comparamos solo año+mes (no el día ni la hora) para evitar problemas de zona horaria.
 */
function esElMesActual(date) {
  const hoy = new Date();
  return (
    date.getFullYear() === hoy.getFullYear() &&
    date.getMonth() === hoy.getMonth()
  );
}

/**
 * Hook de datos de la pantalla Caja.
 *
 * Gestiona:
 * - Navegación mensual (selectedMonth): el mes que se muestra.
 * - Filtros de la lista (búsqueda, dirección, origen, página).
 * - Las dos llamadas al backend: /treasury/cash-summary y /treasury/movements.
 * - CRUD de movimientos manuales (vía useFinanceActions).
 *
 * El backend acepta year+month como enteros opcionales en ambos endpoints.
 * Sin esos params devuelve el mes actual; con ellos filtra al mes indicado.
 */
export function useCash() {
  const [loading, setLoading] = useState(true);
  const [summary, setSummary] = useState(null);
  const [movementsPage, setMovementsPage] = useState(emptyPage);
  const [searchTerm, setSearchTerm] = useState("");
  const [directionFilter, setDirectionFilter] = useState("all");
  const [sourceFilter, setSourceFilter] = useState("all");
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(25);
  const [databaseUnavailable, setDatabaseUnavailable] = useState(false);
  const debouncedSearch = useDebounce(searchTerm, 300);

  // Mes seleccionado: guardamos el primer día del mes como Date.
  // Arrancamos en el mes actual del cliente (no en UTC, para evitar bugs de zona horaria).
  const [selectedMonth, setSelectedMonth] = useState(() => {
    const hoy = new Date();
    return new Date(hoy.getFullYear(), hoy.getMonth(), 1);
  });

  // Navegación de mes: avanzar y retroceder un mes.
  // Creamos siempre el primer día del mes destino para mantener el invariante del estado.
  const goToPreviousMonth = useCallback(() => {
    setSelectedMonth((current) => new Date(current.getFullYear(), current.getMonth() - 1, 1));
    // Al cambiar de mes, volvemos a la primera página para no quedar en una página huérfana.
    setPage(1);
  }, []);

  const goToNextMonth = useCallback(() => {
    setSelectedMonth((current) => new Date(current.getFullYear(), current.getMonth() + 1, 1));
    setPage(1);
  }, []);

  // canGoNext: solo true si el mes elegido es ANTERIOR al mes actual.
  // Deshabilita el botón ▶ en el mes actual (y defensivamente en cualquier
  // mes futuro): no hay datos futuros en caja. Comparamos año*12+mes para
  // que sea robusto a zona horaria y al cruce de año.
  const canGoNext = (() => {
    const hoy = new Date();
    const selIndex = selectedMonth.getFullYear() * 12 + selectedMonth.getMonth();
    const hoyIndex = hoy.getFullYear() * 12 + hoy.getMonth();
    return selIndex < hoyIndex;
  })();

  const loadData = useCallback(async () => {
    setLoading(true);
    try {
      // Mandamos year+month como enteros al backend en ambas llamadas.
      // El backend los acepta juntos o no los acepta en absoluto (sin uno solo).
      const selectedYear = selectedMonth.getFullYear();
      const selectedMonthNumber = selectedMonth.getMonth() + 1; // getMonth() devuelve 0-11

      const summaryParams = new URLSearchParams({
        year: String(selectedYear),
        month: String(selectedMonthNumber),
      });

      const movementsParams = new URLSearchParams({
        page: String(page),
        pageSize: String(pageSize),
        direction: directionFilter,
        sourceType: sourceFilter,
        year: String(selectedYear),
        month: String(selectedMonthNumber),
      });

      if (debouncedSearch.trim()) {
        movementsParams.set("search", debouncedSearch.trim());
      }

      const [summaryRes, movementsRes] = await Promise.all([
        api.get(`/treasury/cash-summary?${summaryParams.toString()}`),
        api.get(`/treasury/movements?${movementsParams.toString()}`),
      ]);

      setSummary(summaryRes);
      setMovementsPage({ ...emptyPage, ...(movementsRes || {}) });
      setDatabaseUnavailable(false);
    } catch (error) {
      console.error("Error loading cash module:", error);
      setMovementsPage(emptyPage);
      setDatabaseUnavailable(isDatabaseUnavailableError(error));
      showError("Error al cargar caja.");
    } finally {
      setLoading(false);
    }
  }, [debouncedSearch, directionFilter, page, pageSize, sourceFilter, selectedMonth]);

  // useEffect principal: se ejecuta cada vez que cambia loadData (que incluye cambios de mes,
  // página, filtros y búsqueda debounceada).
  useEffect(() => {
    loadData();
  }, [loadData]);

  // Al cambiar filtros o de mes, volvemos a la página 1
  // para que no quede mostrando una página inexistente en el nuevo conjunto de datos.
  // selectedMonth se incluye porque cambia el universo de movimientos por completo.
  useEffect(() => {
    setPage(1);
  }, [debouncedSearch, directionFilter, sourceFilter, pageSize, selectedMonth]);

  const actions = useFinanceActions(loadData);

  return {
    loading,
    summary,
    movements: movementsPage.items || [],
    searchTerm,
    setSearchTerm,
    directionFilter,
    setDirectionFilter,
    sourceFilter,
    setSourceFilter,
    page: movementsPage.page || page,
    pageSize: movementsPage.pageSize || pageSize,
    totalCount: movementsPage.totalCount || 0,
    totalPages: movementsPage.totalPages || 0,
    hasPreviousPage: Boolean(movementsPage.hasPreviousPage),
    hasNextPage: Boolean(movementsPage.hasNextPage),
    setPage,
    setPageSize,
    loadData,
    databaseUnavailable,
    // Navegación mensual
    selectedMonth,
    goToPreviousMonth,
    goToNextMonth,
    // handleMonthChange: recibe un Date (primer día del mes) y actualiza el mes seleccionado.
    // Lo usa MonthNavigator a través de su prop onChange.
    handleMonthChange: setSelectedMonth,
    canGoNext,
    ...actions,
  };
}
