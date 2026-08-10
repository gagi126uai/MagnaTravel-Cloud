/**
 * Matcher anti-duplicados INVISIBLE (decisión de Gastón, 2026-08-09 — reemplaza a la
 * "línea inteligente" visible que se revirtió entera). Cuando el buscador normal del
 * catálogo NO encuentra un parecido fuerte, este hook consulta al motor EN SILENCIO
 * para traer mejores candidatos y evitar que el vendedor cree un producto duplicado
 * (P7). El vendedor nunca se entera de que esta consulta existe.
 *
 * POST /api/reservas/{reservaId}/linea-inteligente — el mismo endpoint que ya usaba la
 * línea inteligente (SIEMPRE responde 200; sin clave, con el proveedor caído o con
 * demora, contesta `interpreted:false`, nunca un error). Acá se usan SOLO DOS campos de
 * la respuesta (`productCandidates` y `productSearchText`) — todo lo demás (operador,
 * variante, precio, fechas, duda) se descarta a propósito, ni siquiera llega al llamador.
 *
 * Reglas duras (degradación total, mismo criterio que tenía la línea inteligente):
 *   - Debounce de 600ms y UNA sola consulta en vuelo: si el texto cambió antes de que
 *     volviera la respuesta, esa respuesta se descarta (patrón "clave vigente", igual
 *     que `useVariantPriceSuggestion.js`).
 *   - Corte del lado del front a los 8.5s, contado desde que el PEDIDO REAL sale (no
 *     desde el debounce) — el motor ya corta a 8s (ADR-016 F0a).
 *   - Cualquier error (red caída, 4xx/5xx, timeout) se trata EXACTAMENTE igual que
 *     `interpreted:false`: nunca se expone acá arriba, nunca un console.error ruidoso.
 */

import { useEffect, useRef, useState } from "react";
import { api } from "../../../api";
import { debeDispararDedupMatch, esRespuestaUtilizable } from "./productDedupMatchLogic";

const DEBOUNCE_MS = 600;
// Margen sobre el corte de 8s que ya aplica el motor (ver comentario de arriba).
const TIMEOUT_MS = 8500;

/**
 * @param {{reservaId:string, serviceType:string, text:string, enabled:boolean}} params
 * @returns {{productCandidates:object[], productSearchText:string}|null}
 *   null cuando todavía no hay nada utilizable (incluye "no disparó", "no entendió" y
 *   "falló"): el llamador lo trata como "no hay ayuda extra, seguí con la lista de siempre".
 */
export function useProductDedupMatch({ reservaId, serviceType, text, enabled }) {
  const [dedupResult, setDedupResult] = useState(null);

  // Clave de la combinación vigente en TODO momento (no depende del closure del efecto),
  // para poder descartar una respuesta vieja aunque haya llegado con éxito.
  const claveVigenteRef = useRef("");
  const claveActual = `${serviceType || ""}::${text || ""}`;
  claveVigenteRef.current = claveActual;

  useEffect(() => {
    if (!enabled || !reservaId || !debeDispararDedupMatch(text)) {
      setDedupResult(null);
      return;
    }

    // Fix menor (revisor funcional, 2ª vuelta): también se limpia ACÁ, al entrar a la
    // rama que SÍ va a disparar una consulta nueva — sin esto, el resultado de la
    // consulta ANTERIOR (para un texto distinto) seguía visible durante los 600ms de
    // debounce de la consulta nueva. Un Enter rápido en ese instante podía crear un
    // producto usando el nombre limpio de la búsqueda vieja, no de la actual.
    setDedupResult(null);

    let cancelado = false;
    const claveConsultada = claveActual;
    // El corte de 8.5s arranca cuando el PEDIDO REAL sale (adentro del setTimeout de
    // abajo), no acá afuera — si arrancara ya, el corte real efectivo sería más corto
    // que 8.5s (perdería los 600ms del debounce).
    let controller = null;
    let timeoutId = null;

    const timer = setTimeout(async () => {
      if (cancelado) return;
      controller = new AbortController();
      timeoutId = setTimeout(() => controller.abort(), TIMEOUT_MS);
      try {
        const respuesta = await api.post(
          `/reservas/${reservaId}/linea-inteligente`,
          { text, serviceType },
          { signal: controller.signal }
        );
        if (cancelado || claveConsultada !== claveVigenteRef.current) return;
        if (esRespuestaUtilizable(respuesta)) {
          // SOLO estos dos campos viajan hacia afuera — el resto de la respuesta
          // (operador/variante/precio/fechas/duda) se descarta acá mismo, a propósito.
          setDedupResult({
            productCandidates: respuesta.productCandidates || [],
            productSearchText: respuesta.productSearchText || "",
          });
        } else {
          setDedupResult(null);
        }
      } catch {
        // Degradación total: red caída, timeout, 4xx/5xx — todo es "no hay ayuda extra".
        // Nunca se propaga un error ni se loguea nada hacia la consola.
        if (!cancelado && claveConsultada === claveVigenteRef.current) setDedupResult(null);
      } finally {
        clearTimeout(timeoutId);
      }
    }, DEBOUNCE_MS);

    return () => {
      cancelado = true;
      clearTimeout(timer);
      clearTimeout(timeoutId);
      controller?.abort();
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [reservaId, serviceType, text, enabled]);

  return dedupResult;
}
