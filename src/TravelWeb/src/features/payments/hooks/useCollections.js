import { useCallback, useEffect, useMemo, useState } from "react";
import { api } from "../../../api";
import { showError } from "../../../alerts";

export function useCollections() {
  const [loading, setLoading] = useState(true);
  const [summary, setSummary] = useState(null);
  const [items, setItems] = useState([]);
  const [searchTerm, setSearchTerm] = useState("");
  const [urgencyFilter, setUrgencyFilter] = useState("all");

  const loadData = useCallback(async () => {
    setLoading(true);
    try {
      const [summaryRes, worklistRes] = await Promise.all([
        api.get("/payments/collections-summary"),
        api.get("/payments/collections-worklist"),
      ]);

      setSummary(summaryRes);
      setItems(worklistRes || []);
    } catch (error) {
      console.error("Error loading collections:", error);
      showError("Error al cargar cobranzas.");
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    loadData();
  }, [loadData]);

  const filteredItems = useMemo(() => {
    const needle = searchTerm.trim().toLowerCase();

    return items.filter((item) => {
      const matchesSearch =
        !needle ||
        item.numeroReserva?.toLowerCase().includes(needle) ||
        item.customerName?.toLowerCase().includes(needle) ||
        item.responsibleUserName?.toLowerCase().includes(needle);

      const matchesUrgency =
        urgencyFilter === "all" ||
        (urgencyFilter === "urgent" && item.urgencyStatus === "Urgente") ||
        (urgencyFilter === "blocked" && (item.blocksOperational || item.blocksVoucher));

      return matchesSearch && matchesUrgency;
    });
  }, [items, searchTerm, urgencyFilter]);

  return {
    loading,
    summary,
    items: filteredItems,
    searchTerm,
    setSearchTerm,
    urgencyFilter,
    setUrgencyFilter,
    loadData,
  };
}
