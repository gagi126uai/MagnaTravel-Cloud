/**
 * Hook con TODA la lógica (sin JSX) de "resolver un servicio pendiente hacia adelante"
 * (fix #34, Tanda 3, 2026-07-24). Extraído mecánicamente de `ResolverServicioInline.jsx`
 * (Tanda T5, 2026-08-18) para poder reusar la MISMA lógica en dos formas visuales
 * distintas: el casillero apretado dentro de la columna (ficha de la reserva,
 * `ResolverServicioInline`) y la fila de expansión a todo el ancho (cuenta del
 * operador, `ResolverServicioBotones` + `ResolverServicioCasillero`). El comportamiento
 * no cambió una coma respecto de antes — solo se movió el código de un componente a un
 * hook para que dos componentes distintos puedan compartirlo.
 *
 * Spec completa: docs/ux/guia-ux-gaston.md, sección "Confirmar un servicio DESDE LA
 * FICHA de la reserva (2026-07-24, respuestas de Gastón P1..P4)".
 *
 * Props:
 *   reservaId       — publicId de la reserva (los endpoints "mark-issued" y
 *                      "no-confirmation" son reserva-scoped).
 *   servicePublicId — publicId del servicio (hotel/vuelo/traslado/paquete/asistencia).
 *   recordKind      — "flight"|"hotel"|"transfer"|"assistance"|"package"|"generic".
 *   onResuelto      — callback() cuando el servicio se resolvió con éxito. El padre
 *                      recarga/actualiza el estado (contador "N de M", badge, etc.).
 */

import { useEffect, useRef, useState } from "react";
import { api } from "../../../api";
import { showError, showSuccess } from "../../../alerts";
import { getApiErrorMessage } from "../../../lib/errors";
import {
    resolverAccionesParaServicioPendiente,
    construirRequestResolverServicio,
    resolverMensajeExito,
    debeMostrarCartelEmergente,
} from "./serviceResolutionActions";

export function useResolverServicioAcciones({ reservaId, servicePublicId, recordKind, onResuelto }) {
    const acciones = resolverAccionesParaServicioPendiente(recordKind);

    const [accionAbierta, setAccionAbierta] = useState(null); // tipo de la acción con casillero abierto, o null
    const [numero, setNumero] = useState("");
    const [guardando, setGuardando] = useState(false);
    const [errorMensaje, setErrorMensaje] = useState(null);
    const [mostrarCartel, setMostrarCartel] = useState(false);
    // H8 (2026-07-25): rechazo de la acción SIN casillero ("No requiere confirmación").
    // Antes este camino solo mostraba un toast (showError) sin importar el largo del
    // mensaje — un rechazo largo (ej. candado C2 de destrabe, o el gate de titular de H7)
    // se veía 4 segundos y desaparecía solo, "moría mudo" para el cajero que no llegó a
    // leerlo entero. Mismo criterio que el camino CON casillero: corto = toast, largo =
    // Cartel emergente que hay que cerrar a mano.
    const [errorSinCasillero, setErrorSinCasillero] = useState(null);
    const inputRef = useRef(null);

    // Al abrir el casillero, el foco va directo al input — el usuario puede tipear el
    // número sin hacer clic primero (mismo criterio de foco que el resto de la app).
    useEffect(() => {
        if (accionAbierta && inputRef.current) inputRef.current.focus();
    }, [accionAbierta]);

    const abrirCasillero = (tipo) => {
        setAccionAbierta(tipo);
        setNumero("");
        setErrorMensaje(null);
        setMostrarCartel(false);
    };

    const cerrarCasillero = () => {
        setAccionAbierta(null);
        setNumero("");
        setErrorMensaje(null);
        setMostrarCartel(false);
    };

    // Camino CON casillero (P2=B): si el motor rechaza, el casillero queda abierto con
    // el número intacto y el error se muestra en línea (o en el Cartel emergente si es
    // largo) — nunca un toast que desaparece solo, porque el usuario todavía tiene que
    // decidir qué hacer con lo que escribió.
    const ejecutarAccionConCasillero = async (tipo) => {
        const request = construirRequestResolverServicio({ tipo, recordKind, reservaId, servicePublicId, numero });
        if (!request) return;

        setGuardando(true);
        setErrorMensaje(null);
        try {
            await api[request.method](request.url, request.body);
            showSuccess(resolverMensajeExito(tipo));
            setAccionAbierta(null);
            setNumero("");
            onResuelto?.();
        } catch (error) {
            const mensaje = getApiErrorMessage(error, "No se pudo confirmar el servicio.");
            setErrorMensaje(mensaje);
            setMostrarCartel(debeMostrarCartelEmergente(mensaje));
        } finally {
            setGuardando(false);
        }
    };

    // "No requiere confirmación": único de 1 click, sin casillero — no hay número que
    // cargar. El rechazo corto sigue yendo por toast (como antes); el rechazo LARGO va
    // al Cartel emergente único, para que no se pierda como un toast que se cierra solo.
    const ejecutarAccionSinCasillero = async (tipo) => {
        const request = construirRequestResolverServicio({ tipo, recordKind, reservaId, servicePublicId, numero: null });
        if (!request) return;

        setGuardando(true);
        try {
            await api[request.method](request.url, request.body);
            showSuccess(resolverMensajeExito(tipo));
            onResuelto?.();
        } catch (error) {
            const mensaje = getApiErrorMessage(error, "No se pudo registrar el traslado.");
            if (debeMostrarCartelEmergente(mensaje)) {
                setErrorSinCasillero(mensaje);
            } else {
                showError(mensaje);
            }
        } finally {
            setGuardando(false);
        }
    };

    return {
        acciones,
        accionAbierta,
        numero,
        setNumero,
        guardando,
        errorMensaje,
        mostrarCartel,
        setMostrarCartel,
        errorSinCasillero,
        setErrorSinCasillero,
        inputRef,
        abrirCasillero,
        cerrarCasillero,
        ejecutarAccionConCasillero,
        ejecutarAccionSinCasillero,
    };
}
