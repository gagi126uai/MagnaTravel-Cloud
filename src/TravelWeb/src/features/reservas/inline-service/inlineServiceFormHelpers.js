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

// ─── D13 (spec FIRMADA 2026-08-10): precarga-hack de la frase completa ───────────

/**
 * Cuando el vendedor tira la frase entera en el buscador ("llao llao del 10/02 al
 * 15/02 con delfos") y elige un producto, lo que el motor entendió de ESA frase
 * (operador, fechas) se agrega como sugerencia amarilla en el formulario destino —
 * ADEMÁS de lo que ya trae la venta real del producto elegido (`sale`, en
 * `handleSelectExisting` de cada form).
 *
 * Fix C-5(a) (review 2026-08-10, P-21 "nunca pisa lo escrito a mano"): la versión
 * anterior solo miraba si `sale` ya traía un operador — pero un campo con valor
 * ESCRITO A MANO por el vendedor (antes de elegir este producto) tampoco se puede
 * pisar, aunque `sale` no diga nada al respecto. Por eso ahora la función recibe
 * `formActual` (el form tal cual está ANTES de aplicar esta selección) y solo escribe
 * en un campo si está REALMENTE vacío ahí — operador y fechas por igual.
 *
 * Las fechas nunca chocan con `sale` (lastSale/rateFallback no trae fechas), pero SÍ
 * pueden chocar con una fecha que el vendedor ya haya tipeado a mano en esa solapa —
 * de ahí que `formActual` también se consulte para las dos.
 *
 * @param {{supplier:{supplierPublicId:string,name:string}|null, dates:{from:string,to:string|null}|null}|null} interpretacion
 *   lo que devolvió `extraerInterpretacionParaPrecarga` (productDedupMatchLogic.js), o null
 * @param {{yaHaySupplierDeLaVenta:boolean, camposFecha:string[], formActual:Record<string,any>}} opciones
 *   `yaHaySupplierDeLaVenta` = la venta real (`sale`) ya trae operador (esa SIEMPRE gana).
 *   `camposFecha` son los nombres de campo del form para [desde] o [desde, hasta] — por
 *   ejemplo `["checkIn","checkOut"]` en Hotel, o solo `["pickupDate"]` en Traslado (un
 *   único campo de fecha, sin rango). `formActual` es el form ANTES de esta selección.
 * @returns {{patch: Record<string, any>, sugeridos: Record<string, boolean>}}
 *   `patch` se mezcla (spread) en el `setForm`; `sugeridos` se mezcla en `camposSugeridos`
 *   para que esos campos se pinten de amarillo con el mismo estilo de siempre
 */
export function aplicarInterpretacionComoSugerencia(interpretacion, { yaHaySupplierDeLaVenta, camposFecha, formActual }) {
    const patch = {};
    const sugeridos = {};
    if (!interpretacion) return { patch, sugeridos };

    const actual = formActual || {};

    // El operador de la frase solo se escribe si NI la venta real NI el vendedor (a
    // mano, antes de esta selección) ya tenían uno puesto.
    const supplierLibre = !yaHaySupplierDeLaVenta && !actual.supplierId;
    if (supplierLibre && interpretacion.supplier?.supplierPublicId) {
        patch.supplierId = interpretacion.supplier.supplierPublicId;
        patch.supplierName = interpretacion.supplier.name || null;
        sugeridos.supplierId = true;
    }

    // Las fechas del motor vienen como datetime ISO ("2026-02-10T00:00:00Z"); los campos
    // <input type="date"> del form solo entienden la parte de fecha (YYYY-MM-DD) — mismo
    // recorte que ya usan buildXFormInitial() en ServiceInlineCard.jsx para servicios editados.
    const [campoDesde, campoHasta] = camposFecha || [];
    if (campoDesde && !actual[campoDesde] && interpretacion.dates?.from) {
        patch[campoDesde] = String(interpretacion.dates.from).split("T")[0];
        sugeridos[campoDesde] = true;
    }
    if (campoHasta && !actual[campoHasta] && interpretacion.dates?.to) {
        patch[campoHasta] = String(interpretacion.dates.to).split("T")[0];
        sugeridos[campoHasta] = true;
    }

    return { patch, sugeridos };
}

// ─── C-5(b) (review 2026-08-10): el salto de solapa no puede pisar lo tipeado ─────

