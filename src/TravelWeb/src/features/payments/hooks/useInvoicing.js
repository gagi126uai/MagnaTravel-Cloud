import { useCallback, useEffect, useMemo, useState } from "react";
import { api } from "../../../api";
import { showError } from "../../../alerts";
import { useDebounce } from "../../../hooks/useDebounce";
import { isDatabaseUnavailableError } from "../../../lib/errors";
import { creditNoteTypes } from "../lib/financeUtils";
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

export function useInvoicing() {
  const [loading, setLoading] = useState(true);
  const [summary, setSummary] = useState(null);
  const [workItems, setWorkItems] = useState([]);
  const [invoicesPage, setInvoicesPage] = useState(emptyPage);
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
        sortBy: "createdAt",
        sortDir: "desc",
      });

      if (debouncedSearch.trim()) {
        params.set("search", debouncedSearch.trim());
      }

      const [summaryRes, worklistRes, invoicesRes] = await Promise.all([
        api.get("/invoices/summary"),
        api.get("/invoices/worklist"),
        api.get(`/invoices?${params.toString()}`),
      ]);

      setSummary(summaryRes);
      setWorkItems(worklistRes || []);
      setInvoicesPage({ ...emptyPage, ...(invoicesRes || {}) });
      setDatabaseUnavailable(false);
    } catch (error) {
      console.error("Error loading invoicing:", error);
      setInvoicesPage(emptyPage);
      setDatabaseUnavailable(isDatabaseUnavailableError(error));
      showError("Error al cargar facturacion.");
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

  const filteredWorkItems = useMemo(() => {
    const needle = debouncedSearch.trim().toLowerCase();
    if (!needle) {
      return workItems;
    }

    return workItems.filter((item) => {
      return (
        item.numeroReserva?.toLowerCase().includes(needle) ||
        item.customerName?.toLowerCase().includes(needle) ||
        item.fiscalStatusLabel?.toLowerCase().includes(needle) ||
        item.economicBlockReason?.toLowerCase().includes(needle)
      );
    });
  }, [debouncedSearch, workItems]);

  const invoices = invoicesPage.items || [];
  const issuedInvoices = useMemo(() => {
    return invoices.filter((invoice) => !creditNoteTypes.includes(invoice.tipoComprobante));
  }, [invoices]);

  const creditNotes = useMemo(() => {
    return invoices.filter((invoice) => creditNoteTypes.includes(invoice.tipoComprobante));
  }, [invoices]);

  return {
    loading,
    summary,
    workItems: filteredWorkItems,
    invoices,
    issuedInvoices,
    creditNotes,
    searchTerm,
    setSearchTerm,
    page: invoicesPage.page || page,
    pageSize: invoicesPage.pageSize || pageSize,
    totalCount: invoicesPage.totalCount || 0,
    totalPages: invoicesPage.totalPages || 0,
    hasPreviousPage: Boolean(invoicesPage.hasPreviousPage),
    hasNextPage: Boolean(invoicesPage.hasNextPage),
    setPage,
    setPageSize,
    loadData,
    databaseUnavailable,
    ...actions,
  };
}
