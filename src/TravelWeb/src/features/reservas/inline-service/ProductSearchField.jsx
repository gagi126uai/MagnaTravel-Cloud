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
 */

import { useState, useEffect, useRef, useCallback } from "react";
import { Search, RefreshCw, Plus } from "lucide-react";
import { api } from "../../../api";
import { hasPermission } from "../../../auth";
import { formatDate } from "../../../lib/utils";

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
 * Formatea un valor monetario para mostrar en el dropdown.
 * ej: 48000 → "$48.000"
 */
function formatDropdownPrice(value) {
    const number = Number(value) || 0;
    return `$${number.toLocaleString("es-AR", { minimumFractionDigits: 0, maximumFractionDigits: 0 })}`;
}

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
 */
function SearchResultItem({ result, onSelect, isStrongMatch, canSeeCost, isKeyboardFocused, optionId }) {
    // Construye la línea de última venta: "Ola Mayorista · $48.000/noche · 22/05/2026"
    const lastSaleInfo = (() => {
        const sale = result.lastSale || result.rateFallback;
        if (!sale) return null;

        const priceValue = canSeeCost ? sale.netCost : sale.salePrice;
        // Si no hay precio ni salePrice, no mostramos la línea para no confundir
        if (priceValue == null && !sale.salePrice) return null;

        const price = formatDropdownPrice(priceValue ?? sale.salePrice);
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
        bgClass = "bg-blue-100";
    } else if (isStrongMatch) {
        bgClass = "bg-blue-50 hover:bg-blue-100";
    } else {
        bgClass = "bg-white hover:bg-slate-50";
    }

    return (
        <button
            type="button"
            id={optionId}
            role="option"
            aria-selected={isKeyboardFocused}
            onMouseDown={(event) => event.preventDefault()} // evita blur del input al clickear
            onClick={() => onSelect(result)}
            className={`w-full px-4 py-3 text-left border-b border-slate-100 last:border-b-0 flex justify-between items-start gap-3 transition-colors ${bgClass}`}
            data-testid="catalog-search-result"
        >
            <div className="flex-1 min-w-0">
                <div className="text-sm font-semibold text-slate-900 truncate">{result.name}</div>
                {result.subtitle && (
                    <div className="text-xs text-slate-500 mt-0.5">{result.subtitle}</div>
                )}
                {lastSaleInfo && (
                    <div className="text-xs text-slate-400 mt-0.5">{lastSaleInfo}</div>
                )}
            </div>
            <span className="shrink-0 text-[11px] font-semibold px-2 py-0.5 rounded-full bg-emerald-100 text-emerald-700">
                En tu tarifario
            </span>
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
            className={`w-full px-4 py-3 text-left transition-colors ${isKeyboardFocused ? "bg-blue-100" : "bg-slate-50 hover:bg-slate-100"}`}
            data-testid="catalog-create-new"
        >
            <div className="flex items-center gap-2 text-sm font-semibold text-blue-600">
                <Plus className="w-4 h-4 shrink-0" />
                <span>No es ninguno: crear "{searchText}" como {nombreTipo} nuevo</span>
            </div>
            <div className="text-xs text-slate-400 mt-0.5 ml-6">
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
    onCreateNew,
    disabled,
    label,
    placeholder,
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

    const handleSelectExisting = (result) => {
        skipNextSearch.current = true;
        setKeyboardIndex(-1);
        onSelectExisting(result);
        setShowDropdown(false);
    };

    const handleCreateNew = (text) => {
        setKeyboardIndex(-1);
        setShowDropdown(false);
        onCreateNew(text);
    };

    const handleFocus = () => {
        clearTimeout(blurTimer.current);
        // Re-abrir el dropdown solo si el usuario ya interactuó antes (escribió algo)
        // y hay resultados en caché. En modo edición (sin haber tipeado), el foco
        // no debe disparar ninguna apertura.
        if (userHasInteracted.current && (value || "").trim().length >= MIN_QUERY_LENGTH && results.length > 0) {
            setShowDropdown(true);
        }
    };

    const handleBlur = () => {
        // Pequeño delay para que el click en un resultado no se cancele por el blur
        blurTimer.current = setTimeout(() => {
            setShowDropdown(false);
            setKeyboardIndex(-1);
        }, 150);
    };

    // Cantidad total de opciones navegables: resultados existentes + opción "crear"
    // La opción "crear" solo aparece cuando el texto es suficientemente largo y no está buscando.
    const showCreateOption = !isSearching && (value || "").trim().length >= MIN_QUERY_LENGTH;
    const totalOptions = results.length + (showCreateOption ? 1 : 0);

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
            if (keyboardIndex < results.length) {
                // Enter sobre un resultado existente: elegirlo
                handleSelectExisting(results[keyboardIndex]);
            } else {
                // Enter sobre "Crear nuevo": dispararlo
                handleCreateNew((value || "").trim());
            }
        } else if (event.key === "Escape") {
            event.preventDefault();
            setShowDropdown(false);
            setKeyboardIndex(-1);
        }
    };

    const hasNoResults = !isSearching && results.length === 0 && (value || "").trim().length >= MIN_QUERY_LENGTH;

    // El id del ítem actualmente resaltado por teclado (para aria-activedescendant)
    const activeDescendantId = keyboardIndex >= 0 ? getOptionId(keyboardIndex) : undefined;

    return (
        <div className="relative">
            <label className="block text-xs font-semibold text-slate-600 mb-1" htmlFor={`${listboxId.current}-input`}>
                {label || "Producto"}
            </label>
            <div className="relative">
                <Search className="absolute left-3 top-2.5 w-4 h-4 text-slate-400 pointer-events-none" />
                <input
                    id={`${listboxId.current}-input`}
                    type="text"
                    className="w-full pl-10 pr-10 py-2.5 text-sm border border-slate-200 rounded-lg bg-white focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500 disabled:bg-slate-50 disabled:text-slate-400"
                    placeholder={placeholder || "Buscá en tu catálogo..."}
                    value={value || ""}
                    onChange={(event) => {
                        skipNextSearch.current = false;
                        // El usuario empezó a escribir: habilitamos las búsquedas desde ahora.
                        userHasInteracted.current = true;
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
                    <RefreshCw className="absolute right-3 top-2.5 w-4 h-4 text-blue-500 animate-spin" />
                )}
            </div>

            {/* Dropdown de resultados: solo se muestra cuando hay foco y texto suficiente */}
            {showDropdown && (
                <div
                    id={listboxId.current}
                    className="absolute left-0 right-0 top-full z-50 mt-1 rounded-xl border border-slate-200 bg-white shadow-xl overflow-hidden"
                    role="listbox"
                    aria-label={`Resultados de búsqueda de ${label || "productos"}`}
                >
                    {isSearching && (
                        // Estado "buscando": texto sutil, no bloqueante
                        <div className="px-4 py-3 text-xs text-slate-400 italic" role="status">
                            Buscando…
                        </div>
                    )}

                    {!isSearching && results.length > 0 && results.map((result, index) => (
                        <SearchResultItem
                            key={result.ratePublicId || index}
                            result={result}
                            onSelect={handleSelectExisting}
                            // El primer resultado con score alto se resalta como "el más parecido"
                            isStrongMatch={index === 0 && (result.score == null || result.score >= STRONG_MATCH_THRESHOLD)}
                            canSeeCost={canSeeCost}
                            isKeyboardFocused={keyboardIndex === index}
                            optionId={getOptionId(index)}
                        />
                    ))}

                    {/* Sin resultados: directo a crear (guía UX ronda 2) */}
                    {hasNoResults && !isSearching && (
                        <div className="px-4 py-3 text-xs text-slate-500" role="status">
                            No encontramos "{value}" en tu tarifario
                        </div>
                    )}

                    {/* La opción crear SIEMPRE va al final (candado 2 anti-duplicados).
                        Pasamos serviceType para que el texto diga el tipo correcto:
                        "crear X como aéreo nuevo" / "como hotel nuevo" / etc. */}
                    {showCreateOption && (
                        <CreateNewOption
                            searchText={(value || "").trim()}
                            serviceType={serviceType}
                            onCreateNew={handleCreateNew}
                            isKeyboardFocused={keyboardIndex === results.length}
                            optionId={getOptionId(results.length)}
                        />
                    )}
                </div>
            )}
        </div>
    );
}
