/**
 * Lógica pura (sin React, sin fetch) de la solapa "Copias de seguridad" (Administración,
 * rediseño 2026-07-30 — antes vivía en una ventana flotante dentro de Mantenimiento → Zona
 * peligrosa → "Volver atrás"). Se usa desde CopiasDeSeguridadTab.jsx y sus fichas.
 *
 * TRES acciones sobre un resguardo elegido, bien distintas en alcance (esto lo decide el
 * motor, acá solo se arman los textos y las validaciones de habilitación):
 * - "prueba" (link chico "Ver qué contiene"): restaura el resguardo COMPLETO a una base
 *   separada, de mentira, cuenta lo que trae y la borra. Sirve para comprobar "¿este
 *   resguardo tiene lo que necesito?" sin tocar los datos reales.
 * - "real" (link chico "Reponer configuración"): restaura SOLO la configuración de la
 *   agencia (AFIP, políticas, bot de WhatsApp, reglas de multas/comisiones) sobre los
 *   datos reales, y solo si esas partes están vacías ahora mismo.
 * - "total" (botón principal "Volver a esta copia", ex "Restaurar todo"): devuelve TODO el
 *   sistema al estado del resguardo elegido. A diferencia de los otros dos modos, mientras
 *   esta restauración corre el motor tumba TODA la API con 503 ({code: "MAINTENANCE"}) —
 *   por eso este modo prende la pantalla de mantenimiento global (ver maintenanceState.js y
 *   MaintenanceScreen.jsx) apenas se confirma. Además, por ser la operación más destructiva
 *   del sistema, el motor exige un motivo (mínimo 10 caracteres) de por qué se ejecuta.
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

// ADR-052 (2026-07-29, "Restaurar todo acepta resguardos de versiones anteriores"): valores
// exactos que manda el motor en `versionResguardo` (GET /admin/danger/backups), contrato en
// castellano igual que RESTORE_MODO_*. "actual" es el único caso sin marca ni aviso.
export const VERSION_RESGUARDO_ACTUAL = "actual";
export const VERSION_RESGUARDO_ANTERIOR = "anterior";
export const VERSION_RESGUARDO_POSTERIOR = "posterior";
export const VERSION_RESGUARDO_DESCONOCIDA = "desconocida";

// Bug reportado por el dueño (2026-07-28): el motor YA exigía este motivo (contrato
// SystemDataRestoreRequest.Motivo, obligatorio y con este mínimo SOLO para modo "total" —
// hallazgo de seguridad B6/F-16: la operación más destructiva del sistema tiene que quedar
// auditada con el POR QUÉ), pero la pantalla nunca tuvo el campo para cargarlo. Este número
// tiene que coincidir con el mínimo que valida el backend.
export const MOTIVO_RESTAURAR_TODO_MIN_LENGTH = 10;

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
 * F9 (deuda 30/07): sufijo ESTABLE para los data-testid de una fila de backup, sin usar el
 * nombre de archivo interno del resguardo (`backup.archivo`, un detalle de almacenamiento
 * que puede traer puntos/guiones bajos/mayúsculas raras y que en teoría podría cambiar si
 * algún día se reorganiza cómo se nombran los archivos en disco). Usamos la FECHA del
 * resguardo: es el dato funcional que de verdad identifica "qué copia es esta" para quien
 * mira la pantalla, y la sanitizamos para que sea un valor prolijo de usar en un selector
 * CSS/testid (solo letras, números y guiones).
 *
 * OJO: esto es SOLO para testids/ids de accesibilidad — el `key` de React y el estado de
 * "qué fila está abierta" siguen usando `backup.archivo` como antes (es la clave real que
 * necesita el pedido de restaurar), esto no cambia.
 *
 * @param {{fechaUtc?: string}} backup
 * @returns {string}
 */
