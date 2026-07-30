import { Badge } from "../../../components/ui/badge";

// ADR-052 (2026-07-29, "resguardos de versiones anteriores"): mismo trío de colores que usa
// el resto de esta pantalla (ámbar = acción/aviso, rosa = sin acción/alerta, gris = neutro).
// Es solo una clave visual: la decisión de QUÉ badge mostrar la toma
// `construirBadgeVersionResguardo` en dangerRestoreLogic.js, este componente no sabe nada
// de negocio, solo pinta lo que le llega.
const BADGE_VERSION_CLASSES = {
    ambar: "border-amber-300 bg-amber-50 text-amber-800 dark:border-amber-900/40 dark:bg-amber-950/20 dark:text-amber-300",
    rosa: "border-rose-300 bg-rose-50 text-rose-700 dark:border-rose-800 dark:bg-rose-950/30 dark:text-rose-300",
    gris: "border-slate-300 bg-slate-100 text-slate-600 dark:border-slate-600 dark:bg-slate-800 dark:text-slate-300",
};

/**
 * Una fila de la lista de resguardos disponibles para restaurar (Zona peligrosa → "Volver
 * atrás"). Es un radio: se elige UN resguardo por vez, porque las acciones de abajo
 * (verificar, probar, restaurar) operan siempre sobre "el resguardo seleccionado".
 * Componente puramente de presentación, sin lógica de negocio.
 *
 * `badge` (ADR-052, 2026-07-29): opcional, viene de `construirBadgeVersionResguardo`. Marca
 * si el resguardo es de una versión distinta a la de hoy — SOLO informativo, la fila NUNCA
 * se atenúa por esto (decisión firmada: ningún estado apaga ni oscurece nada, ni siquiera
 * "versión más nueva").
 */
export function BackupListItem({ archivo, etiqueta, badge, isSelected, onSelect, disabled }) {
    return (
        <label
            className={`flex items-center gap-2 rounded-lg border p-3 text-sm transition-colors ${
                isSelected
                    ? "border-indigo-300 bg-indigo-50 dark:border-indigo-800 dark:bg-indigo-950/30"
                    : "border-slate-200 bg-white dark:border-slate-700 dark:bg-slate-900"
            } ${disabled ? "cursor-not-allowed opacity-60" : "cursor-pointer"}`}
        >
            <input
                type="radio"
                name="danger-restore-backup"
                data-testid={`danger-backup-${archivo}`}
                checked={isSelected}
                disabled={disabled}
                onChange={() => onSelect(archivo)}
                className="h-4 w-4 border-slate-300 text-indigo-600 focus:ring-indigo-500"
            />
            <span className="flex-1 text-slate-700 dark:text-slate-200">{etiqueta}</span>
            {badge && (
                <Badge
                    variant="outline"
                    data-testid={`danger-backup-${archivo}-badge`}
                    // data-version (fix de review, punto 4): expone el color semántico
                    // ("ambar"/"rosa"/"gris") como atributo aparte, para que QA pueda afirmar
                    // "es ámbar" sin tener que leer clases de Tailwind (frágil ante cambios de
                    // estilo) — el data-testid de arriba sigue igual, esto se suma, no reemplaza.
                    data-version={badge.color}
                    className={`${BADGE_VERSION_CLASSES[badge.color]} shrink-0`}
                >
                    {badge.texto}
                </Badge>
            )}
        </label>
    );
}
