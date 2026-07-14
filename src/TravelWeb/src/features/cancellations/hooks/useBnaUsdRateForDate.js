/**
 * Consulta el dólar oficial del BNA para UNA fecha puntual (2026-07-14, spec
 * F-2026-1033) y expone el resultado listo para pre-cargar el tipo de cambio del
 * bloque de conversión en ConfirmarMultaOperadorInline.jsx.
 *
 * Se dispara cada vez que cambia `fecha` (la fecha en que el operador cobró la
 * multa), con un pequeño debounce para no disparar un pedido por cada tecla si el
 * usuario escribe la fecha a mano en vez de usar el selector nativo.
 *
 * Dos capas de protección contra respuestas fuera de orden (si el usuario cambia
 * la fecha varias veces rápido):
 *   1. Cleanup por closure (`cancelled = true`), mismo patrón que
 *      useServiceNominalCoverage.js — cubre "el componente se desmontó" o "el
 *      efecto volvió a correr antes de que la promesa anterior resolviera".
 *   2. debeAplicarRespuestaBna (lib/penaltyCrossCurrency.js) — refuerzo explícito:
 *      compara la fecha que se pidió contra la fecha vigente AL MOMENTO en que la
 *      respuesta llega. Si no coinciden, se descarta aunque la promesa haya
 *      resuelto con éxito (cinturón y tirantes: la closure ya debería alcanzar,
 *      pero esta comparación es la pieza que se puede testear sin DOM).
 *
 * Se usa SOLO cuando el bloque de conversión está visible (hayCruce=true) — el
 * componente pasa `enabled` en false el resto del tiempo para no gastar pedidos.
 */

import { useEffect, useRef, useState } from "react";
import { cancellationsApi } from "../api/cancellationsApi";
import { interpretarRespuestaBnaRate, debeAplicarRespuestaBna } from "../lib/penaltyCrossCurrency";

// Debounce corto: la fecha viene de un input type=date (normalmente un solo click
// en el selector nativo, no tecleo caracter a caracter), pero algunos navegadores
// permiten escribirla a mano — 300ms alcanza para no golpear el endpoint de más.
const DEBOUNCE_MS = 300;

/**
 * @param {string} fecha - "YYYY-MM-DD", la fecha en que el operador cobró la multa.
 * @param {{ enabled?: boolean }} [options] - enabled=false desactiva el hook entero
 *   (no dispara ningún fetch) — se usa cuando el bloque de conversión no está visible.
 * @returns {{ tipoCambioSugerido: number|null, fechaSugeridaReal: string|null, cargando: boolean }}
 */
export function useBnaUsdRateForDate(fecha, { enabled = true } = {}) {
  const [tipoCambioSugerido, setTipoCambioSugerido] = useState(null);
  const [fechaSugeridaReal, setFechaSugeridaReal] = useState(null);
  const [cargando, setCargando] = useState(false);

  // Guarda cuál es la fecha "vigente" en todo momento (sin depender de closures del
  // useEffect) para que debeAplicarRespuestaBna siempre compare contra el valor más
  // reciente, aunque la respuesta llegue en medio de un cambio de fecha.
  const fechaVigenteRef = useRef(fecha);
  fechaVigenteRef.current = fecha;

  useEffect(() => {
    // Deshabilitado (bloque de conversión no visible) o sin fecha todavía: no hay
    // nada que consultar — limpiamos cualquier sugerencia previa (ej. el usuario
    // borró la fecha después de haber elegido una).
    if (!enabled || !fecha) {
      setTipoCambioSugerido(null);
      setFechaSugeridaReal(null);
      setCargando(false);
      return;
    }

    let cancelled = false;
    // Limpiamos la sugerencia anterior YA (sin esperar el debounce/fetch): si no,
    // mientras se consulta la fecha nueva quedaría en pantalla, unos instantes, un
    // tipo de cambio "sugerido" que en realidad es de la fecha vieja.
    setTipoCambioSugerido(null);
    setFechaSugeridaReal(null);
    setCargando(true);

    const timer = setTimeout(async () => {
      if (cancelled) return;
      try {
        const respuesta = await cancellationsApi.getBnaUsdRate(fecha);
        if (cancelled) return;
        // Refuerzo explícito (ver comentario de cabecera): si la fecha vigente ya
        // cambió mientras esta consulta estaba en vuelo, esta respuesta es vieja.
        if (!debeAplicarRespuestaBna({ fechaPedida: fecha, fechaVigente: fechaVigenteRef.current })) {
          return;
        }
        const { tipoCambioSugerido: rate, fechaSugeridaReal: rateDate } = interpretarRespuestaBnaRate(respuesta);
        setTipoCambioSugerido(rate);
        setFechaSugeridaReal(rateDate);
      } catch {
        // 204 ya lo maneja interpretarRespuestaBnaRate (api.get devuelve null, no
        // tira error). Acá solo caen errores de red/servidor — caso esperado según
        // la spec: casillero vacío, "escribilo a mano", SIN toast de error. El
        // usuario igual puede seguir cargando el tipo de cambio a mano sin trabarse.
        if (!cancelled) {
          setTipoCambioSugerido(null);
          setFechaSugeridaReal(null);
        }
      } finally {
        if (!cancelled) setCargando(false);
      }
    }, DEBOUNCE_MS);

    return () => {
      cancelled = true;
      clearTimeout(timer);
    };
  }, [fecha, enabled]);

  return { tipoCambioSugerido, fechaSugeridaReal, cargando };
}
