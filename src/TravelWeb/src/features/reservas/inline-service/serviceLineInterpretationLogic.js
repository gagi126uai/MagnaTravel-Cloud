/**
 * Lógica pura de "la línea inteligente" (spec firmada 2026-08-07, §3 y §4 / M-20..M-23).
 *
 * Acá vive TODO lo que se puede decidir sin tocar el DOM ni React, para que se pueda
 * testear sin levantar un componente:
 *   1. Cuándo corresponde disparar la interpretación (3+ palabras).
 *   2. Cómo mapear la respuesta del motor (ServiceLineInterpretationDto) a los campos de
 *      CADA tipo de servicio — cada uno usa nombres de campo distintos en su form.
 *   3. Cómo respetar V10=A ("lo que el vendedor tocó con la mano no se pisa nunca"): un
 *      campo que ya está en el set `camposTocados` queda afuera de cualquier parche.
 *   4. Qué hacer con la duda grande (Sí/No, §4): qué campo vaciar si la respuesta es "No".
 *
 * Los 5 *InlineForm.jsx (Hotel/Aéreo/Traslado/Paquete/Asistencia) consumen estas
 * funciones desde `useServiceLineInterpretationForForm` — ver ese hook para el pegamento
 * con React (estado, efectos, la llamada de red).
 */

// ─── Cuándo disparar la interpretación ─────────────────────────────────────────

// El motor tiene un tope de 40 pedidos por minuto (rate limit "ai-line" en el backend):
// no tiene sentido gastar esa cuota con una sola palabra ("sheraton"), que igual la
// resuelve bien el buscador de catálogo de siempre sin ayuda de nadie.
const MIN_PALABRAS_PARA_INTERPRETAR = 3;

/**
 * @param {string} texto — lo que el vendedor escribió en el buscador de producto
 * @returns {boolean} true si hay palabras suficientes para justificar la consulta
 */
export function debeDispararInterpretacion(texto) {
  if (!texto) return false;
  const palabras = texto.trim().split(/\s+/).filter(Boolean);
  return palabras.length >= MIN_PALABRAS_PARA_INTERPRETAR;
}

// ─── Degradación total (§3.5 / M-23) ───────────────────────────────────────────

/**
 * Decide si una respuesta del motor sirve para precargar algo. Cualquier cosa que no
 * sea "entendí" (interpreted !== true) se trata exactamente igual que un error de red o
 * una IA caída: el buscador de siempre sigue andando, sin cartel ni texto distinto.
 *
 * @param {object|null} dto — la respuesta cruda de POST /linea-inteligente
 */
export function esRespuestaUtilizable(dto) {
  return Boolean(dto && dto.interpreted === true);
}

// ─── Fechas: el motor manda datetime ISO, los inputs date quieren solo el día ──────

export function soloFecha(fechaIso) {
  if (!fechaIso) return "";
  return String(fechaIso).split("T")[0];
}

// ─── Mapa de campos por tipo de servicio ───────────────────────────────────────

/**
 * Traduce el vocabulario del motor (producto/operador/variante/precio/fechas) al
 * vocabulario de CADA form. Cada *InlineForm.jsx usa nombres de campo propios (herencia
 * del modal viejo), así que este mapa es el único lugar que los conoce a todos juntos.
 *
 * `variantFields`: clave = nombre del campo en InterpretedVariantDto (backend),
 *                  valor = nombre del campo en el form de ese tipo.
 * Paquete y Asistencia no tienen variante natural (V2=todos, con esa única excepción
 * documentada en la spec) — quedan con `variantFields: {}`.
 */
