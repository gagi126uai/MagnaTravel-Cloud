/**
 * Hook que carga el resumen mensual de comisiones.
 * Se re-ejecuta cada vez que cambian el año o el mes.
 *
 * Devuelve: { data, loading, error, reload }.
 *  - data: { year, month, sellers: [ { sellerUserId, sellerName, totalsByCurrency } ] }
 *  - error: string o null.
 */
import { useEffect, useState, useCallback } from "react";
import { fetchCommissionsSummary } from "../api/commissionsApi";
import { getApiErrorMessage } from "../../../lib/errors";

export function useCommissionsSummary(year, month) {
  const [data, setData] = useState(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(null);

  const load = useCallback(async () => {
    // Si no tenemos año y mes válidos todavía, no hacemos nada.
    if (!year || !month) return;

    setLoading(true);
    setError(null);

    try {
      const result = await fetchCommissionsSummary(year, month);
      setData(result);
    } catch (err) {
      setError(getApiErrorMessage(err, "No se pudieron cargar las comisiones del mes."));
      setData(null);
    } finally {
      setLoading(false);
    }
  }, [year, month]);

  // Cargamos cada vez que cambia el mes o el año.
  useEffect(() => {
    load();
  }, [load]);

  return { data, loading, error, reload: load };
}
