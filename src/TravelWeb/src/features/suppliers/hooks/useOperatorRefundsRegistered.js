/**
 * Hook para cargar (paginado) la lista de reembolsos YA REGISTRADOS de UN operador
 * (endpoint /suppliers/{id}/operator-refunds/registered).
 *
 * Hermano de useOperatorRefundsPending: ese carga lo que FALTA cobrarle al operador,
 * este carga lo que YA se anotó como recibido (vivo y deshecho) — la solapa lo usa para
 * el bloque "Reembolsos ya registrados" (Tanda P2, spec 2026-07-22).
 *
 * Sigue el mismo patrón que useCreditNoteReconciliation: `data` guarda el PagedResponse
 * completo del backend, y el hook expone items + metadatos de paginación por separado.
 *
 * @param {{ supplierPublicId: string, page: number, pageSize: number, enabled?: boolean }} params
 */

import { useState, useEffect, useCallback } from "react";
import { operatorRefundsApi } from "../api/operatorRefundsApi";

export function useOperatorRefundsRegistered({ supplierPublicId, page, pageSize, enabled = true }) {
  const [data, setData] = useState(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(null);

  // useCallback evita que fetchData cambie de referencia en cada render, lo cual
  // dispararía el useEffect de abajo en loop.
  const fetchData = useCallback(async () => {
    if (!enabled || !supplierPublicId) {
      setData(null);
      setLoading(false);
      setError(null);
      return;
    }
    setLoading(true);
    setError(null);
    try {
      const response = await operatorRefundsApi.getRegisteredBySupplier(supplierPublicId, { page, pageSize });
      setData(response);
    } catch (err) {
      setError(err);
      setData(null);
    } finally {
      setLoading(false);
    }
  }, [supplierPublicId, page, pageSize, enabled]);

  // useEffect con [fetchData]: carga datos al montar y cada vez que cambia el proveedor,
  // la página o el tamaño de página (fetchData cambia de referencia cuando cambia alguno).
  useEffect(() => {
    fetchData();
  }, [fetchData]);

  return {
    items: data?.items ?? [],
    page: data?.page ?? page,
    pageSize: data?.pageSize ?? pageSize,
    totalCount: data?.totalCount ?? 0,
    totalPages: data?.totalPages ?? 0,
    hasNextPage: data?.hasNextPage ?? false,
    hasPreviousPage: data?.hasPreviousPage ?? false,
    loading,
    error,
    reload: fetchData,
  };
}
