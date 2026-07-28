/**
 * Una fila de la lista de resguardos disponibles para restaurar (Zona peligrosa → "Volver
 * atrás"). Es un radio: se elige UN resguardo por vez, porque las acciones de abajo
 * (verificar, probar, restaurar) operan siempre sobre "el resguardo seleccionado".
 * Componente puramente de presentación, sin lógica de negocio.
 */
export function BackupListItem({ archivo, etiqueta, isSelected, onSelect, disabled }) {
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
            <span className="text-slate-700 dark:text-slate-200">{etiqueta}</span>
        </label>
    );
}
