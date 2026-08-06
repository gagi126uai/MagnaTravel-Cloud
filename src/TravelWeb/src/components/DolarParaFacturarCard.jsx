import { AlertTriangle, Clock3, Landmark } from "lucide-react";

const formatRate = (value) =>
  new Intl.NumberFormat("es-AR", {
    style: "currency",
    currency: "ARS",
    minimumFractionDigits: 2,
    maximumFractionDigits: 2,
  }).format(value || 0);

// La fecha llega como DateOnly serializado ("2026-08-04"): se arma "Al 04/08" a mano, sin pasar por
// Date() ni por un huso horario — es una fecha de calendario pura, no un instante.
const formatRateDate = (isoDate) => {
  if (!isoDate) return "-";
  const [year, month, day] = isoDate.split("-");
  if (!year || !month || !day) return "-";
  return `Al ${day}/${month}`;
};

// ADR-011 (enmienda 2026-08-05, decision firmada del dueño): tarjeta 2, hermana de
// BnaUsdSellerRateCard, mismo molde visual. A diferencia de esa (que SOLO muestra datos reales),
// esta muestra EXACTAMENTE el numero que la pantalla de facturar va a sugerir ahora mismo — en
// homologacion eso es el dolar de práctica de ARCA, y esta tarjeta lo avisa con el badge ambar en
// vez de esconderlo.
const TITULO = "Dólar para facturar (ARCA)";
const AYUDA = "El que ARCA acepta en la factura. Se actualiza solo.";

export function DolarParaFacturarCard({ dolar }) {
  if (!dolar) {
    return (
      <div className="overflow-hidden rounded-[1.5rem] border border-slate-200 bg-white p-4 shadow-sm dark:border-slate-800 dark:bg-slate-900">
        <div className="flex items-start justify-between gap-3">
          <div>
            <p className="text-[10px] font-black uppercase tracking-[0.24em] text-slate-400">Referencia operativa</p>
            <h3 className="mt-1 text-base font-black text-slate-900 dark:text-white">{TITULO}</h3>
            <p className="mt-1 text-xs text-slate-500 dark:text-slate-400">No hay tipo de cambio de ARCA disponible.</p>
          </div>
          <div className="rounded-xl bg-slate-100 p-2.5 text-slate-500 dark:bg-slate-800 dark:text-slate-300">
            <Landmark className="h-4 w-4" />
          </div>
        </div>
      </div>
    );
  }

  return (
    <div className="relative overflow-hidden rounded-[1.5rem] border border-slate-200 bg-white p-4 shadow-sm dark:border-slate-800 dark:bg-slate-900">
      <div className="absolute inset-y-0 right-0 w-28 bg-gradient-to-l from-indigo-100/60 to-transparent dark:from-indigo-900/10" />
      <div className="relative flex flex-col gap-4">
        <div className="flex flex-col gap-3 lg:flex-row lg:items-center lg:justify-between">
          <div className="flex items-center gap-3">
            <div className="rounded-xl bg-indigo-100 p-2.5 text-indigo-700 dark:bg-indigo-900/20 dark:text-indigo-300">
              <Landmark className="h-4 w-4" />
            </div>
            <div>
              <p className="text-[10px] font-black uppercase tracking-[0.24em] text-slate-400">Referencia operativa</p>
              <h3 className="text-base font-black text-slate-900 dark:text-white">{TITULO}</h3>
              <p className="mt-0.5 text-xs text-slate-500 dark:text-slate-400">{AYUDA}</p>
            </div>
          </div>
          {dolar.esDePrueba && (
            <div className="inline-flex items-center gap-2 self-start rounded-full bg-amber-100 px-3 py-1 text-[10px] font-black uppercase tracking-[0.16em] text-amber-700 dark:bg-amber-900/20 dark:text-amber-300">
              <AlertTriangle className="h-3 w-3" />
              Dólar de prueba — no es el real
            </div>
          )}
        </div>

        <div className="grid gap-2 md:grid-cols-3">
          <RateTile label="Tipo de cambio" value={dolar.value} />
          <InfoTile label="Fecha" value={formatRateDate(dolar.rateDate)} />
        </div>
      </div>

      <div className="relative mt-3 flex items-center gap-2 text-[11px] text-slate-500 dark:text-slate-400">
        <Clock3 className="h-3 w-3" />
        Se actualiza solo, todos los días.
      </div>
    </div>
  );
}

function InfoTile({ label, value }) {
  return (
    <div className="rounded-xl border border-slate-200/80 bg-slate-50/80 px-3 py-2.5 dark:border-slate-800 dark:bg-slate-950/40">
      <div className="text-[10px] font-black uppercase tracking-[0.22em] text-slate-400">{label}</div>
      <div className="mt-1 text-xs font-bold text-slate-900 dark:text-white">{value}</div>
    </div>
  );
}

function RateTile({ label, value }) {
  return (
    <div className="rounded-xl border border-slate-200/80 bg-slate-50/80 px-3 py-3 dark:border-slate-800 dark:bg-slate-950/40">
      <div className="text-[10px] font-black uppercase tracking-[0.22em] text-slate-400">{label}</div>
      <div className="mt-1 text-lg font-black text-slate-900 dark:text-white">{formatRate(value)}</div>
    </div>
  );
}
