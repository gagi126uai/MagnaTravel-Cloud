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
  const [worklistStatus, setWorklistStatus] = useState("ready");
  const [worklistSearchTerm, setWorklistSearchTerm] = useState("");
  const [worklistCustomerFilter, setWorklistCustomerFilter] = useState("");
  const [worklistReservationFilter, setWorklistReservationFilter] = useState("");
  const [worklistPage, setWorklistPage] = useState(1);
  const [worklistPageSize, setWorklistPageSize] = useState(25);
  const [invoiceKind, setInvoiceKind] = useState("issued");
  const [invoiceSearchTerm, setInvoiceSearchTerm] = useState("");
  const [invoicePeriod, setInvoicePeriod] = useState("");
  const [invoiceCustomerFilter, setInvoiceCustomerFilter] = useState("");
  const [invoiceReservationFilter, setInvoiceReservationFilter] = useState("");
  const [invoiceVoucherNumberFilter, setInvoiceVoucherNumberFilter] = useState("");
  const [invoiceResultFilter, setInvoiceResultFilter] = useState("all");
  const [invoicePage, setInvoicePage] = useState(1);
  const [invoicePageSize, setInvoicePageSize] = useState(25);
  const [databaseUnavailable, setDatabaseUnavailable] = useState(false);

  const debouncedWorklistSearch = useDebounce(worklistSearchTerm, 300);
  const debouncedWorklistCustomer = useDebounce(worklistCustomerFilter, 300);
  const debouncedWorklistReservation = useDebounce(worklistReservationFilter, 300);
  const debouncedInvoiceSearch = useDebounce(invoiceSearchTerm, 300);
  const debouncedInvoiceCustomer = useDebounce(invoiceCustomerFilter, 300);
  const debouncedInvoiceReservation = useDebounce(invoiceReservationFilter, 300);
  const debouncedInvoiceVoucherNumber = useDebounce(invoiceVoucherNumberFilter, 300);

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

      if (debouncedWorklistSearch.trim()) worklistParams.set("search", debouncedWorklistSearch.trim());
      if (debouncedWorklistCustomer.trim()) worklistParams.set("customer", debouncedWorklistCustomer.trim());
      if (debouncedWorklistReservation.trim()) worklistParams.set("reservation", debouncedWorklistReservation.trim());

      if (debouncedInvoiceSearch.trim()) invoicesParams.set("search", debouncedInvoiceSearch.trim());
      if (invoicePeriod) invoicesParams.set("period", invoicePeriod);
      if (debouncedInvoiceCustomer.trim()) invoicesParams.set("customer", debouncedInvoiceCustomer.trim());
      if (debouncedInvoiceReservation.trim()) invoicesParams.set("reservation", debouncedInvoiceReservation.trim());
      if (debouncedInvoiceVoucherNumber.trim()) invoicesParams.set("voucherNumber", debouncedInvoiceVoucherNumber.trim());
      if (invoiceResultFilter !== "all") invoicesParams.set("result", invoiceResultFilter);

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
  }, [
    debouncedInvoiceCustomer,
    debouncedInvoiceReservation,
    debouncedInvoiceSearch,
    debouncedInvoiceVoucherNumber,
    debouncedWorklistCustomer,
    debouncedWorklistReservation,
    debouncedWorklistSearch,
    invoiceKind,
    invoicePage,
    invoicePageSize,
    invoicePeriod,
    invoiceResultFilter,
    worklistPage,
    worklistPageSize,
    worklistStatus,
  ]);

  useEffect(() => { loadData(); }, [loadData]);

  useEffect(() => { setWorklistPage(1); }, [debouncedWorklistSearch, debouncedWorklistCustomer, debouncedWorklistReservation, worklistStatus, worklistPageSize]);
  useEffect(() => { setInvoicePage(1); }, [debouncedInvoiceSearch, debouncedInvoiceCustomer, debouncedInvoiceReservation, debouncedInvoiceVoucherNumber, invoiceKind, invoicePageSize, invoicePeriod, invoiceResultFilter]);

  const actions = useFinanceActions(loadData);

  return {
    loading,
    summary,
    workItems: workItemsPage.items || [],
    invoices: invoicesPage.items || [],
    worklistStatus,
    setWorklistStatus,
    worklistSearchTerm,
    setWorklistSearchTerm,
    worklistCustomerFilter,
    setWorklistCustomerFilter,
    worklistReservationFilter,
    setWorklistReservationFilter,
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
    invoiceSearchTerm,
    setInvoiceSearchTerm,
    invoicePeriod,
    setInvoicePeriod,
    invoiceCustomerFilter,
    setInvoiceCustomerFilter,
    invoiceReservationFilter,
    setInvoiceReservationFilter,
    invoiceVoucherNumberFilter,
    setInvoiceVoucherNumberFilter,
    invoiceResultFilter,
    setInvoiceResultFilter,
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