export function construirSufijoTestIdBackup(backup) {
    return String(backup?.fechaUtc || "sin-fecha").replace(/[^a-zA-Z0-9]/g, "-");
}

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
 * Fix ADR-052 (2026-07-29): traduce el `versionResguardo` que manda el motor a uno de los
 * 4 valores conocidos. Compatibilidad hacia atrás: si el campo viene ausente (API vieja,
 * o cualquier valor que no reconozcamos) se trata IGUAL que "desconocida" — nunca como
 * "actual", porque eso afirmaría una compatibilidad que en realidad no se pudo determinar.
 *
 * @param {string|null|undefined} versionResguardo
 * @returns {"actual"|"anterior"|"posterior"|"desconocida"}
 */
export function normalizarVersionResguardo(versionResguardo) {
    const valoresConocidos = [
        VERSION_RESGUARDO_ACTUAL,
        VERSION_RESGUARDO_ANTERIOR,
        VERSION_RESGUARDO_POSTERIOR,
        VERSION_RESGUARDO_DESCONOCIDA,
    ];
    return valoresConocidos.includes(versionResguardo) ? versionResguardo : VERSION_RESGUARDO_DESCONOCIDA;
}

/**
 * Marca de la fila de la lista de resguardos (ADR-052 §D5/§D6, gate UX 2026-07-29): un
 * badge chico con texto real (nunca solo color, P-9/P-10). La fila NUNCA se atenúa por
 * esto — es información, no un impedimento (decisión firmada: ningún estado apaga nada).
 * "actual" devuelve `null` a propósito: es el camino de hoy, sin marca.
 *
 * `color` es una clave semántica ("ambar"/"rosa"/"gris"), no clases de Tailwind — el
 * componente decide las clases visuales, esta lógica no sabe nada de CSS.
 *
 * @param {string|null|undefined} versionResguardo
 * @returns {{texto: string, color: "ambar"|"rosa"|"gris"}|null}
 */
export function construirBadgeVersionResguardo(versionResguardo) {
    switch (normalizarVersionResguardo(versionResguardo)) {
        case VERSION_RESGUARDO_ANTERIOR:
            return { texto: "Versión anterior", color: "ambar" };
        case VERSION_RESGUARDO_POSTERIOR:
            return { texto: "Versión más nueva", color: "rosa" };
        case VERSION_RESGUARDO_DESCONOCIDA:
            return { texto: "Versión desconocida", color: "gris" };
        default:
            return null;
    }
}

/**
 * Cartel informativo debajo de la lista, cuando el resguardo ELEGIDO no es de la versión
 * de hoy (ADR-052 §D6). Un solo cartel por vez (el del resguardo seleccionado), nunca uno
 * por fila. Ningún botón se apaga por esto — el freno real es el chequeo del motor al
 * confirmar, que revisa antes de tocar nada y avisa en el Cartel emergente de siempre si
 * rechaza (P-13). "actual" devuelve `null`: nada que avisar.
 *
 * Fix de review (B1, bloqueante): estos textos son los LITERALES firmados por el gate UX
 * en `docs/ux/guia-ux-gaston.md`, sección "Textos finales implementados (2026-07-29)" — la
 * versión anterior de este archivo tenía una paráfrasis propia, no la firmada. La guía marca
 * en NEGRITA la primera oración de cada cartel (ahí vive el mensaje central); por eso acá se
 * separa en `titulo` (esa primera oración, para negrita) y `texto` (el resto). El cartel
 * "anterior" incluye la cláusula de alcance agregada por el hallazgo B2 del reviewer anterior
 * ("esto vale para 'Restaurar todo'...") — ya no hace falta una función aparte para ese punto.
 *
 * @param {string|null|undefined} versionResguardo
 * @returns {{titulo: string, texto: string, color: "ambar"|"rosa"|"gris"}|null}
 */