/**
 * D5/D10: cada solapa guarda lo suyo por separado — el estado de los 5 formularios
 * vive LEVANTADO en `ServiceInlineCard`, así que un formulario que se remonta al saltar
 * de solapa (D3) puede traer, en su `form`, datos que el vendedor ya había tipeado A
 * MANO ahí ANTES de haberse ido a buscar en otra solapa (ej: ya había elegido un
 * Operador en Hotel, se fue a Aéreo, buscó algo que resultó ser un hotel, y la ficha
 * saltó de vuelta a Hotel).
 *
 * En el camino NORMAL (buscar y elegir un producto DENTRO de la misma solapa), la venta
 * real del producto elegido SIEMPRE manda sobre lo que hubiera antes — comportamiento
 * de toda la vida, sin cambios. Pero en el camino de "selección PENDIENTE" (llegó por
 * un salto de solapa), pisar esos campos sería tirar a la basura algo que el vendedor
 * ya había cargado — por eso `esSeleccionPendiente` hace que cada campo se escriba
 * SOLO si está vacío en el form actual.
 *
 * @param {object} params
 * @param {object} params.sale — `lastSale`/`rateFallback` del resultado elegido
 * @param {boolean} params.canSeeCost — permiso `cobranzas.see_cost`
 * @param {Record<string,any>} params.formActual — el form ANTES de esta selección
 * @param {boolean} params.esSeleccionPendiente — true si esto vino de un salto de solapa
 * @param {string} params.campoVenta — nombre del campo de precio de venta del form (ej: "unitSalePrice"/"salePrice")
 * @param {string} params.campoCosto — nombre del campo de costo del form (ej: "unitNetCost"/"netCost")
 * @returns {{patch: Record<string, any>, sugeridos: Record<string, boolean>}}
 */
export function resolverPatchDeVentaDelCatalogo({ sale, canSeeCost, formActual, esSeleccionPendiente, campoVenta, campoCosto }) {
    const actual = formActual || {};
    const patch = {};
    const sugeridos = {};

    // "Libre para escribir" = camino normal (siempre gana la venta) O el campo está
    // vacío en el form actual (nadie lo había tocado a mano todavía).
    const libre = (campo) => !esSeleccionPendiente || !actual[campo];

    if (libre("supplierId")) {
        patch.supplierId = sale?.supplierPublicId || "";
        patch.supplierName = sale?.supplierName || null;
        sugeridos.supplierId = Boolean(sale?.supplierPublicId);
    }

    // Fix "moneda fantasma" (review 2026-08-10): la moneda es la UNIDAD del precio, no
    // un campo independiente — `libre("currency")` por sí solo está roto porque los 5
    // buildXFormInitial() arrancan `currency: "ARS"` (nunca vacía, a diferencia del
    // precio que arranca ""), así que en el camino de selección pendiente esa moneda
    // "de fábrica" SIEMPRE parecía "ya tipeada a mano" y nunca se actualizaba — quedaba
    // pisada por ejemplo con una venta en US$ mostrada como si fuera en ARS. La regla
    // correcta: la moneda viaja PEGADA al precio de venta. Si el precio se escribe
    // (`campoVenta` estaba libre), su moneda se escribe también, en amarillo igual que
    // él. Si el precio NO se escribe (el vendedor ya había tipeado uno a mano), la
    // moneda tampoco se toca — sería cambiarle la unidad a un número que no es el de
    // esta venta.
    const ventaLibre = libre(campoVenta);
    if (ventaLibre) {
        patch[campoVenta] = sale?.salePrice != null ? String(sale.salePrice) : "";
        sugeridos[campoVenta] = Boolean(sale?.salePrice);
        patch.currency = sale?.currency || "ARS";
        sugeridos.currency = Boolean(sale?.currency);
    }

    // El costo solo se toca si el vendedor tiene permiso de verlo (F-14): sin permiso,
    // el campo ni siquiera está a la vista, así que no corresponde tocarlo.
    if (canSeeCost && libre(campoCosto)) {
        patch[campoCosto] = sale?.netCost != null ? String(sale.netCost) : actual[campoCosto];
        sugeridos[campoCosto] = sale?.netCost != null;
    }

    return { patch, sugeridos };
}

/**
 * D13: "el nombre del producto que queda en el casillero es el nombre limpio del
 * producto elegido, nunca la frase entera". Cuando el buscador está mostrando la frase
 * completa que tipeó el vendedor y este elige una fila del catálogo, el casillero pasa
 * a mostrar el nombre lindo de ESE resultado — no lo que se había tipeado.
 *
 * @param {{name?:string}|null} catalogResult — la fila elegida del dropdown
 * @param {string} textoActual — lo que había en el campo antes de elegir (fallback)
 */
export function resolverNombreEnCasillero(catalogResult, textoActual) {
    return catalogResult?.name || textoActual || "";
}
