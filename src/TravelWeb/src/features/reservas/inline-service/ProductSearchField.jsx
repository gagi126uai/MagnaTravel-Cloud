/**
 * Campo buscador del catálogo de productos (buscador "find-or-create").
 *
 * Conecta a GET /api/rates/catalog-search?serviceType=Hotel&q={texto}
 * con debounce de 350ms y mínimo 2 caracteres.
 *
 * Muestra un dropdown con los resultados. Cada resultado tiene:
 *   - Nombre en negrita + subtítulo (ciudad)
 *   - Línea chica con la última vez que se vendió (operador, precio, fecha)
 *   - Etiqueta verde "En tu tarifario"
 *   - El primer resultado con score alto queda resaltado (hit)
 *
 * Al final del dropdown, SIEMPRE está la opción "Crear nuevo".
 *
 * Quien no tiene permiso `cobranzas.see_cost` recibe `netCost = null`
 * del backend y ve el precio de VENTA en el dropdown (nunca el costo).
 *
 * Se usa dentro de ServiceInlineCard para el tab Hotel (y en el futuro los otros 4 tipos).
 *
 * MATCHER ANTI-DUPLICADOS (decisión de Gastón, 2026-08-09; ampliado por la spec FIRMADA
 * del buscador versátil, 2026-08-10, D1..D13): cuando conviene (gate: `busquedaLocalDebil`
 * o `pareceLineaCompleta`), este componente consulta en SILENCIO al motor (POST
 * /linea-inteligente) para traer mejores candidatos y evitar que el vendedor cree un
 * producto duplicado (P7). Los candidatos se mezclan en el MISMO dropdown de siempre. Si
 * hay una duda GRANDE, aparece como una línea gris con ✨ (D12); si el vendedor tiró la
 * frase completa, lo que el motor entendió (operador/fechas) viaja al elegir un producto
 * (D13). Si el motor no contesta nada útil (sin clave, caído, tardó), la pantalla es
 * exactamente la de hoy. Ver `useProductDedupMatch.js` y `productDedupMatchLogic.js`.
 *
 * DUDA DE PRODUCTO LOCAL (H-1, 2026-08-11): la ✨ ahora también puede salir SIN el
 * motor — mirando nomás los primeros 2 resultados que ya trajo el buscador de catálogo
 * (`dudaDeProductoLocal`). Hacía falta: el gate que decide cuándo vale la pena consultar
 * al motor (`busquedaLocalDebil`) se apaga justo cuando el buscador local ya encontró dos
 * resultados fuertes casi iguales — el caso donde más falta hace preguntar. La duda local
 * gana sobre la del motor cuando las dos existen.
 *
 * BUSCADOR VERSÁTIL (D1..D9): las filas de OTRO tipo de servicio (ej: un traslado
 * mientras se busca en la solapa Hotel) también aparecen, con una chapita gris con el
 * nombre del tipo — primero las del tipo activo, después las demás (partición dura, D9).
 * Elegir una de otro tipo dispara `onSelectOtherType` en vez de `onSelectExisting`, para
 * que `ServiceInlineCard` salte de solapa sola (D3). En modo edición (`esEdicion`) esto
 * se apaga: el buscador queda limitado a su propio tipo (D6). Ver `crossTypeSearchLogic.js`.
 */

import { useState, useEffect, useMemo, useRef, useCallback } from "react";
import { Search, RefreshCw, Plus } from "lucide-react";
import { api } from "../../../api";
import { hasPermission } from "../../../auth";
import { formatDate, formatCurrency } from "../../../lib/utils";
import { useProductDedupMatch } from "./useProductDedupMatch";
import {
    debeDispararDedupMatch,
    mergearCandidatosDedup,
    resolverTextoDeCrear,
    resolverListaParaMostrar,
    contarOpcionesNavegables,
    busquedaLocalDebil,
    pareceLineaCompleta,
    debeMostrarDuda,
    dudaDeProductoLocal,
} from "./productDedupMatchLogic";
import { esResultadoDeOtroTipo, particionarPorTipo, filtrarPorTipoActivo } from "./crossTypeSearchLogic";

// Mínimo de caracteres para lanzar la búsqueda (igual al backend)
const MIN_QUERY_LENGTH = 2;
// Debounce: espera 350ms desde el último tecleo antes de buscar
const DEBOUNCE_MS = 350;
// Un resultado con score >= a este umbral se resalta como "el más parecido"
const STRONG_MATCH_THRESHOLD = 0.65;
// Cap defensivo: el backend puede mandar más; el dropdown no muestra más de 8 filas
// para no abrumar al usuario y mantener el rendimiento del DOM.
const MAX_DISPLAY_RESULTS = 8;

