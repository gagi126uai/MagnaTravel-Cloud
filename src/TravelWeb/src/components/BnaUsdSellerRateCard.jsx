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
      <div className="overflow-hidden rounded-[2rem] border border-slate-200 bg-white p-6 shadow-sm dark:border-slate-800 dark:bg-slate-900">
        <div className="flex items-start justify-between gap-4">
          <div>
            <p className="text-xs font-black uppercase tracking-[0.28em] text-slate-400">Referencia operativa</p>
            <h3 className="mt-2 text-xl font-black text-slate-900 dark:text-white">Dolar BNA vendedor</h3>
            <p className="mt-2 text-sm text-slate-500 dark:text-slate-400">No hay una cotizacion oficial disponible en este momento.</p>
          </div>
          <div className="rounded-2xl bg-slate-100 p-3 text-slate-500 dark:bg-slate-800 dark:text-slate-300">
            <Landmark className="h-5 w-5" />
          </div>
        </div>
      </div>
    );
  }

  return (
    <div className="relative overflow-hidden rounded-[2rem] border border-slate-200 bg-white p-6 shadow-sm dark:border-slate-800 dark:bg-slate-900">
      <div className="absolute inset-y-0 right-0 w-40 bg-gradient-to-l from-emerald-100/70 to-transparent dark:from-emerald-900/10" />
      <div className="relative flex flex-col gap-6 lg:flex-row lg:items-end lg:justify-between">
        <div className="space-y-3">
          <div className="flex items-center gap-3">
            <div className="rounded-2xl bg-emerald-100 p-3 text-emerald-700 dark:bg-emerald-900/20 dark:text-emerald-300">
              <Landmark className="h-5 w-5" />
            </div>
            <div>
              <p className="text-xs font-black uppercase tracking-[0.28em] text-slate-400">Referencia operativa</p>
              <h3 className="text-xl font-black text-slate-900 dark:text-white">Dolar BNA vendedor</h3>
            </div>
          </div>
          <div className="text-4xl font-black tracking-tight text-slate-900 dark:text-white">{formatRate(rate.value)}</div>
          <div className={`inline-flex items-center gap-2 rounded-full px-3 py-1 text-[11px] font-black uppercase tracking-[0.18em] ${rate.isStale ? "bg-amber-100 text-amber-700 dark:bg-amber-900/20 dark:text-amber-300" : "bg-emerald-100 text-emerald-700 dark:bg-emerald-900/20 dark:text-emerald-300"}`}>
            <RefreshCw className="h-3.5 w-3.5" />
            {rate.isStale ? "Dato desactualizado" : "Actualizado"}
          </div>
        </div>

        <div className="grid gap-3 sm:grid-cols-3">
          <InfoTile label="Fecha publicada" value={rate.publishedDate || "-"} />
          <InfoTile label="Hora publicada" value={rate.publishedTime || "-"} />
          <InfoTile label="Fuente" value="Banco Nacion" />
        </div>
      </div>

      <div className="relative mt-5 flex items-center gap-2 text-xs text-slate-500 dark:text-slate-400">
        <Clock3 className="h-3.5 w-3.5" />
        Ultima consulta: {formatFetchedAt(rate.fetchedAt)}
      </div>
    </div>
  );
}

function InfoTile({ label, value }) {
  return (
    <div className="rounded-2xl border border-slate-200/80 bg-slate-50/80 px-4 py-3 dark:border-slate-800 dark:bg-slate-950/40">
      <div className="text-[10px] font-black uppercase tracking-[0.22em] text-slate-400">{label}</div>
      <div className="mt-1 text-sm font-bold text-slate-900 dark:text-white">{value}</div>
    </div>
  );
}
