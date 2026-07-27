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
 *   sin números de public IDs ni nombres técnicos de tablas.
 */

// Frase exacta que el usuario tiene que tipear para habilitar el borrado.
// A propósito NO se hace ningún trim ni normalización: si el usuario dejó un espacio
// de más o escribió en minúsculas, el botón sigue apagado (evita un "borrado accidental"
// por autocompletado del navegador).
export const FRASE_CONFIRMACION_WIPE = "BORRAR TODO";

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
 * Decide si el botón "Empezar de cero..." puede habilitarse.
 *
 * Reglas (todas tienen que cumplirse):
 * - la frase escrita coincide EXACTO con FRASE_CONFIRMACION_WIPE;
 * - la contraseña no está vacía (la valida el motor, acá solo chequeamos que se cargó algo);
 * - el preview no vino bloqueado (candado fiscal u otro motivo del backend);
 * - no hay un borrado en curso.
 *
 * @param {object} params
 * @param {string} params.frase
 * @param {string} params.password
 * @param {boolean} params.bloqueado
 * @param {boolean} params.ejecutando
 * @returns {boolean}
 */
export function puedeConfirmarWipe({ frase, password, bloqueado, ejecutando }) {
    const fraseExacta = frase === FRASE_CONFIRMACION_WIPE;
    const hayPassword = typeof password === "string" && password.length > 0;
    return fraseExacta && hayPassword && !bloqueado && !ejecutando;
}

/**
 * Arma el texto de la doble confirmación final (se muestra con showConfirm, ANTES
 * de disparar el POST). El texto cambia si el usuario tildó "borrar también la
 * configuración", porque en ese caso hay una consecuencia extra (hay que reconfigurar
 * AFIP antes de poder facturar de nuevo).
 *
 * @param {boolean} incluirConfiguracion
 * @returns {{title: string, text: string, confirmText: string, confirmColor: string}}
 */
export function construirConfirmacionEmpezarDeCero(incluirConfiguracion) {
    const textoBase =
        "Se borran todas las reservas, clientes, operadores, tarifario, países y destinos, " +
        "facturas, cobros, caja y archivos cargados. Los usuarios y la auditoría quedan " +
        "intactos siempre. Antes de borrar se hace un backup completo.";

    const textoConConfiguracion =
        textoBase +
        " TAMBIÉN se borra la configuración de la agencia (AFIP, certificado, reglas de " +
        "multas y comisiones): después vas a tener que volver a cargarla antes de poder " +
        "facturar.";

    return {
        title: "¿Empezar de cero?",
        text: incluirConfiguracion ? textoConConfiguracion : textoBase,
        confirmText: "Sí, empezar de cero",
        confirmColor: "red",
    };
}

/**
 * Arma el resumen que se muestra en el panel de éxito, después de que el motor
 * confirmó el borrado. Se apoya en la misma tabla de etiquetas que el preview para
 * que los dos textos (antes/después) se lean igual.
 *
 * @param {object} params
 * @param {object} params.borrado - conteos de lo efectivamente borrado, mismo shape que el preview.
 * @param {string} params.backupArchivo - nombre del archivo de backup generado.
 * @param {boolean} params.configuracionBorrada - si además se borró la configuración.
 * @returns {{resumenConteos: string, mensajeConfiguracion: string}}
 */
export function construirResumenExitoWipe({ borrado, backupArchivo, configuracionBorrada }) {
    const resumenConteos = construirResumenConteosWipe(borrado);
    const mensajeConfiguracion = configuracionBorrada
        ? "También se borró la configuración: antes de facturar hay que volver a cargar AFIP y las reglas de la agencia."
        : "La configuración de la agencia (AFIP, reglas, cuentas bancarias) se conservó tal cual estaba.";

    return {
        resumenConteos,
        mensajeConfiguracion,
        backupArchivo: backupArchivo || null,
    };
}
