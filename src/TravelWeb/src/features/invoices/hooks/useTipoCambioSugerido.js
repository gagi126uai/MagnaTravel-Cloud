/**
 * Consulta la sugerencia de tipo de cambio (GET /api/exchange-rates/suggestion) para
 * precargar el casillero de TC al facturar en dólares (spec 2026-08-05, hermano de
 * useBnaUsdRateForDate en la carpeta cancellations — mismo patrón de debounce y de
 * doble guarda anti-race, copiado a propósito para no reinventar nada).
 *
 * Se dispara cada vez que cambia `moneda` o `fecha`, con un debounce corto (no hace
 * falta acá porque estas dos pantallas no dejan escribir la fecha a mano, pero se
 * mantiene por si el usuario cambia de moneda varias veces rápido).
 *
 * Dos capas de protección contra respuestas fuera de orden:
 *   1. Cleanup por closure (`cancelado = true`), mismo patrón que
 *      useBnaUsdRateForDate.js / useServiceNominalCoverage.js.
 *   2. Comparación de "clave vigente" (moneda+fecha): si la combinación pedida ya
 *      cambió mientras la consulta estaba en vuelo, la respuesta se descarta aunque
 *      la promesa haya resuelto con éxito (cinturón y tirantes).
 *
 * Se usa SOLO cuando la moneda elegida es dólares — el componente pasa `enabled` en
 * false con pesos, para no gastar pedidos (spec §4 punto 1).
 *
 * SIN toast ni cartel de error en ningún caso: red caída, 204 o el usuario sin
 * permiso para consultar se ven todos igual, "sin sugerencia" — el casillero queda
 * vacío y editable, nunca se traba la pantalla (spec §4 punto 9).
 *
 * Ampliado por la spec "ayuda invisible del tipo de cambio" (2026-08-06): además de
 * la sugerencia y la leyenda, el motor ahora manda `topeDelDia` (el máximo que la
 * factura admite ese día, para el acomodo de A4) y `loCompletaElSistema` (A3: el
 * casillero no se dibuja porque el motor completa el número solo). Los dos viajan
 * en el mismo GET, sin pedidos extra.
 */

import { useEffect, useRef, useState } from "react";
import { api } from "../../../api";
import { interpretarRespuestaSugerenciaTC } from "../lib/exchangeRateSuggestion";

// Mismo debounce que useBnaUsdRateForDate (300ms): alcanza para no golpear el
// endpoint de más si el usuario cambia de moneda varias veces seguidas.
const DEBOUNCE_MS = 300;

/**
 * @param {string} moneda - "USD" (única moneda con sugerencia; con "ARS" no se llama al hook)
 * @param {string} fecha - "YYYY-MM-DD", la fecha de emisión del comprobante (hoy en Argentina)
 * @param {{ enabled?: boolean }} [options] - enabled=false desactiva el hook entero
 *   (no dispara ningún fetch) — se usa cuando la moneda elegida no es USD.
 * @returns {{ tipoCambioSugerido: number|null, leyenda: string|null, cargando: boolean, topeDelDia: number|null, loCompletaElSistema: boolean }}
 */
export function useTipoCambioSugerido(moneda, fecha, { enabled = true } = {}) {
  const [tipoCambioSugerido, setTipoCambioSugerido] = useState(null);
  const [leyenda, setLeyenda] = useState(null);
  const [cargando, setCargando] = useState(false);
  // "Ayuda invisible" (2026-08-06): techo del día para el acomodo (A4) y aviso de
  // "el motor completa solo" (A3). Viajan en la misma respuesta que la sugerencia.
  const [topeDelDia, setTopeDelDia] = useState(null);
  const [loCompletaElSistema, setLoCompletaElSistema] = useState(false);

  // Guarda cuál es la combinación moneda+fecha "vigente" en todo momento (sin
  // depender de closures del useEffect), para que la comparación de abajo siempre
  // sea contra el valor más reciente, aunque la respuesta llegue en medio de un
  // cambio de moneda.
  const claveVigenteRef = useRef(`${moneda}|${fecha}`);
  claveVigenteRef.current = `${moneda}|${fecha}`;

  useEffect(() => {
    // Deshabilitado (moneda distinta de USD) o sin fecha todavía: no hay nada que
    // consultar — limpiamos cualquier sugerencia previa.
    if (!enabled || !moneda || !fecha) {
      setTipoCambioSugerido(null);
      setLeyenda(null);
      setCargando(false);
      setTopeDelDia(null);
      setLoCompletaElSistema(false);
      return;
    }

    let cancelado = false;
    const claveConsultada = `${moneda}|${fecha}`;
    // Limpiamos la sugerencia anterior YA (sin esperar el debounce/fetch): si no,
    // mientras se consulta la moneda nueva quedaría en pantalla, unos instantes, un
    // tipo de cambio "sugerido" que en realidad era de la consulta vieja.
    setTipoCambioSugerido(null);
    setLeyenda(null);
    setTopeDelDia(null);
    setLoCompletaElSistema(false);
    setCargando(true);

    const timer = setTimeout(async () => {
      if (cancelado) return;
      try {
        const respuesta = await api.get(
          `/exchange-rates/suggestion?currency=${encodeURIComponent(moneda)}&date=${encodeURIComponent(fecha)}`
        );
        if (cancelado) return;
        // Refuerzo explícito (ver comentario de cabecera): si la clave vigente ya
        // cambió mientras esta consulta estaba en vuelo, esta respuesta es vieja.
        if (claveConsultada !== claveVigenteRef.current) return;
        const {
          tipoCambioSugerido: sugerido,
          leyenda: leyendaMotor,
          topeDelDia: tope,
          loCompletaElSistema: completaSolo,
        } = interpretarRespuestaSugerenciaTC(respuesta);
        setTipoCambioSugerido(sugerido);
        setLeyenda(leyendaMotor);
        setTopeDelDia(tope);
        setLoCompletaElSistema(completaSolo);
      } catch {
        // 204 ya lo maneja interpretarRespuestaSugerenciaTC (api.get devuelve null,
        // no tira error). Acá solo caen errores de red/servidor/permiso — caso
        // esperado según la spec: casillero vacío, "escribilo a mano", SIN toast de
        // error. El usuario igual puede seguir cargando el tipo de cambio a mano.
        if (!cancelado) {
          setTipoCambioSugerido(null);
          setLeyenda(null);
          setTopeDelDia(null);
          setLoCompletaElSistema(false);
        }
      } finally {
        if (!cancelado) setCargando(false);
      }
    }, DEBOUNCE_MS);

    return () => {
      cancelado = true;
      clearTimeout(timer);
    };
  }, [moneda, fecha, enabled]);

  return { tipoCambioSugerido, leyenda, cargando, topeDelDia, loCompletaElSistema };
}
