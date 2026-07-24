/**
 * Helpers compartidos por los 5 formularios de servicio en línea (Hotel, Aéreo, Traslado,
 * Paquete, Asistencia) dentro de ServiceInlineCard. Hoy solo vive acá la lógica de
 * "Crear nuevo" (Bug #28, Tanda 4, 2026-07-24), pero es el lugar natural para juntar
 * lógica repetida entre los 5 forms si aparece más en el futuro.
 */

/**
 * Bug #28 (Tanda 4, 2026-07-24): antes, tocar "Crear nuevo" en el buscador de producto
 * borraba TODOS los campos relacionados (operador, costo, venta, moneda) sin mirar si el
 * usuario ya los había tipeado a mano. Si el vendedor completaba esos campos ANTES de
 * decidir el nombre del producto nuevo — o los editaba después de elegir uno del catálogo
 * y arrepentirse — ese trabajo se perdía en silencio al crear el producto nuevo.
 *
 * La solución usa `camposSugeridos` (el mismo estado que ya pinta de amarillo los campos
 * que vinieron de una sugerencia del catálogo, ver `handleSelectExisting` en cada form):
 * un campo SOLO se limpia si TODAVÍA está marcado como sugerido (sigue amarillo, es una
 * sugerencia vieja que ya no corresponde al producto nuevo). Si el usuario lo tocó a mano
 * en algún momento (`onChange` de ese campo ya puso `camposSugeridos[campo] = false`), su
 * valor se respeta tal cual está — nunca se pisa.
 *
 * @param {Record<string, any>} valoresActuales — valores actuales del form (antes de crear nuevo)
 * @param {Record<string, boolean>} camposSugeridos — mismas claves que valoresPorDefecto; true = todavía es sugerencia sin tocar
 * @param {Record<string, any>} valoresPorDefecto — valor a usar para los campos que SÍ hay que limpiar
 * @returns {Record<string, any>} objeto con TODAS las claves de valoresPorDefecto, listo para
 *          mezclar (spread) en el nuevo estado del form
 */
export function resolverCamposALimpiarAlCrearNuevo(valoresActuales, camposSugeridos, valoresPorDefecto) {
    const resultado = {};
    for (const campo of Object.keys(valoresPorDefecto)) {
        // Si no hay registro para ese campo en camposSugeridos, lo tratamos como "sigue
        // sugerido" (se limpia) — es la opción segura: nunca deja colgado un dato viejo
        // de un producto que ya no es el elegido.
        const sigueSiendoSugerido = camposSugeridos?.[campo] !== false;
        resultado[campo] = sigueSiendoSugerido ? valoresPorDefecto[campo] : valoresActuales?.[campo];
    }
    return resultado;
}
