import { useCallback, useEffect, useState } from "react";
import { approvalsApi } from "../api/approvalsApi";

// Hook genérico para listar approvals. mode = "pending" | "mine".
export function useApprovalsList(mode = "pending") {
  const [items, setItems] = useState([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(null);

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const data = mode === "pending"
        ? await approvalsApi.getPending()
        : await approvalsApi.getMyRequests();
      setItems(Array.isArray(data) ? data : []);
    } catch (err) {
      console.error("useApprovalsList:", err);
      setError(err);
      setItems([]);
    } finally {
      setLoading(false);
    }
  }, [mode]);

  useEffect(() => {
    load();
  }, [load]);

  return { items, loading, error, reload: load };
}

// Contador de pending para el badge del sidebar. Se refresca al montar y
// expone una funcion para forzar reload (utiles al aprobar/rechazar inline).
export function useApprovalsPendingCount(enabled = true) {
  const [count, setCount] = useState(0);

  const reload = useCallback(async () => {
    if (!enabled) return;
    try {
      const data = await approvalsApi.getPending();
      setCount(Array.isArray(data) ? data.length : 0);
    } catch (err) {
      // Si no tiene permiso o falla, no spamear logs.
      setCount(0);
    }
  }, [enabled]);

  useEffect(() => {
    reload();
  }, [reload]);

  return { count, reload };
}
