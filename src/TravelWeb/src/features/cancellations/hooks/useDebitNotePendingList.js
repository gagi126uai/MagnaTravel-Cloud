/**
 * Hook para cargar la bandeja de cargos de cancelacion pendientes.
 *
 * Llama a GET /api/cancellations/debit-notes/pending y expone
 * el estado de carga, error y la funcion de recarga manual.
 *
 * Patron identico a useCreditNoteReconciliation: fetch al montar,
 * sin polling (el agente usa el boton "Actualizar" para refrescar).
 */

import { useState, useEffect, useCallback } from "react";
import { cancellationsApi } from "../api/cancellationsApi";

export function useDebitNotePendingList() {
  const [items, setItems] = useState([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(null);

  // Encapsulamos el fetch en useCallback para poder pasarlo como "reload"
  // sin que cambie su referencia en cada render (evita loops en useEffect).
  const fetchData = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const data = await cancellationsApi.getPendingDebitNotes();
      setItems(data || []);
    } catch (err) {
      setError(err);
    } finally {
      setLoading(false);
    }
  }, []);

  // useEffect con dependencia vacia: carga los datos una sola vez al montar.
  // El usuario puede refrescar manualmente con "reload".
  useEffect(() => {
    fetchData();
  }, [fetchData]);

  return { items, loading, error, reload: fetchData };
}
