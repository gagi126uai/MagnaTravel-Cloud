/**
 * Consumidor de la selección "pendiente" tras el salto de solapa (spec FIRMADA
 * 2026-08-10, D3/D7): cuando el vendedor elige en el buscador un producto de OTRO tipo,
 * `ServiceInlineCard` cambia de solapa sola y deja esa elección guardada en
 * `seleccionPendiente` hasta que el formulario del tipo DESTINO la consuma.
 *
 * Cada uno de los 5 formularios (Hotel, Aéreo, Traslado, Paquete, Asistencia) usa este
 * hook para mirar si la pendiente es DE SU TIPO y, si lo es, aplicarla exactamente igual
 * que si el vendedor la hubiera elegido del propio buscador (mismo `handleSelectExisting`
 * de siempre, con la interpretación de la frase como segundo argumento — ver D13).
 *
 * La decisión de "hay que aplicar ahora" es 100% lógica pura (`debeAplicarSeleccionPendiente`
 * en `crossTypeSearchLogic.js`, sin React) — este hook es solo el envoltorio que la
 * conecta a un efecto y guarda, en un ref, cuál fue la ÚLTIMA pendiente ya aplicada.
 */

import { useEffect, useRef } from "react";
import { debeAplicarSeleccionPendiente } from "./crossTypeSearchLogic";

/**
 * @param {object} params
 * @param {{serviceType:string, result:object, interpretacion:object|null}|null} params.seleccionPendiente
 * @param {string} params.serviceType — el tipo de ESTE formulario (ej: "Hotel")
 * @param {(result:object, interpretacion:object|null) => void} params.onSeleccionar
 *   el `handleSelectExisting` propio del formulario. Desde la auditoría de coherencia
 *   2026-08-10 (#1) ya no hace falta avisarle "esto vino de un salto de solapa": la
 *   regla de "nunca pisar lo tipeado a mano" es la MISMA para los dos caminos (normal y
 *   pendiente) — la decide `resolverPatchDeVentaDelCatalogo` mirando `camposSugeridos`
 *   del form, no quién lo llamó.
 * @param {() => void} params.onConsumida — avisa a `ServiceInlineCard` que ya se aplicó
 *   (así limpia `seleccionPendiente` y no queda colgada para el próximo salto)
 */
export function useSeleccionPendienteDelTipo({ seleccionPendiente, serviceType, onSeleccionar, onConsumida }) {
    // Recuerda la ÚLTIMA pendiente que ESTE formulario ya aplicó, por REFERENCIA — así un
    // efecto que corre dos veces (StrictMode en desarrollo) no vuelve a disparar la misma
    // selección ni avisa "consumido" dos veces.
    const ultimaAplicadaRef = useRef(null);

    useEffect(() => {
        if (!debeAplicarSeleccionPendiente({ seleccionPendiente, serviceType, ultimaAplicada: ultimaAplicadaRef.current })) {
            return;
        }
        ultimaAplicadaRef.current = seleccionPendiente;
        onSeleccionar(seleccionPendiente.result, seleccionPendiente.interpretacion);
        onConsumida();
        // Deps a propósito solo en la pendiente y el tipo: `onSeleccionar`/`onConsumida` son
        // funciones nuevas en cada render del formulario (no memoizadas con useCallback) — si
        // entraran acá, el efecto correría en cada tecleo del vendedor sin que la pendiente
        // haya cambiado, y `debeAplicarSeleccionPendiente` ya filtra correctamente cuándo
        // corresponde actuar.
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [seleccionPendiente, serviceType]);
}
