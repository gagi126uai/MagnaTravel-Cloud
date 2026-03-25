import { useCallback, useEffect, useState } from "react";
import { api } from "../../../api";
import { showError } from "../../../alerts";
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

export function useCollections() {
  const [loading, setLoading] = useState(true);
  const [summary, setSummary] = useState(null);
  const [collectionsPage, setCollectionsPage] = useState(emptyPage);
  const [searchTerm, setSearchTerm] = useState("");
  const [urgencyFilter, setUrgencyFilter] = useState("all");
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
        urgency: urgencyFilter,
        sortBy: "startDate",
        sortDir: urgencyFilter === "urgent" ? "asc" : "desc",
      });

      if (debouncedSearch.trim()) {
        params.set("search", debouncedSearch.trim());
      }

      const [summaryRes, worklistRes] = await Promise.all([
        api.get("/payments/collections-summary"),
        api.get(`/payments/collections-worklist?${params.toString()}`),
      ]);

      setSummary(summaryRes);
      setCollectionsPage({ ...emptyPage, ...(worklistRes || {}) });
      setDatabaseUnavailable(false);
    } catch (error) {
      console.error("Error loading collections:", error);
      setCollectionsPage(emptyPage);
      setDatabaseUnavailable(isDatabaseUnavailableError(error));
      showError("Error al cargar cobranzas.");
    } finally {
      setLoading(false);
    }
  }, [debouncedSearch, page, pageSize, urgencyFilter]);

  useEffect(() => {
    loadData();
  }, [loadData]);

  useEffect(() => {
    setPage(1);
  }, [debouncedSearch, urgencyFilter, pageSize]);

  return {
    loading,
    summary,
    items: collectionsPage.items || [],
    searchTerm,
    setSearchTerm,
    urgencyFilter,
    setUrgencyFilter,
    page: collectionsPage.page || page,
    pageSize: collectionsPage.pageSize || pageSize,
    totalCount: collectionsPage.totalCount || 0,
    totalPages: collectionsPage.totalPages || 0,
    hasPreviousPage: Boolean(collectionsPage.hasPreviousPage),
    hasNextPage: Boolean(collectionsPage.hasNextPage),
    setPage,
    setPageSize,
    databaseUnavailable,
    loadData,
  };
}
