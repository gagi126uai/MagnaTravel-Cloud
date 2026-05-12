import { useEffect, useMemo, useRef } from "react";

// Tiempos del backoff adaptativo (ms).
const PHASE_FAST_INTERVAL = 3_000;   // primeros 30 s
const PHASE_SLOW_INTERVAL = 10_000;  // hasta 2 min
const PHASE_FAST_DURATION = 30_000;  // duración de la fase rápida
const MAX_DURATION       = 120_000;  // stop después de 2 min

/**
 * Polling adaptativo mientras haya items en estado transitorio.
 *
 * - Detecta si `items` contiene alguno con fiscalStatus === "in_progress"
 *   o invoices con annulmentStatus === "Pending".
 * - Si hay → fast poll (3 s) por 30 s, luego slow poll (10 s) hasta 2 min, luego stop.
 * - Si no hay → no hace nada.
 * - Al cambiar `items` el timer se reinicia solo si la condición sigue activa.
 *
 * @param {object[]} items  Lista de work items o invoices visibles en pantalla.
 * @param {Function} reload Callback que dispara la recarga (ya provisto por el padre).
 * @param {object}  [options]
 * @param {number}  [options.fastInterval]  ms para fase rápida (default 3000).
 * @param {number}  [options.slowInterval]  ms para fase lenta (default 10000).
 * @param {number}  [options.fastDuration]  ms hasta cambiar de fase (default 30000).
 * @param {number}  [options.maxDuration]   ms hasta detener el polling (default 120000).
 */
export function useInvoicePolling(items, reload, options = {}) {
  const {
    fastInterval = PHASE_FAST_INTERVAL,
    slowInterval = PHASE_SLOW_INTERVAL,
    fastDuration = PHASE_FAST_DURATION,
    maxDuration  = MAX_DURATION,
  } = options;

  // Ref estable para el callback — evita reiniciar el efecto cuando `reload`
  // cambia de referencia entre renders.
  const reloadRef = useRef(reload);
  useEffect(() => { reloadRef.current = reload; }, [reload]);

  // Derivado booleano para evitar reiniciar el timer cuando `items` cambia
  // de referencia pero la condicion sigue siendo la misma.
  const hasPending = useMemo(
    () => items.some(
      (item) =>
        item?.fiscalStatus === "in_progress" ||
        item?.annulmentStatus === "Pending" ||
        // Soporte para items planos de MovementsTimeline (invoice con resultado PENDING)
        item?.resultado === "PENDING"
    ),
    [items]
  );

  useEffect(() => {
    if (!hasPending) return;

    const startedAt = Date.now();
    let timeoutId;

    const schedule = () => {
      const elapsed = Date.now() - startedAt;
      if (elapsed >= maxDuration) return; // stop

      const interval = elapsed < fastDuration ? fastInterval : slowInterval;
      timeoutId = setTimeout(async () => {
        await reloadRef.current();
        schedule();
      }, interval);
    };

    schedule();

    return () => {
      clearTimeout(timeoutId);
    };
  }, [hasPending, fastInterval, slowInterval, fastDuration, maxDuration]);
}
