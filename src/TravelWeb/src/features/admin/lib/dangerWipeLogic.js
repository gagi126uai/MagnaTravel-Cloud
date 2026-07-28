/**
 * Lógica pura (sin React, sin fetch) de la pantalla "Empezar de cero" (Zona peligrosa,
 * Administración → Mantenimiento). Se usa desde EmpezarDeCeroModal.jsx.
 *
 * Reglas del negocio que vive acá (para poder testearlas sin renderizar el modal):
 * - la frase de confirmación tiene que coincidir EXACTO, letra por letra (P-9/T-5:
 *   nada de "recortar espacios de más" para el usuario — si escribió mal, no habilita);
 * - el botón de borrar queda SIEMPRE visible pero apagado con el motivo a la vista
 *   (P-9: "apagado con motivo", nunca ocultar la acción sin explicar por qué);
 * - el texto de conteos y el de la doble confirmación se arman acá, en criollo,
 *   sin números de public IDs ni nombres técnicos de tablas;
 * - BORRADO POR GRUPOS (obra 2026-07-27, firma del dueño "tilda solo y avisa"): el usuario
 *   elige QUÉ grupos borrar, no todo o nada. Si un grupo tildado arrastra a otro (ej.
 *   "clientes" arrastra a "reservas y su plata" porque cada reserva tiene un titular), ese
 *   otro se tilda solo y queda bloqueado mientras el que lo arrastra siga tildado — esto
 *   vale IGUAL si el usuario también lo había tildado él mismo (ver `estaForzadoPorOtroGrupo`,
 *   fix de review 2026-07-27 al bug de "click mudo": un checkbox que se ve habilitado pero
 *   destildarlo no hacía nada, sin explicación).
 */

// Frase exacta que el usuario tiene que tipear para habilitar el borrado.
// A propósito NO se hace ningún trim ni normalización: si el usuario dejó un espacio
// de más o escribió en minúsculas, el botón sigue apagado (evita un "borrado accidental"
// por autocompletado del navegador).
export const FRASE_CONFIRMACION_WIPE = "BORRAR TODO";

// Nombres de grupo, EXACTOS a como los espera el backend en `grupos: string[]` del POST
// (ver TravelApi.Application.Constants.WipeGroups). Son los mismos strings que trae
// `dependencias` en el preview, así que no hay traducción de por medio.
export const WIPE_GRUPO_RESERVAS_Y_PLATA = "reservasYPlata";
export const WIPE_GRUPO_CLIENTES = "clientes";
export const WIPE_GRUPO_OPERADORES = "operadores";
export const WIPE_GRUPO_TARIFARIO = "tarifario";
export const WIPE_GRUPO_PAISES_Y_DESTINOS = "paisesYDestinos";
export const WIPE_GRUPO_POSIBLES_CLIENTES = "posiblesClientes";
export const WIPE_GRUPO_CONFIGURACION = "configuracion";

export const TODOS_LOS_GRUPOS_WIPE = [
    WIPE_GRUPO_RESERVAS_Y_PLATA,
    WIPE_GRUPO_CLIENTES,
    WIPE_GRUPO_OPERADORES,
    WIPE_GRUPO_TARIFARIO,
    WIPE_GRUPO_PAISES_Y_DESTINOS,
    WIPE_GRUPO_POSIBLES_CLIENTES,
    WIPE_GRUPO_CONFIGURACION,
];

// Orden y etiquetas en criollo de cada grupo de datos del preview. El orden acá define
// el orden en que se muestran los conteos en la pantalla (de lo más "grande" del
// negocio del día a día, a lo más de catálogo/config).
const ETIQUETAS_CONTEOS_WIPE = [
    { clave: "reservas", singular: "reserva", plural: "reservas" },
    { clave: "clientes", singular: "cliente", plural: "clientes" },
    { clave: "operadores", singular: "operador", plural: "operadores" },
    { clave: "pasajeros", singular: "pasajero", plural: "pasajeros" },
    { clave: "facturas", singular: "factura", plural: "facturas" },
    { clave: "cobros", singular: "cobro", plural: "cobros" },
    { clave: "movimientosCaja", singular: "movimiento de caja", plural: "movimientos de caja" },
    { clave: "archivos", singular: "archivo", plural: "archivos" },
    { clave: "paisesYDestinos", singular: "país o destino cargado", plural: "países y destinos cargados" },
    { clave: "tarifario", singular: "tarifa del tarifario", plural: "tarifas del tarifario" },
    { clave: "posiblesClientes", singular: "cliente potencial", plural: "clientes potenciales" },
];

