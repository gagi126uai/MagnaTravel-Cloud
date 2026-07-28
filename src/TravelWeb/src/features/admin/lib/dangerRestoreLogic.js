/**
 * Lógica pura (sin React, sin fetch) de "Volver atrás" (Zona peligrosa, Administración →
 * Mantenimiento, obra 2026-07-27 Parte B "restaurar desde la app", firma del dueño "el
 * usuario tiene que poder volver atrás"). Se usa desde RestaurarResguardoModal.jsx.
 *
 * Dos modos de restauración, bien distintos en alcance (esto lo decide el motor, acá solo
 * se arman los textos y las validaciones de habilitación):
 * - "prueba": restaura el resguardo COMPLETO a una base separada, de mentira. Sirve para
 *   comprobar "¿este resguardo tiene lo que necesito?" sin tocar los datos reales.
 * - "real": restaura SOLO la configuración de la agencia (AFIP, políticas, bot de
 *   WhatsApp, reglas de multas/comisiones) sobre los datos reales, y solo si esas partes
 *   están vacías ahora mismo.
 *
 * T-5 (nunca nombres técnicos en pantalla): el PEDIDO (POST /restore) todavía manda los 5
 * nombres técnicos de tabla en `tablas` (son la lista blanca que valida el backend contra
 * WipeGroups.ConfiguracionTables). Pero la RESPUESTA cambió (fix de review 2026-07-27,
 * "restore modo real ya no es todo-o-nada"): el motor ahora devuelve `tablasRestauradas` /
 * `tablasSalteadas` / `mensaje` con nombres de NEGOCIO ya traducidos (WipeGroups.
 * ConfiguracionTableLabels, ej. "la conexión con AFIP") — este archivo YA NO tiene su
 * propio diccionario de traducción para la respuesta (era un doble mapa que podía
 * desincronizarse del backend); se muestra tal cual lo manda el motor.
 */

import { construirFilasConteosWipe } from "./dangerWipeLogic.js";

// Misma frase "a prueba de dedos" que Empezar de cero, pero con su propio texto porque
// la acción es otra (restaurar, no borrar) — así un admin no puede copiar/pegar la frase
// equivocada de un flujo al otro sin darse cuenta.
export const FRASE_CONFIRMACION_RESTORE = "RESTAURAR TODO";

export const RESTORE_MODO_PRUEBA = "prueba";
export const RESTORE_MODO_REAL = "real";

// Nombres TÉCNICOS de las 5 tablas de configuración que se mandan en el PEDIDO (POST
// /admin/danger/restore, campo `tablas`) cuando modo="real" — tienen que coincidir con
// TravelApi.Application.Constants.WipeGroups.ConfiguracionTables porque el backend valida
// contra esa lista blanca. Esta pantalla no ofrece elegir tablas sueltas: siempre pide
// las 5. Ojo: esto es SOLO para el pedido: la RESPUESTA ya no usa estos nombres técnicos
// (ver `construirResumenExitoRealRestore` más abajo).
export const TABLAS_CONFIGURACION_RESTORE = [
    "AgencySettings",
    "AfipSettings",
    "OperationalFinanceSettings",
    "ApprovalPolicies",
    "WhatsAppBotConfigs",
];

// Etiqueta de NEGOCIO exacta que manda el motor para AfipSettings (WipeGroups.
// ConfiguracionTableLabels["AfipSettings"] = "la conexión con AFIP"). Se usa SOLO para
// detectar si hubo que destacar el aviso de homologación — no es una traducción propia,
// es la búsqueda de un valor que el backend ya nos manda tal cual.
const ETIQUETA_NEGOCIO_AFIP = "la conexión con AFIP";

/**
 * Formatea el tamaño de un archivo de resguardo en criollo (ej. "1,2 MB", "340 KB"),
 * con coma decimal (es-AR) en vez de punto.
 *
 * @param {number} bytes
 * @returns {string}
 */
