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

/**
 * @param {boolean} enabled - ADR-044 T4 (2026-07-10): "Comprobantes por resolver"
 *   funde esta bandeja con la de notas de crédito por revisar, pero el endpoint de
 *   ESTA bandeja exige el permiso `cobranzas.invoice_annul` — un usuario que solo
 *   tenga `cobranzas.view_all` (el mínimo para entrar a Facturación) recibiría un 403
 *   si igual la llamáramos. `enabled=false` salta el fetch por completo (items queda
 *   vacío, sin loading ni error) para que el componente pueda mostrar SOLO la parte
 *   de notas de crédito sin generar un error falso. Default `true`: el comportamiento
 *   de siempre (bandeja standalone) no cambia para quien ya la usaba.
 */
export function useDebitNotePendingList(enabled = true) {
  const [items, setItems] = useState([]);
  const [loading, setLoading] = useState(enabled);
  const [error, setError] = useState(null);

  // Encapsulamos el fetch en useCallback para poder pasarlo como "reload"
  // sin que cambie su referencia en cada render (evita loops en useEffect).
  const fetchData = useCallback(async () => {
    if (!enabled) return;
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
  }, [enabled]);

  // useEffect con dependencia [enabled, fetchData]: si "enabled" pasa de false a true
  // (ej. el usuario recibe el permiso en otra pestaña y esta se remonta), carga recién ahí.
  useEffect(() => {
    fetchData();
  }, [fetchData]);

  return { items, loading, error, reload: fetchData };
}