/**
 * Catálogo de los 7 grupos que el usuario puede elegir para borrar. `conteoClaves` dice
 * qué campos del objeto `conteos` del preview pertenecen a ese grupo (para armar el
 * detalle "59 reservas · 142 cobros" al lado de cada checkbox). "configuracion" no tiene
 * conteoClaves porque el preview no cuenta filas de configuración (son 5 tablas de ajustes,
 * no "cosas" que tenga sentido contar para el usuario).
 */
export const GRUPOS_WIPE_META = [
    {
        clave: WIPE_GRUPO_RESERVAS_Y_PLATA,
        etiqueta: "Reservas y su plata",
        descripcion: "Reservas, pasajeros, facturas, cobros, caja y archivos adjuntos.",
        conteoClaves: ["reservas", "pasajeros", "facturas", "cobros", "movimientosCaja", "archivos"],
    },
    {
        clave: WIPE_GRUPO_CLIENTES,
        etiqueta: "Clientes",
        descripcion: "Los clientes cargados y sus límites de crédito.",
        conteoClaves: ["clientes"],
    },
    {
        clave: WIPE_GRUPO_OPERADORES,
        etiqueta: "Operadores",
        descripcion: "Los proveedores/operadores y su cuenta corriente.",
        conteoClaves: ["operadores"],
    },
    {
        clave: WIPE_GRUPO_TARIFARIO,
        etiqueta: "Tarifario",
        descripcion: "Las tarifas y paquetes cargados en el tarifario.",
        conteoClaves: ["tarifario"],
    },
    {
        clave: WIPE_GRUPO_PAISES_Y_DESTINOS,
        etiqueta: "Países y destinos",
        descripcion: "Los países y destinos cargados.",
        conteoClaves: ["paisesYDestinos"],
    },
    {
        clave: WIPE_GRUPO_POSIBLES_CLIENTES,
        etiqueta: "Clientes potenciales",
        descripcion: "Los clientes potenciales y los presupuestos armados para ellos.",
        conteoClaves: ["posiblesClientes"],
    },
    {
        clave: WIPE_GRUPO_CONFIGURACION,
        etiqueta: "Configuración de la agencia",
        descripcion: "AFIP, políticas de aprobación, bot de WhatsApp y reglas de multas/comisiones.",
        conteoClaves: [],
    },
];

function encontrarMetaDeGrupo(clave) {
    return GRUPOS_WIPE_META.find((meta) => meta.clave === clave) || null;
}

/**
 * Arma la lista de filas para pintar los conteos del preview (una por grupo, con
 * su cantidad). Sirve tanto para el bloque "esto se borra" como para el resumen final
 * de éxito, reusando la misma tabla de etiquetas.
 *
 * @param {object} conteos - objeto { reservas, clientes, ... } que manda el backend.
 * @returns {{clave: string, etiqueta: string, cantidad: number}[]}
 */
export function construirFilasConteosWipe(conteos) {
    if (!conteos || typeof conteos !== "object") return [];

    return ETIQUETAS_CONTEOS_WIPE.map(({ clave, singular, plural }) => {
        const cantidad = Number(conteos[clave]) || 0;
        const etiqueta = cantidad === 1 ? singular : plural;
        return { clave, etiqueta, cantidad };
    });
}

/**
 * Texto corto en criollo con los conteos separados por "·", para mostrar arriba
 * del modal (ej. "30 reservas · 16 clientes · 8 operadores"). Los grupos en cero
 * se omiten para no ensuciar la lectura con "0 archivos".
 *
 * @param {object} conteos
 * @returns {string}
 */
