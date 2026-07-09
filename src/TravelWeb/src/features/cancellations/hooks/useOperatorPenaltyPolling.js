/**
 * Refresca la situación de la multa del operador SOLA, cada ~10 s, mientras el cartel
 * de la ficha esté en la familia "procesando" (DebitNoteQueued: "se está emitiendo la
 * multa al cliente, puede demorar unos minutos"). Antes de este hook, ese cartel
 * quedaba TRABADO para siempre aunque la base ya dijera "emitida" — solo un F5 manual
 * lo destrababa, y el texto prometía "puede demorar unos minutos" sin que la pantalla
 * se enterara sola de nada.
 *
 * Mismo patrón que useInvoicePolling (payments/hooks): ref estable para el callback
 * (para no reiniciar el timer si `onTick` cambia de referencia entre renders), timer
 * recursivo con setTimeout (no setInterval, para no encolar un tick nuevo si el
 * anterior todavía no terminó), y cleanup del timer en unmount o al dejar de pollear.
 * La lógica de "¿debo pollear?" y "¿se agotó el tope de espera?" vive en funciones
 * puras aparte (operatorPenaltyBanner.js) con sus propios tests — este hook solo las
 * orquesta con reloj real.
 *
 * Se usa en OperatorPenaltyStepPanel.jsx.
 */

import { useEffect, useRef, useState } from "react";
import { debePollearSituacionMulta, seAgotoElBudgetDePollingDeMulta } from "../operatorPenaltyBanner";

const POLL_INTERVAL_MS = 10_000; // cada ~10 s, como pidió el dueño del producto.
const MAX_POLL_DURATION_MS = 180_000; // tope prudente: ~3 minutos y dejamos de insistir solos.

/**
 * @param {"pregunta"|"procesando"|"accionTrabada"|"waived"|"soloLectura"} familia
 *   Familia actual del cartel (ver familiaDeEstadoMulta). Solo se pollea en "procesando".
 * @param {() => void|Promise<void>} onTick
 *   Refresco SILENCIOSO (sin toast de éxito) — en la ficha de la reserva es el mismo
 *   `onResuelto` que ya usa el resto del panel al resolver una acción del agente: refresca
 *   la reserva entera y, si la situación cambió de familia, el cartel se reemplaza solo
 *   en el próximo render. Si el fetch falla, el propio callback ya se encarga de avisar
 *   (no es responsabilidad de este hook).
 * @param {object} [options]
 * @param {number} [options.intervalMs] - ms entre cada refresco (default 10 000).
 * @param {number} [options.maxDurationMs] - tope total de polling (default 180 000).
 * @returns {boolean} true si se agotó el tope de espera sin que el backend resolviera
 *   todavía — el componente usa esto para sumar la línea "¿Tarda mucho? Actualizá la
 *   página." debajo del cartel.
 */
export function useOperatorPenaltyPolling(familia, onTick, options = {}) {
  const { intervalMs = POLL_INTERVAL_MS, maxDurationMs = MAX_POLL_DURATION_MS } = options;
  const [seAgoto, setSeAgoto] = useState(false);

  const onTickRef = useRef(onTick);
  useEffect(() => {
    onTickRef.current = onTick;
  }, [onTick]);

  // useEffect con dependencia en `familia`: arranca/corta el timer cada vez que la
  // familia del cartel cambia (por ejemplo, si el backend resuelve la ND y pasa de
  // "procesando" a "soloLectura"), y siempre lo limpia al desmontar el panel.
  useEffect(() => {
    // Cada vez que entramos de nuevo a "procesando" arrancamos el tope fresco — si el
    // agente vuelve a ver el cartel más tarde en otra visita, no arrastra el aviso de
    // "tarda mucho" de la vez anterior.
    setSeAgoto(false);

    if (!debePollearSituacionMulta(familia)) return;

    const startedAt = Date.now();
    let timeoutId;
    // Bandera de cancelación (hallazgo del review 2026-07-09): clearTimeout solo alcanza si el
    // timer todavía no disparó. Si el tick YA está en medio del fetch cuando el panel se desmonta
    // (o la familia cambia), el cleanup no tiene nada que limpiar y, al resolver el fetch,
    // schedule() encadenaría un timer nuevo que nadie limpia — polling zombie hasta agotar el
    // tope. Con la bandera, el tick en vuelo termina pero NO encadena el siguiente.
    let cancelled = false;

    const schedule = () => {
      const elapsed = Date.now() - startedAt;
      if (seAgotoElBudgetDePollingDeMulta(elapsed, maxDurationMs)) {
        setSeAgoto(true);
        return; // deja de pollear: el cartel suma la línea "actualizá la página a mano".
      }
      timeoutId = setTimeout(async () => {
        await onTickRef.current();
        if (!cancelled) {
          schedule();
        }
      }, intervalMs);
    };

    schedule();

    return () => {
      cancelled = true;
      clearTimeout(timeoutId);
    };
  }, [familia, intervalMs, maxDurationMs]);

  return seAgoto;
}
