/**
 * Hook de datos para la pantalla global de Facturación.
 *
 * Llama a GET /api/invoices con filtros server-side y paginación.
 * El endpoint devuelve PagedResponse<InvoiceListDto> con todos los comprobantes
 * de la agencia (scope según permisos del usuario — ver GetOwnerScopeOrNullAsync).
 *
 * Cuándo re-fetcha: cada vez que cambian filters, page o pageSize.
 * El componente padre es responsable de resetear page=1 al cambiar filtros.
 *
 * Guarda de respuestas obsoletas (stale-response guard):
 *   El filtro "buscarNumero" tiene debounce en el padre, pero igual pueden llegar
 *   dos requests en vuelo simultáneamente (paginación + filtro, por ejemplo).
 *   requestIdRef asegura que solo el último request actualiza el estado.
 */
import { useCallback, useEffect, useRef, useState } from "react";
import { api } from "../../../api";
import { getApiErrorMessage } from "../../../lib/errors";
import { buildInvoiceQueryParams } from "../lib/facturacionGlobalFilters";

export function useFacturacionGlobal({ filters, page, pageSize }) {
  const [items, setItems] = useState([]);
  const [cargando, setCargando] = useState(true);
  const [error, setError] = useState(null);
  const [totalCount, setTotalCount] = useState(0);
  const [totalPages, setTotalPages] = useState(0);
  const [hasPreviousPage, setHasPreviousPage] = useState(false);
  const [hasNextPage, setHasNextPage] = useState(false);

  // Contador que se incrementa con cada llamada; permite descartar respuestas viejas.
  const requestIdRef = useRef(0);

  const cargar = useCallback(async () => {
    // Capturamos el ID de ESTE request antes del await.
    // Si llega otro request antes de que este termine, requestIdRef.current será mayor
    // y los setters de abajo no van a pisar el estado con datos obsoletos.
    const requestId = ++requestIdRef.current;

    setCargando(true);
    setError(null);
    try {
      const params = buildInvoiceQueryParams(filters, page, pageSize);
      const response = await api.get(`/invoices?${params.toString()}`);

      // Guardia: si mientras esperábamos llegó un request más nuevo, ignorar esta respuesta.
      if (requestId !== requestIdRef.current) return;

      setItems(response?.items ?? []);
      setTotalCount(response?.totalCount ?? 0);
      setTotalPages(response?.totalPages ?? 0);
      setHasPreviousPage(response?.hasPreviousPage ?? false);
      setHasNextPage(response?.hasNextPage ?? false);
    } catch (err) {
      if (requestId !== requestIdRef.current) return;
      setError(getApiErrorMessage(err) || "No se pudieron cargar los comprobantes.");
    } finally {
      // Solo apagamos el spinner si este request sigue siendo el vigente.
      // Si no lo es, el spinner lo apagará el request más nuevo cuando termine.
      if (requestId === requestIdRef.current) {
        setCargando(false);
      }
    }
  }, [filters, page, pageSize]); // Re-carga cuando cambian filtros o paginación

  // useEffect con dependencia en `cargar` (que incluye filters/page/pageSize).
  // Cada vez que alguno cambia, cargar() se recrea y este effect se re-ejecuta.
  useEffect(() => {
    cargar();
  }, [cargar]);

  return {
    items,
    cargando,
    error,
    totalCount,
    totalPages,
    hasPreviousPage,
    hasNextPage,
    reload: cargar,
  };
}