/**
 * Convierte la fecha/hora de la última venta a texto legible para el dropdown.
 * ej: "2026-05-22T14:03:00Z" → "22/05/2026"
 *
 * soldAt es un instante REAL (CreatedAt del servicio vendido, no una fecha-solo-día
 * elegida por el usuario), así que corresponde mostrarlo en hora local — usamos la
 * formatDate() central de utils.js, que ya distingue ambos casos (ver su comentario).
 */
function formatSoldDate(isoDate) {
    if (!isoDate) return null;
    const formateada = formatDate(isoDate);
    return formateada === "-" ? null : formateada;
}

/**
 * Una fila del dropdown. Muestra nombre, subtítulo, última venta y etiqueta verde.
 * Si `isStrongMatch` es true, el fondo es azul claro (el más parecido).
 * Si `isKeyboardFocused` es true, el fondo es azul oscuro (navegación con teclado).
 *
 * `otherTypeLabel` (D1/D2/D8, spec 2026-08-10): cuando el resultado es de OTRO tipo de
 * servicio que la solapa activa, trae la palabra del negocio ("Aéreo", "Traslado"...) y
 * se pinta una chapita GRIS a la izquierda de la verde "En tu tarifario" — misma forma y
 * tamaño, ninguna caja nueva. Si es del tipo activo, `otherTypeLabel` viene null (D1: la
 * solapa ya dice el tipo, repetirlo en cada fila sería decir lo mismo dos veces).
 */
function SearchResultItem({ result, onSelect, isStrongMatch, canSeeCost, isKeyboardFocused, optionId, otherTypeLabel }) {
    // Construye la línea de última venta: "Ola Mayorista · $48.000/noche · 22/05/2026"
    const lastSaleInfo = (() => {
        const sale = result.lastSale || result.rateFallback;
        if (!sale) return null;

        const priceValue = canSeeCost ? sale.netCost : sale.salePrice;
        // Si no hay precio ni salePrice, no mostramos la línea para no confundir
        if (priceValue == null && !sale.salePrice) return null;

        // Bug #26 (Tanda 4, 2026-07-24): antes esto SIEMPRE mostraba "$" con formato
        // es-AR, aunque la última venta hubiera sido en dólares — formatCurrency() usa
        // la moneda REAL de esa venta (sale.currency), "ARS" solo si el dato es legacy
        // y no la trae.
        const price = formatCurrency(priceValue ?? sale.salePrice, sale.currency || "ARS");
        const unit = sale.priceUnit === "noche_habitacion" ? "/noche" : "";
        const parts = [
            sale.supplierName,
            `${price}${unit}`,
            formatSoldDate(sale.soldAt),
        ].filter(Boolean);
        return parts.join(" · ");
    })();

    let bgClass;
    if (isKeyboardFocused) {
        // Foco de teclado: resaltado más marcado que el hover normal
        bgClass = "bg-blue-100 dark:bg-blue-900/40";
    } else if (isStrongMatch) {
        bgClass = "bg-blue-50 hover:bg-blue-100 dark:bg-blue-950/30 dark:hover:bg-blue-900/40";
    } else {
        bgClass = "bg-white hover:bg-slate-50 dark:bg-slate-900 dark:hover:bg-slate-800";
    }

    return (
        <button
            type="button"
            id={optionId}
            role="option"
            aria-selected={isKeyboardFocused}
            onMouseDown={(event) => event.preventDefault()} // evita blur del input al clickear
            onClick={() => onSelect(result)}
            className={`w-full px-4 py-3 text-left border-b border-slate-100 last:border-b-0 flex justify-between items-start gap-3 transition-colors dark:border-slate-800 ${bgClass}`}
            data-testid="catalog-search-result"
        >
            <div className="flex-1 min-w-0">
                <div className="text-sm font-semibold text-slate-900 truncate dark:text-white">{result.name}</div>
                {result.subtitle && (
                    <div className="text-xs text-slate-500 mt-0.5 dark:text-slate-400">{result.subtitle}</div>
                )}
                {lastSaleInfo && (
                    <div className="text-xs text-slate-400 mt-0.5 dark:text-slate-500">{lastSaleInfo}</div>
                )}
            </div>
            <div className="shrink-0 flex items-center gap-1.5">
                {otherTypeLabel && (
                    <span
                        className="text-[11px] font-semibold px-2 py-0.5 rounded-full bg-slate-200 text-slate-600 dark:bg-slate-700 dark:text-slate-300"
                        data-testid="catalog-search-result-type-badge"
                    >
                        {otherTypeLabel}
                    </span>
                )}
                <span className="text-[11px] font-semibold px-2 py-0.5 rounded-full bg-emerald-100 text-emerald-700 dark:bg-emerald-900/30 dark:text-emerald-300">
                    En tu tarifario
                </span>
            </div>
        </button>
    );
}

