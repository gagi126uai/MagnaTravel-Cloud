/**
 * Hook para cargar la bandeja "Notas de crédito por revisar".
 *
 * Llama a GET /api/cancellations/pending-credit-note-review y expone
 * el estado de carga, error y la función de recarga manual.
 *
 * HOY la lista viene vacía casi siempre: el flujo NC parcial (ADR-025 §3)
 * está congelado hasta la firma del contador. El empty state de la bandeja
 * lo explica al usuario para que no lo interprete como un error.
 *
 * Patrón idéntico a useDebitNotePendingList.
 */

import { useState, useEffect, useCallback } from "react";
import { cancellationsApi } from "../api/cancellationsApi";

export function usePendingCreditNoteReviewList() {
  const [items, setItems] = useState([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(null);

  // Encapsulamos el fetch en useCallback para que la referencia sea estable
  // y no cause loops si se pasa como dependencia de un useEffect externo.
  const fetchData = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const data = await cancellationsApi.pendingCreditNoteReview();
      setItems(data || []);
    } catch (err) {
      setError(err);
    } finally {
      setLoading(false);
    }
  }, []);

  // useEffect con dependencia vacía: carga los datos una sola vez al montar.
  // El usuario puede refrescar con el botón "Actualizar" que llama a reload.
  useEffect(() => {
    fetchData();
  }, [fetchData]);

  return { items, loading, error, reload: fetchData };
}
