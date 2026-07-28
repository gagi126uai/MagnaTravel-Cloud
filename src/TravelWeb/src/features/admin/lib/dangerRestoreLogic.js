/**
 * Lógica pura (sin React, sin fetch) de "Volver atrás" (Zona peligrosa, Administración →
 * Mantenimiento, obra 2026-07-27 Parte B "restaurar desde la app", firma del dueño "el
 * usuario tiene que poder volver atrás"). Se usa desde RestaurarResguardoModal.jsx.
 *
 * TRES modos de restauración, bien distintos en alcance (esto lo decide el motor, acá solo
 * se arman los textos y las validaciones de habilitación):
 * - "prueba" (botón "Ver qué contiene"): restaura el resguardo COMPLETO a una base
 *   separada, de mentira, cuenta lo que trae y la borra. Sirve para comprobar "¿este
 *   resguardo tiene lo que necesito?" sin tocar los datos reales.
 * - "real" (botón "Restaurar configuración"): restaura SOLO la configuración de la
 *   agencia (AFIP, políticas, bot de WhatsApp, reglas de multas/comisiones) sobre los
 *   datos reales, y solo si esas partes están vacías ahora mismo.
 * - "total" (botón "Restaurar todo", obra 2026-07-27 "Restaurar todo desde la app"):
 *   devuelve TODO el sistema al estado del resguardo elegido. A diferencia de los otros
 *   dos modos, mientras esta restauración corre el motor tumba TODA la API con 503
 *   ({code: "MAINTENANCE"}) — por eso este modo prende la pantalla de mantenimiento
 *   global (ver maintenanceState.js y MaintenanceScreen.jsx) apenas se confirma.
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
export const RESTORE_MODO_TOTAL = "total";

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
 * Fix de review (P-9/P-10, "prohibido tooltip"): el motivo por el que "Ver qué contiene" /
 * "Restaurar configuración" / "Restaurar todo" están apagados tiene que verse SIEMPRE como
 * texto, no solo en el `title`. Las tres acciones comparten el mismo gate (elegir resguardo +
 * frase + password), así que un solo motivo alcanza para las tres.
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
 * Cambia según el modo porque el alcance real es MUY distinto (uno no toca nada real, otro
 * repone configuración vacía, y el tercero vuelve TODO el sistema para atrás).
 *
 * `fechaBackup` (ya formateada por el componente, ej. "27/07/2026 22:33") solo se usa en el
 * modo "total", para que el aviso durísimo diga a qué momento exacto vuelve el sistema — en
 * los otros dos modos se ignora sin problema si se manda igual.
 *
 * @param {"prueba"|"real"|"total"} modo
 * @param {{fechaBackup?: string|null}} [contexto]
 * @returns {{title: string, text: string, confirmText: string, confirmColor: string}}
 */
export function construirConfirmacionRestore(modo, { fechaBackup } = {}) {
    if (modo === RESTORE_MODO_TOTAL) {
        const fechaTexto = fechaBackup || "de este resguardo";
        return {
            title: "¿Restaurar TODO el sistema?",
            text:
                `Esto devuelve TODO el sistema a como estaba el ${fechaTexto}. Lo que hayas cargado ` +
                "después se pierde. Antes se guarda un resguardo del estado actual, así podés volver " +
                "a este momento si te arrepentís.",
            confirmText: "Sí, restaurar todo",
            confirmColor: "red",
        };
    }

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
        title: "¿Ver el contenido de este resguardo?",
        text:
            "Para mostrarte el detalle, se restaura el resguardo completo en una base de PRUEBA " +
            "separada, se cuenta lo que trae y esa copia se borra. Esto NO toca ningún dato real " +
            "del sistema.",
        confirmText: "Sí, ver contenido",
        confirmColor: "indigo",
    };
}

/**
 * Explicación fija de qué hace cada acción de este modal, para el hallazgo del dueño
 * "tampoco deja en claro cómo lo hace o qué conecta". Se muestra siempre, arriba de los
 * botones, antes de que el usuario toque nada. No depende de ningún dato de la pantalla
 * (es texto fijo) pero vive acá — no hardcodeado en el JSX — para poder testearlo igual
 * que el resto de los textos de esta pantalla, y para que quien lo cambie tenga que
 * pensarlo como un texto de negocio, no como un detalle visual suelto.
 *
 * Fix de review (unificación 2026-07-27, firmado): "Ver qué contiene" pasó a hacer lo mismo
 * que antes hacía "Probar en una copia" (esa acción por separado desapareció, era lo mismo
 * con otro nombre), y se agregó "Restaurar todo".
 *
 * @returns {string[]}
 */
export function construirExplicacionAccionesRestore() {
    return [
        "Ver qué contiene: arma una copia de prueba con este resguardo, te muestra el detalle y la borra al terminar. Pide la frase y la contraseña de abajo para confirmar.",
        "Restaurar configuración: repone en el sistema real solo las partes de configuración que estén vacías.",
        "Restaurar todo: vuelve TODO el sistema al estado de este resguardo. Antes guarda un resguardo del estado actual.",
    ];
}