export function formatearTamanioArchivo(bytes) {
    const numero = Number(bytes) || 0;
    if (numero < 1024) return `${numero} B`;

    const unidades = ["KB", "MB", "GB"];
    let valor = numero / 1024;
    let indiceUnidad = 0;

    while (valor >= 1024 && indiceUnidad < unidades.length - 1) {
        valor /= 1024;
        indiceUnidad += 1;
    }

    const valorFormateado = valor.toFixed(valor < 10 ? 1 : 0).replace(".", ",");
    return `${valorFormateado} ${unidades[indiceUnidad]}`;
}

/**
 * Arma la etiqueta de un resguardo para la lista (ej. "Resguardo del 27/07/2026 22:33 —
 * 1,2 MB"). Recibe `formatearFecha` inyectado (en vez de importar formatDateTime acá
 * directo) para que el test de esta lógica no dependa de zona horaria del entorno que
 * corre los tests — en el componente real se le pasa formatDateTime de lib/utils.js.
 *
 * @param {{archivo: string, fechaUtc: string, tamanioBytes: number}} backup
 * @param {(fecha: string) => string} formatearFecha
 * @returns {string}
 */
export function construirEtiquetaBackup(backup, formatearFecha) {
    const fecha = formatearFecha(backup.fechaUtc);
    const tamanio = formatearTamanioArchivo(backup.tamanioBytes);
    return `Resguardo del ${fecha} — ${tamanio}`;
}

/**
 * Decide si el botón "Confirmar" del formulario de frase+contraseña puede habilitarse.
 * Misma regla que Empezar de cero: frase EXACTA, contraseña cargada, sin ejecución en curso.
 *
 * @param {object} params
 * @param {string} params.frase
 * @param {string} params.password
 * @param {boolean} params.ejecutando
 * @returns {boolean}
 */
export function puedeConfirmarRestore({ frase, password, ejecutando }) {
    const fraseExacta = frase === FRASE_CONFIRMACION_RESTORE;
    const hayPassword = typeof password === "string" && password.length > 0;
    return fraseExacta && hayPassword && !ejecutando;
}

/**
 * Fix de review (P-9/P-10, "prohibido tooltip"): el motivo por el que "Probar en una copia"
 * / "Restaurar configuración" están apagados tiene que verse SIEMPRE como texto, no solo en
 * el `title`. Las dos acciones comparten el mismo gate (elegir resguardo + frase + password),
 * así que un solo motivo alcanza para las dos.
 *
 * @param {object} params
 * @param {string|null} params.archivoSeleccionado
 * @param {string} params.frase
 * @param {string} params.password
 * @returns {string|null}
 */
export function construirMotivoRestoreDeshabilitado({ archivoSeleccionado, frase, password }) {
    if (!archivoSeleccionado) return "Elegí un resguardo para continuar.";
    if (frase !== FRASE_CONFIRMACION_RESTORE) return `Escribí la frase exacta "${FRASE_CONFIRMACION_RESTORE}" para confirmar.`;
    if (!password) return "Cargá tu contraseña para confirmar.";
    return null;
}

/**
 * Texto de la doble confirmación (showConfirm) antes de disparar el POST de restauración.
 * Cambia según el modo porque el alcance real es MUY distinto (uno no toca nada real, el
 * otro sí, aunque acotado a configuración vacía).
 *
 * @param {"prueba"|"real"} modo
 * @returns {{title: string, text: string, confirmText: string, confirmColor: string}}
 */
export function construirConfirmacionRestore(modo) {
    if (modo === RESTORE_MODO_REAL) {
        return {
            title: "¿Restaurar la configuración?",
            text:
                "Se repone la configuración de la agencia (AFIP, políticas de aprobación, bot de " +
                "WhatsApp, reglas de multas y comisiones) desde este resguardo, parte por parte: las " +
                "partes que estén vacías ahora mismo se reponen, y las que ya tengan datos cargados " +
                "NO se tocan (nunca se pisa nada). Esto NO toca reservas, clientes ni ningún otro " +
                "dato real. Si se repone la conexión con AFIP, vuelve siempre en modo homologación.",
            confirmText: "Sí, restaurar configuración",
            confirmColor: "amber",
        };
    }

    return {
        title: "¿Probar este resguardo?",
        text:
            "Se restaura el resguardo completo en una base de PRUEBA separada, para que puedas " +
            "verificar que tiene lo que necesitás. Esto NO toca ningún dato real del sistema.",
        confirmText: "Sí, probar",
        confirmColor: "indigo",
    };
}

