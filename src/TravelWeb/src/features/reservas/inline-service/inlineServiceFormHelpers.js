/**
 * Helpers compartidos por los 5 formularios de servicio en línea (Hotel, Aéreo, Traslado,
 * Paquete, Asistencia) dentro de ServiceInlineCard. Hoy solo vive acá la lógica de
 * "Crear nuevo" (Bug #28, Tanda 4, 2026-07-24), pero es el lugar natural para juntar
 * lógica repetida entre los 5 forms si aparece más en el futuro.
 */

import { hoyArgentina } from "../../../lib/utils.js";

// ─── Fix #3 (auditoría de coherencia 2026-08-10, GRAVE) ───────────────────────────

/**
 * Al editar un servicio, ¿con qué producto del tarifario está vinculado? El DTO que
 * devuelve el backend lo expone como `ratePublicId` (ver HotelBookingDto.cs y
 * equivalentes) — nunca `rateId`. Leer el nombre equivocado (bug real, hasta este fix)
 * dejaba `form.rateId` SIEMPRE en `null` al editar, así que el PUT nunca mandaba el
 * producto vinculado — y el candado "anti-clobber" del backend revertía en SILENCIO
 * cualquier cambio de RoomType/MealPlan/Operador/Nombre/Ciudad que el vendedor hiciera
 * (el motor interpretaba "no tocaron el producto" y pisaba con los valores viejos).
 *
 * `?? serviceToEdit.rateId` queda como red de contención por si algún día aparece un
 * DTO legacy con el nombre viejo — hoy nunca debería hacer falta.
 *
 * @param {{ratePublicId?:string, rateId?:string}|null} serviceToEdit
 * @returns {string|null}
 */
export function resolverRateIdDeEdicion(serviceToEdit) {
    return serviceToEdit?.ratePublicId ?? serviceToEdit?.rateId ?? null;
}

/**
 * Bug #28 (Tanda 4, 2026-07-24): antes, tocar "Crear nuevo" en el buscador de producto
 * borraba TODOS los campos relacionados (operador, costo, venta, moneda) sin mirar si el
 * usuario ya los había tipeado a mano. Si el vendedor completaba esos campos ANTES de
 * decidir el nombre del producto nuevo — o los editaba después de elegir uno del catálogo
 * y arrepentirse — ese trabajo se perdía en silencio al crear el producto nuevo.
 *
 * Fix regresión #1+#6 (re-review 2026-08-10): la primera versión de este fix usaba
 * `camposSugeridos` (el estado que pinta el AMARILLO) para decidir qué preservar — pero
 * `camposSugeridos` también se apaga con cualquier tecleo en el buscador (fix #6, "el
 * amarillo ya no corresponde a nada"), así que dejó de servir como señal confiable de
 * "esto lo tipeó el vendedor a mano". Repro real: elegir Hotel A, después escribir el
 * nombre de otro hotel buscando B (eso ya apaga TODOS los amarillos) y tocar "Crear
 * nuevo" — con `camposSugeridos` como señal, esto interpretaba "todo tocado a mano" y
 * preservaba la plata/operador de A en el producto nuevo, sin que el vendedor los
 * hubiera tipeado. Ahora usa `camposTocadosAMano` — un estado APARTE que se prende
 * SOLO en el `onChange` real de cada campo (nunca por tipear en el buscador) y sigue
 * protegido aunque el amarillo se haya apagado. Ver `ServiceInlineCard.jsx` (fix #4)
 * para dónde vive.
 *
 * @param {Record<string, any>} valoresActuales — valores actuales del form (antes de crear nuevo)
 * @param {Record<string, boolean>} camposTocadosAMano — mismas claves que valoresPorDefecto; true = el vendedor tipeó/eligió ESE campo a mano
 * @param {Record<string, any>} valoresPorDefecto — valor a usar para los campos que SÍ hay que limpiar
 * @returns {Record<string, any>} objeto con TODAS las claves de valoresPorDefecto, listo para
 *          mezclar (spread) en el nuevo estado del form
 */
