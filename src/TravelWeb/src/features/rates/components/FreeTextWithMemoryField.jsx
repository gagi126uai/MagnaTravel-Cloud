/**
 * Campo de texto libre "CON MEMORIA" (spec firmada 2026-08-07, §5.2 / V4=B ajustada):
 * el nombre fino de la habitación ("Superior", "Vista al mar") o el vehículo del
 * traslado. La primera vez se escribe como sea; después el sistema ofrece lo que ya se
 * usó (GET /api/rates/variant-names) para frenar variaciones de tipeo ("dbl sup",
 * "SUPERIOR", "superio" pasan a ser UNA sola cosa del lado del servidor al guardar).
 *
 * Se usa en tres lugares: la fichita "+ Agregar producto", "Corregir" en la ficha del
 * producto, y el campo Categoría/Vehículo de las fichas de servicio (Hotel/Traslado) —
 * reuso real, no abstracción prematura.
 *
 * Navegación por teclado (fix ronda 2 de review): mismo patrón ya resuelto en
 * ProductSearchField (↑/↓ recorren las opciones, Enter elige, Esc cierra) — antes solo
 * se podía elegir con mouse, así que TABULAR fuera del campo dejaba la lista abierta sin
 * forma de usarla y sin forma prolija de cerrarla.
 */
import { useEffect, useRef, useState } from "react";
import { api } from "../../../api";
import { buildFreeTextMemoryOptions } from "../lib/freeTextWithMemoryLogic";

const DEBOUNCE_MS = 300;

