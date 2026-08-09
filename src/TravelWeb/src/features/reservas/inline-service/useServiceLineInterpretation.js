/**
 * "La línea inteligente" (spec firmada 2026-08-07, §3 / M-20..M-23): mientras el
 * vendedor escribe la frase en el buscador de producto de la ficha de servicio, este
 * hook la manda al motor para que la interprete y devuelva el servicio armado.
 *
 * POST /api/reservas/{reservaId}/linea-inteligente — SIEMPRE responde 200 (el motor
 * nunca tira un error de "inteligencia artificial"; sin clave, con el proveedor caído o
 * con demora, contesta `interpreted:false`).
 *
 * Reglas duras que este hook cumple (§3.5, degradación total):
 *   - Solo dispara con 3+ palabras (ver `debeDispararInterpretacion`) — el motor tiene un
 *     tope de 40 pedidos/minuto por usuario, no hay que gastarlo con una palabra sola.
 *   - Debounce de 600ms y UNA sola consulta en vuelo: si el texto cambió antes de que
 *     volviera la respuesta, esa respuesta se descarta (mismo patrón de "clave vigente"
 *     que ya usa `useVariantPriceSuggestion.js` — ver ese archivo para el porqué).
 *   - Corte del lado del front a los 8.5s: el motor ya corta a 8s (ADR-016 F0a); este
 *     margen extra solo evita que una conexión colgada deje la ficha "pensando" para
 *     siempre si la respuesta nunca llega a completarse.
 *   - Cualquier error (red caída, 4xx/5xx, timeout) se trata EXACTAMENTE igual que
 *     `interpreted:false`: nunca se expone acá arriba. El componente que use este hook
 *     ve un `interpretation` en null y sigue funcionando como si la IA no existiera.
 */

import { useEffect, useRef, useState } from "react";
import { api } from "../../../api";
import { debeDispararInterpretacion, esRespuestaUtilizable } from "./serviceLineInterpretationLogic";

const DEBOUNCE_MS = 600;
// Margen sobre el corte de 8s que ya aplica el motor (ver comentario de arriba).
const TIMEOUT_MS = 8500;

/**
 * @param {{reservaId:string, serviceType:string, text:string, enabled:boolean}} params
 * @returns {{interpretation:object|null, isThinking:boolean}}
 *   `interpretation` es la última respuesta UTILIZABLE (interpreted:true) para el texto
 *   VIGENTE, o null si todavía no hay nada que aplicar (incluye "no entendió" y "falló").
 *   `isThinking` sirve para reusar el mismo "Buscando…" sutil que ya pinta el buscador
 *   de catálogo (spec §3.2) — nunca para mostrar un cartel nuevo.
 */
export function useServiceLineInterpretation({ reservaId, serviceType, text, enabled }) {
  const [interpretation, setInterpretation] = useState(null);
  const [isThinking, setIsThinking] = useState(false);

  // Clave de la combinación vigente en TODO momento (no depende del closure del efecto),
  // para poder descartar una respuesta vieja aunque haya llegado con éxito — el vendedor
  // pudo haber seguido escribiendo (o borrado todo) mientras la consulta viajaba.
  const claveVigenteRef = useRef("");
  const claveActual = `${serviceType || ""}::${text || ""}`;
  claveVigenteRef.current = claveActual;

  useEffect(() => {
    if (!enabled || !reservaId || !debeDispararInterpretacion(text)) {
      setIsThinking(false);
      return;
    }

    let cancelado = false;
    const claveConsultada = claveActual;
    // Fix menor (revisor funcional): el corte de 8.5s tiene que arrancar cuando el
    // PEDIDO REAL sale (adentro del setTimeout de abajo), no acá afuera — si el timer de
    // 8.5s arrancara ya, el corte real efectivo sería 8.5s - 600ms de debounce = 7.9s,
    // más corto de lo que dice el nombre de la constante.
    let controller = null;
    let timeoutId = null;

    const timer = setTimeout(async () => {
      if (cancelado) return;
      controller = new AbortController();
      timeoutId = setTimeout(() => controller.abort(), TIMEOUT_MS);
      setIsThinking(true);
      try {
        const respuesta = await api.post(
          `/reservas/${reservaId}/linea-inteligente`,
          { text, serviceType },
          { signal: controller.signal }
        );
        if (cancelado || claveConsultada !== claveVigenteRef.current) return;
        setInterpretation(esRespuestaUtilizable(respuesta) ? respuesta : null);
      } catch {
        // Degradación total (§3.5): red caída, timeout, 4xx/5xx — todo es "no entendió".
        // Nunca se propaga un error hacia el componente que usa este hook.
        if (!cancelado && claveConsultada === claveVigenteRef.current) setInterpretation(null);
      } finally {
        clearTimeout(timeoutId);
        if (!cancelado) setIsThinking(false);
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

  return { interpretation, isThinking };
}
