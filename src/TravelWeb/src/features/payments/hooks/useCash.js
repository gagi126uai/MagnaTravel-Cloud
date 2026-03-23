import { useCallback, useEffect, useMemo, useState } from "react";
import { api } from "../../../api";
import { showError } from "../../../alerts";
import { useFinanceActions } from "./useFinanceActions";

export function useCash() {
  const [loading, setLoading] = useState(true);
  const [summary, setSummary] = useState(null);
  const [movements, setMovements] = useState([]);
  const [searchTerm, setSearchTerm] = useState("");
  const [directionFilter, setDirectionFilter] = useState("all");
  const [sourceFilter, setSourceFilter] = useState("all");

  const loadData = useCallback(async () => {
    setLoading(true);
    try {
      const [summaryRes, movementsRes] = await Promise.all([
        api.get("/treasury/cash-summary"),
        api.get("/treasury/movements"),
      ]);

      setSummary(summaryRes);
      setMovements((movementsRes || []).sort((a, b) => new Date(b.occurredAt) - new Date(a.occurredAt)));
    } catch (error) {
      console.error("Error loading cash module:", error);
      showError("Error al cargar caja.");
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    loadData();
  }, [loadData]);

  const actions = useFinanceActions(loadData);

  const filteredMovements = useMemo(() => {
    const needle = searchTerm.trim().toLowerCase();

    return movements.filter((movement) => {
      const matchesSearch =
        !needle ||
        movement.description?.toLowerCase().includes(needle) ||
        movement.reference?.toLowerCase().includes(needle) ||
        movement.numeroReserva?.toLowerCase().includes(needle) ||
        movement.supplierName?.toLowerCase().includes(needle) ||
        movement.method?.toLowerCase().includes(needle);

      const matchesDirection =
        directionFilter === "all" || movement.direction?.toLowerCase() === directionFilter;

      const matchesSource =
        sourceFilter === "all" ||
        movement.sourceType === sourceFilter;

      return matchesSearch && matchesDirection && matchesSource;
    });
  }, [movements, searchTerm, directionFilter, sourceFilter]);

  return {
    loading,
    summary,
    movements: filteredMovements,
    searchTerm,
    setSearchTerm,
    directionFilter,
    setDirectionFilter,
    sourceFilter,
    setSourceFilter,
    loadData,
    ...actions,
  };
}
