/**
 * Lógica pura de "Caja sin carreras" (fix #41, Tanda 3, 2026-07-24): evita que useCash.js
 * dispare pedidos duplicados al backend y evita que una respuesta VIEJA (que tardó más en
 * volver) pise a una más nueva en pantalla.
 *
 * Separada del hook (que vive en features/payments/hooks/useCash.js) para poder testear
 * la DECISIÓN sin montar React — mismo criterio que el resto de los archivos de lib/.
 */

/**
 * True si la respuesta que acaba de llegar es VIEJA (salió otro pedido más nuevo mientras
 * esta estaba en vuelo). Se usa comparando un id incremental: cada pedido nuevo saca un
 * número más alto; si el número con el que salió esta respuesta ya no es el vigente,
 * hay que descartarla sin tocar el estado.
 *
 * @param {number} requestId - el número que sacó ESTE pedido al salir.
 * @param {number} requestIdVigente - el número del ÚLTIMO pedido que salió (puede ser
 *   el mismo `requestId` si no salió ninguno más nuevo mientras tanto).
 * @returns {boolean}
 */
export function esRespuestaObsoleta(requestId, requestIdVigente) {
    return requestId !== requestIdVigente;
}

/**
 * Decide qué hacer en la corrida del efecto de carga de useCash: pedir datos ahora, o
 * solo reiniciar la página (sin pedir todavía, porque el cambio de página va a volver a
 * disparar la corrida que sí pide).
 *
 * Antes de este fix, useCash tenía DOS useEffect separados: uno pedía datos en cada
 * cambio, otro reiniciaba la página a 1 cuando cambiaba un filtro/mes. Cambiar un filtro
 * disparaba los DOS en la misma tanda — un pedido con la página VIEJA bajo el filtro
 * NUEVO (que se tira), y recién el segundo (ya en página 1) traía lo correcto: dos
 * pedidos reales al backend por un solo cambio del usuario.
 *
 * @param {{ firmaAnterior: string|null, firmaActual: string, esPrimeraCorrida: boolean, page: number }} params
 *   `firmaAnterior`/`firmaActual` son un identificador de los filtros+mes ACTUALES (sin la
 *   página) — típicamente `JSON.stringify([...])` de esos valores. Si difieren, cambió
 *   algo que no sea la página.
 * @returns {"reiniciar-pagina"|"pedir-datos"}
 */
export function decidirAccionCargaCaja({ firmaAnterior, firmaActual, esPrimeraCorrida, page }) {
    const cambiaronFiltros = !esPrimeraCorrida && firmaAnterior !== firmaActual;

    if (cambiaronFiltros && page !== 1) {
        return "reiniciar-pagina";
    }

    return "pedir-datos";
}