export const SERVICE_LINE_FIELD_MAPS = {
  Hotel: {
    nameField: "hotelName",
    cityField: "city",
    costField: "unitNetCost",
    saleField: "unitSalePrice",
    currencyField: "currency",
    dateFromField: "checkIn",
    dateToField: "checkOut",
    variantFields: { roomType: "roomType", mealPlan: "mealPlan", roomCategory: "roomCategory" },
  },
  Aereo: {
    nameField: "routeName",
    cityField: null,
    costField: "netCost",
    saleField: "salePrice",
    currencyField: "currency",
    dateFromField: "departureDate",
    dateToField: "returnDate",
    variantFields: { cabinClass: "cabinClass" },
  },
  Traslado: {
    nameField: "routeName",
    cityField: null,
    costField: "netCost",
    saleField: "salePrice",
    currencyField: "currency",
    dateFromField: "pickupDate",
    dateToField: null,
    variantFields: { vehicleType: "vehicleType" },
  },
  Paquete: {
    nameField: "packageName",
    cityField: null,
    costField: "unitNetCost",
    saleField: "unitSalePrice",
    currencyField: "currency",
    dateFromField: "startDate",
    dateToField: "endDate",
    variantFields: {},
  },
  Asistencia: {
    nameField: "planName",
    cityField: null,
    costField: "unitNetCost",
    saleField: "unitSalePrice",
    currencyField: "currency",
    dateFromField: "validFrom",
    dateToField: "validTo",
    variantFields: {},
  },
};

// ─── Momento 3: precargar el PRODUCTO cuando el motor lo reconoció directo ─────

/**
 * Arma el patch de identidad del producto (nombre + ciudad + rateId) cuando el motor
 * devolvió un match directo (`dto.product`). Nunca pisa un producto que el vendedor ya
 * resolvió (a mano, del buscador, o de una interpretación anterior) — por eso recibe
 * `productoYaResuelto` en vez de mirarlo acá adentro.
 *
 * @returns {object|null} patch para mezclar en el form, o null si no corresponde aplicar nada
 */
export function construirPatchDeProducto({ dto, serviceType, productoYaResuelto }) {
  const mapa = SERVICE_LINE_FIELD_MAPS[serviceType];
  if (!mapa || !dto?.product || productoYaResuelto) return null;

  const patch = {
    [mapa.nameField]: dto.product.name,
    rateId: dto.product.ratePublicId,
    // Limpiamos por las dudas: un producto reconocido por el motor nunca es "nuevo".
    newCatalogProduct: null,
  };
  if (mapa.cityField && dto.product.subtitle) {
    patch[mapa.cityField] = dto.product.subtitle;
  }
  return patch;
}

// ─── El resto de la frase: operador, variante, precio, fechas ─────────────────

/**
 * Arma el patch de TODO lo que no es la identidad del producto — se aplica sin importar
 * si el producto ya quedó resuelto o no (§3.4: "el resto de la frase igual se
 * aprovecha" al elegir un parecido de la bandeja de creación).
 *
 * V10=A ("lo tocado nunca se pisa"): cualquier campo presente en `camposTocados` queda
 * afuera del patch, aunque el motor haya traído un valor para él.
 *
 * @param {{dto:object, serviceType:string, canSeeCost:boolean, camposTocados:Set<string>}} params
 * @returns {{patch:object, camposSugeridos:string[]}} — `camposSugeridos` son los
 *   nombres de campo que hay que pintar de amarillo (mismo estilo que el resto de la ficha)
 */
