import { useCallback, useEffect, useMemo, useState } from "react";
import { api } from "../../../api";
import { showError } from "../../../alerts";
import { creditNoteTypes } from "../lib/financeUtils";
import { useFinanceActions } from "./useFinanceActions";

export function useInvoicing() {
  const [loading, setLoading] = useState(true);
  const [summary, setSummary] = useState(null);
  const [workItems, setWorkItems] = useState([]);
  const [invoices, setInvoices] = useState([]);
  const [searchTerm, setSearchTerm] = useState("");

  const loadData = useCallback(async () => {
    setLoading(true);
    try {
      const [summaryRes, worklistRes, invoicesRes] = await Promise.all([
        api.get("/invoices/summary"),
        api.get("/invoices/worklist"),
        api.get("/invoices"),
      ]);

      setSummary(summaryRes);
      setWorkItems(worklistRes || []);
      setInvoices((invoicesRes || []).sort((a, b) => new Date(b.createdAt) - new Date(a.createdAt)));
    } catch (error) {
      console.error("Error loading invoicing:", error);
      showError("Error al cargar facturacion.");
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    loadData();
  }, [loadData]);

  const actions = useFinanceActions(loadData);

  const filteredWorkItems = useMemo(() => {
    const needle = searchTerm.trim().toLowerCase();
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
  }, [workItems, searchTerm]);

  const filteredInvoices = useMemo(() => {
    const needle = searchTerm.trim().toLowerCase();
    if (!needle) {
      return invoices;
    }

    return invoices.filter((invoice) => {
      return (
        invoice.reserva?.numeroReserva?.toLowerCase().includes(needle) ||
        invoice.reserva?.customerName?.toLowerCase().includes(needle) ||
        invoice.numeroComprobante?.toString().includes(needle) ||
        invoice.forceReason?.toLowerCase().includes(needle)
      );
    });
  }, [invoices, searchTerm]);

  const issuedInvoices = useMemo(() => {
    return filteredInvoices.filter((invoice) => !creditNoteTypes.includes(invoice.tipoComprobante));
  }, [filteredInvoices]);

  const creditNotes = useMemo(() => {
    return filteredInvoices.filter((invoice) => creditNoteTypes.includes(invoice.tipoComprobante));
  }, [filteredInvoices]);

  return {
    loading,
    summary,
    workItems: filteredWorkItems,
    invoices: filteredInvoices,
    issuedInvoices,
    creditNotes,
    searchTerm,
    setSearchTerm,
    loadData,
    ...actions,
  };
}
