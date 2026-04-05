import { useCallback, useEffect, useMemo, useState } from "react";
import { useNavigate, useSearchParams } from "react-router-dom";
import {
  Building2,
  Copy,
  Eye,
  Globe2,
  Loader2,
  MapPinned,
  Pencil,
  Plus,
  Rocket,
  Search,
} from "lucide-react";
import { api, buildAppUrl } from "../../../api";
import { showConfirm, showError, showSuccess } from "../../../alerts";
import { hasPermission } from "../../../auth";
import { Button } from "../../../components/ui/button";
import { ListPageHeader } from "../../../components/ui/ListPageHeader";
import { ListToolbar } from "../../../components/ui/ListToolbar";
import { ListEmptyState } from "../../../components/ui/ListEmptyState";
import { useDebounce } from "../../../hooks/useDebounce";
import {
  buildCountryPublicationSnippet,
  buildDestinationPublicationSnippet,
  formatLongDate,
  formatMoney,
  mapCountryForm,
} from "../lib/publicationUtils";

const inputClass =
  "w-full rounded-lg border border-slate-200 bg-white px-3 py-2.5 text-sm text-slate-900 outline-none transition focus:border-indigo-500 focus:ring-1 focus:ring-indigo-500 dark:border-slate-700 dark:bg-slate-900 dark:text-white";

const emptyCountryForm = {
  publicId: null,
  name: "",
};