export function construirResumenConteosWipe(conteos) {
    const filas = construirFilasConteosWipe(conteos).filter((fila) => fila.cantidad > 0);

    if (filas.length === 0) {
        return "Por ahora no hay datos de negocio cargados para borrar.";
    }

    return filas.map((fila) => `${fila.cantidad} ${fila.etiqueta}`).join(" · ");
}

/**
 * Arma el detalle en criollo de UN grupo puntual (ej. "59 reservas · 142 cobros"), filtrando
 * los conteos a solo las claves que pertenecen a ese grupo (`conteoClaves` del catálogo).
 * Para "configuracion" (sin conteoClaves) devuelve null: ese grupo no se cuenta por filas.
 *
 * @param {string} grupo
 * @param {object} conteos
 * @returns {string|null}
 */
export function construirDetalleConteoGrupoWipe(grupo, conteos) {
    const meta = encontrarMetaDeGrupo(grupo);
    if (!meta || meta.conteoClaves.length === 0) return null;

    const filas = construirFilasConteosWipe(conteos).filter(
        (fila) => meta.conteoClaves.includes(fila.clave) && fila.cantidad > 0
    );

    if (filas.length === 0) return "sin datos cargados por ahora";
    return filas.map((fila) => `${fila.cantidad} ${fila.etiqueta}`).join(" · ");
}

/**
 * Arma la lista de los 7 grupos ya lista para pintar en pantalla: etiqueta, descripción,
 * y el detalle de conteo de cada uno. Es la fuente única que usa EmpezarDeCeroModal para
 * dibujar los checkboxes (no arma disponibilidad/bloqueo, eso lo resuelven las funciones
 * de dependencias de más abajo, que necesitan además saber qué tildó el usuario).
 *
 * @param {object} conteos
 * @returns {{clave: string, etiqueta: string, descripcion: string, detalleConteo: string|null}[]}
 */
export function construirGruposWipeParaMostrar(conteos) {
    return GRUPOS_WIPE_META.map((meta) => ({
        clave: meta.clave,
        etiqueta: meta.etiqueta,
        descripcion: meta.descripcion,
        detalleConteo: construirDetalleConteoGrupoWipe(meta.clave, conteos),
    }));
}

/**
 * Cierre transitivo de "a qué grupos arrastra" a partir de un conjunto de grupos tildados
 * a mano. `dependencias` es el mapa que manda el backend: grupo -> grupos que arrastra.
 * Hoy la cadena tiene un solo nivel (clientes/operadores arrastran a reservasYPlata, que no
 * arrastra a nadie), pero el cierre transitivo deja la función correcta aunque el backend
 * sume más niveles el día de mañana.
 */
function cerrarDependenciasWipe(gruposSemilla, dependencias) {
    const cerrado = new Set(gruposSemilla);
    let siguioCreciendo = true;

    while (siguioCreciendo) {
        siguioCreciendo = false;
        for (const grupo of cerrado) {
            const arrastrados = dependencias?.[grupo] || [];
            for (const arrastrado of arrastrados) {
                if (!cerrado.has(arrastrado)) {
                    cerrado.add(arrastrado);
                    siguioCreciendo = true;
                }
            }
        }
    }

    return cerrado;
}

/**
 * Selección EFECTIVA a partir de lo que el usuario tildó a mano: sus grupos + todos los
 * que arrastran (cierre transitivo). Esto es lo que hay que mandar en el POST — nunca solo
 * lo que el usuario clickeó, porque el backend rechaza (409) una lista incoherente con las
 * dependencias.
 *
 * @param {string[]} gruposManual - grupos que el usuario tildó a mano.
 * @param {Record<string,string[]>} dependencias - mapa del preview (grupo -> grupos que arrastra).
 * @returns {string[]}
 */
export function calcularSeleccionEfectivaWipe(gruposManual, dependencias) {
    return Array.from(cerrarDependenciasWipe(new Set(gruposManual), dependencias));
}

