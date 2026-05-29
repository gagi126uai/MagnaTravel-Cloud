import { useCallback, useEffect, useState } from "react";
import { creditNoteReconciliationApi } from "../api/creditNoteReconciliationApi";

/**
 * Hook para cargar y paginar la bandeja de reconciliacion de NC parciales.
 *
 * Recibe los filtros activos (status, year, month, page, pageSize) como parametros
 * y los manda al backend cada vez que cambian. Devuelve la respuesta paginada completa
 * (items + metadatos de paginacion) junto con estados de loading y error.
 *
 * Uso tipico:
 *   const { data, loading, error, reload } = useCreditNoteReconciliation({ status, year, month, page, pageSize });
 *
 * @param {Object} filters - Filtros actuales de la bandeja.
 * @param {string} filters.status - "pending" | "resolved" | "all"
 * @param {number|null} filters.year - Anio del filtro mensual (null = todo el historial).
 * @param {number|null} filters.month - Mes 1..12 (null = todo el historial).
 * @param {number} filters.page - Pagina actual.
 * @param {number} filters.pageSize - Items por pagina.
 */
export function useCreditNoteReconciliation({ status, year, month, page, pageSize }) {
  // `data` es el PagedResponse completo: { items, page, pageSize, totalCount, totalPages, hasNextPage, hasPreviousPage }
  const [data, setData] = useState(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(null);

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const response = await creditNoteReconciliationApi.list({ status, year, month, page, pageSize });
      // La API devuelve data directo (el api client ya extrae el body).
      setData(response);
    } catch (err) {
      console.error("useCreditNoteReconciliation:", err);
      setError(err);
      setData(null);
    } finally {
      setLoading(false);
    }
  }, [status, year, month, page, pageSize]);

  // useEffect se dispara cada vez que cambia alguno de los filtros (reflejados en `load`).
  useEffect(() => {
    load();
  }, [load]);

  return {
    // Lista de casos del backend (array de PartialCreditNoteReconciliationDto).
    items: data?.items ?? [],
    // Metadatos de paginacion del PagedResponse.
    totalCount: data?.totalCount ?? 0,
    totalPages: data?.totalPages ?? 0,
    hasNextPage: data?.hasNextPage ?? false,
    hasPreviousPage: data?.hasPreviousPage ?? false,
    loading,
    error,
    // Permite que la pagina fuerce un re-fetch (ej. despues de anular un recibo).
    reload: load,
  };
}
