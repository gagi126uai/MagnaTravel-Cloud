import { Loader2, Pencil, Plus, Search } from "lucide-react";
import { Button } from "../../../../components/ui/button";

const inputClass =
  "w-full rounded-md border border-slate-300 bg-white px-3 py-2 text-sm text-slate-900 outline-none transition focus:border-indigo-500 focus:ring-2 focus:ring-indigo-500/20 dark:border-slate-700 dark:bg-slate-950 dark:text-white";

export function PackagesCountrySidebar({
  countries,
  countriesLoading,
  countrySearch,
  onCountrySearchChange,
  selectedCountryPublicId,
  onSelectCountry,
  selectedCountry,
  portfolioSummary,
  canEdit,
  onCreateCountry,
  onEditCountry,
}) {
  return (
    <aside className="space-y-4 xl:sticky xl:top-6">
      <section className="rounded-lg border border-slate-200 bg-white p-4 shadow-sm dark:border-slate-800 dark:bg-slate-900/50">
        <div className="flex items-start justify-between gap-3">
          <div className="min-w-0">
            <h2 className="text-base font-semibold text-slate-900 dark:text-white">Paises</h2>
            <p className="mt-1 text-sm text-slate-500 dark:text-slate-400">
              Elegi un pais para administrar sus destinos.
            </p>
          </div>

          {canEdit ? (
            <Button type="button" onClick={onCreateCountry} size="sm" className="gap-2">
              <Plus className="h-4 w-4" />
              Nuevo
            </Button>
          ) : null}
        </div>

        <div className="mt-4 grid grid-cols-3 gap-2">
          <SidebarStat label="Paises" value={portfolioSummary.totalCountries} />
          <SidebarStat label="Destinos" value={portfolioSummary.totalDestinations} />
          <SidebarStat label="Visibles" value={portfolioSummary.publishedDestinations} />
        </div>

        <div className="relative mt-4">
          <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" />
          <input
            type="text"
            value={countrySearch}
            onChange={(event) => onCountrySearchChange(event.target.value)}
            placeholder="Buscar pais..."
            className={`${inputClass} pl-9`}
          />
        </div>

        {canEdit && selectedCountry ? (
          <Button type="button" variant="outline" onClick={() => onEditCountry(selectedCountry)} className="mt-3 w-full gap-2">
            <Pencil className="h-4 w-4" />
            Editar pais seleccionado
          </Button>
        ) : null}
      </section>

      <section className="overflow-hidden rounded-lg border border-slate-200 bg-white shadow-sm dark:border-slate-800 dark:bg-slate-900/50">
        {countriesLoading ? (
          <div className="flex h-48 items-center justify-center">
            <Loader2 className="h-6 w-6 animate-spin text-indigo-500" />
          </div>
        ) : countries.length === 0 ? (
          <div className="px-4 py-10 text-center">
            <p className="text-sm font-medium text-slate-900 dark:text-white">No hay paises cargados</p>
            <p className="mt-1 text-sm text-slate-500 dark:text-slate-400">
              Crea el primero para empezar a ordenar la oferta.
            </p>
          </div>
        ) : (
          <div className="max-h-[calc(100vh-18rem)] overflow-y-auto">
            {countries.map((country) => {
              const selected = country.publicId === selectedCountryPublicId;

              return (
                <button
                  key={country.publicId}
                  type="button"
                  onClick={() => onSelectCountry(country.publicId)}
                  className={`flex w-full items-start justify-between gap-3 border-l-2 px-4 py-3 text-left transition ${
                    selected
                      ? "border-l-indigo-600 bg-indigo-50/70 dark:border-l-indigo-400 dark:bg-indigo-500/10"
                      : "border-l-transparent hover:bg-slate-50 dark:hover:bg-slate-800/40"
                  }`}
                >
                  <div className="min-w-0">
                    <p className={`truncate text-sm font-medium ${selected ? "text-indigo-700 dark:text-indigo-300" : "text-slate-900 dark:text-white"}`}>
                      {country.name}
                    </p>
                    <p className="mt-1 text-xs text-slate-500 dark:text-slate-400">
                      {country.totalDestinations} destinos · {country.publishedDestinations} visibles · {country.draftDestinations} borrador
                    </p>
                  </div>
                  <span className="shrink-0 rounded-md bg-slate-100 px-2 py-1 text-[11px] font-semibold text-slate-600 dark:bg-slate-800 dark:text-slate-300">
                    {country.totalDestinations}
                  </span>
                </button>
              );
            })}
          </div>
        )}
      </section>
    </aside>
  );
}

function SidebarStat({ label, value }) {
  return (
    <div className="rounded-md border border-slate-200 bg-slate-50 px-3 py-2 dark:border-slate-800 dark:bg-slate-950/40">
      <p className="text-[11px] font-semibold uppercase tracking-wide text-slate-500 dark:text-slate-400">{label}</p>
      <p className="mt-1 text-base font-semibold text-slate-900 dark:text-white">{value}</p>
    </div>
  );
}
