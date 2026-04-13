import { Copy, Eye, Loader2, Pencil, Plus, Rocket, Search } from "lucide-react";
import { Button } from "../../../../components/ui/button";
import { ListToolbar } from "../../../../components/ui/ListToolbar";
import { formatLongDate, formatMoney, formatShortDate } from "../../lib/publicationUtils";

const inputClass =
  "w-full rounded-md border border-slate-300 bg-white px-3 py-2 text-sm text-slate-900 outline-none transition focus:border-indigo-500 focus:ring-2 focus:ring-indigo-500/20 dark:border-slate-700 dark:bg-slate-950 dark:text-white";

export function PackagesDestinationsPanel({
  selectedCountry,
  destinations,
  filteredDestinations,
  loading,
  searchValue,
  onSearchChange,
  filterValue,
  onFilterChange,
  canEdit,
  canPublish,
  onEditCountry,
  onCreateDestination,
  onPreviewCountry,
  onCopyCountry,
  onEditDestination,
  onViewDestination,
  onCopyDestination,
  onPublishDestination,
  onUnpublishDestination,
}) {
  const visibleCount = destinations.filter((destination) => destination.isPublished).length;
  const draftCount = Math.max(destinations.length - visibleCount, 0);

  return (
    <div className="space-y-4">
      <section className="rounded-lg border border-slate-200 bg-white p-4 shadow-sm dark:border-slate-800 dark:bg-slate-900/50">
        <div className="flex flex-col gap-4 lg:flex-row lg:items-start lg:justify-between">
          <div className="min-w-0">
            <p className="text-xs font-semibold uppercase tracking-[0.18em] text-slate-500 dark:text-slate-400">
              Pais seleccionado
            </p>
            <h2 className="mt-1 text-2xl font-semibold tracking-tight text-slate-900 dark:text-white">{selectedCountry.name}</h2>
            <p className="mt-1 text-sm text-slate-500 dark:text-slate-400">
              Gestiona la grilla de destinos, su estado comercial y sus accesos de publicacion.
            </p>
          </div>

          <div className="flex flex-wrap gap-2">
            {canEdit ? (
              <Button type="button" variant="outline" onClick={() => onEditCountry(selectedCountry)} className="gap-2">
                <Pencil className="h-4 w-4" />
                Editar pais
              </Button>
            ) : null}
            {canPublish ? (
              <>
                <Button type="button" variant="outline" onClick={() => onPreviewCountry(selectedCountry)} className="gap-2">
                  <Eye className="h-4 w-4" />
                  Vista previa
                </Button>
                <Button type="button" variant="outline" onClick={() => onCopyCountry(selectedCountry)} className="gap-2">
                  <Copy className="h-4 w-4" />
                  Copiar codigo
                </Button>
              </>
            ) : null}
          </div>
        </div>

        <div className="mt-4 flex flex-wrap gap-2">
          <MetricPill label="Destinos" value={destinations.length} />
          <MetricPill label="Visibles" value={visibleCount} tone="emerald" />
          <MetricPill label="Borrador" value={draftCount} tone="amber" />
        </div>
      </section>

      <ListToolbar
        searchSlot={
          <div className="relative">
            <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" />
            <input
              type="text"
              value={searchValue}
              onChange={(event) => onSearchChange(event.target.value)}
              placeholder="Buscar por destino, titulo o texto..."
              className={`${inputClass} pl-9`}
            />
          </div>
        }
        filterSlot={
          canPublish ? (
            <select value={filterValue} onChange={(event) => onFilterChange(event.target.value)} className={inputClass}>
              <option value="all">Todos los estados</option>
              <option value="visible">Visibles en el sitio</option>
              <option value="hidden">En borrador</option>
            </select>
          ) : null
        }
        actionSlot={
          canEdit ? (
            <Button type="button" onClick={onCreateDestination} className="gap-2">
              <Plus className="h-4 w-4" />
              Nuevo destino
            </Button>
          ) : null
        }
      />

      {loading ? (
        <section className="flex min-h-[280px] items-center justify-center rounded-lg border border-slate-200 bg-white shadow-sm dark:border-slate-800 dark:bg-slate-900/50">
          <Loader2 className="h-6 w-6 animate-spin text-indigo-500" />
        </section>
      ) : filteredDestinations.length === 0 ? (
        <section className="rounded-lg border border-slate-200 bg-white px-4 py-12 text-center shadow-sm dark:border-slate-800 dark:bg-slate-900/50">
          <p className="text-sm font-medium text-slate-900 dark:text-white">
            {destinations.length === 0 ? "Todavia no hay destinos en este pais" : "No encontramos destinos con ese filtro"}
          </p>
          <p className="mt-1 text-sm text-slate-500 dark:text-slate-400">
            {destinations.length === 0
              ? "Crea el primer destino para empezar a cargar contenido, salidas y publicacion."
              : "Ajusta la busqueda o cambia el estado para ver otros resultados."}
          </p>
        </section>
      ) : (
        <>
          <section className="hidden overflow-hidden rounded-lg border border-slate-200 bg-white shadow-sm dark:border-slate-800 dark:bg-slate-900/50 md:block">
            <div className="overflow-x-auto">
              <table className="min-w-full border-collapse text-left text-sm">
                <thead>
                  <tr className="border-b border-slate-200 bg-slate-50 dark:border-slate-800 dark:bg-slate-950/40">
                    <th className="px-4 py-3 text-xs font-semibold uppercase tracking-wide text-slate-500 dark:text-slate-400">Orden</th>
                    <th className="px-4 py-3 text-xs font-semibold uppercase tracking-wide text-slate-500 dark:text-slate-400">Destino</th>
                    <th className="px-4 py-3 text-xs font-semibold uppercase tracking-wide text-slate-500 dark:text-slate-400">Proxima salida</th>
                    <th className="px-4 py-3 text-xs font-semibold uppercase tracking-wide text-slate-500 dark:text-slate-400">Precio desde</th>
                    <th className="px-4 py-3 text-xs font-semibold uppercase tracking-wide text-slate-500 dark:text-slate-400">Estado</th>
                    <th className="px-4 py-3 text-xs font-semibold uppercase tracking-wide text-slate-500 dark:text-slate-400">Actualizado</th>
                    <th className="px-4 py-3 text-right text-xs font-semibold uppercase tracking-wide text-slate-500 dark:text-slate-400">Acciones</th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-slate-100 dark:divide-slate-800">
                  {filteredDestinations.map((destination) => {
                    const status = getDestinationStatus(destination);
                    const updatedLabel = formatUpdatedDate(destination.updatedAt || destination.createdAt);

                    return (
                      <tr key={destination.publicId} className="align-top hover:bg-slate-50/70 dark:hover:bg-slate-800/30">
                        <td className="px-4 py-4 text-sm font-medium text-slate-600 dark:text-slate-300">
                          {destination.displayOrder}
                        </td>
                        <td className="px-4 py-4">
                          <div className="min-w-0">
                            {canEdit ? (
                              <button
                                type="button"
                                onClick={() => onEditDestination(destination.publicId)}
                                className="truncate text-left text-sm font-semibold text-slate-900 hover:text-indigo-600 dark:text-white dark:hover:text-indigo-300"
                              >
                                {destination.title || destination.name}
                              </button>
                            ) : (
                              <p className="truncate text-sm font-semibold text-slate-900 dark:text-white">
                                {destination.title || destination.name}
                              </p>
                            )}
                            <p className="mt-1 text-sm text-slate-500 dark:text-slate-400">
                              {destination.tagline || destination.name}
                            </p>
                          </div>
                        </td>
                        <td className="px-4 py-4 text-sm text-slate-600 dark:text-slate-300">
                          {formatLongDate(destination.nextDepartureDate)}
                        </td>
                        <td className="px-4 py-4 text-sm font-medium text-slate-900 dark:text-white">
                          {destination.fromPrice != null ? formatMoney(destination.fromPrice, destination.currency) : "-"}
                        </td>
                        <td className="px-4 py-4">
                          <StatusBadge status={status} />
                          {destination.publishIssues?.length > 0 && !destination.isPublished ? (
                            <p className="mt-1 text-xs text-slate-500 dark:text-slate-400">
                              {destination.publishIssues.length} pendiente{destination.publishIssues.length === 1 ? "" : "s"}
                            </p>
                          ) : null}
                        </td>
                        <td className="px-4 py-4 text-sm text-slate-600 dark:text-slate-300">{updatedLabel}</td>
                        <td className="px-4 py-4">
                          <div className="flex justify-end gap-1">
                            {canEdit ? (
                              <Button
                                type="button"
                                variant="ghost"
                                size="sm"
                                onClick={() => onEditDestination(destination.publicId)}
                                className="gap-2 text-slate-600 hover:text-slate-900 dark:text-slate-300 dark:hover:text-white"
                              >
                                <Pencil className="h-4 w-4" />
                                Editar
                              </Button>
                            ) : null}
                            {canPublish ? (
                              <IconActionButton title="Vista previa" onClick={() => onViewDestination(destination)}>
                                <Eye className="h-4 w-4" />
                              </IconActionButton>
                            ) : null}
                            {canPublish ? (
                              <IconActionButton title="Copiar codigo" onClick={() => onCopyDestination(destination)}>
                                <Copy className="h-4 w-4" />
                              </IconActionButton>
                            ) : null}
                            {canPublish ? (
                              destination.isPublished ? (
                                <IconActionButton title="Ocultar del sitio" onClick={() => onUnpublishDestination(destination)}>
                                  <Rocket className="h-4 w-4" />
                                </IconActionButton>
                              ) : (
                                <IconActionButton
                                  title="Publicar"
                                  onClick={() => onPublishDestination(destination)}
                                  disabled={!destination.canPublish}
                                >
                                  <Rocket className="h-4 w-4" />
                                </IconActionButton>
                              )
                            ) : null}
                          </div>
                        </td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>
          </section>

          <section className="space-y-3 md:hidden">
            {filteredDestinations.map((destination) => (
              <DestinationMobileRow
                key={destination.publicId}
                destination={destination}
                canEdit={canEdit}
                canPublish={canPublish}
                onEditDestination={onEditDestination}
                onViewDestination={onViewDestination}
                onCopyDestination={onCopyDestination}
                onPublishDestination={onPublishDestination}
                onUnpublishDestination={onUnpublishDestination}
              />
            ))}
          </section>
        </>
      )}
    </div>
  );
}

function DestinationMobileRow({
  destination,
  canEdit,
  canPublish,
  onEditDestination,
  onViewDestination,
  onCopyDestination,
  onPublishDestination,
  onUnpublishDestination,
}) {
  const status = getDestinationStatus(destination);

  return (
    <article className="rounded-lg border border-slate-200 bg-white p-4 shadow-sm dark:border-slate-800 dark:bg-slate-900/50">
      <div className="flex items-start justify-between gap-3">
        <div className="min-w-0">
          <p className="text-base font-semibold text-slate-900 dark:text-white">{destination.title || destination.name}</p>
          <p className="mt-1 text-sm text-slate-500 dark:text-slate-400">{destination.tagline || destination.name}</p>
        </div>
        <StatusBadge status={status} />
      </div>

      <div className="mt-4 grid grid-cols-2 gap-3 text-sm">
        <MobileMetric label="Orden" value={destination.displayOrder} />
        <MobileMetric label="Actualizado" value={formatUpdatedDate(destination.updatedAt || destination.createdAt)} />
        <MobileMetric label="Proxima salida" value={formatShortDate(destination.nextDepartureDate)} />
        <MobileMetric
          label="Precio"
          value={destination.fromPrice != null ? formatMoney(destination.fromPrice, destination.currency) : "-"}
        />
      </div>

      {destination.publishIssues?.length > 0 && !destination.isPublished ? (
        <p className="mt-3 text-xs text-slate-500 dark:text-slate-400">
          {destination.publishIssues.length} pendiente{destination.publishIssues.length === 1 ? "" : "s"} para publicarlo
        </p>
      ) : null}

      <div className="mt-4 flex flex-wrap gap-2">
        {canEdit ? (
          <Button type="button" variant="outline" size="sm" onClick={() => onEditDestination(destination.publicId)} className="gap-2">
            <Pencil className="h-4 w-4" />
            Editar
          </Button>
        ) : null}
        {canPublish ? (
          <Button type="button" variant="outline" size="sm" onClick={() => onViewDestination(destination)} className="gap-2">
            <Eye className="h-4 w-4" />
            Vista previa
          </Button>
        ) : null}
        {canPublish ? (
          <Button type="button" variant="outline" size="sm" onClick={() => onCopyDestination(destination)} className="gap-2">
            <Copy className="h-4 w-4" />
            Copiar
          </Button>
        ) : null}
        {canPublish ? (
          destination.isPublished ? (
            <Button type="button" variant="outline" size="sm" onClick={() => onUnpublishDestination(destination)} className="gap-2">
              <Rocket className="h-4 w-4" />
              Ocultar
            </Button>
          ) : (
            <Button
              type="button"
              size="sm"
              onClick={() => onPublishDestination(destination)}
              disabled={!destination.canPublish}
              className="gap-2"
            >
              <Rocket className="h-4 w-4" />
              Publicar
            </Button>
          )
        ) : null}
      </div>
    </article>
  );
}

function IconActionButton({ children, title, disabled = false, onClick }) {
  return (
    <Button
      type="button"
      variant="ghost"
      size="icon"
      onClick={onClick}
      disabled={disabled}
      title={title}
      className="h-8 w-8 text-slate-500 hover:text-slate-900 dark:text-slate-300 dark:hover:text-white"
    >
      {children}
    </Button>
  );
}

function MetricPill({ label, value, tone = "slate" }) {
  const tones = {
    slate: "border-slate-200 bg-slate-50 text-slate-700 dark:border-slate-800 dark:bg-slate-950/40 dark:text-slate-200",
    emerald:
      "border-emerald-200 bg-emerald-50 text-emerald-700 dark:border-emerald-900/40 dark:bg-emerald-900/10 dark:text-emerald-300",
    amber:
      "border-amber-200 bg-amber-50 text-amber-700 dark:border-amber-900/40 dark:bg-amber-900/10 dark:text-amber-300",
  };

  return (
    <span className={`inline-flex items-center gap-2 rounded-md border px-3 py-1.5 text-sm font-medium ${tones[tone] || tones.slate}`}>
      <span>{label}</span>
      <span className="rounded-md bg-white/70 px-1.5 py-0.5 text-xs font-semibold dark:bg-slate-900/40">{value}</span>
    </span>
  );
}

function StatusBadge({ status }) {
  const tones = {
    visible: "bg-emerald-100 text-emerald-700 dark:bg-emerald-900/30 dark:text-emerald-300",
    ready: "bg-blue-100 text-blue-700 dark:bg-blue-900/30 dark:text-blue-300",
    draft: "bg-amber-100 text-amber-700 dark:bg-amber-900/30 dark:text-amber-300",
  };

  return <span className={`inline-flex rounded-md px-2 py-1 text-xs font-semibold ${tones[status.key]}`}>{status.label}</span>;
}

function MobileMetric({ label, value }) {
  return (
    <div className="rounded-md border border-slate-200 bg-slate-50 px-3 py-2 dark:border-slate-800 dark:bg-slate-950/40">
      <p className="text-[11px] font-semibold uppercase tracking-wide text-slate-500 dark:text-slate-400">{label}</p>
      <p className="mt-1 text-sm font-medium text-slate-900 dark:text-white">{value}</p>
    </div>
  );
}

function getDestinationStatus(destination) {
  if (destination.isPublished) {
    return { key: "visible", label: "Visible" };
  }

  if (destination.canPublish) {
    return { key: "ready", label: "Lista" };
  }

  return { key: "draft", label: "Borrador" };
}

function formatUpdatedDate(value) {
  if (!value) {
    return "-";
  }

  const date = new Date(value);
  if (Number.isNaN(date.getTime())) {
    return "-";
  }

  return date.toLocaleDateString("es-AR");
}
