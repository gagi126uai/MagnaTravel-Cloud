import React, { useEffect, useRef, useState } from "react";
import { ChevronDown, RefreshCw } from "lucide-react";
import { api } from "../api";
import { formatCurrency } from "../lib/utils";
import {
    ESPERA_REFRESCO_DOLAR_MS,
    ESTADO_ACTUALIZAR_DOLAR,
    botonActualizarDolarDeshabilitado,
    faltaDatoDelDolar,
    formatearFechaDolarTira,
    hayOtrasMonedasParaMostrar,
    textoBotonActualizarDolar,
} from "../lib/dolarTiraDashboardLogic";

// Copia CARÁCTER POR CARÁCTER del contenedor de ReservaKPIs.jsx:19 (molde firmado, guía P2=A).
// El review 2026-08-05 bloqueó un desvío acá (items-center/gap-3/py-2.5): no volver a desviarse.
const CLASES_CONTENEDOR =
    "flex flex-wrap items-baseline gap-x-6 gap-y-2 rounded-xl border border-slate-200 bg-slate-50 px-4 py-3 dark:border-slate-800 dark:bg-slate-900/40";
const CLASES_ROTULO = "text-[11px] font-bold uppercase tracking-wide text-slate-400 dark:text-slate-500";

/**
 * Tira fina de una línea con el dólar Banco Nación, arriba de los KPIs del
 * dashboard. Reemplaza a la tarjeta grande `BnaUsdSellerRateCard` (desaprobada
 * por el dueño el 2026-08-05 — "es feo"): se monta TAL CUAL en `AdminDashboard.jsx`
 * y `AgentDashboard.jsx` (P5=B de la spec: la misma tira para admin y vendedor, acá
 * no hay ningún número fiscal que ocultarle a nadie).
 *
 * Mismo molde visual que la tira firmada del listado de Reservas (`ReservaKPIs.jsx`,
 * guía UX 2026-08-03 P2=A): rótulo chiquito en mayúsculas + número grande, SIN
 * badge de color (P11=A: lo que solo informa va gris y en una línea; el color se
 * reserva para lo que pide hacer algo, y un dato de referencia no pide nada).
 *
 * Un solo dólar (P2=B, decidido por investigación del rubro ERP): el "para
 * facturar" no se pinta acá — ya vive precargado en las pantallas de facturar; el
 * dato sigue viajando en el DTO del dashboard, simplemente no se usa en esta tira.
 *
 * Decisiones completas: docs/ux/specs/2026-08-06-dolar-en-dashboard.md (P1..P6,
 * firmada 2026-08-05).
 *
 * Botón "actualizar" (2026-08-05, TARDE — orden textual del dueño mirando el dashboard EN VIVO,
 * pisa la línea "NO botón de refrescar ni link" que dice la spec de arriba, firmada esa misma
 * mañana): el scraper del BNA venía roto desde el 8/7 y su recuerdo viejo le ganaba siempre al
 * dato fresco. El botón es fantasma gris, mismo lenguaje que el resto de la tira (P11=A: no pide
 * ninguna decisión, no lleva color) — pendiente: trasladar esta decisión a una adenda formal del
 * doc de specs (ver informe de la tanda).
 *
 * @param {{ value?: number|null }|null} rate
 * @param {() => void} [onRefrescar] callback para volver a pedir `/reports/dashboard` — lo maneja
 *   la página dueña del estado (`AdminDashboard.jsx` / `AgentDashboard.jsx`), este componente no
 *   guarda el dashboard entero.
 */
