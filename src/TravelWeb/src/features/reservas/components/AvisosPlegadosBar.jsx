import React, { useState } from 'react';
import { ChevronDown } from 'lucide-react';
import { formatearContadorAvisos } from '../avisosFicha';

/**
 * Barra que agrupa los avisos INFORMATIVOS de la ficha de reserva (spec UX
 * 2026-07-05, respuesta 5A: "arriba la foto, abajo solo lo que hay que hacer").
 * Se usa debajo de lo accionable (banner "con cambios" + candado) para que los
 * avisos que NO piden ninguna acción ahora mismo no compitan visualmente con eso.
 *
 * Reglas de la spec:
 *   - 0 avisos → no se muestra nada.
 *   - 1 solo aviso → se muestra DIRECTO, sin plegado (un "1 aviso más [Ver]" para
 *     un solo aviso es más fricción de clic que mostrarlo de una).
 *   - 2 o más avisos → arranca PLEGADO por defecto (no se persiste entre cargas)
 *     con una barra "N avisos más [Ver ▾]"; al abrir, se ven todos.
 *
 * @param {{ avisos: Array<{ key: string, node: React.ReactNode }> }} props
 */
export function AvisosPlegadosBar({ avisos }) {
    // Plegado por defecto en cada carga de la página — no hace falta persistirlo,
    // es más simple y el vendedor lo abre en dos clics si lo necesita.
    const [abierto, setAbierto] = useState(false);

    if (!avisos || avisos.length === 0) return null;

    if (avisos.length === 1) {
        return <>{avisos[0].node}</>;
    }

    return (
        <div className="space-y-2">
            <button
                type="button"
                onClick={() => setAbierto(previo => !previo)}
                aria-expanded={abierto}
                data-testid="avisos-plegados-toggle"
                className="flex w-full items-center justify-between gap-2 rounded-xl border border-amber-200 bg-amber-50 px-4 py-2 text-sm font-semibold text-amber-800 transition-colors hover:bg-amber-100 dark:border-amber-800/50 dark:bg-amber-950/20 dark:text-amber-300 dark:hover:bg-amber-950/40"
            >
                <span data-testid="avisos-plegados-contador">{formatearContadorAvisos(avisos.length)}</span>
                <span className="flex items-center gap-1 text-xs font-bold uppercase tracking-wide">
                    Ver
                    <ChevronDown
                        className={`h-4 w-4 transition-transform ${abierto ? 'rotate-180' : ''}`}
                        aria-hidden="true"
                    />
                </span>
            </button>

            {abierto && (
                <div className="space-y-2" data-testid="avisos-plegados-contenido">
                    {avisos.map(aviso => (
                        <div key={aviso.key}>{aviso.node}</div>
                    ))}
                </div>
            )}
        </div>
    );
}
