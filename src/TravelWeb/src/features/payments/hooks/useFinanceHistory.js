import { useCallback, useEffect, useState } from "react";
import { api } from "../../../api";
import { showError } from "../../../alerts";
import { useDebounce } from "../../../hooks/useDebounce";
import { isDatabaseUnavailableError } from "../../../lib/errors";
import { useFinanceActions } from "./useFinanceActions";

const emptyPage = {
  items: [],
  page: 1,
  pageSize: 25,
  totalCount: 0,
  totalPages: 0,
  hasPreviousPage: false,
  hasNextPage: false,
};

export function useFinanceHistory() {
  const [loading, setLoading] = useState(true);
  const [historyPage, setHistoryPage] = useState(emptyPage);
  const [searchTerm, setSearchTerm] = useState("");
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
        sortBy: "occurredAt",
        sortDir: "desc",
      });

      if (debouncedSearch.trim()) {
        params.set("search", debouncedSearch.trim());
      }

      const response = await api.get(`/payments/history?${params.toString()}`);
      setHistoryPage({ ...emptyPage, ...(response || {}) });
      setDatabaseUnavailable(false);
    } catch (error) {
      console.error("Error loading finance history:", error);
      setHistoryPage(emptyPage);
      setDatabaseUnavailable(isDatabaseUnavailableError(error));
      showError("Error al cargar historial.");
    } finally {
      setLoading(false);
    }
  }, [debouncedSearch, page, pageSize]);

  useEffect(() => {
    loadData();
  }, [loadData]);

  useEffect(() => {
    setPage(1);
  }, [debouncedSearch, pageSize]);

  const actions = useFinanceActions(loadData);

  return {
    loading,
    timeline: historyPage.items || [],
    searchTerm,
    setSearchTerm,
    page: historyPage.page || page,
    pageSize: historyPage.pageSize || pageSize,
    totalCount: historyPage.totalCount || 0,
    totalPages: historyPage.totalPages || 0,
    hasPreviousPage: Boolean(historyPage.hasPreviousPage),
    hasNextPage: Boolean(historyPage.hasNextPage),
    setPage,
    setPageSize,
    loadData,
    databaseUnavailable,
    ...actions,
  };
}
