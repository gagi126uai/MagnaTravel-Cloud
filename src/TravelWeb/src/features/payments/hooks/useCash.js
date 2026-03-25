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

  const loadData = useCallback(async () => {
    setLoading(true);
    try {
      const params = new URLSearchParams({
        page: String(page),
        pageSize: String(pageSize),
        direction: directionFilter,
        sourceType: sourceFilter,
      });

      if (debouncedSearch.trim()) {
        params.set("search", debouncedSearch.trim());
      }

      const [summaryRes, movementsRes] = await Promise.all([
        api.get("/treasury/cash-summary"),
        api.get(`/treasury/movements?${params.toString()}`),
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
  }, [debouncedSearch, directionFilter, page, pageSize, sourceFilter]);

  useEffect(() => {
    loadData();
  }, [loadData]);

  useEffect(() => {
    setPage(1);
  }, [debouncedSearch, directionFilter, sourceFilter, pageSize]);

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
    ...actions,
  };
}