export function resolverCamposALimpiarAlCrearNuevo(valoresActuales, camposTocadosAMano, valoresPorDefecto) {
    const resultado = {};
    for (const campo of Object.keys(valoresPorDefecto)) {
        // Solo se preserva si HAY un registro explícito de que el vendedor lo tocó a
        // mano — sin registro (undefined) se trata como "no tocado" (se limpia): opción
        // segura, nunca deja colgado un dato viejo de un producto que ya no es el elegido.
        const estaTocadoAMano = camposTocadosAMano?.[campo] === true;
        resultado[campo] = estaTocadoAMano ? valoresActuales?.[campo] : valoresPorDefecto[campo];
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
 * pisar, aunque `sale` no diga nada al respecto.
 *
 * Fix regresión #1+#6 (re-review 2026-08-10): "vacío" ya no alcanza como señal — un
 * campo puede tener VALOR (de una sugerencia anterior, ej. eligió Hotel A y ahora elige
 * Hotel B) sin que el vendedor lo haya tocado a mano; ESE sí tiene que poder
 * reemplazarse. La señal correcta es `camposTocadosAMano` (ver
 * `resolverCamposALimpiarAlCrearNuevo` arriba, mismo criterio): libre = vacío O no
 * tocado a mano.
 *
 * Las fechas nunca chocan con `sale` (lastSale/rateFallback no trae fechas), pero SÍ
 * pueden chocar con una fecha que el vendedor ya haya tipeado a mano en esa solapa —
 * de ahí que `formActual`/`camposTocadosAMano` también se consulten para las dos.
 *
 * @param {{supplier:{supplierPublicId:string,name:string}|null, dates:{from:string,to:string|null}|null}|null} interpretacion
 *   lo que devolvió `extraerInterpretacionParaPrecarga` (productDedupMatchLogic.js), o null
 * @param {{yaHaySupplierDeLaVenta:boolean, camposFecha:string[], formActual:Record<string,any>, camposTocadosAMano:Record<string,boolean>, hoy?:string}} opciones
 *   `yaHaySupplierDeLaVenta` = la venta real (`sale`) ya trae operador (esa SIEMPRE gana).
 *   `camposFecha` son los nombres de campo del form para [desde] o [desde, hasta] — por
 *   ejemplo `["checkIn","checkOut"]` en Hotel, o solo `["pickupDate"]` en Traslado (un
 *   único campo de fecha, sin rango). `formActual` es el form ANTES de esta selección.
 *   `camposTocadosAMano` dice cuáles de esos valores actuales son del vendedor (no se
 *   pisan) y cuáles vinieron de una selección anterior (reemplazables). `hoy` (Bug 4,
 *   QA 11/08/2026) es "YYYY-MM-DD" y solo existe para que los tests puedan fijar una
 *   fecha de referencia reproducible — en producción SIEMPRE se usa el día real
 *   (default `hoyArgentina()`), nunca se pasa a mano desde los 5 formularios.
 * @returns {{patch: Record<string, any>, sugeridos: Record<string, boolean>}}
 *   `patch` se mezcla (spread) en el `setForm`; `sugeridos` se mezcla en `camposSugeridos`
 *   para que esos campos se pinten de amarillo con el mismo estilo de siempre
 */
export function aplicarInterpretacionComoSugerencia(interpretacion, { yaHaySupplierDeLaVenta, camposFecha, formActual, camposTocadosAMano, hoy = hoyArgentina() }) {
    const patch = {};
    const sugeridos = {};
    if (!interpretacion) return { patch, sugeridos };

    const actual = formActual || {};
    const tocados = camposTocadosAMano || {};
    // Libre = vacío O no tocado a mano (mismo criterio que resolverPatchDeVentaDelCatalogo).
    const libre = (campo) => !actual[campo] || tocados[campo] !== true;

    // El operador de la frase solo se escribe si NI la venta real NI el vendedor (a
    // mano) ya tenían uno puesto.
    const supplierLibre = !yaHaySupplierDeLaVenta && libre("supplierId");
    if (supplierLibre && interpretacion.supplier?.supplierPublicId) {
        patch.supplierId = interpretacion.supplier.supplierPublicId;
        patch.supplierName = interpretacion.supplier.name || null;
        sugeridos.supplierId = true;
    }

    // Las fechas del motor vienen como datetime ISO ("2026-02-10T00:00:00Z"); los campos
    // <input type="date"> del form solo entienden la parte de fecha (YYYY-MM-DD) — mismo
    // recorte que ya usan buildXFormInitial() en ServiceInlineCard.jsx para servicios editados.
    //
    // Bug 4 (QA 11/08/2026): el motor arma esta interpretación SIN certeza del año ("del
    // 05/03 al 12/03" puede ser de este año o del que viene) y a veces erró para el lado
    // del pasado — precargaba un viaje que ya sucedió. El año de una fecha SUGERIDA no
    // puede quedar a criterio del modelo: lo corregimos acá ANTES de pintarla en amarillo
    // (clampearFechasSugeridasAlFuturo, más abajo en este archivo) — PERO solo cuando el
    // año realmente es una adivinanza del motor (`interpretacion.anioAmbiguo`, ver
    // productDedupMatchLogic.js). Fix dominio (review 11/08/2026): si el vendedor escribió
    // el año explícito en la frase ("del 05/03/2025 al 12/03/2025", carga retroactiva de
    // un viaje que YA VIAJÓ — situación legítima), el sistema NO puede "corregirla" al
    // futuro sin pisar lo que el vendedor quiso decir a propósito.
    const [campoDesde, campoHasta] = camposFecha || [];
    const fechaDesdeCruda = interpretacion.dates?.from ? String(interpretacion.dates.from).split("T")[0] : null;
    const fechaHastaCruda = interpretacion.dates?.to ? String(interpretacion.dates.to).split("T")[0] : null;
    const { from: fechaDesdeAjustada, to: fechaHastaAjustada } = interpretacion.anioAmbiguo === true
        ? clampearFechasSugeridasAlFuturo({ from: fechaDesdeCruda, to: fechaHastaCruda, hoy })
        : { from: fechaDesdeCruda, to: fechaHastaCruda };
    if (campoDesde && libre(campoDesde) && fechaDesdeAjustada) {
        patch[campoDesde] = fechaDesdeAjustada;
        sugeridos[campoDesde] = true;
    }
    if (campoHasta && libre(campoHasta) && fechaHastaAjustada) {
        patch[campoHasta] = fechaHastaAjustada;
        sugeridos[campoHasta] = true;
    }

    return { patch, sugeridos };
}

// ─── Bug 4 (QA 11/08/2026): la frase no puede sugerir un viaje en el pasado ──────

/**
 * Le suma años a una fecha "YYYY-MM-DD" hasta que deje de ser anterior al mínimo
 * pedido. Se usa para que la precarga de la frase (D13) nunca sugiera una fecha ya
 * pasada: si el motor interpretó "05/03" sin decir el año y hoy ya es agosto, la
 * fecha ingenua (05/03 de ESTE año) quedó atrás — le sumamos años hasta que caiga en
 * el futuro (o en hoy mismo).
 *
 * @param {string} fechaISO — "YYYY-MM-DD"
 * @param {Date} minimo — fecha (hora 00:00) que el resultado no puede ser anterior a
 * @returns {string} la fecha ajustada, mismo formato "YYYY-MM-DD"
 */
function sumarAniosHastaNoQuedarEnElPasado(fechaISO, minimo) {
    const fecha = new Date(`${fechaISO}T00:00:00`);
    if (Number.isNaN(fecha.getTime())) return fechaISO; // fecha rara: mejor no tocarla

    // Tope de seguridad: nunca debería hacer falta más de un par de vueltas, pero
    // evita un loop infinito si algún día llega una fecha corrupta.
    let vueltas = 0;
    while (fecha < minimo && vueltas < 100) {
        fecha.setFullYear(fecha.getFullYear() + 1);
        vueltas += 1;
    }

    // Fix I1 (review): NUNCA fecha.toISOString() acá — ese método siempre convierte a
    // UTC, y `fecha` es un Date construido en hora LOCAL (mismo patrón anti-bug que ya
    // documenta hoyArgentina() en lib/utils.js). En una máquina con offset UTC positivo
    // (ej. corriendo un test en un CI en otro huso), la medianoche local cae en el DÍA
    // ANTERIOR en UTC — cada fecha ajustada salía un día antes. Armamos el string a
    // mano con los getters LOCALES (getFullYear/getMonth/getDate), que no pasan por UTC.
    const anio = fecha.getFullYear();
    const mes = String(fecha.getMonth() + 1).padStart(2, "0");
    const dia = String(fecha.getDate()).padStart(2, "0");
    return `${anio}-${mes}-${dia}`;
}

/**
 * Bug 4 (QA 11/08/2026, decisión del dueño): "aep igr latam del 05/03 al 12/03"
 * precargó una fecha de ida que YA HABÍA PASADO (el motor no tiene forma de estar
 * seguro de qué año quiso decir el vendedor). El año de una fecha SUGERIDA no puede
 * quedar a criterio del modelo — acá se lo corrige, siempre, de forma determinística:
 *
 *   1. Si la fecha de INICIO sugerida quedó en el pasado, se le suman años hasta que
 *      sea hoy o futura.
 *   2. La fecha de FIN nunca puede quedar antes que el inicio YA corregido — si el
 *      ajuste del punto 1 hace que el rango "cruce de año" (ej. sugerido 28/12 al
 *      03/01), el fin también se corrige para seguir siendo posterior al inicio.
 *
 * Regla P-21 (el sistema sugiere, nunca decide) sigue vigente: esto NO reemplaza el
 * amarillo editable, solo evita que la sugerencia amarilla sea, de entrada, un
 * absurdo (un viaje que ya sucedió). Nunca toca una fecha tipeada A MANO por el
 * vendedor — eso ya lo filtra `libre(campo)` en `aplicarInterpretacionComoSugerencia`
 * antes de llegar acá; esta función solo conoce fechas SUGERIDAS.
 *
 * @param {{from:string|null, to:string|null, hoy:string}} params — `from`/`to` en
 *   "YYYY-MM-DD" (o null si el motor no sugirió esa punta), `hoy` = hoyArgentina()
 * @returns {{from:string|null, to:string|null}}
 */
export function clampearFechasSugeridasAlFuturo({ from, to, hoy }) {
    const minimoDeHoy = new Date(`${hoy}T00:00:00`);
    const fromAjustado = from ? sumarAniosHastaNoQuedarEnElPasado(from, minimoDeHoy) : from;

    // El fin no puede quedar antes que el inicio YA corregido — si no hay inicio
    // sugerido en este mismo llamado, lo comparamos contra hoy nomás.
    const minimoParaElFin = fromAjustado ? new Date(`${fromAjustado}T00:00:00`) : minimoDeHoy;
    const toAjustado = to ? sumarAniosHastaNoQuedarEnElPasado(to, minimoParaElFin) : to;

    return { from: fromAjustado, to: toAjustado };
}

// ─── Regla transversal (auditoría de coherencia 2026-08-10, #1/#2/#7) ─────────────

/**
 * "Lo que el vendedor escribió a mano NUNCA se pisa ni se borra; lo que vino de una
 * selección se reemplaza con la selección siguiente." Antes esta guarda solo regía en
 * el camino de "selección pendiente" (salto de solapa, D3) — un bug real que el dueño
 * reportó: elegir un producto DENTRO de la misma solapa pisaba un operador que el
 * vendedor acababa de elegir a mano en el select de Operador.
 *
 * Fix REGRESIÓN #1+#6 (re-review 2026-08-10): la primera versión de este fix usaba
 * `camposSugeridos` (el estado del AMARILLO) para decidir "reemplazable o no" — pero
 * `camposSugeridos` cumple OTRO trabajo, apagarse con cualquier tecleo en el buscador
 * (fix #6), así que dejó de ser una señal confiable de "esto es del vendedor". Repro
 * real: elegir Hotel A (precio/operador quedan sugeridos, amarillo) → escribir el
 * nombre de Hotel B en el buscador (eso apaga TODOS los amarillos, fix #6, sin que el
 * vendedor haya tocado ningún campo de precio/operador) → elegir Hotel B → con
 * `camposSugeridos` ya en `false` en todos lados, el sistema interpretaba "todo es del
 * vendedor" y NO reemplazaba nada — Hotel B se guardaba con la plata de A, sin amarillo.
 *
 * Los dos significados están SEPARADOS ahora en dos estados distintos (viven en
 * `ServiceInlineCard.jsx`, fix #4):
 *   - `camposSugeridos`: SOLO pinta amarillo. Se apaga con cualquier tecleo (fix #6).
 *   - `camposTocadosAMano`: se prende ÚNICAMENTE en el `onChange` real de CADA campo
 *     (el vendedor tocó ESE campo puntual) — nunca por tipear en el buscador. Solo se
 *     resetea entero cuando cambia el CONTEXTO (salto de solapa que limpia el origen,
 *     `limpiarBusquedaDelFormOrigen` — fix #11), nunca por elegir/crear un producto
 *     (ahí, al contrario, lo tocado a mano se PROTEGE).
 *
 * `libre(campo) = vacío O NO tocado a mano` — un campo con valor que vino de una
 * selección ANTERIOR (nunca tocado a mano por el vendedor) es reemplazable por la
 * selección nueva; eso es lo que hace que Hotel B reemplace la plata de Hotel A.
 *
 * Fix #2 (bug reportado por el dueño): si la venta elegida NO trae operador (producto
 * sin ventas registradas todavía), NUNCA se escribe `supplierId`/`supplierName` — ni
 * siquiera para "limpiarlo": antes esto borraba en silencio un operador que el vendedor
 * ya había elegido.
 *
 * Fix #7 (moneda/precio fantasma + borde de la re-review): el precio de venta SOLO se
 * escribe si la venta trae un valor REAL Y una moneda real — ni `0` ni vacío cuentan (un
 * `rateFallback` sin precio curado no tiene nada que sugerir, y un precio sin moneda no
 * es plata utilizable): nunca se pinta un número sin unidad ni "$0" sin amarillo. La
 * moneda viaja PEGADA al precio — solo se toca cuando el precio TAMBIÉN se escribe.
 *
 * @param {object} params
 * @param {object} params.sale — `lastSale`/`rateFallback` del resultado elegido
 * @param {boolean} params.canSeeCost — permiso `cobranzas.see_cost`
 * @param {Record<string,any>} params.formActual — el form ANTES de esta selección
 * @param {Record<string,boolean>} params.camposTocadosAMano — ANTES de esta selección
 *   (mismas claves que el form) — dice cuáles de los valores actuales son del vendedor
 *   (protegidos) y cuáles vinieron de una selección anterior (reemplazables)
 * @param {string} params.campoVenta — nombre del campo de precio de venta del form (ej: "unitSalePrice"/"salePrice")
 * @param {string} params.campoCosto — nombre del campo de costo del form (ej: "unitNetCost"/"netCost")
 * @returns {{patch: Record<string, any>, sugeridos: Record<string, boolean>}}
 */
export function resolverPatchDeVentaDelCatalogo({ sale, canSeeCost, formActual, camposTocadosAMano, campoVenta, campoCosto }) {
    const actual = formActual || {};
    const tocados = camposTocadosAMano || {};
    const patch = {};
    const sugeridos = {};

    // Libre para escribir = el campo está vacío, O tiene un valor pero NO fue tocado a
    // mano por el vendedor (vino de una selección anterior, es reemplazable).
    const libre = (campo) => !actual[campo] || tocados[campo] !== true;

    if (libre("supplierId") && sale?.supplierPublicId) {
        patch.supplierId = sale.supplierPublicId;
        patch.supplierName = sale.supplierName || null;
        sugeridos.supplierId = true;
    }

    // Borde de la re-review: precio Y moneda tienen que venir los DOS para que
    // corresponda escribir algo — un precio sin moneda no es plata utilizable.
    if (libre(campoVenta) && sale?.salePrice && sale?.currency) {
        patch[campoVenta] = String(sale.salePrice);
        sugeridos[campoVenta] = true;
        patch.currency = sale.currency;
        sugeridos.currency = true;
    }

    // El costo solo se toca si el vendedor tiene permiso de verlo (F-14): sin permiso,
    // el campo ni siquiera está a la vista, así que no corresponde tocarlo.
    if (canSeeCost && libre(campoCosto) && sale?.netCost) {
        patch[campoCosto] = String(sale.netCost);
        sugeridos[campoCosto] = true;
    }

    return { patch, sugeridos };
}

// ─── Fix residual ítem B (re-review 2026-08-10) ───────────────────────────────────

/**
 * Al saltar de solapa (D3), `limpiarBusquedaDelFormOrigen` (ServiceInlineCard.jsx) usa
 * `resolverCamposALimpiarAlCrearNuevo` para que los campos tocados a mano CONSERVEN su
 * valor y los demás vuelvan al default — hasta acá bien. El bug estaba en el paso
 * siguiente: la bandera `camposTocadosAMano` (la que dice "esto es protegido") se
 * apagaba ENTERA, para TODOS los campos, aunque el valor tipeado a mano siguiera vivo
 * en el form. Resultado: el vendedor tipeaba un precio a mano, saltaba de solapa,
 * volvía, elegía un producto del buscador — y ese precio (que seguía ahí, a la vista)
 * quedaba "libre" para la nueva selección y se pisaba en silencio. Mismo bug de "plata
 * equivocada" que el fix #1+#6, pero por la puerta del salto-de-solapa en vez del
 * tecleo en el buscador.
 *
 * Fix: solo apagar la bandera de los campos que EFECTIVAMENTE volvieron al default (el
 * vendedor nunca los tocó) — los tocados a mano quedan con su bandera en `true`, tal
 * cual estaban, protegidos para la próxima selección.
 *
 * @param {Record<string, boolean>} camposTocadosAMano — mapa ANTES de limpiar el origen
 * @param {Record<string, any>} camposLimpios — el resultado de `resolverCamposALimpiarAlCrearNuevo`
 *   para ESTE mismo tipo de servicio (mismas claves) — solo se usa para saber QUÉ claves
 *   evaluar, no sus valores
 * @returns {Record<string, boolean>} el nuevo mapa, listo para reemplazar el estado completo
 */
export function resolverTocadosAManoTrasLimpiarOrigen(camposTocadosAMano, camposLimpios) {
    const resultado = { ...camposTocadosAMano };
    for (const campo of Object.keys(camposLimpios || {})) {
        if (camposTocadosAMano?.[campo] !== true) {
            resultado[campo] = false;
        }
    }
    return resultado;
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

// ─── D13-bis (spec FIRMADA 2026-08-10): "crear nuevo" tampoco queda pelado ────────

/**
 * Fix "crear nuevo pelado" (D13-bis): el vendedor puede tirar la frase completa
 * ("llao llao del 10/02 al 15/02 con delfos") y terminar en "crear nuevo" porque el
 * producto todavía no existe en el tarifario — antes de este fix, el nombre limpio se
 * usaba para crear el producto pero el operador y las fechas de la frase se tiraban.
 *
 * El operador del recuadro "producto nuevo" (violeta) arranca SIEMPRE vacío en el
 * instante en que se abre — si la interpretación trae un operador REAL (matcheado por
 * el motor entre los proveedores de la agencia, nunca inventado), se usa como
 * sugerencia inicial ahí. Si el motor no matcheó ningún operador, no se inventa nada:
 * el campo queda vacío como siempre y el vendedor lo elige a mano.
 *
 * Las FECHAS de la frase no pasan por acá — esas reusan `aplicarInterpretacionComoSugerencia`
 * (mismas guardas de "nunca pisa lo tipeado a mano" que ya usa `handleSelectExisting`),
 * porque van a un campo del form normal (siempre visible), no al recuadro del producto
 * nuevo.
 *
 * @param {{supplier:{supplierPublicId:string}|null}|null} interpretacion
 * @returns {string} el supplierPublicId sugerido, o "" si no hay ninguno para sugerir
 */
export function resolverOperadorSugeridoParaProductoNuevo(interpretacion) {
    return interpretacion?.supplier?.supplierPublicId || "";
}

// ─── Bug 2 (QA 11/08/2026): "habitaciones"/"pasajeros" aceptaban -1 ──────────────

/**
 * Filtra lo que el vendedor tipeó en un campo de CANTIDAD (habitaciones, pasajeros) —
 * son cosas que se CUENTAN, nunca plata ni algo negativo. El min={1} de un
 * <input type="number"> nativo es solo decorativo (el navegador igual deja escribir
 * "-1", como reportó QA); acá sacamos cualquier caracter que no sea un dígito, así el
 * campo nunca puede terminar en negativo, con coma ni con letras. Dejamos pasar el
 * string vacío para que el vendedor pueda borrar todo y volver a escribir.
 *
 * Esto es SOLO el reflejo en pantalla (T-3): el mínimo real de "al menos 1" lo exige
 * validarForm() en ServiceInlineCard.jsx antes de guardar (con el mensaje que ve el
 * vendedor), y buildPayload() lo vuelve a garantizar como red final antes de mandar
 * al backend — el backend sigue siendo quien manda la última palabra.
 *
 * @param {string} textoTipeado
 * @returns {string} el mismo texto, sin nada que no sea un dígito
 */
export function sanitizarCantidadPositiva(textoTipeado) {
    const texto = textoTipeado || "";
    // Fix I2 (review): antes sacábamos CUALQUIER separador y pegábamos los dígitos de
    // los dos lados ("1,5" → "15") — bug real: un vendedor que se equivoca y tipea
    // "1,5 pasajeros" terminaba mandando 15 pasajeros, x10 el valor real. Una cantidad
    // no tiene decimales, así que ante una coma o un punto nos quedamos con la parte
    // ENTERA de ADELANTE nomás ("1,5" → "1"), y de ahí sacamos cualquier cosa que no
    // sea dígito (signo "-", espacios sueltos, etc.).
    const parteEntera = texto.split(/[.,]/)[0];
    return parteEntera.replace(/[^\d]/g, "");
}
