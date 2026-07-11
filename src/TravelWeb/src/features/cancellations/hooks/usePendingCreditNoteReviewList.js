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

/**
 * @param {boolean} enabled - ADR-044 T4 fix F1 (2026-07-10): "Comprobantes por
 *   resolver" funde esta bandeja con la de multas, pero ESTE endpoint exige el
 *   permiso `cobranzas.view_all` — un usuario que solo tenga `cobranzas.invoice_annul`
 *   (el mínimo para ver la parte de multas) recibiría un 403 si igual la llamáramos.
 *   `enabled=false` salta el fetch por completo (items queda vacío, sin loading ni
 *   error). Default `true`: el comportamiento de siempre (bandeja standalone) no
 *   cambia para quien ya la usaba.
 */
export function usePendingCreditNoteReviewList(enabled = true) {
  const [items, setItems] = useState([]);
  const [loading, setLoading] = useState(enabled);
  const [error, setError] = useState(null);

  // Encapsulamos el fetch en useCallback para que la referencia sea estable
  // y no cause loops si se pasa como dependencia de un useEffect externo.
  const fetchData = useCallback(async () => {
    if (!enabled) return;
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
  }, [enabled]);

  // useEffect con dependencia vacía: carga los datos una sola vez al montar.
  // El usuario puede refrescar con el botón "Actualizar" que llama a reload.
  useEffect(() => {
    fetchData();
  }, [fetchData]);

  return { items, loading, error, reload: fetchData };
}
