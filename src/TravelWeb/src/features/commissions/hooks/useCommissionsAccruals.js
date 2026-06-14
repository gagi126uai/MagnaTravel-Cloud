/**
 * Hook que carga el detalle de comisiones de un vendedor en un rango de fechas.
 * Solo carga si `sellerUserId` tiene valor (se activa cuando el usuario hace clic
 * en un vendedor de la lista).
 *
 * Devuelve: { items, loading, error, reload }.
 *  - items: CommissionAccrualDto[] con { publicId, reservaPublicId, reservaNumber,
 *            currency, amount, ratePercent, status, createdAt, ... }
 */
import { useEffect, useState, useCallback } from "react";
import { fetchCommissionsAccruals } from "../api/commissionsApi";
import { getApiErrorMessage } from "../../../lib/errors";

export function useCommissionsAccruals(sellerUserId, from, to) {
  const [items, setItems] = useState([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState(null);

  const load = useCallback(async () => {
    // Si no hay vendedor seleccionado, limpiamos la lista y salimos.
    if (!sellerUserId || !from || !to) {
      setItems([]);
      return;
    }

    setLoading(true);
    setError(null);

    try {
      const result = await fetchCommissionsAccruals(sellerUserId, from, to);
      // El backend puede devolver PagedResponse con .items, o directamente un array.
      setItems(Array.isArray(result) ? result : (result?.items ?? []));
    } catch (err) {
      setError(getApiErrorMessage(err, "No se pudo cargar el detalle de comisiones."));
      setItems([]);
    } finally {
      setLoading(false);
    }
  }, [sellerUserId, from, to]);

  useEffect(() => {
    load();
  }, [load]);

  return { items, loading, error, reload: load };
}