export function construirAvisoVersionResguardo(versionResguardo) {
    switch (normalizarVersionResguardo(versionResguardo)) {
        case VERSION_RESGUARDO_ANTERIOR:
            return {
                color: "ambar",
                titulo: "Este resguardo es más viejo que el sistema de hoy.",
                // Fix de review (item 12, firmado por Gastón el 2026-07-30): SOLO se actualiza la mención
                // del nombre del botón ("Restaurar todo" → "Volver a esta copia", el nombre nuevo de la
                // acción principal). El resto del aviso queda palabra por palabra, sin tocar.
                texto:
                    "Se puede usar igual: primero se traen los datos y después el sistema se pone al " +
                    "día solo. Puede tardar un poco más de lo normal. Si ese último paso falla, el " +
                    "sistema vuelve solo a como está ahora, sin perder nada. Esto vale para " +
                    "\"Volver a esta copia\": las otras dos acciones pueden avisarte que este resguardo no " +
                    "les sirve.",
            };
        case VERSION_RESGUARDO_POSTERIOR:
            return {
                color: "rosa",
                titulo: "Este resguardo parece de una versión más nueva que el sistema de hoy.",
                texto:
                    "Lo más probable es que no se pueda usar: antes de tocar nada, el sistema lo " +
                    "revisa y, si es así, lo rechaza y te avisa sin haber cambiado nada. Si igual " +
                    "necesitás volver a este punto, avisale al equipo técnico.",
            };
        case VERSION_RESGUARDO_DESCONOCIDA:
            return {
                color: "gris",
                titulo: "No pudimos determinar de qué versión es este resguardo.",
                texto: "Podés intentar igual: si no se puede usar, te lo avisamos antes de tocar nada.",
            };
        default:
            return null;
    }
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
 * Fix de bug reportado por el dueño (2026-07-28, P-9/T-5): "Restaurar todo" es el único de
 * los tres modos donde el motor exige un motivo (mínimo 10 caracteres, recortando espacios
 * en los extremos igual que hace el backend con `Trim()`) — los otros dos modos lo ignoran,
 * así que esta validación no les aplica.
 *
 * @param {string} motivo
 * @returns {boolean}
 */
export function motivoRestaurarTodoEsValido(motivo) {
    return typeof motivo === "string" && motivo.trim().length >= MOTIVO_RESTAURAR_TODO_MIN_LENGTH;
}

/**
 * Motivo por el que el botón principal ("Volver a esta copia", el ex "Restaurar todo") sigue
 * apagado, en el mismo criterio "prohibido tooltip" (P-9) que `construirMotivoRestoreDeshabilitado`.
 * A diferencia de aquel (compartido por las tres acciones: resguardo/frase/contraseña), este
 * helper es SOLO el delta extra que exige el modo "total" — el motivo del porqué. El componente
 * decide cuándo conviene mostrar este texto (nunca junto al motivo genérico de las tres
 * acciones, así no queda duplicado): acá no hace falta saber nada de ese otro gate.
 *
 * Fix de review (B4): antes esta función recibía también `motivoAccionDeshabilitada` y lo
 * reenviaba tal cual cuando estaba presente — una rama que nunca se alcanzaba en la práctica,
 * porque el componente ya oculta este texto con `!motivoAccionDeshabilitada &&` antes de
 * siquiera mirar este valor. Se saca esa rama muerta y el helper queda como lo que
 * realmente es: la validación del motivo, nada más.
 *
 * Rediseño 2026-07-30 (P7=A): el texto nombra el botón con su nombre NUEVO ("Volver a esta
 * copia"), porque en la pantalla vieja decía "Restaurar todo" y ese botón ya no existe así.
 *
 * @param {string} motivoRestaurarTodo - lo que el usuario tipeó en "¿Por qué volvés a esta copia?".
 * @returns {string|null}
 */
export function construirMotivoRestaurarTodoDeshabilitado(motivoRestaurarTodo) {
    if (motivoRestaurarTodoEsValido(motivoRestaurarTodo)) return null;
    // Fix de review (B2/P-9): el hint vive debajo de la acción principal — sin nombrarla, el
    // admin no sabe a qué botón se refiere. Unificamos "caracteres" acá (antes decía "letras"),
    // igual que el error inline de este mismo campo.
    return `Para "Volver a esta copia" falta escribir el motivo (mínimo ${MOTIVO_RESTAURAR_TODO_MIN_LENGTH} caracteres).`;
}

/**
 * Texto fijo de la marca roja que queda pegada a la ficha de una copia cuando el motor
 * rechaza la acción (rediseño 2026-07-30 §4.7/P10=A). No es el mensaje del motor (ese va
 * TAL CUAL dentro del Cartel emergente, P-13) — es solo el rótulo corto de la ficha que
 * invita a releerlo con "Ver el motivo". Cambia según qué acción falló, para no decir "no
 * se cambió nada" en una acción que de entrada no cambia nada real (Ver qué contiene).
 *
 * @param {"prueba"|"real"|"total"} modo
 * @returns {string}
 */
export function construirTextoMarcaRechazo(modo) {
    if (modo === RESTORE_MODO_TOTAL) return "No se pudo volver a esta copia. No se cambió nada.";
    if (modo === RESTORE_MODO_REAL) return "No se pudo reponer la configuración desde esta copia. No se cambió nada.";
    return "No se pudo ver el contenido de esta copia.";
}

// Rediseño 2026-07-30 (§4.5, ajuste post-firma): ORDEN REAL en el que el motor hace los 3
// pasos de "Volver a esta copia" — primero trae los datos a una base aparte (sin tocar nada
// real todavía), RECIÉN DESPUÉS guarda el resguardo del estado actual (así, si el archivo
// elegido está roto, no se gastan minutos de mantenimiento al pedo) y al final actualiza el
// sistema. Mismo orden y mismos 3 códigos que el backend (TravelApi.Application.DTOs.
// RestoreProgressSteps) — el texto de cada paso es el mismo que el motor ya manda cuando le
// toca ser el paso EN CURSO (acá queda repetido para poder pintar, en negro, los pasos que
// YA pasaron y los que todavía faltan, que el motor no manda).
export const PASO_RESTORE_DATOS = "datos";
export const PASO_RESTORE_RESGUARDO = "resguardo";
export const PASO_RESTORE_ACTUALIZACION = "actualizacion";

const ORDEN_PASOS_RESTORE_TOTAL = [
    { codigo: PASO_RESTORE_DATOS, texto: "Trayendo los datos de la copia elegida" },
    { codigo: PASO_RESTORE_RESGUARDO, texto: "Guardamos una copia de cómo está el sistema ahora" },
    { codigo: PASO_RESTORE_ACTUALIZACION, texto: "Poniendo el sistema al día" },
];

/**
 * Arma la checklist de 3 pasos de la pantalla de espera de "Volver a esta copia" (única
 * operación del producto donde el usuario no puede seguir trabajando mientras corre, spec
 * 2026-07-30 §4.5). El CÓDIGO de cada paso (que manda el motor en `GET /system/status`)
 * decide el orden (fijo) y el estado de cada línea: "done" (✓) para los que ya pasaron,
 * "doing" (◐) para el que está en curso, "pending" (○) para los que faltan.
 *
 * El TEXTO del paso en curso se toma de `pasoTexto` (lo que manda el motor, P-13: nunca se
 * reescribe); los demás usan el mismo texto fijo de arriba — es el MISMO texto que el motor
 * ya mostró (o va a mostrar) cuando a ellos les tocó ser el paso actual, no una redacción
 * propia del front.
 *
 * Si `paso` es `null` o no es ninguno de los 3 códigos conocidos (sin restauración en curso,
 * o un valor viejo que este front todavía no contempla), NINGÚN paso se marca — spec 8A: "si
 * paso es null, checklist sin estado marcado", todo en "pending".
 *
 * @param {{paso: string|null|undefined, pasoTexto: string|null|undefined}} estado
 * @returns {{codigo: string, texto: string, estado: "done"|"doing"|"pending"}[]}
 */
export function construirPasosEsperaRestoreTotal({ paso, pasoTexto } = {}) {
    const indiceActual = ORDEN_PASOS_RESTORE_TOTAL.findIndex((item) => item.codigo === paso);

    return ORDEN_PASOS_RESTORE_TOTAL.map((item, indice) => {
        if (indiceActual === -1) {
            return { codigo: item.codigo, texto: item.texto, estado: "pending" };
        }
        if (indice < indiceActual) {
            return { codigo: item.codigo, texto: item.texto, estado: "done" };
        }
        if (indice === indiceActual) {
            return { codigo: item.codigo, texto: pasoTexto || item.texto, estado: "doing" };
        }
        return { codigo: item.codigo, texto: item.texto, estado: "pending" };
    });
}

// Rediseño 2026-07-30 (§7 punto 1): los 3 valores exactos que manda el motor en
// `porQueSeGuardo` (GET /admin/danger/backups), calcados de BackupOriginLabels en el backend
// (TravelApi.Application.DTOs.SystemDataRestoreDtos.cs). "Guardada a mano" es el default del
// propio backend para cualquier origen que no pueda determinar.
export const ORIGEN_BACKUP_EMPEZAR_DE_CERO = "Antes de empezar de cero";
export const ORIGEN_BACKUP_VOLVER_A_COPIA = "Antes de volver a una copia";
export const ORIGEN_BACKUP_MANUAL = "Guardada a mano";

/**
 * Fix de review (mismo criterio que `normalizarVersionResguardo`): el motor ya manda
 * `porQueSeGuardo` traducido en criollo, pero acá se normaliza contra la lista blanca de las
 * 3 frases firmadas — un valor que no sea ninguna de esas tres (API vieja, caché, un origen
 * nuevo que este front todavía no contempla, vacío o ausente) cae SIEMPRE en "Guardada a
 * mano", nunca se muestra tal cual un texto que no reconocemos.
 *
 * @param {string|null|undefined} porQueSeGuardo
 * @returns {string}
 */
export function resolverPorQueSeGuardo(porQueSeGuardo) {
    const valoresConocidos = [ORIGEN_BACKUP_EMPEZAR_DE_CERO, ORIGEN_BACKUP_VOLVER_A_COPIA, ORIGEN_BACKUP_MANUAL];
    return valoresConocidos.includes(porQueSeGuardo) ? porQueSeGuardo : ORIGEN_BACKUP_MANUAL;
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
 * `versionResguardo` (ADR-052, 2026-07-29): también solo aplica al modo "total". Cuando el
 * resguardo elegido es "anterior", el dueño firmó una LÍNEA EXTRA al final de este mismo
 * "¿Seguro?" (no un paso de confirmación aparte) avisando que el sistema se actualiza solo
 * después de traer los datos. Para "actual"/"posterior"/"desconocida" el texto no cambia.
 *
 * @param {"prueba"|"real"|"total"} modo
 * @param {{fechaBackup?: string|null, versionResguardo?: string|null}} [contexto]
 * @returns {{title: string, text: string, confirmText: string, confirmColor: string}}
 */
export function construirConfirmacionRestore(modo, { fechaBackup, versionResguardo } = {}) {
    if (modo === RESTORE_MODO_TOTAL) {
        const fechaTexto = fechaBackup || "de este resguardo";
        let text =
            `Esto devuelve TODO el sistema a como estaba el ${fechaTexto}. Lo que hayas cargado ` +
            "después se pierde. Antes se guarda un resguardo del estado actual, así podés volver " +
            "a este momento si te arrepentís.";

        if (normalizarVersionResguardo(versionResguardo) === VERSION_RESGUARDO_ANTERIOR) {
            text += " Este resguardo es más viejo: después de traer los datos, el sistema se pone al día solo.";
        }

        return {
            title: "¿Restaurar TODO el sistema?",
            text,
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
 * F6 (medio 31/07): mientras el motor tira abajo y repone TODA la API para una restauración
 * "Volver a esta copia", el PROPIO pedido POST que arrancó esa restauración puede perder la
 * conexión a mitad de camino (el contenedor de la API se reinicia). Eso NO es un rechazo del
 * motor — es la reconexión natural de una restauración que sigue en curso. Antes, cualquier
 * error acá se trataba igual que un rechazo real: se apagaba la pantalla de mantenimiento
 * (`deactivateMaintenance()`) y se mostraba un cartel de "no se pudo completar la operación"
 * que podía ser directamente falso (la restauración quizás terminó bien igual).
 *
 * Distinguimos por la FORMA del error, no por su texto (los textos pueden variar entre
 * navegadores — "Failed to fetch", "Load failed", etc.):
 *   - Si `error.status` es undefined, el `fetch()` nunca llegó a tener una respuesta HTTP real
 *     (corte de red/conexión) — típico de un contenedor reiniciándose a mitad de pedido.
 *   - Si el motor SÍ respondió pero con el 503 "MAINTENANCE" (ver esErrorDeMantenimiento),
 *     también es "seguimos esperando": literalmente nos está diciendo que sigue restaurando.
 *   - Fix de review (bug real de PROD, plan tanda F): en PROD hay un nginx DEL HOST (no del
 *     motor) delante de la API con un timeout de 60 segundos (ver `nginx.conf`, sección del
 *     location que expone la API) — la restauración total tarda MINUTOS, así que ese nginx
 *     corta la conexión antes de que el motor termine y le devuelve al navegador un error
 *     genérico de gateway (408/502/504), con el HTML de la página de error de nginx en el
 *     cuerpo, no un JSON del motor. Ese corte es del intermediario, no un rechazo del motor:
 *     hay que seguir esperando exactamente igual que con el corte de red (status undefined).
 *
 * Cualquier OTRO error (400/409/403 con mensaje real del motor, o un 503 SIN el code
 * "MAINTENANCE" — ej. la base de datos caída por otro motivo) sigue siendo un rechazo
 * genuino y se muestra como tal — acá no cambia nada.
 *
 * @param {{status?: number, code?: string|null}} error
 * @returns {boolean} true si hay que seguir esperando (NO apagar mantenimiento, NO mostrar rechazo)
 */
export function debeSeguirEsperandoTrasErrorDeRestoreTotal(error) {
    if (error?.status === undefined) return true;

    // 408 (Request Timeout), 502 (Bad Gateway) y 504 (Gateway Timeout): errores típicos de un
    // proxy/gateway que corta la conexión, no del motor. El motor real solo puede devolver el
    // 503+MAINTENANCE de esErrorDeMantenimiento mientras restaura; estos tres códigos SIEMPRE
    // vienen de un intermediario (nginx del host, load balancer, etc.), nunca del propio backend.
    if (error.status === 408 || error.status === 502 || error.status === 504) return true;

    return esErrorDeMantenimiento({ status: error.status, code: error.code });
}

/**
 * Fix de review (2026-08, plan tanda F): decide si la pantalla de mantenimiento (Maintenance
 * Screen.jsx) corresponde a una restauración TOTAL ("Volver a esta copia") o a mantenimiento
 * por otro motivo, y arma el título EXACTO que va en el `<h1>` de esa pantalla. Se extrae del
 * componente a esta función pura para poder testear con node:test los DOS títulos posibles
 * sin montar React (este repo no tiene RTL/jsdom — solo lógica pura + node:test).
 *
 * Misma regla que ya vivía inline en el componente: sabemos con certeza que es una
 * restauración total cuando esta pestaña la disparó (conoce `fechaResguardo`, la vio en la
 * lista antes de tocar el botón) o cuando el motor ya publicó algún paso de restore (`paso`
 * viene de GET /system/status y SOLO esa acción lo llena). Si ninguna de las dos es cierta,
 * no inventamos "estamos volviendo a una copia": es mantenimiento por otro motivo.
 *
 * @param {{fechaResguardo?: string|null, paso?: string|null}} [params]
 * @returns {{esRestoreTotal: boolean, titulo: string}}
 */
export function calcularTituloPantallaMantenimiento({ fechaResguardo = null, paso = null } = {}) {
    const esRestoreTotal = Boolean(fechaResguardo) || Boolean(paso);

    if (!esRestoreTotal) {
        return { esRestoreTotal: false, titulo: "El sistema está en mantenimiento" };
    }

    return {
        esRestoreTotal: true,
        titulo: fechaResguardo
            ? `Estamos volviendo a la copia del ${fechaResguardo}`
            : "Estamos volviendo a una copia anterior",
    };
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
        // Fix de review (Guía P7=A, vocabulario de esta pantalla): "resguardo" → "copia" — este texto es
        // propio de la pantalla nueva (no es un literal firmado ADR-052 ni un "¿Seguro?"), así que sigue el
        // vocabulario nuevo de "Copias de seguridad".
        encabezado: "Esto es lo que contiene la copia:",
        filas,
        sinDatos: filas.length === 0,
        mensajeSinDatos: "La copia no tiene datos de negocio cargados.",
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
