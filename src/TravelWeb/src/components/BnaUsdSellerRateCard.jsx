import { Clock3, Landmark, RefreshCw } from "lucide-react";

const formatRate = (value) =>
  new Intl.NumberFormat("es-AR", {
    style: "currency",
    currency: "ARS",
    minimumFractionDigits: 2,
    maximumFractionDigits: 2,
  }).format(value || 0);

const formatFetchedAt = (value) => {
  if (!value) return "-";
  return new Date(value).toLocaleString("es-AR", {
    day: "2-digit",
    month: "2-digit",
    year: "numeric",
    hour: "2-digit",
    minute: "2-digit",
  });
};

export function BnaUsdSellerRateCard({ rate }) {
  if (!rate) {
    return (
      <div className="overflow-hidden rounded-[1.5rem] border border-slate-200 bg-white p-4 shadow-sm dark:border-slate-800 dark:bg-slate-900">
        <div className="flex items-start justify-between gap-3">
          <div>
            <p className="text-[10px] font-black uppercase tracking-[0.24em] text-slate-400">Referencia operativa</p>
            <h3 className="mt-1 text-base font-black text-slate-900 dark:text-white">BNA billetes</h3>
            <p className="mt-1 text-xs text-slate-500 dark:text-slate-400">No hay cotizacion oficial disponible.</p>
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
      <div className="absolute inset-y-0 right-0 w-28 bg-gradient-to-l from-emerald-100/60 to-transparent dark:from-emerald-900/10" />
      <div className="relative flex flex-col gap-4">
        <div className="flex flex-col gap-3 lg:flex-row lg:items-center lg:justify-between">
          <div className="flex items-center gap-3">
            <div className="rounded-xl bg-emerald-100 p-2.5 text-emerald-700 dark:bg-emerald-900/20 dark:text-emerald-300">
              <Landmark className="h-4 w-4" />
            </div>
            <div>
              <p className="text-[10px] font-black uppercase tracking-[0.24em] text-slate-400">Referencia operativa</p>
              <h3 className="text-base font-black text-slate-900 dark:text-white">BNA billetes vendedor</h3>
            </div>
          </div>
          <div className={`inline-flex items-center gap-2 self-start rounded-full px-3 py-1 text-[10px] font-black uppercase tracking-[0.16em] ${rate.isStale ? "bg-amber-100 text-amber-700 dark:bg-amber-900/20 dark:text-amber-300" : "bg-emerald-100 text-emerald-700 dark:bg-emerald-900/20 dark:text-emerald-300"}`}>
            <RefreshCw className="h-3 w-3" />
            {rate.isStale ? "Dato desactualizado" : "Actualizado"}
          </div>
        </div>

        <div className="grid gap-2 md:grid-cols-3">
            <RateTile label="Dolar vendedor" value={rate.value} />
            <RateTile label="Euro vendedor" value={rate.euroValue} />
            <RateTile label="Real vendedor" value={rate.realValue} note="cada 100 unidades" />
          </div>

        <div className="grid gap-2 md:grid-cols-3">
          <InfoTile label="Fecha publicada" value={rate.publishedDate || "-"} />
          <InfoTile label="Hora publicada" value={rate.publishedTime || "-"} />
          <InfoTile label="Fuente" value="Banco Nacion" />
        </div>
      </div>

      <div className="relative mt-3 flex items-center gap-2 text-[11px] text-slate-500 dark:text-slate-400">
        <Clock3 className="h-3 w-3" />
        Ultima consulta: {formatFetchedAt(rate.fetchedAt)}
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

function RateTile({ label, value, note }) {
  return (
    <div className="rounded-xl border border-slate-200/80 bg-slate-50/80 px-3 py-3 dark:border-slate-800 dark:bg-slate-950/40">
      <div className="text-[10px] font-black uppercase tracking-[0.22em] text-slate-400">{label}</div>
      <div className="mt-1 text-lg font-black text-slate-900 dark:text-white">{formatRate(value)}</div>
      {note && <div className="mt-0.5 text-[10px] font-medium text-slate-500 dark:text-slate-400">{note}</div>}
    </div>
  );
}