/**
 * Fix bloqueante de review (2026-07-27, "click mudo"): ¿este grupo queda tildado igual
 * aunque el usuario lo destilde a mano, porque ALGÚN OTRO grupo que sigue tildado lo
 * arrastra? Se calcula sacando el grupo del conjunto tildado a mano y viendo si el cierre
 * de dependencias de LOS DEMÁS todavía lo trae de vuelta — funciona sin importar si el
 * grupo en cuestión también estaba tildado a mano (ese era el bug: antes solo se marcaba
 * como "arrastrado" a un grupo que el usuario NUNCA había tildado él mismo; si el usuario
 * lo había tildado — ej. con "Seleccionar todo" — el checkbox se mostraba habilitado pero
 * clickearlo no hacía nada, sin ninguna explicación).
 *
 * Es la ÚNICA función que decide "¿está bloqueado?" — la usan tanto
 * `calcularGruposArrastradosWipe` (para pintar el motivo) como `alternarGrupoWipe` (para
 * frenar el destilde): así no puede haber un lugar que diga "bloqueado" y otro que diga
 * "libre" para el mismo grupo.
 *
 * @param {string} grupo
 * @param {string[]} gruposManual
 * @param {Record<string,string[]>} dependencias
 * @returns {boolean}
 */
export function estaForzadoPorOtroGrupo(grupo, gruposManual, dependencias) {
    const manualSinEste = new Set(gruposManual);
    manualSinEste.delete(grupo);
    return cerrarDependenciasWipe(manualSinEste, dependencias).has(grupo);
}

/**
 * Para cada grupo actualmente seleccionado (tildado a mano o arrastrado) que sigue
 * forzado por OTRO grupo, devuelve quién lo arrastra. Se usa para pintar el checkbox
 * bloqueado con el motivo SIEMPRE a la vista (regla "tilda solo y avisa" + P-9 "apagado
 * con motivo"): el checkbox de ese grupo queda tildado, disabled, y con un texto tipo
 * "se borra también porque depende de Clientes" — esto vale IGUAL si el usuario además
 * lo había tildado él mismo (ver `estaForzadoPorOtroGrupo`), no solo cuando nunca lo tocó.
 *
 * @param {string[]} gruposManual
 * @param {Record<string,string[]>} dependencias
 * @returns {Record<string, string[]>} grupo arrastrado -> lista de grupos que lo arrastran.
 */
export function calcularGruposArrastradosWipe(gruposManual, dependencias) {
    const efectivo = cerrarDependenciasWipe(new Set(gruposManual), dependencias);
    const arrastrados = {};

    for (const grupo of efectivo) {
        if (!estaForzadoPorOtroGrupo(grupo, gruposManual, dependencias)) continue;

        const responsables = [];
        for (const otro of efectivo) {
            if (otro === grupo) continue;
            if ((dependencias?.[otro] || []).includes(grupo)) responsables.push(otro);
        }
        arrastrados[grupo] = responsables;
    }

    return arrastrados;
}

/**
 * Tilda o destilda un grupo, respetando la regla "no se puede destildar un grupo mientras
 * el que lo arrastra siga tildado". Devuelve la nueva lista de grupos tildados A MANO
 * (no la selección efectiva — para eso está `calcularSeleccionEfectivaWipe`).
 *
 * - Tildar: siempre se permite, se agrega a la lista.
 * - Destildar: si `estaForzadoPorOtroGrupo` dice que igual queda arrastrado por otro grupo
 *   tildado, el pedido se ignora y se devuelve la lista sin cambios — el checkbox además
 *   debería estar disabled en la UI (con el motivo debajo, nunca solo en el título), esto
 *   es un cinturón extra por si se llama desde otro lado.
 *
 * @param {object} params
 * @param {string[]} params.gruposManual
 * @param {string} params.grupo
 * @param {Record<string,string[]>} params.dependencias
 * @param {boolean} params.tildar
 * @returns {string[]}
 */
