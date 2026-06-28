/**
 * Hook para cargar la lista de reembolsos pendientes del operador.
 *
 * - Sin supplierPublicId → usa el endpoint GLOBAL (/operator-refunds/pending).
 * - Con supplierPublicId → usa el endpoint de ese proveedor (/suppliers/{id}/operator-refunds/pending).
 *
 * Expone items, estado de carga, error y la función reload (botón "Actualizar").
 * No hay polling automático: el agente refresca manualmente.
 *
 * Patrón idéntico a useDebitNotePendingList del módulo de cancelaciones.
 *
 * @param {string|null} supplierPublicId - GUID del proveedor, o null para la bandeja global.
 */

import { useState, useEffect, useCallback } from "react";
import { operatorRefundsApi } from "../api/operatorRefundsApi";

export function useOperatorRefundsPending(supplierPublicId = null) {
  const [items, setItems] = useState([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(null);

  // useCallback evita que fetchData cambie de referencia en cada render,
  // lo cual dispararía el useEffect en loop.
  const fetchData = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const data = supplierPublicId
        ? await operatorRefundsApi.getPendingBySupplier(supplierPublicId)
        : await operatorRefundsApi.getPending();
      setItems(data || []);
    } catch (err) {
      setError(err);
    } finally {
      setLoading(false);
    }
  }, [supplierPublicId]);

  // useEffect con [fetchData]: carga datos al montar y cada vez que cambia
  // el proveedor (cuando supplierPublicId cambia, fetchData cambia también).
  useEffect(() => {
    fetchData();
  }, [fetchData]);

  return { items, loading, error, reload: fetchData };
}