export default function PackagesPage() {
  const canEdit = hasPermission("paquetes.edit");
  const canPublish = hasPermission("paquetes.publish");
  const navigate = useNavigate();
  const [searchParams, setSearchParams] = useSearchParams();

  const [countries, setCountries] = useState([]);
  const [countriesLoading, setCountriesLoading] = useState(true);
  const [countrySearch, setCountrySearch] = useState("");
  const [selectedCountryPublicId, setSelectedCountryPublicId] = useState(searchParams.get("country") || "");

  const [destinations, setDestinations] = useState([]);
  const [destinationsLoading, setDestinationsLoading] = useState(false);
  const [destinationSearch, setDestinationSearch] = useState("");
  const [destinationStatusFilter, setDestinationStatusFilter] = useState("all");

  const [countryModalOpen, setCountryModalOpen] = useState(false);
  const [countrySaving, setCountrySaving] = useState(false);
  const [countryForm, setCountryForm] = useState(emptyCountryForm);

  const debouncedCountrySearch = useDebounce(countrySearch, 250);

  const syncSelectedCountry = useCallback(
    (countryPublicId) => {
      setSelectedCountryPublicId(countryPublicId);

      const nextParams = new URLSearchParams(searchParams);
      if (countryPublicId) {
        nextParams.set("country", countryPublicId);
      } else {
        nextParams.delete("country");
      }

      setSearchParams(nextParams, { replace: true });
    },
    [searchParams, setSearchParams]
  );

  const loadCountries = useCallback(async () => {
    setCountriesLoading(true);
    try {
      const query = debouncedCountrySearch.trim();
      const response = await api.get(`/countries${query ? `?search=${encodeURIComponent(query)}` : ""}`);
      setCountries(Array.isArray(response) ? response : []);
    } catch (error) {
      showError(error.message || "No pudimos cargar los paises.");
      setCountries([]);
    } finally {
      setCountriesLoading(false);
    }
  }, [debouncedCountrySearch]);

  const loadDestinations = useCallback(async (countryPublicId) => {
    if (!countryPublicId) {
      setDestinations([]);
      return;
    }

    setDestinationsLoading(true);
    try {
      const response = await api.get(`/countries/${countryPublicId}/destinations`);
      setDestinations(Array.isArray(response) ? response : []);
    } catch (error) {
      showError(error.message || "No pudimos cargar los destinos.");
      setDestinations([]);
    } finally {
      setDestinationsLoading(false);
    }
  }, []);

  useEffect(() => {
    loadCountries();
  }, [loadCountries]);

  useEffect(() => {
    const paramCountry = searchParams.get("country") || "";
    if (paramCountry !== selectedCountryPublicId) {
      setSelectedCountryPublicId(paramCountry);
    }
  }, [searchParams, selectedCountryPublicId]);

  useEffect(() => {
    if (countriesLoading) {
      return;
    }

    if (countries.length === 0) {
      if (selectedCountryPublicId) {
        syncSelectedCountry("");
      }
      return;
    }

    const exists = countries.some((country) => country.publicId === selectedCountryPublicId);
    if (!exists) {
      syncSelectedCountry(countries[0].publicId);
    }
  }, [countries, countriesLoading, selectedCountryPublicId, syncSelectedCountry]);

  useEffect(() => {
    loadDestinations(selectedCountryPublicId);
  }, [loadDestinations, selectedCountryPublicId]);

  const selectedCountry = useMemo(
    () => countries.find((country) => country.publicId === selectedCountryPublicId) || null,
    [countries, selectedCountryPublicId]
  );

  const portfolioSummary = useMemo(
    () =>
      countries.reduce(
        (summary, country) => {
          const totalDestinations = Number(country.totalDestinations || 0);
          const publishedDestinations = Number(country.publishedDestinations || 0);
          const draftDestinations =
            country.draftDestinations != null
              ? Number(country.draftDestinations || 0)
              : Math.max(totalDestinations - publishedDestinations, 0);

          summary.totalCountries += 1;
          summary.totalDestinations += totalDestinations;
          summary.publishedDestinations += publishedDestinations;
          summary.draftDestinations += draftDestinations;

          return summary;
        },
        {
          totalCountries: 0,
          totalDestinations: 0,
          publishedDestinations: 0,
          draftDestinations: 0,
        }
      ),
    [countries]
  );

  const filteredDestinations = useMemo(() => {
    return destinations.filter((destination) => {
      const searchValue = destinationSearch.trim().toLowerCase();
      const matchesSearch =
        !searchValue ||
        `${destination.title || ""} ${destination.name || ""} ${destination.tagline || ""}`
          .toLowerCase()
          .includes(searchValue);

      if (!matchesSearch) {
        return false;
      }

      if (!canPublish || destinationStatusFilter === "all") {
        return true;
      }

      if (destinationStatusFilter === "visible") {
        return destination.isPublished;
      }

      return !destination.isPublished;
    });
  }, [canPublish, destinationSearch, destinationStatusFilter, destinations]);

  function openCreateCountryModal() {
    setCountryForm(emptyCountryForm);
    setCountryModalOpen(true);
  }

  function openEditCountryModal(country) {
    setCountryForm(mapCountryForm(country));
    setCountryModalOpen(true);
  }

  function closeCountryModal() {
    setCountryModalOpen(false);
    setCountryForm(emptyCountryForm);
  }

  async function saveCountry(event) {
    event.preventDefault();

    if (!countryForm.name.trim()) {
      showError("Ingresa el nombre del pais.");
      return;
    }

    setCountrySaving(true);
    try {
      const payload = { name: countryForm.name.trim() };
      const saved = countryForm.publicId
        ? await api.put(`/countries/${countryForm.publicId}`, payload)
        : await api.post("/countries", payload);

      await loadCountries();
      syncSelectedCountry(saved.publicId);
      closeCountryModal();
      showSuccess(countryForm.publicId ? "Pais actualizado." : "Pais creado.");
    } catch (error) {
      showError(error.message || "No pudimos guardar el pais.");
    } finally {
      setCountrySaving(false);
    }
  }

  function openDestinationEditor(destinationPublicId) {
    if (!destinationPublicId) {
      return;
    }

    const nextParams = new URLSearchParams();
    if (selectedCountryPublicId) {
      nextParams.set("country", selectedCountryPublicId);
    }

    navigate(`/packages/destinations/${destinationPublicId}${nextParams.toString() ? `?${nextParams.toString()}` : ""}`);
  }

  function openCreateDestination() {
    if (!selectedCountryPublicId) {
      showError("Selecciona un pais antes de crear un destino.");
      return;
    }

    navigate(`/packages/destinations/new?country=${selectedCountryPublicId}`);
  }

  function openCountryPreview(country) {
    if (!country?.slug) {
      showError("Todavia no pudimos preparar la vista previa de este pais.");
      return;
    }

    window.open(buildAppUrl(`/preview/countries/${country.slug}`), "_blank", "noopener,noreferrer");
  }

  function openDestinationPreview(destination) {
    if (!destination?.slug) {
      showError("Guarda el destino antes de verlo como cliente.");
      return;
    }

    const previewUrl = new URL(buildAppUrl(`/preview/packages/${destination.slug}`));
    if (destination.countrySlug) {
      previewUrl.searchParams.set("countrySlug", destination.countrySlug);
    }

    window.open(previewUrl.toString(), "_blank", "noopener,noreferrer");
  }

  async function copyCountryPublication(country) {
    if (!country?.countryPagePath) {
      showError("El pais todavia no esta listo para copiarlo a la web.");
      return;
    }

    try {
      await navigator.clipboard.writeText(buildCountryPublicationSnippet(country));
      showSuccess("Codigo para la web copiado.");
    } catch {
      showError("No pudimos copiar el codigo para la web.");
    }
  }

  async function copyDestinationPublication(destination) {
    if (!destination?.publicPagePath) {
      showError("Guarda el destino antes de copiarlo para la web.");
      return;
    }

    try {
      await navigator.clipboard.writeText(buildDestinationPublicationSnippet(destination));
      showSuccess("Codigo para la web copiado.");
    } catch {
      showError("No pudimos copiar el codigo para la web.");
    }
  }

  async function handlePublish(destination) {
    const confirmed = await showConfirm({
      title: "Mostrar destino en el sitio",
      eyebrow: "Publicacion web",
      text: "El destino quedara visible para los clientes dentro del sitio.",
      confirmText: "Mostrar en el sitio",
      confirmColor: "emerald",
    });

    if (!confirmed) {
      return;
    }

    try {
      const updated = await api.patch(`/destinations/${destination.publicId}/publish`);
      setDestinations((current) =>
        current.map((item) => (item.publicId === updated.publicId ? updated : item))
      );
      showSuccess("El destino ya esta visible en el sitio.");
    } catch (error) {
      showError(error.message || "No pudimos mostrar el destino en el sitio.");
    }
  }

  async function handleUnpublish(destination) {
    const confirmed = await showConfirm({
      title: "Retirar destino del sitio",
      eyebrow: "Publicacion web",
      text: "El destino dejara de mostrarse para los clientes, pero seguira disponible dentro del ERP.",
      confirmText: "Retirar del sitio",
      confirmColor: "amber",
    });

    if (!confirmed) {
      return;
    }

    try {
      const updated = await api.patch(`/destinations/${destination.publicId}/unpublish`);
      setDestinations((current) =>
        current.map((item) => (item.publicId === updated.publicId ? updated : item))
      );
      showSuccess("El destino dejo de mostrarse en el sitio.");
    } catch (error) {
      showError(error.message || "No pudimos retirar el destino del sitio.");
    }
  }

  return (
    <div className="animate-in fade-in space-y-4 duration-500 md:space-y-6">
      <ListPageHeader
        title="Paises y destinos"
        subtitle="Organiza la oferta del sitio por pais, administra los destinos y deja lista la informacion comercial."
        actions={
          canEdit ? (
            <>
              <Button variant="outline" onClick={openCreateCountryModal} className="gap-2">
                <Plus className="h-4 w-4" />
                Nuevo pais
              </Button>
              {selectedCountry ? (
                <Button onClick={openCreateDestination} className="gap-2">
                  <Plus className="h-4 w-4" />
                  Nuevo destino
                </Button>
              ) : null}
            </>
          ) : null
        }
      />

      <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
        <SummaryCard label="Paises cargados" value={portfolioSummary.totalCountries} />
        <SummaryCard label="Destinos cargados" value={portfolioSummary.totalDestinations} />
        <SummaryCard label="Visibles en el sitio" value={portfolioSummary.publishedDestinations} tone="emerald" />
        <SummaryCard label="En preparacion" value={portfolioSummary.draftDestinations} tone="amber" />
      </div>

      <div className="grid items-start gap-5 xl:grid-cols-[320px_minmax(0,1fr)]">
        <aside className="space-y-4 xl:sticky xl:top-20">
          <div className="overflow-hidden rounded-2xl border border-slate-200 bg-white shadow-sm dark:border-slate-800 dark:bg-slate-900/50">
            <div className="border-b border-slate-200 bg-slate-50/70 px-5 py-5 dark:border-slate-800 dark:bg-slate-950/30">
              <div className="flex items-center justify-between gap-3">
                <div>
                  <h2 className="text-sm font-semibold text-slate-900 dark:text-white">Paises</h2>
                  <p className="mt-1 text-sm text-slate-500 dark:text-slate-400">
                    Selecciona un pais para ver sus destinos.
                  </p>
                </div>
                <span className="rounded-full bg-slate-100 px-2.5 py-1 text-xs font-semibold text-slate-600 dark:bg-slate-800 dark:text-slate-200">
                  {countries.length}
                </span>
              </div>

              <div className="relative mt-4">
                <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" />
                <input
                  type="text"
                  value={countrySearch}
                  onChange={(event) => setCountrySearch(event.target.value)}
                  placeholder="Buscar pais..."
                  className="w-full rounded-lg border border-slate-200 bg-slate-50 py-2.5 pl-10 pr-4 text-sm text-slate-900 outline-none transition focus:border-indigo-500 focus:bg-white dark:border-slate-700 dark:bg-slate-950 dark:text-white"
                />
              </div>
            </div>

            <div className="space-y-2 overflow-y-auto p-3 xl:max-h-[calc(100vh-255px)]">
              {countriesLoading ? (
                <div className="flex h-48 items-center justify-center">
                  <Loader2 className="h-6 w-6 animate-spin text-indigo-500" />
                </div>
              ) : countries.length === 0 ? (
                <ListEmptyState
                  icon={Building2}
                  title="Todavia no hay paises"
                  description="Crea el primero para empezar a organizar el sitio."
                  compact
                />
              ) : (
                countries.map((country) => (
                  <CountryListItem
                    key={country.publicId}
                    country={country}
                    selected={country.publicId === selectedCountryPublicId}
                    onClick={() => syncSelectedCountry(country.publicId)}
                  />
                ))
              )}
            </div>
          </div>
        </aside>

        <section className="space-y-4">
          {!selectedCountry ? (
            <div className="rounded-2xl border border-slate-200 bg-white p-5 shadow-sm dark:border-slate-800 dark:bg-slate-900/50">
              <ListEmptyState
                icon={Globe2}
                title="Selecciona un pais"
                description="Cuando elijas un pais, aqui vas a ver el resumen de su oferta y los destinos cargados."
              />
            </div>
          ) : (
            <>
              <div className="rounded-2xl border border-slate-200 bg-white p-5 shadow-sm dark:border-slate-800 dark:bg-slate-900/50 lg:p-6">
                <div className="flex flex-col gap-5 2xl:flex-row 2xl:items-start 2xl:justify-between">
                  <div className="min-w-0 flex-1">
                    <div className="flex flex-wrap gap-2">
                      <Badge tone="slate">Pais seleccionado</Badge>
                      <Badge tone="emerald">{selectedCountry.publishedDestinations} visibles</Badge>
                      <Badge tone="amber">{selectedCountry.draftDestinations} en preparacion</Badge>
                    </div>
                    <h2 className="mt-4 text-2xl font-bold tracking-tight text-slate-900 dark:text-white lg:text-3xl">
                      {selectedCountry.name}
                    </h2>
                    <p className="mt-2 max-w-2xl text-sm text-slate-500 dark:text-slate-400">
                      Administra los destinos de este pais, sus salidas y la informacion que se muestra en el sitio.
                    </p>
                  </div>

                  {canEdit ? (
                    <div className="flex flex-wrap gap-2 2xl:justify-end">
                      <Button variant="outline" onClick={() => openEditCountryModal(selectedCountry)} className="gap-2">
                        <Pencil className="h-4 w-4" />
                        Editar pais
                      </Button>
                      <Button onClick={openCreateDestination} className="gap-2">
                        <Plus className="h-4 w-4" />
                        Nuevo destino
                      </Button>
                    </div>
                  ) : null}
                </div>

                <div className="mt-6 grid gap-3 sm:grid-cols-3">
                  <SummaryCard label="Destinos cargados" value={selectedCountry.totalDestinations} />
                  <SummaryCard label="Visibles en el sitio" value={selectedCountry.publishedDestinations} tone="emerald" />
                  <SummaryCard label="En preparacion" value={selectedCountry.draftDestinations} tone="amber" />
                </div>

                {canPublish ? (
                  <div className="mt-6 rounded-2xl border border-slate-200 bg-slate-50/80 p-4 dark:border-slate-800 dark:bg-slate-950/40">
                    <div className="flex flex-col gap-3 lg:flex-row lg:items-center lg:justify-between">
                      <div>
                        <p className="text-sm font-semibold text-slate-900 dark:text-white">Publicacion en el sitio</p>
                        <p className="mt-1 text-sm text-slate-500 dark:text-slate-400">
                          Revisa la experiencia del cliente y copia el codigo necesario para mostrar este pais en la web.
                        </p>
                      </div>
                      <div className="flex flex-wrap gap-2">
                        <Button variant="outline" onClick={() => openCountryPreview(selectedCountry)} className="gap-2">
                          <Eye className="h-4 w-4" />
                          Ver como cliente
                        </Button>
                        <Button variant="outline" onClick={() => copyCountryPublication(selectedCountry)} className="gap-2">
                          <Copy className="h-4 w-4" />
                          Copiar para la web
                        </Button>
                      </div>
                    </div>
                  </div>
                ) : null}
              </div>

              <ListToolbar
                actionSlot={
                  <div className="rounded-full bg-slate-100 px-3 py-1 text-xs font-semibold text-slate-600 dark:bg-slate-800 dark:text-slate-200">
                    {filteredDestinations.length} destino{filteredDestinations.length === 1 ? "" : "s"}
                  </div>
                }
                searchSlot={
                  <div className="relative">
                    <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" />
                    <input
                      type="text"
                      value={destinationSearch}
                      onChange={(event) => setDestinationSearch(event.target.value)}
                      placeholder="Buscar destino..."
                      className="w-full rounded-lg border border-slate-200 bg-slate-50 py-2.5 pl-10 pr-4 text-sm text-slate-900 outline-none transition focus:border-indigo-500 focus:bg-white dark:border-slate-700 dark:bg-slate-950 dark:text-white"
                    />
                  </div>
                }
                filterSlot={
                  canPublish ? (
                    <select
                      value={destinationStatusFilter}
                      onChange={(event) => setDestinationStatusFilter(event.target.value)}
                      className={inputClass}
                    >
                      <option value="all">Todos</option>
                      <option value="visible">Visibles en el sitio</option>
                      <option value="hidden">En preparacion</option>
                    </select>
                  ) : null
                }
              />

              {destinationsLoading ? (
                <div className="flex items-center justify-center rounded-2xl border border-slate-200 bg-white px-4 py-14 shadow-sm dark:border-slate-800 dark:bg-slate-900/50">
                  <Loader2 className="h-7 w-7 animate-spin text-indigo-500" />
                </div>
              ) : filteredDestinations.length === 0 ? (
                <ListEmptyState
                  icon={MapPinned}
                  title={destinations.length === 0 ? "Todavia no hay destinos cargados" : "No encontramos destinos"}
                  description={
                    destinations.length === 0
                      ? "Crea el primer destino de este pais para empezar a cargar salidas y contenido."
                      : "Ajusta la busqueda o el filtro para ver otros resultados."
                  }
                  className="rounded-2xl border border-slate-200 bg-white shadow-sm dark:border-slate-800 dark:bg-slate-900/50"
                />
              ) : (
                <div className="space-y-3">
                  {filteredDestinations.map((destination) => (
                    <DestinationListCard
                      key={destination.publicId}
                      destination={destination}
                      canEdit={canEdit}
                      canPublish={canPublish}
                      onEdit={() => openDestinationEditor(destination.publicId)}
                      onView={() => openDestinationPreview(destination)}
                      onCopy={() => copyDestinationPublication(destination)}
                      onPublish={() => handlePublish(destination)}
                      onUnpublish={() => handleUnpublish(destination)}
                    />
                  ))}
                </div>
              )}
            </>
          )}
        </section>
      </div>

      <CountryModal
        open={countryModalOpen}
        form={countryForm}
        saving={countrySaving}
        onChange={(name) => setCountryForm((current) => ({ ...current, name }))}
        onClose={closeCountryModal}
        onSubmit={saveCountry}
      />
    </div>
  );
}

