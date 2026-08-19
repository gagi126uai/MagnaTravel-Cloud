/**
 * Dropdown de sugerencias de pasajeros históricos de la agencia (base propia).
 *
 * Se muestra debajo del campo que disparó la búsqueda (nombre o documento) en
 * PasajeroInlineForm, cuando se está dando de alta un pasajero nuevo. Portado
 * del extinto PassengerFormModal (T5, jubilación del modal, 2026-08-18) — antes
 * vivía adentro del modal, ahora es un componente propio para que el inline lo
 * pueda usar sin importar nada del modal muerto.
 *
 * Props:
 *   sugerencias         — array de resultados del backend
 *   cargando            — si la búsqueda está en curso
 *   onElegir(sugerencia) — callback al seleccionar un ítem
 *   onCerrar()          — callback para cerrar el dropdown sin elegir
 */

import { History, Loader2, X } from "lucide-react";
import { formatearSubtituloSugerencia } from "../lib/pasajeroSearchLogic.js";

export function DropdownHistorico({ sugerencias, cargando, onElegir, onCerrar }) {
    // No mostramos el dropdown si está vacío y no está cargando (nada que ofrecer).
    if (!cargando && sugerencias.length === 0) return null;

    return (
        <div
            className="absolute left-0 right-0 z-[100] mt-1 overflow-hidden rounded-[10px] border border-blue-100 bg-white shadow-xl dark:border-blue-900/40 dark:bg-slate-800"
            role="listbox"
            aria-label="Pasajeros de viajes anteriores"
        >
            <div className="flex items-center justify-between border-b border-slate-100 bg-blue-50 px-3 py-2 dark:border-slate-700 dark:bg-blue-950/30">
                <span className="flex items-center gap-1.5 text-[11px] font-bold uppercase tracking-wider text-blue-600 dark:text-blue-400">
                    <History className="h-3 w-3" aria-hidden="true" />
                    Pasajeros de viajes anteriores
                </span>
                <button
                    type="button"
                    onClick={onCerrar}
                    aria-label="Cerrar sugerencias"
                    className="text-slate-400 hover:text-slate-600 dark:hover:text-slate-300"
                >
                    <X className="h-3.5 w-3.5" />
                </button>
            </div>

            {cargando && (
                <div className="flex items-center gap-2 px-4 py-3 text-sm text-slate-500">
                    <Loader2 className="h-4 w-4 animate-spin text-blue-400" aria-hidden="true" />
                    Buscando...
                </div>
            )}

            {!cargando && sugerencias.length > 0 && (
                <div className="max-h-48 overflow-y-auto">
                    {sugerencias.map((sugerencia, index) => (
                        <button
                            key={`historico-${sugerencia.documentType}-${sugerencia.documentNumber}-${index}`}
                            type="button"
                            role="option"
                            // Fix deuda menor (2026-08-18): un role="option" dentro de un
                            // role="listbox" necesita aria-selected para que el lector de
                            // pantalla no lo marque como estado inválido. Este menú es
                            // "elegir y cerrar" (sin selección que persista en pantalla),
                            // por eso siempre va en false — nunca queda un ítem "marcado".
                            aria-selected={false}
                            onClick={() => onElegir(sugerencia)}
                            className="group w-full border-b border-slate-50 px-4 py-2.5 text-left transition-colors last:border-0 hover:bg-blue-50 dark:border-slate-700 dark:hover:bg-blue-900/30"
                        >
                            <div className="truncate text-sm font-semibold text-slate-900 group-hover:text-blue-600 dark:text-white dark:group-hover:text-blue-300">
                                {sugerencia.fullName}
                            </div>
                            <div className="text-[11px] text-slate-500 dark:text-slate-400">
                                {formatearSubtituloSugerencia(sugerencia)}
                            </div>
                        </button>
                    ))}
                </div>
            )}

            {!cargando && sugerencias.length === 0 && (
                <div className="px-4 py-3 text-sm text-slate-400">
                    Sin coincidencias en la base de pasajeros.
                </div>
            )}
        </div>
    );
}
