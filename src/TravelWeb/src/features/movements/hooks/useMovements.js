import { useCallback, useEffect, useState } from "react";
import { movementsApi } from "../api/movementsApi";

// B1.15 Fase D' (2026-05-11): hook para cargar movements con filtros.
//
// Uso:
//   const { items, totalCount, loading, reload } = useMovements({ reservaId: 123 });
//
// Re-fetcha cuando cambia cualquier filtro. reload() forza refetch sin cambiar
// filtros (util tras un mutate desde otra accion).
export function useMovements(filters = {}) {
  const [items, setItems] = useState([]);
  const [totalCount, setTotalCount] = useState(0);
  const [totalPages, setTotalPages] = useState(0);
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(25);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(null);

  // Serializo filtros para deps estable. No incluyo page/pageSize del state
  // local — esos se manejan abajo.
  const filterKey = JSON.stringify({
    reservaId: filters.reservaId ?? null,
    customerId: filters.customerId ?? null,
    kinds: filters.kinds ?? null,
    dateFrom: filters.dateFrom ?? null,
    dateTo: filters.dateTo ?? null,
    search: filters.search ?? null,
  });

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const response = await movementsApi.list({ ...filters, page, pageSize });
      setItems(Array.isArray(response?.items) ? response.items : []);
      setTotalCount(response?.totalCount ?? 0);
      setTotalPages(response?.totalPages ?? 0);
    } catch (err) {
      console.error("useMovements:", err);
      setError(err);
      setItems([]);
      setTotalCount(0);
      setTotalPages(0);
    } finally {
      setLoading(false);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [filterKey, page, pageSize]);

  useEffect(() => {
    load();
  }, [load]);

  // Reset a page 1 cuando cambian los filtros (semantica esperable).
  useEffect(() => {
    setPage(1);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [filterKey]);

  return {
    items,
    totalCount,
    totalPages,
    page,
    pageSize,
    setPage,
    setPageSize,
    loading,
    error,
    reload: load,
  };
}
