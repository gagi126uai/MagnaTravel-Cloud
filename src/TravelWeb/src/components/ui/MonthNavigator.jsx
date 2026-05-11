import { Calendar, ChevronLeft, ChevronRight } from "lucide-react";

// MonthNavigator — filtro de mes canónico del sistema.
//
// Patron: navegacion mensual con chevron prev/next + label "Mayo 2026".
// Extraido de PaymentsInvoicingPage y replicado en Cobranza y Facturación
// para mantener sinergia visual entre pantallas del módulo financiero.
//
// Props:
//   month:    Date | null — primer dia del mes activo (ej. new Date(2026, 4, 1)).
//             Si es null, el componente muestra "Todo el historial" y los
//             chevrons navegan hacia el mes actual como punto de partida.
//             El caller interpreta month=null como "no enviar filtros de fecha".
//   onChange: (date: Date | null) => void — recibe el primer dia del mes
//             seleccionado, o null cuando el usuario activa "todo el historial".
//   disabled: boolean (opcional) — cuando true, deshabilita prev/next/Hoy.
//             Util para evitar race conditions durante fetch en curso.
//
// Uso:
//   const [month, setMonth] = useState(() => {
//     const now = new Date();
//     return new Date(now.getFullYear(), now.getMonth(), 1);
//   });
//
//   <MonthNavigator month={month} onChange={setMonth} disabled={loading} />
//
// Para convertir el mes a params ISO para el backend, usa monthToBounds:
//   const { from, to } = monthToBounds(month);
//   (no llamar con month=null — validar antes)

/**
 * Convierte un Date (primer día del mes) a { from, to } ISO "YYYY-MM-DD".
 * Usa aritmética local — no pasa por UTC — para evitar el bug de timezone
 * donde toISOString() en GMT-3 serializa el 1 de mayo como "2026-04-30".
 *
 * Solo llamar cuando month != null.
 *
 * @param {Date} month — primer día del mes (ej. new Date(2026, 4, 1))
 * @returns {{ from: string, to: string }} — rango ISO YYYY-MM-DD
 *
 * Casos esperados (sin setup Vitest — validar manualmente o con QA):
 *   monthToBounds(new Date(2026, 4,  1)) → { from: "2026-05-01", to: "2026-05-31" }  // mayo 31 días
 *   monthToBounds(new Date(2026, 1, 15)) → { from: "2026-02-01", to: "2026-02-28" }  // feb no bisiesto
 *   monthToBounds(new Date(2024, 1, 15)) → { from: "2024-02-01", to: "2024-02-29" }  // feb bisiesto
 *   monthToBounds(new Date(2025, 11, 25)) → { from: "2025-12-01", to: "2025-12-31" } // diciembre fin de año
 */
const fmt = (d) =>
  `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`;

export function monthToBounds(month) {
  const from = new Date(month.getFullYear(), month.getMonth(), 1);
  const to = new Date(month.getFullYear(), month.getMonth() + 1, 0);
  return { from: fmt(from), to: fmt(to) };
}

/** Devuelve true si `date` pertenece al mes/año actual. */
function isCurrentMonth(date) {
  const now = new Date();
  return (
    date.getFullYear() === now.getFullYear() &&
    date.getMonth() === now.getMonth()
  );
}

export function MonthNavigator({ month, onChange, disabled = false }) {
  const now = new Date();

  // Cuando month es null mostramos "Todo el historial" y usamos el mes actual
  // como base para navegar si el usuario aprieta prev/next.
  const base = month ?? new Date(now.getFullYear(), now.getMonth(), 1);

  const monthName = month
    ? month.toLocaleDateString("es-AR", { month: "long", year: "numeric" })
    : "Todo el historial";

  const showTodayButton = month !== null && !isCurrentMonth(month);

  const handlePrev = () => {
    if (disabled) return;
    onChange(new Date(base.getFullYear(), base.getMonth() - 1, 1));
  };

  const handleNext = () => {
    if (disabled) return;
    onChange(new Date(base.getFullYear(), base.getMonth() + 1, 1));
  };

  const handleToday = () => {
    if (disabled) return;
    onChange(new Date(now.getFullYear(), now.getMonth(), 1));
  };

  const btnBase =
    "rounded p-1.5 text-slate-500 transition-colors dark:text-slate-400";
  const btnEnabled =
    "hover:bg-slate-100 hover:text-slate-700 dark:hover:bg-slate-700 dark:hover:text-white";
  const btnDisabled = "opacity-50 cursor-not-allowed";

  return (
    <div className="flex w-full items-center justify-between gap-1 rounded-lg border border-slate-200 bg-white p-1 dark:border-slate-700 dark:bg-slate-800/50 sm:w-auto sm:justify-center shadow-sm">
      <button
        type="button"
        onClick={handlePrev}
        disabled={disabled}
        className={`${btnBase} ${disabled ? btnDisabled : btnEnabled}`}
        title="Mes anterior"
        aria-label="Mes anterior"
        data-testid="month-nav-prev"
      >
        <ChevronLeft className="w-4 h-4" />
      </button>
      <div className="flex items-center gap-1.5 px-1 sm:px-2">
        <Calendar className="w-3.5 h-3.5 text-indigo-500" />
        <span
          className="min-w-[110px] text-center text-sm font-medium capitalize text-slate-700 dark:text-slate-200"
          data-testid="month-nav-label"
        >
          {monthName}
        </span>
        {showTodayButton && (
          <button
            type="button"
            onClick={handleToday}
            disabled={disabled}
            className={`text-xs text-indigo-600 dark:text-indigo-400 hover:underline ${disabled ? "opacity-50 cursor-not-allowed" : ""}`}
            aria-label="Volver al mes actual"
            data-testid="month-nav-today"
          >
            Hoy
          </button>
        )}
      </div>
      <button
        type="button"
        onClick={handleNext}
        disabled={disabled}
        className={`${btnBase} ${disabled ? btnDisabled : btnEnabled}`}
        title="Mes siguiente"
        aria-label="Mes siguiente"
        data-testid="month-nav-next"
      >
        <ChevronRight className="w-4 h-4" />
      </button>
    </div>
  );
}