export function construirPatchDeResto({ dto, serviceType, canSeeCost, camposTocados }) {
  const mapa = SERVICE_LINE_FIELD_MAPS[serviceType];
  const tocados = camposTocados || new Set();
  const patch = {};
  const camposSugeridos = [];
  if (!mapa || !dto) return { patch, camposSugeridos };

  if (dto.supplier?.supplierPublicId && !tocados.has("supplierId")) {
    patch.supplierId = dto.supplier.supplierPublicId;
    // supplierName viaja aparte (mismo patrón "C5" que ya usan los 5 forms al elegir del
    // buscador): sirve para mostrar el operador sugerido aunque no esté en la lista de
    // proveedores de ESTA reserva en particular.
    patch.supplierName = dto.supplier.name || null;
    camposSugeridos.push("supplierId");
  }

  if (dto.variant) {
    for (const [claveDelMotor, campoDelForm] of Object.entries(mapa.variantFields)) {
      const valor = dto.variant[claveDelMotor];
      if (valor && campoDelForm && !tocados.has(campoDelForm)) {
        patch[campoDelForm] = valor;
        camposSugeridos.push(campoDelForm);
      }
    }
  }

  // El precio que interpreta la frase es SIEMPRE costo (ver InterpretedPriceDto en el
  // backend, F-14/M-27): sin permiso de ver costos el motor ya lo manda en null, así que
  // acá alcanza con revisar si vino algo — nunca hace falta volver a chequear el permiso.
  if (canSeeCost && dto.price?.amount != null && mapa.costField && !tocados.has(mapa.costField)) {
    patch[mapa.costField] = String(dto.price.amount);
    camposSugeridos.push(mapa.costField);
    if (dto.price.currency && mapa.currencyField && !tocados.has(mapa.currencyField)) {
      patch[mapa.currencyField] = dto.price.currency;
      camposSugeridos.push(mapa.currencyField);
    }
  }

  if (dto.dates) {
    if (dto.dates.from && mapa.dateFromField && !tocados.has(mapa.dateFromField)) {
      patch[mapa.dateFromField] = soloFecha(dto.dates.from);
      camposSugeridos.push(mapa.dateFromField);
    }
    if (dto.dates.to && mapa.dateToField && !tocados.has(mapa.dateToField)) {
      patch[mapa.dateToField] = soloFecha(dto.dates.to);
      camposSugeridos.push(mapa.dateToField);
    }
  }

  return { patch, camposSugeridos };
}

// ─── Momento 4: el producto no existe todavía (§3.4) ───────────────────────────

/**
 * Cuando el motor NO reconoció un producto exacto pero sí trae parecidos, hay que
 * ofrecerlos en el MISMO desplegable del buscador (Q1=A: un solo casillero), con
 * "crear {productSearchText}" al final en vez del texto crudo que escribió el vendedor.
 *
 * @returns {{candidates:object[], createText:string}|null}
 */
export function construirOverrideBuscador({ dto, productoYaResuelto }) {
  if (!dto || productoYaResuelto) return null;
  // Hubo match directo (Momento 3): no hace falta que el vendedor elija nada.
  if (dto.product) return null;
  if (!dto.productCandidates || dto.productCandidates.length === 0) return null;
  return {
    candidates: dto.productCandidates,
    createText: dto.productSearchText || "",
  };
}

// ─── La duda grande (§4 / M-22) ────────────────────────────────────────────────

// Mismos códigos que ServiceLineDoubtFields en el backend (viajan por CÓDIGO, T-13 —
// nunca se compara contra el texto de la pregunta).
export const DOUBT_FIELD = {
  PRICE: "precio",
  SUPPLIER: "operador",
  DATES: "fechas",
};

/**
 * Una duda solo tiene sentido si el campo al que apunta está a la vista en ESTE form Y
 * si todavía es territorio del sistema. Dos motivos para NO ofrecerla:
 *   1. El campo no existe en pantalla (ej. duda de precio sin permiso de ver costos).
 *   2. El campo YA lo tocó el vendedor con la mano (V10=A). Bug bloqueante (revisor
 *      funcional): si el vendedor escribió "50" de costo a mano y la frase decía "48", el
 *      motor puede devolver igual la duda "¿48 es el precio por noche?" — mostrarla y
 *      dejar que "No" borre el campo destruiría el 50 que el vendedor SÍ quiso poner.
 *      Un campo tocado ya no es una decisión del sistema: no hay nada que preguntarle.
 *
 * @param {{doubt:object, serviceType:string, canSeeCost:boolean, camposTocados?:Set<string>}} params
 */