/**
 * Texto en criollo del resultado de "Ver qué contiene" (verify). Si el backend marcó el
 * resguardo como inválido, se muestra el motivo tal cual lo manda (ya viene en criollo,
 * mismo criterio que el resto de los rechazos del motor). Nunca se menciona la cantidad
 * de tablas ni ningún otro detalle técnico (P-1: nada de jerga de base de datos).
 *
 * @param {{valido: boolean, motivo: string|null, tieneTablasClave: boolean}} resultado
 * @returns {string}
 */
export function construirTextoVerificacionRestore({ valido, motivo, tieneTablasClave }) {
    if (!valido) {
        return motivo || "Este resguardo no se puede usar.";
    }

    return tieneTablasClave
        ? "Se pudo leer el resguardo: tiene toda la información necesaria para restaurar."
        : "Se pudo leer el resguardo, pero podría faltarle alguna parte clave. Revisá con cuidado antes de restaurar.";
}

/**
 * Arma el resumen de éxito del modo "prueba": reusa las mismas filas de conteo que
 * Empezar de cero (`construirFilasConteosWipe`, misma lectura visual en las dos pantallas:
 * "esto es lo que hay"), pero con SU PROPIO texto para cuando no hay nada — el texto de
 * Empezar de cero dice "no hay datos... para borrar", que no tiene sentido acá (fix de
 * review: antes se colaba ese texto de otro flujo en el panel de éxito de "probar resguardo").
 *
 * @param {object} params
 * @param {object} params.conteos
 * @param {string|null} params.advertencia
 * @returns {{resumenConteos: string, advertencia: string|null}}
 */
export function construirResumenExitoPruebaRestore({ conteos, advertencia }) {
    const filas = construirFilasConteosWipe(conteos).filter((fila) => fila.cantidad > 0);
    const resumenConteos = filas.length > 0
        ? filas.map((fila) => `${fila.cantidad} ${fila.etiqueta}`).join(" · ")
        : "El resguardo no tenía datos de negocio cargados (quedó vacío).";

    return {
        resumenConteos,
        advertencia: advertencia || null,
    };
}

/**
 * Arma el resumen de éxito del modo "real" (fix de review 2026-07-27: el restore YA NO es
 * todo-o-nada, repone lo que está vacío y SALTEA lo que ya tenía datos). El motor arma un
 * `mensaje` en criollo listo para mostrar TAL CUAL (mismo criterio que el resto de los
 * textos del motor en esta pantalla, ver construirTextoVerificacionRestore) — dice qué se
 * repuso, qué se salteó por ya tener datos, y si corresponde agrega el aviso de que AFIP
 * volvió forzado a homologación. Acá NO se arma ni se traduce nada de ese texto: solo se
 * detecta si conviene destacar visualmente el aviso de AFIP (`incluyeAfip`), buscando la
 * etiqueta de negocio de AFIP dentro de `tablasRestauradas` (que el motor ya manda
 * traducida — no es una tabla técnica).
 *
 * @param {object} params
 * @param {string|null} params.mensaje - texto ya armado por el motor, se muestra tal cual.
 * @param {string[]} params.tablasRestauradas - nombres de NEGOCIO de lo que se repuso.
 * @param {string|null} params.advertencia
 * @returns {{mensaje: string, incluyeAfip: boolean, advertencia: string|null}}
 */
export function construirResumenExitoRealRestore({ mensaje, tablasRestauradas, advertencia }) {
    const lista = Array.isArray(tablasRestauradas) ? tablasRestauradas : [];
    const incluyeAfip = lista.includes(ETIQUETA_NEGOCIO_AFIP);

    return {
        mensaje: mensaje || "No había nada para restaurar.",
        incluyeAfip,
        advertencia: advertencia || null,
    };
}
