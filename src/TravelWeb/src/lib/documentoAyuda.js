/**
 * Texto de ayuda del casillero "número de documento", según el tipo elegido
 * (mini-tanda firmada 2026-07-31, obra "cada campo acepta solo lo que va en ese campo").
 *
 * POR QUÉ EXISTE: el casillero mostraba siempre la misma ayuda ("DNI o CUIT" en el
 * formulario de pasajeros), así que el vendedor no tenía forma de saber qué espera el
 * sistema para el tipo que acaba de elegir. El motor SÍ es exigente con el DNI
 * (`DocumentNumberValidator`: 7 u 8 números, sin puntos) y flojo con el resto, y la
 * pantalla tiene que contar esa misma regla ANTES de que el vendedor escriba, no después
 * con un error.
 *
 * Es solo texto de ayuda: no valida ni bloquea nada. La validación de verdad vive en el
 * motor (nunca se confía en la pantalla).
 */

// Texto por tipo. Los tipos que el motor NO valida con formato estricto comparten la
// ayuda genérica, para no prometer un formato que después no se exige.
const AYUDA_POR_TIPO = {
    DNI: "7 u 8 números, sin puntos",
    Pasaporte: "Como figura en el pasaporte",
    // CUIT y CUIL solo existen en la ficha de cliente (el pasajero no factura). Se muestra
    // un ejemplo con guiones porque es como se lee en la constancia de AFIP.
    CUIT: "20-30111222-0",
    CUIL: "20-30111222-0",
};

const AYUDA_GENERICA = "Número de documento";

/**
 * Devuelve la ayuda que corresponde al tipo de documento elegido.
 *
 * @param {string|null|undefined} tipoDocumento - "DNI" | "Pasaporte" | "Cedula" | "Otro" | "CUIT" | "CUIL"
 * @returns {string} texto listo para usar como placeholder del casillero.
 */
export function ayudaNumeroDocumento(tipoDocumento) {
    return AYUDA_POR_TIPO[tipoDocumento] || AYUDA_GENERICA;
}