export function FreeTextWithMemoryField({ serviceType, value, onChange, label, placeholder, id, dataTestId }) {
  const [suggestions, setSuggestions] = useState([]);
  const [showDropdown, setShowDropdown] = useState(false);
  const blurTimer = useRef(null);
  // Identificador único del listbox, para aria-activedescendant (mismo mecanismo que
  // ProductSearchField).
  const listboxId = useRef(`free-text-memory-listbox-${Math.random().toString(36).slice(2)}`);

  // Índice de navegación por teclado:
  //   -1 = ninguno seleccionado (cursor en el input)
  //   0..opciones.length-1 = una sugerencia ya escrita antes
  //   opciones.length = la opción "Usar tal cual"
  const [keyboardIndex, setKeyboardIndex] = useState(-1);

  // Vuelve a pedir las sugerencias cada vez que cambia el texto (debounce corto, mismo
  // criterio que el resto de los buscadores del tarifario). serviceType nunca cambia en
  // el medio de la carga de UN servicio, pero se incluye igual por si el campo se
  // reutiliza en una pantalla que sí lo cambia (ficha de servicio con tabs).
  useEffect(() => {
    const timer = setTimeout(async () => {
      try {
        const params = new URLSearchParams({ serviceType: serviceType || "", q: value || "" });
        const data = await api.get(`/rates/variant-names?${params.toString()}`);
        setSuggestions(Array.isArray(data) ? data : []);
        // Nuevas sugerencias: el índice de teclado anterior ya no corresponde a nada.
        setKeyboardIndex(-1);
      } catch {
        // Sin sugerencias no se traba nada: el campo sigue siendo texto libre (P-11).
        setSuggestions([]);
      }
    }, DEBOUNCE_MS);
    return () => clearTimeout(timer);
  }, [serviceType, value]);

  useEffect(() => () => clearTimeout(blurTimer.current), []);

  const { suggestions: opciones, showUseAsIsOption } = buildFreeTextMemoryOptions(value, suggestions);
  const hayAlgoParaMostrar = opciones.length > 0 || showUseAsIsOption;
  const totalOpciones = opciones.length + (showUseAsIsOption ? 1 : 0);

  const elegirOpcion = (texto) => {
    onChange(texto);
    setShowDropdown(false);
    setKeyboardIndex(-1);
  };

  // Navegación con teclado dentro del dropdown (↑↓ Enter Esc) — mismo patrón que
  // ProductSearchField, para que un vendedor de teclado pueda usar este campo sin mouse.
  const handleKeyDown = (event) => {
    if (!showDropdown || !hayAlgoParaMostrar) return;

    if (event.key === "ArrowDown") {
      event.preventDefault();
      setKeyboardIndex((prev) => (prev < totalOpciones - 1 ? prev + 1 : 0));
    } else if (event.key === "ArrowUp") {
      event.preventDefault();
      setKeyboardIndex((prev) => (prev > 0 ? prev - 1 : totalOpciones - 1));
    } else if (event.key === "Enter" && keyboardIndex >= 0) {
      event.preventDefault();
      if (keyboardIndex < opciones.length) {
        elegirOpcion(opciones[keyboardIndex]);
      } else {
        elegirOpcion((value || "").trim());
      }
    } else if (event.key === "Escape") {
      event.preventDefault();
      setShowDropdown(false);
      setKeyboardIndex(-1);
    }
  };

  const getOptionId = (index) => `${listboxId.current}-option-${index}`;
  const activeDescendantId = keyboardIndex >= 0 ? getOptionId(keyboardIndex) : undefined;

  return (
    <div className="relative">
      {label && (
        <label className="block text-xs font-semibold text-slate-600 dark:text-slate-400 mb-1" htmlFor={id}>
          {label}
        </label>
      )}
      <input
        id={id}
        type="text"
        className="w-full py-2 px-3 text-sm border rounded-lg bg-white border-slate-200 focus:outline-none focus:ring-1 focus:border-blue-500 focus:ring-blue-500 dark:border-slate-700 dark:bg-slate-900 dark:text-white"
        value={value || ""}
        onChange={(event) => { onChange(event.target.value); setShowDropdown(true); }}
        onFocus={() => setShowDropdown(true)}
        onBlur={() => { blurTimer.current = setTimeout(() => { setShowDropdown(false); setKeyboardIndex(-1); }, 150); }}
        onKeyDown={handleKeyDown}
        placeholder={placeholder}
        autoComplete="off"
        data-testid={dataTestId}
        aria-label={label}
        aria-expanded={showDropdown}
        aria-haspopup="listbox"
        aria-owns={listboxId.current}
        aria-autocomplete="list"
        aria-activedescendant={activeDescendantId}
        role="combobox"
      />
      {showDropdown && hayAlgoParaMostrar && (
        <div
          id={listboxId.current}
          className="absolute left-0 right-0 top-full z-50 mt-1 rounded-xl border border-slate-200 bg-white shadow-xl overflow-hidden dark:border-slate-700 dark:bg-slate-900"
          role="listbox"
          aria-label={`Nombres ya usados${label ? ` para ${label}` : ""}`}
        >
          {opciones.map((opcion, index) => (
            <button
              key={opcion}
              id={getOptionId(index)}
              type="button"
              role="option"
              aria-selected={keyboardIndex === index}
              onMouseDown={(event) => event.preventDefault()}
              onClick={() => elegirOpcion(opcion)}
              className={`block w-full px-3 py-2 text-left text-sm border-b border-slate-100 last:border-b-0 dark:border-slate-800 dark:text-slate-200 ${
                keyboardIndex === index ? "bg-blue-100 dark:bg-blue-900/40" : "text-slate-700 hover:bg-slate-50 dark:hover:bg-slate-800"
              }`}
            >
              {opcion}
            </button>
          ))}
          {showUseAsIsOption && (
            <button
              id={getOptionId(opciones.length)}
              type="button"
              role="option"
              aria-selected={keyboardIndex === opciones.length}
              onMouseDown={(event) => event.preventDefault()}
              onClick={() => elegirOpcion((value || "").trim())}
              className={`block w-full px-3 py-2 text-left text-sm font-semibold text-blue-600 dark:text-blue-400 ${
                keyboardIndex === opciones.length ? "bg-blue-100 dark:bg-blue-900/40" : "bg-slate-50 hover:bg-slate-100 dark:bg-slate-800 dark:hover:bg-slate-700"
              }`}
            >
              + Usar "{(value || "").trim()}" tal cual
            </button>
          )}
        </div>
      )}
    </div>
  );
}