function CountryListItem({ country, selected, onClick }) {
  const stateClass =
    country.publishedDestinations > 0
      ? "bg-emerald-100 text-emerald-700 dark:bg-emerald-900/30 dark:text-emerald-300"
      : "bg-amber-100 text-amber-700 dark:bg-amber-900/30 dark:text-amber-300";

  return (
    <button
      type="button"
      onClick={onClick}
      className={`w-full rounded-xl border px-4 py-3 text-left transition ${
        selected
          ? "border-indigo-200 bg-indigo-50/70 shadow-sm dark:border-indigo-900/60 dark:bg-indigo-900/20"
          : "border-slate-200 bg-white hover:border-slate-300 hover:bg-slate-50 dark:border-slate-800 dark:bg-slate-900 dark:hover:border-slate-700 dark:hover:bg-slate-800"
      }`}
    >
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="min-w-0">
          <p className="text-sm font-semibold text-slate-900 dark:text-white">{country.name}</p>
          <p className="mt-1 text-xs text-slate-500 dark:text-slate-400">{country.totalDestinations} destinos cargados</p>
        </div>
        <span className={`shrink-0 rounded-full px-2 py-1 text-[11px] font-semibold ${stateClass}`}>
          {country.publishedDestinations > 0 ? `${country.publishedDestinations} visibles` : "En preparacion"}
        </span>
      </div>
    </button>
  );
}