/**
 * Detecta si un pedido a la API falló porque el motor está en medio de una restauración
 * TOTAL (contrato nuevo, obra 2026-07-27: "mientras dura la restauración total, cualquier
 * llamada a /api/** devuelve 503 con {code: 'MAINTENANCE'}"). La usa api.js, en el único
 * lugar donde se arma el error de cualquier pedido fallido, para prender la pantalla de
 * mantenimiento global sin importar qué pantalla estaba pidiendo qué cosa.
 *
 * OJO: el 503 SOLO se interpreta como mantenimiento si además viene con ese code exacto —
 * un 503 "normal" (ej. la base de datos caída por otro motivo, ver isDatabaseUnavailableError
 * en lib/errors.js) NO tiene que prender esta pantalla, porque ahí no hay ninguna
 * restauración en curso de la que "esperar a que vuelva".
 *
 * @param {{status: number, code: string|null}} info
 * @returns {boolean}
 */
export function esErrorDeMantenimiento({ status, code }) {
    return status === 503 && code === "MAINTENANCE";
}

/**
 * Arma el resumen de éxito del modo "total" (obra 2026-07-27 "Restaurar todo"): el motor
 * manda un `mensaje` en criollo listo para mostrar TAL CUAL (mismo criterio que el resto
 * de los textos del motor en esta pantalla), más `backupPrevio` (el resguardo del estado
 * actual que se guardó automáticamente ANTES de restaurar, por si hay que volver atrás de
 * esto) y `restauradoDe` (una etiqueta de qué resguardo se aplicó, según el motor). Acá NO
 * se arma ni se traduce ese texto, solo se blindan los `null` para que la pantalla nunca
 * muestre "undefined". OJO (T-5): el componente NO usa `restauradoDe` tal cual para
 * mostrarlo en pantalla — arma su propia etiqueta con `construirEtiquetaBackup` a partir
 * del resguardo que el propio usuario eligió, para no depender de que el motor mande un
 * texto ya "limpio" de nombres técnicos. Este campo queda igual en el resumen por si algún
 * consumidor futuro lo necesita.
 *
 * @param {{mensaje: string|null, backupPrevio: string|null, restauradoDe: string|null}} params
 * @returns {{mensaje: string, backupPrevio: string|null, restauradoDe: string|null}}
 */
export function construirResumenExitoTotalRestore({ mensaje, backupPrevio, restauradoDe }) {
    return {
        mensaje: mensaje || "El sistema se restauró correctamente.",
        backupPrevio: backupPrevio || null,
        restauradoDe: restauradoDe || null,
    };
}

/**
 * Arma el resumen de éxito del modo "prueba": reusa las mismas filas de conteo que
 * Empezar de cero (`construirFilasConteosWipe`, misma lectura visual en las dos pantallas:
 * "esto es lo que hay"), pero con SU PROPIO texto para cuando no hay nada — el texto de
 * Empezar de cero dice "no hay datos... para borrar", que no tiene sentido acá.
 *
 * Fix de hallazgo del dueño ("le indicó lo que restauró pero después no pude ver realmente
 * qué contenía, es como que no hizo nada"): antes esto se mostraba en UN renglón separado
 * por "·" y sin decir qué se hizo con la copia de prueba. Ahora devuelve las filas en una
 * lista (una por tipo de dato, para mostrar en varias líneas como en Empezar de cero) más
 * un texto fijo que explica el PROCESO en criollo — para que quede claro que la copia se
 * armó aparte, se contó y se borró, y que en ningún momento se tocaron los datos reales.
 *
 * @param {object} params
 * @param {object} params.conteos
 * @param {string|null} params.advertencia
 * @returns {{
 *   encabezado: string,
 *   filas: {clave: string, etiqueta: string, cantidad: number}[],
 *   sinDatos: boolean,
 *   mensajeSinDatos: string,
 *   comoSeHizo: string,
 *   advertencia: string|null,
 * }}
 */
export function construirResumenExitoPruebaRestore({ conteos, advertencia }) {
    const filas = construirFilasConteosWipe(conteos).filter((fila) => fila.cantidad > 0);

    return {
        encabezado: "Esto es lo que contiene el resguardo:",
        filas,
        sinDatos: filas.length === 0,
        mensajeSinDatos: "El resguardo no tiene datos de negocio cargados.",
        comoSeHizo:
            "Cómo se hizo: armamos una copia aparte de tu base, le cargamos el resguardo, contamos lo que " +
            "tiene y borramos la copia. Tus datos no se tocaron en ningún momento.",
        advertencia: advertencia || null,
    };
}

/**
 * Arma el resumen de éxito del modo "real" (fix de review 2026-07-27: el restore YA NO es
 * todo-o-nada, repone lo que está vacío y SALTEA lo que ya tenía datos). El motor arma un
 * `mensaje` en criollo listo para mostrar TAL CUAL (mismo criterio que el resto de los
 * textos del motor en esta pantalla) — dice qué se repuso, qué se salteó por ya tener
 * datos cargados, y si corresponde agrega el aviso de que AFIP
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