export function alternarGrupoWipe({ gruposManual, grupo, dependencias, tildar }) {
    const manualSet = new Set(gruposManual);

    if (tildar) {
        manualSet.add(grupo);
        return Array.from(manualSet);
    }

    if (estaForzadoPorOtroGrupo(grupo, gruposManual, dependencias)) {
        return Array.from(manualSet); // bloqueado: otro grupo tildado todavía lo necesita.
    }

    manualSet.delete(grupo);
    return Array.from(manualSet);
}

/** Atajo "Seleccionar todo": equivale al "Empezar de cero" total de antes. */
export function seleccionarTodosLosGruposWipe() {
    return [...TODOS_LOS_GRUPOS_WIPE];
}

/**
 * Decide si el botón "Empezar de cero..." puede habilitarse.
 *
 * Reglas (todas tienen que cumplirse):
 * - hay AL MENOS un grupo seleccionado (nada que tildar = nada que borrar);
 * - la frase escrita coincide EXACTO con FRASE_CONFIRMACION_WIPE;
 * - la contraseña no está vacía (la valida el motor, acá solo chequeamos que se cargó algo);
 * - el preview no vino bloqueado (candado fiscal u otro motivo del backend);
 * - no hay un borrado en curso.
 *
 * @param {object} params
 * @param {string[]} params.grupos - selección efectiva (con dependencias ya resueltas).
 * @param {string} params.frase
 * @param {string} params.password
 * @param {boolean} params.bloqueado
 * @param {boolean} params.ejecutando
 * @returns {boolean}
 */
export function puedeConfirmarWipe({ grupos, frase, password, bloqueado, ejecutando }) {
    const hayGrupoSeleccionado = Array.isArray(grupos) && grupos.length > 0;
    const fraseExacta = frase === FRASE_CONFIRMACION_WIPE;
    const hayPassword = typeof password === "string" && password.length > 0;
    return hayGrupoSeleccionado && fraseExacta && hayPassword && !bloqueado && !ejecutando;
}

/**
 * Fix de review (P-9/P-10, "prohibido tooltip"): el motivo por el que el botón "Empezar de
 * cero..." está apagado tiene que estar SIEMPRE a la vista como texto, no solo en el
 * `title` (que en mobile/táctil nadie llega a ver). Devuelve el motivo vigente, o `null` si
 * el botón puede habilitarse. Revisa las mismas condiciones que `puedeConfirmarWipe`, en un
 * orden pensado para que el usuario resuelva primero lo más obvio (elegir qué borrar) antes
 * de pedirle la frase/contraseña.
 *
 * @param {object} params
 * @param {string[]} params.grupos - selección efectiva.
 * @param {string} params.frase
 * @param {string} params.password
 * @param {boolean} params.bloqueado
 * @param {string|null} [params.motivoBloqueo]
 * @returns {string|null}
 */
export function construirMotivoWipeDeshabilitado({ grupos, frase, password, bloqueado, motivoBloqueo }) {
    if (bloqueado) return motivoBloqueo || "El borrado está bloqueado.";
    if (!Array.isArray(grupos) || grupos.length === 0) return "Elegí al menos un grupo para borrar.";
    if (frase !== FRASE_CONFIRMACION_WIPE) return `Escribí la frase exacta "${FRASE_CONFIRMACION_WIPE}" para confirmar.`;
    if (!password) return "Cargá tu contraseña para confirmar.";
    return null;
}

/**
 * Arma la lista completa de "esto vuela", para el resumen que se muestra ANTES de
 * confirmar (regla del dueño: "resumen antes de confirmar: lista COMPLETA de lo que
 * vuela"). Una fila por grupo seleccionado, con su detalle de conteo si lo tiene.
 *
 * @param {string[]} gruposSeleccionados - selección efectiva (ya con dependencias resueltas).
 * @param {object} conteos
 * @returns {{clave: string, etiqueta: string, detalleConteo: string|null}[]}
 */
export function construirResumenCompletoSeleccionWipe(gruposSeleccionados, conteos) {
    return GRUPOS_WIPE_META.filter((meta) => gruposSeleccionados.includes(meta.clave)).map((meta) => ({
        clave: meta.clave,
        etiqueta: meta.etiqueta,
        detalleConteo: construirDetalleConteoGrupoWipe(meta.clave, conteos),
    }));
}

