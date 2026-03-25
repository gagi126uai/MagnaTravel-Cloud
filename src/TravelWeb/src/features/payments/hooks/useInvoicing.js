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

export function useInvoicing() {
  const [loading, setLoading] = useState(true);
  const [summary, setSummary] = useState(null);
  const [workItemsPage, setWorkItemsPage] = useState(emptyPage);
  const [invoicesPage, setInvoicesPage] = useState(emptyPage);
  const [searchTerm, setSearchTerm] = useState("");
  const [worklistStatus, setWorklistStatus] = useState("ready");
  const [worklistPage, setWorklistPage] = useState(1);
  const [worklistPageSize, setWorklistPageSize] = useState(25);
  const [invoiceKind, setInvoiceKind] = useState("issued");
  const [invoicePage, setInvoicePage] = useState(1);
  const [invoicePageSize, setInvoicePageSize] = useState(25);
  const [databaseUnavailable, setDatabaseUnavailable] = useState(false);
  const debouncedSearch = useDebounce(searchTerm, 300);

  const loadData = useCallback(async () => {
    setLoading(true);
    try {
      const worklistParams = new URLSearchParams({
        page: String(worklistPage),
        pageSize: String(worklistPageSize),
        status: worklistStatus,
        sortBy: "startDate",
        sortDir: worklistStatus === "ready" ? "asc" : "desc",
      });

      const invoicesParams = new URLSearchParams({
        page: String(invoicePage),
        pageSize: String(invoicePageSize),
        kind: invoiceKind,
        sortBy: "createdAt",
        sortDir: "desc",
      });

      if (debouncedSearch.trim()) {
        worklistParams.set("search", debouncedSearch.trim());
        invoicesParams.set("search", debouncedSearch.trim());
      }

      const [summaryRes, worklistRes, invoicesRes] = await Promise.all([
        api.get("/invoices/summary"),
        api.get(`/invoices/worklist?${worklistParams.toString()}`),
        api.get(`/invoices?${invoicesParams.toString()}`),
      ]);

      setSummary(summaryRes);
      setWorkItemsPage({ ...emptyPage, ...(worklistRes || {}) });
      setInvoicesPage({ ...emptyPage, ...(invoicesRes || {}) });
      setDatabaseUnavailable(false);
    } catch (error) {
      console.error("Error loading invoicing:", error);
      setWorkItemsPage(emptyPage);
      setInvoicesPage(emptyPage);
      setDatabaseUnavailable(isDatabaseUnavailableError(error));
      showError("Error al cargar facturacion.");
    } finally {
      setLoading(false);
    }
  }, [debouncedSearch, invoiceKind, invoicePage, invoicePageSize, worklistPage, worklistPageSize, worklistStatus]);

  useEffect(() => {
    loadData();
  }, [loadData]);

  useEffect(() => {
    setWorklistPage(1);
  }, [debouncedSearch, worklistStatus, worklistPageSize]);

  useEffect(() => {
    setInvoicePage(1);
  }, [debouncedSearch, invoiceKind, invoicePageSize]);

  const actions = useFinanceActions(loadData);

  return {
    loading,
    summary,
    workItems: workItemsPage.items || [],
    invoices: invoicesPage.items || [],
    searchTerm,
    setSearchTerm,
    worklistStatus,
    setWorklistStatus,
    worklistPage: workItemsPage.page || worklistPage,
    worklistPageSize: workItemsPage.pageSize || worklistPageSize,
    worklistTotalCount: workItemsPage.totalCount || 0,
    worklistTotalPages: workItemsPage.totalPages || 0,
    worklistHasPreviousPage: Boolean(workItemsPage.hasPreviousPage),
    worklistHasNextPage: Boolean(workItemsPage.hasNextPage),
    setWorklistPage,
    setWorklistPageSize,
    invoiceKind,
    setInvoiceKind,
    invoicePage: invoicesPage.page || invoicePage,
    invoicePageSize: invoicesPage.pageSize || invoicePageSize,
    invoiceTotalCount: invoicesPage.totalCount || 0,
    invoiceTotalPages: invoicesPage.totalPages || 0,
    invoiceHasPreviousPage: Boolean(invoicesPage.hasPreviousPage),
    invoiceHasNextPage: Boolean(invoicesPage.hasNextPage),
    setInvoicePage,
    setInvoicePageSize,
    loadData,
    databaseUnavailable,
    ...actions,
  };
}
