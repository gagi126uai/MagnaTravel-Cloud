/**
 * Hook para cargar la lista de reembolsos pendientes de UN operador
 * (endpoint /suppliers/{id}/operator-refunds/pending).
 *
 * El modo global (sin supplierPublicId) se eliminó junto con la bandeja
 * /operator-refunds (decisión 5, spec 2026-07-03 P1=C): los reembolsos se ven
 * operador por operador, en la solapa "Reembolsos" de cada ficha.
 *
 * Expone items, estado de carga, error y la función reload (botón "Actualizar").
 * No hay polling automático: el agente refresca manualmente.
 *
 * Patrón idéntico a useDebitNotePendingList del módulo de cancelaciones.
 *
 * @param {string} supplierPublicId - GUID del proveedor (obligatorio).
 */

import { useState, useEffect, useCallback } from "react";
import { operatorRefundsApi } from "../api/operatorRefundsApi";

export function useOperatorRefundsPending(supplierPublicId) {
  const [items, setItems] = useState([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(null);

  // useCallback evita que fetchData cambie de referencia en cada render,
  // lo cual dispararía el useEffect en loop.
  const fetchData = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const data = await operatorRefundsApi.getPendingBySupplier(supplierPublicId);
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