export function puedeMostrarDuda({ doubt, serviceType, canSeeCost, camposTocados }) {
  if (!doubt) return false;
  const mapa = SERVICE_LINE_FIELD_MAPS[serviceType];
  if (!mapa) return false;
  const tocados = camposTocados || new Set();

  if (doubt.field === DOUBT_FIELD.PRICE) {
    return canSeeCost && Boolean(mapa.costField) && !tocados.has(mapa.costField);
  }
  if (doubt.field === DOUBT_FIELD.SUPPLIER) {
    return !tocados.has("supplierId");
  }
  if (doubt.field === DOUBT_FIELD.DATES) {
    // Si CUALQUIERA de las dos puntas ya la tocó el vendedor, no se pregunta: no hay
    // forma de saber si la duda del motor sigue aplicando a lo que quedó en pantalla.
    const fechaFromTocada = mapa.dateFromField && tocados.has(mapa.dateFromField);
    const fechaToTocada = mapa.dateToField && tocados.has(mapa.dateToField);
    return Boolean(mapa.dateFromField) && !fechaFromTocada && !fechaToTocada;
  }
  return false;
}

/**
 * Qué hacer cuando el vendedor contesta la duda. "Sí" no cambia nada (el amarillo queda
 * como está). "No" vacía el/los campo(s) que la duda señala y dice cuál hay que enfocar.
 *
 * La duda de fechas puede involucrar las dos puntas (entrada/salida): al no poder saber
 * cuál de las dos estaba mal, se vacían ambas y el cursor va a la primera — es más fácil
 * volver a escribir dos fechas que adivinar cuál de las dos "sí" quedó bien. Esto NO
 * cambia con este fix (queda anotado para Gastón, no es parte del bloqueante).
 *
 * V10=A, segunda capa de defensa: aunque `puedeMostrarDuda` ya debería haber evitado
 * ofrecer una duda sobre un campo tocado, acá se vuelve a filtrar `camposAVaciar` contra
 * `camposTocados` — si por cualquier motivo la UI llega a invocar esto con una duda
 * "vieja" sobre un campo que el vendedor tocó mientras tanto, jamás se borra.
 *
 * @returns {{camposAVaciar:string[], campoParaEnfocar:string|null}}
 */
export function resolverRespuestaDuda({ doubt, respuestaEsSi, serviceType, camposTocados }) {
  if (!doubt || respuestaEsSi) return { camposAVaciar: [], campoParaEnfocar: null };
  const mapa = SERVICE_LINE_FIELD_MAPS[serviceType];
  if (!mapa) return { camposAVaciar: [], campoParaEnfocar: null };
  const tocados = camposTocados || new Set();

  let camposCandidatos = [];
  if (doubt.field === DOUBT_FIELD.PRICE) {
    camposCandidatos = [mapa.costField];
  } else if (doubt.field === DOUBT_FIELD.SUPPLIER) {
    camposCandidatos = ["supplierId"];
  } else if (doubt.field === DOUBT_FIELD.DATES) {
    camposCandidatos = [mapa.dateFromField, mapa.dateToField];
  }

  // Filtro V10=A: nunca se vacía un campo que el vendedor ya tocó, aunque la duda lo señale.
  const camposAVaciar = camposCandidatos.filter((campo) => campo && !tocados.has(campo));
  const campoParaEnfocar = camposAVaciar[0] || null;
  return { camposAVaciar, campoParaEnfocar };
}

// ─── Momento 4: aprovechar el resto sin pisar lo ya sugerido/tocado (§3.4) ─────

/**
 * Al elegir un producto "parecido" del desplegable (Momento 4), la última venta de ESE
 * producto en el catálogo trae su propio operador/costo/venta/moneda — pero para
 * entonces el RESTO de la frase (operador, precio, fechas, variante) puede haber quedado
 * precargado por la línea inteligente desde ANTES de elegir nada (`construirPatchDeResto`
 * no depende de que el producto esté resuelto). Bug bloqueante (revisor funcional): el
 * catálogo no puede pisar lo que ya está sugerido por el motor ni lo que el vendedor tocó.
 *
 * Sale solo se aplica a operador/costo/venta/moneda — la identidad del producto (nombre,
 * ciudad, rateId) la sigue resolviendo el *InlineForm directamente, como siempre.
 *
 * @param {{
 *   serviceType: string,
 *   sale: {supplierPublicId?, supplierName?, netCost?, salePrice?, currency?},
 *   canSeeCost: boolean,
 *   camposActualmenteSugeridos: Record<string, boolean>,
 *   camposTocados: Set<string>,
 * }} params
 * @returns {{patch: object, camposSugeridos: Record<string, boolean>}} — `camposSugeridos`
 *   es un PARCIAL para hacer MERGE sobre el objeto vigente (nunca reemplazarlo entero:
 *   así no se apaga el amarillo de campos que esta función ni mira, como habitación/fechas).
 */