function SummaryCard({ label, value, tone = "slate" }) {
  const toneClass =
    tone === "emerald"
      ? "bg-emerald-50 text-emerald-800 dark:bg-emerald-900/20 dark:text-emerald-200"
      : tone === "amber"
        ? "bg-amber-50 text-amber-800 dark:bg-amber-900/20 dark:text-amber-200"
        : "bg-slate-50 text-slate-900 dark:bg-slate-950/40 dark:text-white";

  return (
    <div className={`rounded-xl border border-slate-200 px-4 py-4 dark:border-slate-800 ${toneClass}`}>
      <p className="text-xs font-semibold uppercase tracking-[0.18em] text-slate-500 dark:text-slate-400">{label}</p>
      <p className="mt-2 break-words text-2xl font-bold tracking-tight">{value}</p>
    </div>
  );
}

function DestinationStateBadge({ destination }) {
  if (destination.isPublished) {
    return <Badge tone="emerald">Visible</Badge>;
  }

  if (destination.canPublish) {
    return <Badge tone="blue">Lista para mostrar</Badge>;
  }

  return <Badge tone="amber">En preparacion</Badge>;
}

function DestinationListCard({ destination, canEdit, canPublish, onEdit, onView, onCopy, onPublish, onUnpublish }) {
  const stateLabel = destination.isPublished
    ? "Visible en el sitio"
    : destination.canPublish
      ? "Lista para mostrar"
      : "En preparacion";
  const activeDeparturesLabel =
    destination.departureCount > destination.activeDepartureCount
      ? `${destination.activeDepartureCount} de ${destination.departureCount} activas`
      : `${destination.activeDepartureCount} activas`;

  return (
    <article className="rounded-2xl border border-slate-200 bg-white p-4 shadow-sm transition hover:border-slate-300 dark:border-slate-800 dark:bg-slate-900/50 dark:hover:border-slate-700 lg:p-5">
      <div className="flex flex-col gap-4 2xl:flex-row 2xl:items-start 2xl:justify-between">
        <div className="min-w-0 flex-1">
          <div className="flex flex-col gap-4 lg:flex-row lg:items-start lg:justify-between">
            <div className="min-w-0">
              <div className="flex flex-wrap gap-2">
                {canPublish ? <DestinationStateBadge destination={destination} /> : null}
                <Badge tone="slate">{activeDeparturesLabel}</Badge>
              </div>

              <h3 className="mt-3 text-lg font-semibold tracking-tight text-slate-900 dark:text-white">
                {destination.title || destination.name}
              </h3>

              {destination.title && destination.name && destination.title !== destination.name ? (
                <p className="mt-1 text-sm text-slate-500 dark:text-slate-400">{destination.name}</p>
              ) : destination.tagline ? (
                <p className="mt-1 text-sm text-slate-500 dark:text-slate-400">{destination.tagline}</p>
              ) : null}
            </div>

            <div className="rounded-xl border border-slate-200 bg-slate-50 px-4 py-3 dark:border-slate-800 dark:bg-slate-950/40">
              <p className="text-xs font-semibold uppercase tracking-[0.18em] text-slate-500 dark:text-slate-400">
                Precio desde
              </p>
              <p className="mt-1 text-lg font-semibold text-slate-900 dark:text-white">
                {formatMoney(destination.fromPrice, destination.currency)}
              </p>
            </div>
          </div>

          <div className="mt-4 grid gap-3 sm:grid-cols-2 xl:grid-cols-3">
            <DestinationMetaItem label="Proxima salida" value={formatLongDate(destination.nextDepartureDate)} />
            <DestinationMetaItem label="Salidas" value={activeDeparturesLabel} />
            <DestinationMetaItem label="Publicacion" value={stateLabel} />
          </div>
        </div>
        <div className="flex flex-wrap gap-2 2xl:max-w-[320px] 2xl:justify-end">
          {canEdit ? (
            <Button variant="outline" size="sm" onClick={onEdit} className="gap-2">
              <Pencil className="h-4 w-4" />
              Editar
            </Button>
          ) : null}
          {canPublish ? (
            <>
              <Button variant="outline" size="sm" onClick={onView} className="gap-2">
                <Eye className="h-4 w-4" />
                Vista previa
              </Button>
              <Button variant="outline" size="sm" onClick={onCopy} className="gap-2">
                <Copy className="h-4 w-4" />
                Copiar codigo
              </Button>
              {destination.isPublished ? (
                <Button variant="outline" size="sm" onClick={onUnpublish} className="gap-2">
                  <Rocket className="h-4 w-4" />
                  Ocultar
                </Button>
              ) : destination.canPublish ? (
                <Button size="sm" onClick={onPublish} className="gap-2">
                  <Rocket className="h-4 w-4" />
                  Publicar
                </Button>
              ) : null}
            </>
          ) : null}
        </div>
      </div>
    </article>
  );
}

