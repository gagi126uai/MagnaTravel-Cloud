import React from 'react';

/**
 * Fila de aviso de UNA sola línea para la ficha de reserva (regla P11, Tanda 2 del
 * rediseño de Reservas, 2026-08-03). Reemplaza los bloques amarillos apilados de
 * antes por tres variantes fijas:
 *
 *   - "accion": ámbar. El aviso te pide hacer algo YA (ej. el candado de una reserva
 *     Confirmada). Texto a la izquierda, UN botón a la derecha.
 *   - "info": gris. El aviso solo informa, no hace falta actuar ahora (ej. "1 servicio
 *     sin confirmar"). Puede traer un botón chico ("Ver") que lleva a la parte de la
 *     ficha donde está el detalle — es opcional.
 *   - "terminal": rojo. La reserva quedó sin efecto (Anulada) — no hay botón porque no
 *     hay ninguna acción posible desde acá.
 *
 * Este componente NO decide qué aviso corresponde ni cuándo mostrarlo — esa decisión
 * sigue viviendo en avisosFicha.js (fuente única de "qué avisos hay que mostrar").
 * Acá solo se pinta la fila con el texto y el botón que le pasan.
 *
 * `botonDeshabilitado` (ADR-053, 2026-08-13): opcional, para el caso de un botón que
 * dispara una llamada al backend (ej. "Volver a calcular las fechas") — evita el doble
 * click mientras la llamada está en curso. Por defecto false: la mayoría de los avisos
 * de esta fila no lo necesitan.
 */
export function AvisoFila({ variante = 'info', children, textoBoton, onClickBoton, dataTestId, botonDeshabilitado = false }) {
    const estilosPorVariante = {
        accion: 'border-amber-200 bg-amber-50 text-amber-900 dark:border-amber-800/50 dark:bg-amber-950/30 dark:text-amber-200',
        info: 'border-slate-200 bg-slate-50 text-slate-600 dark:border-slate-700 dark:bg-slate-800/40 dark:text-slate-300',
        terminal: 'border-rose-200 bg-rose-50 text-rose-800 dark:border-rose-900/40 dark:bg-rose-950/20 dark:text-rose-300',
    };
    const estilosBotonPorVariante = {
        accion: 'border-amber-300 bg-white text-amber-800 hover:bg-amber-100 dark:border-amber-700 dark:bg-slate-800 dark:text-amber-200 dark:hover:bg-amber-900/30',
        info: 'border-slate-300 bg-white text-slate-600 hover:bg-slate-100 dark:border-slate-600 dark:bg-slate-800 dark:text-slate-300 dark:hover:bg-slate-700',
    };

    return (
        <div
            className={`flex items-center justify-between gap-3 rounded-xl border px-4 py-2 text-sm ${estilosPorVariante[variante] || estilosPorVariante.info}`}
            data-testid={dataTestId}
            role="status"
        >
            <span className="min-w-0">{children}</span>
            {/* El botón es opcional a propósito: la variante "terminal" nunca lo trae
                (no hay ninguna acción posible sobre una reserva sin efecto), y algunos
                avisos "info" tampoco tienen a dónde llevar. */}
            {textoBoton && onClickBoton && (
                <button
                    type="button"
                    onClick={onClickBoton}
                    disabled={botonDeshabilitado}
                    className={`flex-shrink-0 rounded-lg border px-3 py-1 text-xs font-bold transition-colors disabled:opacity-60 disabled:cursor-not-allowed ${estilosBotonPorVariante[variante] || estilosBotonPorVariante.info}`}
                >
                    {textoBoton}
                </button>
            )}
        </div>
    );
}