export function construirPatchDeSeleccionManual({ serviceType, sale, canSeeCost, camposActualmenteSugeridos, camposTocados }) {
  const mapa = SERVICE_LINE_FIELD_MAPS[serviceType];
  const tocados = camposTocados || new Set();
  const sugeridos = camposActualmenteSugeridos || {};
  const patch = {};
  const camposSugeridos = {};
  if (!mapa) return { patch, camposSugeridos };

  // Un campo se puede pisar con la venta del catálogo SOLO si no está protegido: ni
  // tocado por el vendedor, ni ya sugerido (amarillo) por la línea inteligente.
  const puedePisar = (campo) => Boolean(campo) && !tocados.has(campo) && !sugeridos[campo];

  const ventaCruda = sale || {};

  if (puedePisar("supplierId")) {
    patch.supplierId = ventaCruda.supplierPublicId || "";
    patch.supplierName = ventaCruda.supplierName || null;
    camposSugeridos.supplierId = Boolean(ventaCruda.supplierPublicId);
  }
  if (canSeeCost && puedePisar(mapa.costField)) {
    patch[mapa.costField] = ventaCruda.netCost != null ? String(ventaCruda.netCost) : "";
    camposSugeridos[mapa.costField] = ventaCruda.netCost != null;
  }
  if (puedePisar(mapa.saleField)) {
    patch[mapa.saleField] = ventaCruda.salePrice != null ? String(ventaCruda.salePrice) : "";
    camposSugeridos[mapa.saleField] = Boolean(ventaCruda.salePrice);
  }
  if (puedePisar(mapa.currencyField)) {
    patch[mapa.currencyField] = ventaCruda.currency || "ARS";
    camposSugeridos[mapa.currencyField] = Boolean(ventaCruda.currency);
  }

  return { patch, camposSugeridos };
}

// ─── Segunda vuelta del revisor funcional: no destapar lo que quedó protegido ──

/**
 * Al elegir un producto (existente O un parecido de Momento 4), Hotel/Aéreo/Traslado
 * sueltan los flags "tocado" de precio/moneda para devolverle la vía libre a la
 * sugerencia POR VARIANTE (`useVariantPriceSuggestion`) — es el comportamiento de
 * siempre: elegir un producto es una decisión fresca.
 *
 * Bug bloqueante (revisor funcional, segunda vuelta): soltar esos flags SIEMPRE, incluso
 * cuando el precio venía protegido porque salió de LA FRASE (Momento 4,
 * `construirPatchDeSeleccionManual` decidió NO pisarlo porque ya estaba sugerido),
 * deja ese precio desprotegido — 300ms después la sugerencia por variante lo pisa o lo
 * vacía igual, aunque siga pintado de amarillo en pantalla.
 *
 * La regla correcta: soltar el flag SOLO si el campo se pisó de verdad con la venta del
 * catálogo (`camposSugeridosDeVenta` lo trae). Si el campo NO aparece ahí es porque
 * `construirPatchDeSeleccionManual` lo dejó afuera por estar protegido — en ese caso el
 * flag tiene que seguir en pie.
 *
 * El camino manual sin IA (`fromAiOverride` false) no cambia: siempre suelta, como
 * siempre hizo — ahí no hay nada que la línea inteligente haya podido proteger antes.
 *
 * @param {{fromAiOverride:boolean, campo:string, camposSugeridosDeVenta:Record<string,any>}} params
 * @returns {boolean} true si corresponde `setXTocadoPorElUsuario(false)`
 */
export function debeResetearTocadoTrasSeleccion({ fromAiOverride, campo, camposSugeridosDeVenta }) {
  if (!fromAiOverride) return true;
  return Object.prototype.hasOwnProperty.call(camposSugeridosDeVenta || {}, campo);
}