/**
 * Mapeo de serviceType (valor del backend/form) → nombre legible en español
 * para el texto de creación: "crear X como aéreo nuevo", "como hotel nuevo", etc.
 *
 * Si llega un tipo desconocido, cae al genérico "servicio nuevo".
 */
const NOMBRE_TIPO_SERVICIO = {
    Aereo: "aéreo",
    Hotel: "hotel",
    Traslado: "traslado",
    Paquete: "paquete",
    Asistencia: "asistencia",
};

function nombreTipoServicio(serviceType) {
    return NOMBRE_TIPO_SERVICIO[serviceType] || "servicio";
}

/**
 * Mismo mapa que NOMBRE_TIPO_SERVICIO, pero con la palabra tal como se escribe en las
 * solapas ("Hotel", "Aéreo"...) — para la chapita gris de fila-de-otro-tipo (D2/D8): "no
 * un valor interno, nunca una sigla, nunca una explicación al lado".
 */
const NOMBRE_TIPO_SERVICIO_CHAPITA = {
    Aereo: "Aéreo",
    Hotel: "Hotel",
    Traslado: "Traslado",
    Paquete: "Paquete",
    Asistencia: "Asistencia",
};

function nombreTipoServicioChapita(serviceType) {
    return NOMBRE_TIPO_SERVICIO_CHAPITA[serviceType] || null;
}

/**
 * Última opción del dropdown: crear el producto nuevo.
 * Según la guía UX: "Revisá los de arriba antes — si ya existe, elegirlo evita duplicados."
 *
 * Recibe serviceType para mostrar el tipo correcto en el texto (no siempre "hotel").
 */
function CreateNewOption({ searchText, serviceType, onCreateNew, isKeyboardFocused, optionId }) {
    // Nombre legible para el usuario: "aéreo nuevo", "hotel nuevo", etc.
    const nombreTipo = nombreTipoServicio(serviceType);

    return (
        <button
            type="button"
            id={optionId}
            role="option"
            aria-selected={isKeyboardFocused}
            onMouseDown={(event) => event.preventDefault()}
            onClick={() => onCreateNew(searchText)}
            className={`w-full px-4 py-3 text-left transition-colors ${isKeyboardFocused ? "bg-blue-100 dark:bg-blue-900/40" : "bg-slate-50 hover:bg-slate-100 dark:bg-slate-800/60 dark:hover:bg-slate-800"}`}
            data-testid="catalog-create-new"
        >
            <div className="flex items-center gap-2 text-sm font-semibold text-primary dark:text-primary">
                <Plus className="w-4 h-4 shrink-0" />
                <span>No es ninguno: crear "{searchText}" como {nombreTipo} nuevo</span>
            </div>
            <div className="text-xs text-slate-400 mt-0.5 ml-6 dark:text-slate-500">
                Revisá los de arriba antes — si ya existe, elegirlo evita duplicados.
            </div>
        </button>
    );
}