/**
 * Arma el texto de la doble confirmación final (se muestra con showConfirm, ANTES
 * de disparar el POST). Menciona los grupos elegidos por su nombre, y el texto cambia
 * si entre ellos está "configuración" (consecuencia extra: hay que reconfigurar AFIP).
 *
 * @param {string[]} gruposSeleccionados - selección efectiva.
 * @returns {{title: string, text: string, confirmText: string, confirmColor: string}}
 */
export function construirConfirmacionEmpezarDeCero(gruposSeleccionados) {
    const incluyeConfiguracion = gruposSeleccionados.includes(WIPE_GRUPO_CONFIGURACION);
    const etiquetas = GRUPOS_WIPE_META
        .filter((meta) => gruposSeleccionados.includes(meta.clave))
        .map((meta) => meta.etiqueta);

    const textoBase =
        `Se borra para siempre: ${etiquetas.join(", ")}. Los usuarios y la auditoría quedan ` +
        "intactos siempre. Antes de borrar se hace un backup completo.";

    const textoConConfiguracion =
        textoBase +
        " TAMBIÉN se borra la configuración de la agencia (AFIP, certificado, reglas de " +
        "multas y comisiones): después vas a tener que volver a cargarla antes de poder " +
        "facturar.";

    return {
        title: "¿Empezar de cero?",
        text: incluyeConfiguracion ? textoConConfiguracion : textoBase,
        confirmText: "Sí, borrar",
        confirmColor: "red",
    };
}

/**
 * Arma el resumen que se muestra en el panel de éxito, después de que el motor
 * confirmó el borrado. Se apoya en la misma tabla de etiquetas que el preview para
 * que los dos textos (antes/después) se lean igual.
 *
 * Fix de review (T-5, "nombre de archivo técnico"): el backend devuelve `backupArchivo`
 * como un nombre de archivo técnico (ej. "wipe-20260727-101500.dump") que NUNCA se muestra
 * en pantalla. En su lugar, se arma un mensaje que apunta a la pantalla "Volver atrás" con
 * la MISMA etiqueta de fecha que usa esa lista (ver `construirEtiquetaBackup` en
 * dangerRestoreLogic.js) — el `ahora`/`formatearFecha` se inyectan (en vez de usar
 * `new Date()` acá adentro) para que este armado siga siendo testeable sin depender del
 * reloj real ni de la zona horaria del entorno que corre los tests.
 *
 * @param {object} params
 * @param {object} params.borrado - conteos de lo efectivamente borrado, mismo shape que el preview.
 * @param {string} params.backupArchivo - nombre técnico del archivo de backup (NUNCA se muestra tal cual).
 * @param {string[]} params.gruposBorrados - grupos que el motor terminó borrando (resuelto final).
 * @param {Date} [params.ahora] - instante a mostrar como fecha del backup (default: ahora real).
 * @param {(fecha: Date) => string} params.formatearFecha - formateador de fecha (ej. formatDateTime de lib/utils.js).
 * @returns {{resumenConteos: string, mensajeConfiguracion: string, mensajeBackup: string|null}}
 */
export function construirResumenExitoWipe({ borrado, backupArchivo, gruposBorrados, ahora = new Date(), formatearFecha }) {
    const resumenConteos = construirResumenConteosWipe(borrado);
    const configuracionBorrada = Array.isArray(gruposBorrados) && gruposBorrados.includes(WIPE_GRUPO_CONFIGURACION);
    const mensajeConfiguracion = configuracionBorrada
        ? "También se borró la configuración: antes de facturar hay que volver a cargar AFIP y las reglas de la agencia."
        : "La configuración de la agencia (AFIP, reglas, cuentas bancarias) se conservó tal cual estaba.";

    const mensajeBackup = backupArchivo && typeof formatearFecha === "function"
        ? `El resguardo quedó guardado. Lo vas a encontrar en "Volver atrás" como "Resguardo del ${formatearFecha(ahora)}".`
        : null;

    return {
        resumenConteos,
        mensajeConfiguracion,
        mensajeBackup,
    };
}