function DestinationMetaItem({ label, value }) {
  return (
    <div className="rounded-xl border border-slate-200 bg-slate-50/70 px-4 py-3 dark:border-slate-800 dark:bg-slate-950/30">
      <p className="text-[11px] font-semibold uppercase tracking-[0.16em] text-slate-500 dark:text-slate-400">{label}</p>
      <p className="mt-1 text-sm font-medium text-slate-900 dark:text-white">{value}</p>
    </div>
  );
}

function CountryModal({ open, form, saving, onChange, onClose, onSubmit }) {
  if (!open) {
    return null;
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-slate-950/60 p-4 backdrop-blur-sm">
      <div className="w-full max-w-md rounded-xl border border-slate-200 bg-white shadow-2xl dark:border-slate-800 dark:bg-slate-900">
        <div className="border-b border-slate-200 px-6 py-4 dark:border-slate-800">
          <h3 className="text-lg font-semibold text-slate-900 dark:text-white">{form.publicId ? "Editar pais" : "Nuevo pais"}</h3>
          <p className="mt-1 text-sm text-slate-500 dark:text-slate-400">
            Crea un pais para agrupar destinos y ordenar la oferta del sitio.
          </p>
        </div>

        <form onSubmit={onSubmit} className="space-y-4 p-6">
          <label className="block space-y-2">
            <span className="text-sm font-medium text-slate-700 dark:text-slate-200">Nombre del pais</span>
            <input
              type="text"
              value={form.name}
              onChange={(event) => onChange(event.target.value)}
              placeholder="Ej. Republica Dominicana"
              className={inputClass}
            />
          </label>

          <div className="flex justify-end gap-2 border-t border-slate-200 pt-4 dark:border-slate-800">
            <Button type="button" variant="outline" onClick={onClose}>
              Cancelar
            </Button>
            <Button type="submit" disabled={saving} className="gap-2">
              {saving ? <Loader2 className="h-4 w-4 animate-spin" /> : null}
              Guardar
            </Button>
          </div>
        </form>
      </div>
    </div>
  );
}

function Badge({ children, tone = "slate" }) {
  const toneClass =
    tone === "emerald"
      ? "bg-emerald-100 text-emerald-700 dark:bg-emerald-900/30 dark:text-emerald-300"
      : tone === "blue"
        ? "bg-blue-100 text-blue-700 dark:bg-blue-900/30 dark:text-blue-300"
        : tone === "amber"
          ? "bg-amber-100 text-amber-700 dark:bg-amber-900/30 dark:text-amber-300"
          : "bg-slate-100 text-slate-700 dark:bg-slate-800 dark:text-slate-200";

  return <span className={`rounded-full px-2.5 py-1 text-xs font-semibold ${toneClass}`}>{children}</span>;
}