export function ProductSearchField({
    serviceType,
    value,
    onChange,
    onSelectExisting,
    // D2/D3 (spec 2026-08-10): cuando el vendedor elige una fila de OTRO tipo de
    // servicio, este callback (result, interpretacion) reemplaza a onSelectExisting —
    // ServiceInlineCard lo usa para saltar de solapa sola. Opcional: si no se pasa (o el
    // form está en modo edición), el cruce de tipos simplemente no aplica.
    onSelectOtherType,
    onCreateNew,
    disabled,
    label,
    placeholder,
    // Opcional: sin reservaId el matcher anti-duplicados simplemente no dispara (mismo
    // comportamiento que si el motor estuviera caído) — este campo funciona igual que
    // siempre para quien no lo pase.
    reservaId,
    // D6 (spec 2026-08-10): editando un servicio ya cargado no se puede cambiar de tipo
    // — el cruce de tipos se apaga (se filtra al tipo activo) y elegir una fila nunca
    // dispara onSelectOtherType, aunque el backend siga mandando resultados mezclados.
    esEdicion,
    // Fix #9 (auditoría de coherencia 2026-08-10): el rateId YA vinculado del form (si
    // hay uno). Con un producto ya elegido, el matcher IA no tiene sentido (la identidad
    // ya está resuelta) y cualquier duda vieja quedaría obsoleta — ver matcherHabilitado
    // y debeMostrarDuda más abajo.
    rateId,
    // Fix #1 (auditoría de coherencia 2026-08-10, bug reportado por Gastón): el operador
    // que el vendedor ya eligió a mano en el form (si hay uno). No filtra ningún
    // resultado — solo ordena: dentro de cada bloque de tipo, las filas cuya última
    // venta fue con ESE operador van primero (el dato ya viaja con el vendedor).
    supplierIdElegido,
}) {
    const canSeeCost = hasPermission("cobranzas.see_cost");

    const [results, setResults] = useState([]);
    const [isSearching, setIsSearching] = useState(false);
    const [showDropdown, setShowDropdown] = useState(false);
    // Cuando el usuario elige un resultado, seteamos el nombre en el input y no
    // queremos que ese cambio lance otra búsqueda. Este ref lo evita.
    const skipNextSearch = useRef(false);
    // BUG FIX: en modo edición el componente recibe `value` precargado desde el inicio.
    // Sin este flag, el useEffect del debounce corre en el mount con ese valor ya largo
    // y dispara la búsqueda/apertura del dropdown aunque el usuario no haya tocado nada.
    // La solución: solo buscar si el usuario realmente interactuó (onChange).
    const userHasInteracted = useRef(false);
    const blurTimer = useRef(null);
    // Identificador único para el listbox (a11y: aria-owns)
    const listboxId = useRef(`catalog-listbox-${Math.random().toString(36).slice(2)}`);

    // Fix C-6 (review 2026-08-10, D12 "no reaparece al volver al campo"): Esc o blur
    // cierran el desplegable, pero `dedupResult.duda` sigue viva ahí adentro (nada la
    // invalida hasta que el texto cambie) — sin este ref, un refoco reabriría el
    // desplegable con la MISMA duda ya descartada. Se prende en Esc/blur, se apaga en
    // cuanto el vendedor sigue tipeando (texto nuevo = duda vieja ya no aplica de todos
    // modos). Ver `debeMostrarDuda` (productDedupMatchLogic.js) para la decisión pura.
    const dudaDescartadaRef = useRef(false);

    // Índice de navegación por teclado:
    //   -1 = ninguno seleccionado (cursor en el input)
    //   0..results.length-1 = un resultado existente
    //   results.length = la opción "Crear nuevo"
    const [keyboardIndex, setKeyboardIndex] = useState(-1);

    // Genera el id del item para aria-activedescendant
    const getOptionId = (index) => `${listboxId.current}-option-${index}`;

    const searchCatalog = useCallback(async (query) => {
        if (!query || query.trim().length < MIN_QUERY_LENGTH) {
            setResults([]);
            setShowDropdown(false);
            return;
        }
        setIsSearching(true);
        try {
            const params = new URLSearchParams({ serviceType, q: query.trim() });
            const data = await api.get(`/rates/catalog-search?${params}`);
            // Cap defensivo: nunca mostramos más de MAX_DISPLAY_RESULTS resultados
            // aunque el backend mande más (evita dropdown enorme y lento).
            setResults((data || []).slice(0, MAX_DISPLAY_RESULTS));
            // Resetear el índice de teclado al obtener nuevos resultados
            setKeyboardIndex(-1);
            // Solo abrimos el dropdown si hay resultados O si el usuario sigue escribiendo
            setShowDropdown(true);
        } catch {
            // Si falla la búsqueda no bloqueamos al usuario: sigue pudiendo cargar a mano
            setResults([]);
        } finally {
            setIsSearching(false);
        }
    }, [serviceType]);

    // Debounce: espera DEBOUNCE_MS desde el último tecleo antes de buscar.
    // Condiciones para NO buscar:
    //   1. skipNextSearch: recién elegimos un resultado (evita re-búsqueda por el setState del nombre).
    //   2. userHasInteracted: el usuario nunca escribió (evita abrir el dropdown en modo edición
    //      donde `value` ya viene precargado desde el padre al montar el componente).
    useEffect(() => {
        if (skipNextSearch.current) {
            skipNextSearch.current = false;
            return;
        }
        // Si el usuario aún no interactuó con el campo (ej: modo edición con valor precargado),
        // no lanzamos ninguna búsqueda ni abrimos el dropdown.
        if (!userHasInteracted.current) {
            return;
        }
        const query = value || "";
        if (query.trim().length < MIN_QUERY_LENGTH) {
            setResults([]);
            setShowDropdown(false);
            setKeyboardIndex(-1);
            return;
        }
        const timer = setTimeout(() => searchCatalog(query), DEBOUNCE_MS);
        return () => clearTimeout(timer);
    }, [value, searchCatalog]);

    // Limpiar el timer de blur al desmontar para no hacer setState en componente muerto
    useEffect(() => () => clearTimeout(blurTimer.current), []);

    // ─── Matcher anti-duplicados (P7, decisión 2026-08-09) ────────────────────────────
    // Gate D5 (spec 2026-08-10, "llamarlo MENOS"): antes disparaba con cualquier texto
    // sin parecido fuerte local; ahora hace falta ADEMÁS que la búsqueda local haya
    // venido floja (`busquedaLocalDebil`) O que el texto "parezca una frase completa"
    // (`pareceLineaCompleta`, D13) — una frase puede traer fechas/operador que valen la
    // pena interpretar aunque el nombre del producto YA haya matcheado fuerte por sí solo.
    // Fix menor (revisor funcional, 2ª vuelta): también se apaga mientras
    // `skipNextSearch` está activo — ese flag significa "el vendedor ACABA de elegir o
    // crear un producto", momento en el que la identidad ya quedó resuelta y consultar
    // igual sería una llamada desperdiciada (cuota del motor, plata).
    // Fix #9 (auditoría de coherencia 2026-08-10): mismo criterio pero MÁS ancho que
    // `skipNextSearch` — ese flag solo dura el instante de la selección; `rateId` sigue
    // seteado después, mientras el producto siga vinculado. Si el vendedor vuelve a
    // enfocar el campo o edita otra cosa sin desvincular el producto, el matcher no
    // tiene que re-consultar: la identidad ya está resuelta.
    const matcherHabilitado =
        userHasInteracted.current &&
        !isSearching &&
        !skipNextSearch.current &&
        !rateId &&
        debeDispararDedupMatch(value) &&
        (busquedaLocalDebil(results) || pareceLineaCompleta(value));
    const dedupResult = useProductDedupMatch({
        reservaId,
        serviceType,
        text: value,
        enabled: matcherHabilitado,
    });

    // La lista fresca: los resultados de siempre + lo que el matcher haya sumado, sin
    // duplicar (nunca reordena lo que ya estaba). DERIVADA con useMemo — sin estado, sin
    // efecto — para que no exista NUNCA un render con esta lista "atrasada" respecto de
    // `results`/`dedupResult` (fix de la 2ª vuelta: la versión con useState+useEffect
    // metía un render de por medio, y en ESE render `hasNoResults` alcanzaba a leer la
    // lista vieja — flasheaba "No encontramos..." una fracción de segundo antes de cada
    // lista exitosa, incluso sin IA de por medio: regresión visible vs 0d94e806).
    const resultadosMergeados = useMemo(
        () => (dedupResult ? mergearCandidatosDedup(results, dedupResult.productCandidates, MAX_DISPLAY_RESULTS) : results),
        [results, dedupResult]
    );

    // Buscador versátil (spec 2026-08-10, D1..D9): desde acá el backend ya no filtra por
    // `serviceType` (es solo una preferencia de orden), así que el cruce de tipos se
    // resuelve del lado del front. Editando (D6) el buscador queda limitado a su propio
    // tipo; fuera de edición, partición dura (D9): primero el tipo de la solapa activa
    // — y, dentro de cada bloque, fix #1 (auditoría 2026-08-10): si el vendedor ya
    // eligió un operador a mano, las filas cuya última venta fue con ESE operador
    // quedan primero (no filtra nada, solo ordena).
    const resultadosFrescos = useMemo(
        () => (esEdicion
            ? filtrarPorTipoActivo(resultadosMergeados, serviceType)
            : particionarPorTipo(resultadosMergeados, serviceType, supplierIdElegido)),
        [resultadosMergeados, serviceType, esEdicion, supplierIdElegido]
    );

    // Bug bloqueante B2 (revisor funcional): si el vendedor está navegando el dropdown
    // con las flechas (`keyboardIndex >= 0`) y en ESE momento el matcher hace crecer
    // `resultadosFrescos`, el índice que apuntaba a "crear" pasaría a apuntar a un
    // producto existente — un Enter rápido lo elegiría por error. `listaCongeladaRef`
    // guarda la ÚLTIMA lista fresca vista mientras NO se estaba navegando; apenas arranca
    // la navegación deja de actualizarse sola (nunca se le vuelve a escribir hasta que
    // `keyboardIndex` vuelve a -1), así que queda "congelada" tal cual estaba antes de
    // que el vendedor tocara una flecha — sin usar estado ni efecto, así no hay ningún
    // render de por medio: `resolverListaParaMostrar` decide, en ESTE MISMO render, cuál
    // de las dos lista corresponde, y `hasNoResults`/`totalOptions` leen ese resultado
    // ÚNICO más abajo (nunca una versión vieja).
    const listaCongeladaRef = useRef(resultadosFrescos);
    if (keyboardIndex < 0) {
        listaCongeladaRef.current = resultadosFrescos;
    }
    const resultadosParaMostrar = resolverListaParaMostrar({
        keyboardIndex,
        listaCongelada: listaCongeladaRef.current,
        listaFresca: resultadosFrescos,
    });

    // El texto de "crear ..." usa el nombre limpio que sacó el motor cuando lo hay, para
    // que un alta nueva no nazca con la frase entera de basura en el nombre.
    const textoParaCrear = resolverTextoDeCrear(dedupResult?.productSearchText, value);

    // Duda de producto LOCAL (H-1, 2026-08-11): mira los primeros 2 resultados que YA
    // trajo el buscador de catálogo (sin depender del motor) y arma la misma pregunta
    // "¿el de A o el de B?" cuando hace falta. Se recalcula con useMemo cada vez que
    // cambian los resultados mostrados — así sigue el mismo ritmo que ve el vendedor.
    // La local GANA sobre la del motor: si el buscador local ya detectó la ambigüedad,
    // no hace falta esperar la respuesta (más lenta) de /linea-inteligente.
    const dudaLocal = useMemo(() => dudaDeProductoLocal(resultadosFrescos), [resultadosFrescos]);
    const dudaVigente = dudaLocal ?? dedupResult?.duda ?? null;

    const handleSelectExisting = (result) => {
        // Mismo housekeeping de cierre en los dos caminos (mismo tipo u otro tipo): el
        // dropdown se cierra y no queda ninguna búsqueda vieja al acecho.
        skipNextSearch.current = true;
        setKeyboardIndex(-1);
        setShowDropdown(false);

        // D13: lo que el motor entendió de LA FRASE tipeada (operador/fechas) viaja junto
        // con la selección, sea del mismo tipo o de otro — se lee acá (no más abajo) para
        // capturar el valor VIGENTE de este render, antes de que `skipNextSearch` recién
        // puesto apague el matcher y borre `dedupResult` en el próximo render.
        const interpretacionVigente = dedupResult?.interpretacion || null;

        // D2/D3: si la fila es de OTRO tipo de servicio, no se carga acá — se avisa al
        // padre para que salte de solapa solo. En modo edición (D6) esto nunca aplica.
        if (!esEdicion && esResultadoDeOtroTipo(result, serviceType)) {
            onSelectOtherType?.(result, interpretacionVigente);
            return;
        }
        onSelectExisting(result, interpretacionVigente);
    };

    const handleCreateNew = (text) => {
        // Bug bloqueante (revisor funcional): `text` puede ser `textoParaCrear` (el
        // nombre que limpió el matcher), distinto de `value` tal cual lo escribió el
        // vendedor. Sin este flag, el padre hace onCreateNew → setForm → `value` cambia
        // → el efecto de debounce de la búsqueda normal lo ve como un tecleo nuevo y
        // relanza la búsqueda: el desplegable REAPARECE ~350ms después, tapando el
        // recuadro de "producto nuevo" que el vendedor recién abrió. Mismo patrón que ya
        // usa handleSelectExisting acá arriba.
        skipNextSearch.current = true;
        setKeyboardIndex(-1);
        setShowDropdown(false);
        // D13-bis (spec 2026-08-10, fix "crear nuevo pelado"): la interpretación vigente
        // de la frase también viaja al camino de crear — mismo criterio que
        // handleSelectExisting (leer `dedupResult` ACÁ, antes de que `skipNextSearch`
        // recién puesto apague el matcher y borre `dedupResult` en el próximo render).
        const interpretacionVigente = dedupResult?.interpretacion || null;
        onCreateNew(text, interpretacionVigente);
    };

    const handleFocus = () => {
        clearTimeout(blurTimer.current);
        // Re-abrir el dropdown solo si el usuario ya interactuó antes (escribió algo)
        // y hay resultados en caché. En modo edición (sin haber tipeado), el foco
        // no debe disparar ninguna apertura.
        if (userHasInteracted.current && (value || "").trim().length >= MIN_QUERY_LENGTH && resultadosParaMostrar.length > 0) {
            setShowDropdown(true);
        }
    };

    const handleBlur = () => {
        // Pequeño delay para que el click en un resultado no se cancele por el blur
        blurTimer.current = setTimeout(() => {
            setShowDropdown(false);
            setKeyboardIndex(-1);
            // C-6: perder el foco también cierra/descarta la duda — "ni reaparece al
            // volver al campo" (D12). Si el click SÍ era sobre un resultado, esto no
            // importa: la fila ya se eligió y el desplegable de todos modos se cierra.
            dudaDescartadaRef.current = true;
        }, 150);
    };

    // Cantidad total de opciones navegables: resultados existentes + opción "crear".
    // La opción "crear" solo aparece cuando el texto es suficientemente largo y no está
    // buscando. La línea con ✨ (D12) NUNCA entra acá — contarOpcionesNavegables ni
    // siquiera la recibe como parámetro, así que estructuralmente no puede sumar.
    const showCreateOption = !isSearching && (value || "").trim().length >= MIN_QUERY_LENGTH;
    const totalOptions = contarOpcionesNavegables({
        cantidadResultados: resultadosParaMostrar.length,
        hayOpcionCrear: showCreateOption,
    });

    // Maneja la navegación con teclado dentro del dropdown (↑↓ Enter Esc).
    // Esto permite que usuarios de teclado / lectores de pantalla usen el buscador sin mouse.
    const handleKeyDown = (event) => {
        if (!showDropdown) return;

        if (event.key === "ArrowDown") {
            event.preventDefault();
            setKeyboardIndex((prev) => (prev < totalOptions - 1 ? prev + 1 : 0));
        } else if (event.key === "ArrowUp") {
            event.preventDefault();
            setKeyboardIndex((prev) => (prev > 0 ? prev - 1 : totalOptions - 1));
        } else if (event.key === "Enter" && keyboardIndex >= 0) {
            event.preventDefault();
            if (keyboardIndex < resultadosParaMostrar.length) {
                // Enter sobre un resultado existente: elegirlo
                handleSelectExisting(resultadosParaMostrar[keyboardIndex]);
            } else {
                // Enter sobre "Crear nuevo": dispararlo
                handleCreateNew(textoParaCrear);
            }
        } else if (event.key === "Escape") {
            event.preventDefault();
            setShowDropdown(false);
            setKeyboardIndex(-1);
            // C-6: Esc descarta la duda con ✨ para siempre en este texto — un refoco
            // después no puede resucitarla (D12).
            dudaDescartadaRef.current = true;
        }
    };

    const hasNoResults = !isSearching && resultadosParaMostrar.length === 0 && (value || "").trim().length >= MIN_QUERY_LENGTH;

    // El id del ítem actualmente resaltado por teclado (para aria-activedescendant)
    const activeDescendantId = keyboardIndex >= 0 ? getOptionId(keyboardIndex) : undefined;

    return (
        <div className="relative">
            <label className="block text-[11px] font-semibold tracking-wide text-slate-500 mb-1 dark:text-slate-400" htmlFor={`${listboxId.current}-input`}>
                {label || "Producto"}
            </label>
            <div className="relative">
                <Search className="absolute left-3 top-2.5 w-4 h-4 text-slate-400 pointer-events-none dark:text-slate-500" />
                <input
                    id={`${listboxId.current}-input`}
                    type="text"
                    className="w-full pl-10 pr-10 py-2 text-[13px] border border-slate-300 rounded-[7px] bg-white text-slate-800 focus:border-primary focus:outline-none focus:ring-1 focus:ring-primary disabled:bg-slate-50 disabled:text-slate-400 dark:bg-slate-900 dark:text-slate-100 dark:border-slate-600 dark:disabled:bg-slate-800/60 dark:disabled:text-slate-500"
                    placeholder={placeholder || "Buscá en tu catálogo..."}
                    value={value || ""}
                    onChange={(event) => {
                        skipNextSearch.current = false;
                        // El usuario empezó a escribir: habilitamos las búsquedas desde ahora.
                        userHasInteracted.current = true;
                        // C-6: seguir tipeando re-arma la posibilidad de la ✨ (D12: "se
                        // va sola al seguir tipeando" — pero también puede volver, con
                        // una duda NUEVA, para el texto nuevo).
                        dudaDescartadaRef.current = false;
                        onChange(event.target.value);
                    }}
                    onFocus={handleFocus}
                    onBlur={handleBlur}
                    onKeyDown={handleKeyDown}
                    disabled={disabled}
                    autoComplete="off"
                    data-testid="product-search-field"
                    aria-label={label || "Buscador de productos"}
                    aria-expanded={showDropdown}
                    aria-haspopup="listbox"
                    aria-owns={listboxId.current}
                    aria-autocomplete="list"
                    aria-activedescendant={activeDescendantId}
                    role="combobox"
                />
                {isSearching && (
                    <RefreshCw className="absolute right-3 top-2.5 w-4 h-4 text-primary dark:text-primary animate-spin" />
                )}
            </div>

            {/* Dropdown de resultados: solo se muestra cuando hay foco y texto suficiente */}
            {showDropdown && (
                <div
                    id={listboxId.current}
                    className="absolute left-0 right-0 top-full z-50 mt-1 rounded-xl border border-slate-200 bg-white shadow-xl overflow-hidden dark:border-slate-700 dark:bg-slate-900"
                    role="listbox"
                    aria-label={`Resultados de búsqueda de ${label || "productos"}`}
                >
                    {isSearching && (
                        // Estado "buscando": texto sutil, no bloqueante
                        <div className="px-4 py-3 text-xs text-slate-400 italic dark:text-slate-500" role="status">
                            Buscando…
                        </div>
                    )}

                    {/* La duda con ✨ (D11/D12, spec 2026-08-10; H-1 2026-08-11): un renglón
                        gris de UNA línea, arriba de todo dentro del desplegable, pegado
                        abajo del casillero. NO es una opción — no lleva optionId, no es
                        clickeable, no entra en totalOptions (ver contarOpcionesNavegables
                        más arriba). Se contesta eligiendo la fila de abajo, que es la
                        respuesta. `dudaVigente` prioriza la duda LOCAL (armada mirando los
                        2 primeros resultados del buscador, sin motor) sobre la del motor —
                        la local no depende de una consulta lenta ni de que el gate del
                        motor la deje pasar. `debeMostrarDuda` (fix C-4/C-6/#9) filtra que
                        sea de PRODUCTO, que no haya sido descartada con Esc/blur
                        (dudaDescartadaRef) y que NO haya ya un producto vinculado (rateId)
                        — con la identidad resuelta, cualquier duda vieja da vueltas de más.
                        Se apaga sola en cuanto el vendedor sigue tipeando (los resultados
                        cambian y dudaLocal se recalcula; dedupResult se reinicia también)
                        o cierra el desplegable. */}
                    {debeMostrarDuda({ duda: dudaVigente, isSearching, dudaDescartada: dudaDescartadaRef.current, hayProductoVinculado: Boolean(rateId) }) && (
                        <div
                            className="px-4 py-2 text-xs text-slate-500 bg-slate-50 border-b border-slate-100 dark:text-slate-400 dark:bg-slate-800/60 dark:border-slate-700"
                            role="status"
                            data-testid="catalog-search-duda"
                        >
                            <span aria-hidden="true">✨ </span>
                            {dudaVigente.question}
                        </div>
                    )}

                    {!isSearching && resultadosParaMostrar.length > 0 && resultadosParaMostrar.map((result, index) => (
                        <SearchResultItem
                            key={result.ratePublicId || index}
                            result={result}
                            onSelect={handleSelectExisting}
                            // El primer resultado con score alto se resalta como "el más parecido"
                            isStrongMatch={index === 0 && (result.score == null || result.score >= STRONG_MATCH_THRESHOLD)}
                            canSeeCost={canSeeCost}
                            isKeyboardFocused={keyboardIndex === index}
                            optionId={getOptionId(index)}
                            // Chapita gris (D1/D2/D8): solo en filas de OTRO tipo, y nunca
                            // en modo edición (ahí el buscador ya viene filtrado a su tipo).
                            otherTypeLabel={
                                !esEdicion && esResultadoDeOtroTipo(result, serviceType)
                                    ? nombreTipoServicioChapita(result.serviceType)
                                    : null
                            }
                        />
                    ))}

                    {/* Sin resultados: directo a crear (guía UX ronda 2) */}
                    {hasNoResults && !isSearching && (
                        <div className="px-4 py-3 text-xs text-slate-500 dark:text-slate-400" role="status">
                            No encontramos "{value}" en tu tarifario
                        </div>
                    )}

                    {/* La opción crear SIEMPRE va al final (candado 2 anti-duplicados).
                        Pasamos serviceType para que el texto diga el tipo correcto:
                        "crear X como aéreo nuevo" / "como hotel nuevo" / etc. */}
                    {showCreateOption && (
                        <CreateNewOption
                            searchText={textoParaCrear}
                            serviceType={serviceType}
                            onCreateNew={handleCreateNew}
                            isKeyboardFocused={keyboardIndex === resultadosParaMostrar.length}
                            optionId={getOptionId(resultadosParaMostrar.length)}
                        />
                    )}
                </div>
            )}
        </div>
    );
}