export function DolarBnaTira({ rate, onRefrescar }) {
    // Estado propio SOLO para abrir/cerrar "otras monedas" — no se persiste entre
    // cargas de la página, como el mismo patrón de AvisosPlegadosBar.jsx.
    const [otrasMonedasAbierto, setOtrasMonedasAbierto] = useState(false);
    const [estadoActualizar, setEstadoActualizar] = useState(ESTADO_ACTUALIZAR_DOLAR.QUIETO);
    const timeoutRefrescoRef = useRef(null);

    // Limpieza si el componente se desmonta con un refresco en curso (ej. el usuario navega a otra
    // pantalla apenas apretó el botón): evita el warning de React "setState en un componente
    // desmontado" y una llamada a onRefrescar que ya nadie necesita.
    useEffect(() => () => {
        if (timeoutRefrescoRef.current) clearTimeout(timeoutRefrescoRef.current);
    }, []);

    const handleActualizarClick = async () => {
        if (botonActualizarDolarDeshabilitado(estadoActualizar)) return;

        setEstadoActualizar(ESTADO_ACTUALIZAR_DOLAR.BUSCANDO);
        try {
            await api.post("/exchange-rates/refresh");
        } catch {
            // Mismo patrón que useTipoCambioSugerido: si el pedido falla (red caída, sin permiso,
            // lo que sea), la tira queda EXACTAMENTE como estaba — sin toast rojo, no es una falla
            // que el usuario tenga que resolver. No tiene sentido esperar ni refrescar el dashboard
            // si ni siquiera se pudo pedir la actualización.
            setEstadoActualizar(ESTADO_ACTUALIZAR_DOLAR.QUIETO);
            return;
        }

        // El backend encoló el job en background y ya respondió (fire-and-forget): le damos unos
        // segundos de margen y volvemos a pedir el dashboard UNA sola vez, no polling infinito.
        timeoutRefrescoRef.current = setTimeout(() => {
            setEstadoActualizar(ESTADO_ACTUALIZAR_DOLAR.QUIETO);
            onRefrescar?.();
        }, ESPERA_REFRESCO_DOLAR_MS);
    };

    const botonActualizar = (
        <button
            type="button"
            onClick={handleActualizarClick}
            disabled={botonActualizarDolarDeshabilitado(estadoActualizar)}
            data-testid="dolar-tira-actualizar"
            className="flex items-center gap-1 text-[11px] font-bold text-slate-400 transition-colors hover:text-slate-600 disabled:cursor-not-allowed disabled:opacity-70 dark:text-slate-500 dark:hover:text-slate-300"
        >
            <RefreshCw className="h-3 w-3" aria-hidden="true" />
            {textoBotonActualizarDolar(estadoActualizar)}
        </button>
    );

    if (faltaDatoDelDolar(rate)) {
        return (
            <div className={CLASES_CONTENEDOR} data-testid="dolar-tira">
                <span className={CLASES_ROTULO}>DÓLAR BANCO NACIÓN</span>
                <span className="text-sm font-medium text-slate-400 dark:text-slate-500">
                    sin dato hoy
                </span>
                <span className="ml-auto">{botonActualizar}</span>
            </div>
        );
    }

    const mostrarOtrasMonedas = hayOtrasMonedasParaMostrar(rate);
    const textoFecha = formatearFechaDolarTira(rate.publishedDate, rate.isStale);

    return (
        <div className={CLASES_CONTENEDOR} data-testid="dolar-tira">
            <span className={CLASES_ROTULO}>DÓLAR BANCO NACIÓN</span>
            <span className="text-base font-extrabold text-slate-900 dark:text-white">
                {formatCurrency(rate.value, "ARS")}
            </span>

            {mostrarOtrasMonedas && (
                <div className="relative">
                    <button
                        type="button"
                        onClick={() => setOtrasMonedasAbierto((previo) => !previo)}
                        aria-expanded={otrasMonedasAbierto}
                        aria-controls="dolar-tira-otras-monedas"
                        data-testid="dolar-tira-otras-monedas-toggle"
                        className="flex items-center gap-1 text-xs font-bold text-slate-400 transition-colors hover:text-slate-600 dark:text-slate-500 dark:hover:text-slate-300"
                    >
                        otras monedas
                        <ChevronDown
                            className={`h-3 w-3 transition-transform ${otrasMonedasAbierto ? "rotate-180" : ""}`}
                            aria-hidden="true"
                        />
                    </button>

                    {otrasMonedasAbierto && (
                        <div
                            id="dolar-tira-otras-monedas"
                            data-testid="dolar-tira-otras-monedas-contenido"
                            className="absolute left-0 top-full z-10 mt-1 min-w-[9rem] space-y-1 rounded-lg border border-slate-200 bg-white p-2.5 text-xs shadow-md dark:border-slate-700 dark:bg-slate-900"
                        >
                            {rate.euroValue != null && (
                                <div className="flex items-center justify-between gap-3">
                                    <span className="font-bold uppercase text-slate-400">Euro</span>
                                    <span className="font-semibold text-slate-700 dark:text-slate-200">
                                        {formatCurrency(rate.euroValue, "ARS")}
                                    </span>
                                </div>
                            )}
                            {rate.realValue != null && (
                                <div className="flex items-center justify-between gap-3">
                                    <span className="font-bold uppercase text-slate-400">Real</span>
                                    <span className="font-semibold text-slate-700 dark:text-slate-200">
                                        {formatCurrency(rate.realValue, "ARS")}
                                    </span>
                                </div>
                            )}
                        </div>
                    )}
                </div>
            )}

            <div className="ml-auto flex items-center gap-2">
                {textoFecha && (
                    <span className="text-[11px] text-slate-400 dark:text-slate-500">{textoFecha}</span>
                )}
                {botonActualizar}
            </div>
        </div>
    );
}
