import { ArrowLeft, Copy, Eye, Loader2, Rocket, Save } from "lucide-react";
import { Button } from "../../../../components/ui/button";
import { formatLongDate, formatMoney } from "../../lib/publicationUtils";

const inputClass =
  "w-full rounded-md border border-slate-300 bg-white px-3 py-2 text-sm text-slate-900 outline-none transition focus:border-indigo-500 focus:ring-2 focus:ring-indigo-500/20 dark:border-slate-700 dark:bg-slate-950 dark:text-white";

export function DestinationEditorSidebar({
  canEdit,
  canPublish,
  saving,
  form,
  publicationState,
  nextDepartureDate,
  fromPrice,
  countryOverrideActive,
  onBack,
  onSave,
  onDisplayOrderChange,
  onPreview,
  onCopy,
  onPublish,
  onUnpublish,
}) {
  return (
    <aside className="space-y-4 xl:sticky xl:top-6">
      <section className="rounded-lg border border-slate-200 bg-white p-4 shadow-sm dark:border-slate-800 dark:bg-slate-900/50">
        <p className="text-xs font-semibold uppercase tracking-[0.18em] text-slate-500 dark:text-slate-400">Guardar</p>
        <div className="mt-3 space-y-2">
          {canEdit ? (
            <Button type="button" onClick={onSave} disabled={saving} className="w-full gap-2">
              {saving ? <Loader2 className="h-4 w-4 animate-spin" /> : <Save className="h-4 w-4" />}
              {form.publicId ? "Guardar cambios" : "Guardar destino"}
            </Button>
          ) : null}
          <Button type="button" variant="outline" onClick={onBack} className="w-full gap-2">
            <ArrowLeft className="h-4 w-4" />
            Volver a destinos
          </Button>
        </div>
      </section>

      <section className="rounded-lg border border-slate-200 bg-white p-4 shadow-sm dark:border-slate-800 dark:bg-slate-900/50">
        <p className="text-xs font-semibold uppercase tracking-[0.18em] text-slate-500 dark:text-slate-400">Resumen</p>
        <div className="mt-3 space-y-4">
          <div>
            <p className="text-sm font-medium text-slate-700 dark:text-slate-200">Estado</p>
            <span className={`mt-2 inline-flex rounded-md px-2.5 py-1 text-xs font-semibold ${publicationToneMap[publicationState.tone] || publicationToneMap.slate}`}>
              {publicationState.label}
            </span>
          </div>

          <SidebarField label="Pais" value={form.countryName || "-"} />
          <SidebarField label="Pais en sitio" value={countryOverrideActive ? "Oculto" : "Visible"} />

          <label className="block space-y-2">
            <span className="text-sm font-medium text-slate-700 dark:text-slate-200">Orden de aparicion</span>
            <input
              type="number"
              min="0"
              value={form.displayOrder}
              onChange={(event) => onDisplayOrderChange(event.target.value)}
              className={inputClass}
              disabled={!canEdit}
            />
          </label>

          <SidebarField label="Proxima salida" value={formatLongDate(nextDepartureDate)} />
          <SidebarField
            label="Precio desde"
            value={fromPrice ? formatMoney(fromPrice.salePrice, fromPrice.currency) : "-"}
          />
        </div>
      </section>

      <section className="rounded-lg border border-slate-200 bg-white p-4 shadow-sm dark:border-slate-800 dark:bg-slate-900/50">
        <p className="text-xs font-semibold uppercase tracking-[0.18em] text-slate-500 dark:text-slate-400">Publicacion</p>

        {!form.publicId ? (
          <p className="mt-3 text-sm text-slate-500 dark:text-slate-400">
            Guarda el destino una vez para habilitar la vista previa, la copia del embed y la publicacion.
          </p>
        ) : (
          <>
            <div className="mt-3 flex flex-wrap gap-2">
              {canPublish ? (
                <Button type="button" variant="outline" size="sm" onClick={onPreview} disabled={!form.slug} className="gap-2">
                  <Eye className="h-4 w-4" />
                  Vista previa
                </Button>
              ) : null}
              {canPublish ? (
                <Button
                  type="button"
                  variant="outline"
                  size="sm"
                  onClick={onCopy}
                  disabled={!form.publicPagePath}
                  className="gap-2"
                >
                  <Copy className="h-4 w-4" />
                  Copiar codigo
                </Button>
              ) : null}
            </div>

            {form.publishIssues?.length > 0 ? (
              <div className="mt-4 rounded-md border border-amber-200 bg-amber-50 p-3 dark:border-amber-900/40 dark:bg-amber-900/10">
                <p className="text-sm font-medium text-amber-800 dark:text-amber-200">Falta completar</p>
                <ul className="mt-2 space-y-2 text-sm text-amber-700 dark:text-amber-300">
                  {form.publishIssues.map((issue) => (
                    <li key={issue} className="flex gap-2">
                      <span className="mt-1 h-1.5 w-1.5 shrink-0 rounded-full bg-current" />
                      <span>{issue}</span>
                    </li>
                  ))}
                </ul>
              </div>
            ) : (
              <div className="mt-4 rounded-md border border-emerald-200 bg-emerald-50 p-3 dark:border-emerald-900/40 dark:bg-emerald-900/10">
                <p className="text-sm font-medium text-emerald-800 dark:text-emerald-200">
                  {form.isPublished && !countryOverrideActive
                    ? "El destino ya esta visible en el sitio."
                    : "El destino esta listo para publicarse."}
                </p>
              </div>
            )}

            {countryOverrideActive ? (
              <div className="mt-4 rounded-md border border-amber-200 bg-amber-50 p-3 dark:border-amber-900/40 dark:bg-amber-900/10">
                <p className="text-sm font-medium text-amber-800 dark:text-amber-200">El pais esta retirado del sitio</p>
                <p className="mt-1 text-sm text-amber-700 dark:text-amber-300">
                  Aunque este destino quede publicado, su enlace publico y el embed quedan bloqueados hasta volver a publicar el pais.
                </p>
              </div>
            ) : null}

            {canPublish ? (
              <div className="mt-4">
                {form.isPublished ? (
                  <Button type="button" variant="outline" onClick={onUnpublish} className="w-full gap-2">
                    <Rocket className="h-4 w-4" />
                    Retirar del sitio
                  </Button>
                ) : (
                  <Button type="button" onClick={onPublish} disabled={!form.canPublish} className="w-full gap-2">
                    <Rocket className="h-4 w-4" />
                    Mostrar en el sitio
                  </Button>
                )}
              </div>
            ) : null}
          </>
        )}
      </section>
    </aside>
  );
}

function SidebarField({ label, value }) {
  return (
    <div>
      <p className="text-sm font-medium text-slate-700 dark:text-slate-200">{label}</p>
      <p className="mt-1 text-sm text-slate-900 dark:text-white">{value}</p>
    </div>
  );
}

const publicationToneMap = {
  slate: "bg-slate-100 text-slate-700 dark:bg-slate-800 dark:text-slate-200",
  emerald: "bg-emerald-100 text-emerald-700 dark:bg-emerald-900/30 dark:text-emerald-300",
  amber: "bg-amber-100 text-amber-700 dark:bg-amber-900/30 dark:text-amber-300",
  blue: "bg-blue-100 text-blue-700 dark:bg-blue-900/30 dark:text-blue-300",
};
