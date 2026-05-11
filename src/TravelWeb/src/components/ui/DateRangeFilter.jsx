import { useEffect, useMemo, useState } from "react";
import { Calendar } from "lucide-react";

// B1.15 Fase D'.B+ (2026-05-11): filtro de fechas reutilizable con presets.
//
// Props:
//   value: { from: string|null, to: string|null }  (formato "YYYY-MM-DD" o null)
//   onChange: ({ from, to }) => void
//   defaultPreset: "last90" (default) | "thisMonth" | "lastMonth" | "thisYear" | "last30" | "all"
//
// Comportamiento:
//   - Al montar, si value es vacio, aplica defaultPreset y notifica.
//   - Al cambiar preset, calcula from/to y notifica.
//   - Preset "Personalizado" muestra 2 inputs date.
//   - Preset "Todo el historial" envia {from: null, to: null} (el backend lo
//     interpreta como sin filtro).
//
// El backend de /api/movements y /api/reservas aceptan dateFrom/dateTo o
// createdFrom/createdTo en formato ISO. Pasamos el string "YYYY-MM-DD" tal cual.

const PRESET_OPTIONS = [
  { value: "last30", label: "Últimos 30 días" },
  { value: "last90", label: "Últimos 90 días" },
  { value: "thisMonth", label: "Este mes" },
  { value: "lastMonth", label: "Mes pasado" },
  { value: "thisYear", label: "Este año" },
  { value: "custom", label: "Personalizado" },
  { value: "all", label: "Todo el historial" },
];

function computeRange(preset) {
  const today = new Date();
  const toIso = (d) => d.toISOString().slice(0, 10);

  switch (preset) {
    case "last30": {
      const from = new Date(today);
      from.setDate(from.getDate() - 30);
      return { from: toIso(from), to: toIso(today) };
    }
    case "last90": {
      const from = new Date(today);
      from.setDate(from.getDate() - 90);
      return { from: toIso(from), to: toIso(today) };
    }
    case "thisMonth": {
      const from = new Date(today.getFullYear(), today.getMonth(), 1);
      return { from: toIso(from), to: toIso(today) };
    }
    case "lastMonth": {
      const from = new Date(today.getFullYear(), today.getMonth() - 1, 1);
      const to = new Date(today.getFullYear(), today.getMonth(), 0); // último día del mes pasado
      return { from: toIso(from), to: toIso(to) };
    }
    case "thisYear": {
      const from = new Date(today.getFullYear(), 0, 1);
      return { from: toIso(from), to: toIso(today) };
    }
    case "all": return { from: null, to: null };
    case "custom":
    default: return null;
  }
}

export default function DateRangeFilter({ value, onChange, defaultPreset = "last90" }) {
  const [preset, setPreset] = useState(defaultPreset);
  // Para custom: estado local de los inputs.
  const [customFrom, setCustomFrom] = useState(value?.from || "");
  const [customTo, setCustomTo] = useState(value?.to || "");
  const initialized = useMemo(() => Boolean(value?.from || value?.to), []); // eslint-disable-line react-hooks/exhaustive-deps

  // Inicializacion: si el caller no nos paso valores, aplicamos defaultPreset.
  useEffect(() => {
    if (initialized) return;
    const range = computeRange(defaultPreset);
    if (range && onChange) onChange(range);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const handlePresetChange = (newPreset) => {
    setPreset(newPreset);
    if (newPreset === "custom") return; // No aplica hasta que el user toque inputs.
    const range = computeRange(newPreset);
    if (range && onChange) {
      onChange(range);
      setCustomFrom(range.from || "");
      setCustomTo(range.to || "");
    }
  };

  const handleCustomChange = (key, val) => {
    if (key === "from") setCustomFrom(val);
    else setCustomTo(val);
    const next = {
      from: key === "from" ? val || null : customFrom || null,
      to: key === "to" ? val || null : customTo || null,
    };
    if (onChange) onChange(next);
  };

  return (
    <div className="flex flex-wrap items-center gap-2">
      <Calendar className="h-4 w-4 text-slate-400 flex-shrink-0" />
      <select
        value={preset}
        onChange={(event) => handlePresetChange(event.target.value)}
        className="rounded-lg border border-slate-300 dark:border-slate-600 dark:bg-slate-800 dark:text-white px-3 py-1.5 text-sm"
      >
        {PRESET_OPTIONS.map((option) => (
          <option key={option.value} value={option.value}>{option.label}</option>
        ))}
      </select>
      {preset === "custom" ? (
        <>
          <input
            type="date"
            value={customFrom}
            onChange={(event) => handleCustomChange("from", event.target.value)}
            className="rounded-lg border border-slate-300 dark:border-slate-600 dark:bg-slate-800 dark:text-white px-3 py-1.5 text-sm"
            aria-label="Desde"
          />
          <span className="text-slate-400 text-xs">→</span>
          <input
            type="date"
            value={customTo}
            onChange={(event) => handleCustomChange("to", event.target.value)}
            className="rounded-lg border border-slate-300 dark:border-slate-600 dark:bg-slate-800 dark:text-white px-3 py-1.5 text-sm"
            aria-label="Hasta"
          />
        </>
      ) : null}
    </div>
  );
}
