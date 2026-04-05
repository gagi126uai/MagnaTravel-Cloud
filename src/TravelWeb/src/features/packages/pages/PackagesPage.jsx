import { useCallback, useEffect, useMemo, useState } from "react";
import { useNavigate, useSearchParams } from "react-router-dom";
import {
  ArrowLeft,
  Building2,
  ChevronRight,
  Copy,
  Eye,
  Globe2,
  ImagePlus,
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
import { ListEmptyState } from "../../../components/ui/ListEmptyState";
import { ListPageHeader } from "../../../components/ui/ListPageHeader";
import { useDebounce } from "../../../hooks/useDebounce";
import {
  buildCountryPublicationSnippet,
  buildDestinationPublicationSnippet,
  formatLongDate,
  formatMoney,
  mapCountryForm,
} from "../lib/publicationUtils";

const inputClass =
  "w-full rounded-2xl border border-slate-200 bg-white px-4 py-3 text-sm text-slate-900 outline-none transition placeholder:text-slate-400 focus:border-sky-500 focus:ring-4 focus:ring-sky-500/10 dark:border-slate-700 dark:bg-slate-900 dark:text-white";
const cardClass =
  "rounded-[28px] bg-white shadow-[0_18px_45px_rgba(15,23,42,0.06)] ring-1 ring-slate-950/5 dark:bg-slate-900/70 dark:ring-white/10";
const mutedPanelClass =
  "rounded-[24px] bg-slate-50/90 ring-1 ring-slate-200/80 dark:bg-slate-950/40 dark:ring-slate-800/80";

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
  const [isDesktopViewport, setIsDesktopViewport] = useState(() => {
    if (typeof window === "undefined") {
      return true;
    }

    return window.matchMedia("(min-width: 768px)").matches;
  });

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
    if (typeof window === "undefined") {
      return undefined;
    }

    const mediaQuery = window.matchMedia("(min-width: 768px)");
    const handleChange = () => setIsDesktopViewport(mediaQuery.matches);

    handleChange();
    if (typeof mediaQuery.addEventListener === "function") {
      mediaQuery.addEventListener("change", handleChange);
      return () => mediaQuery.removeEventListener("change", handleChange);
    }

    mediaQuery.addListener(handleChange);
    return () => mediaQuery.removeListener(handleChange);
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

    if (!selectedCountryPublicId) {
      if (isDesktopViewport) {
        syncSelectedCountry(countries[0].publicId);
      }
      return;
    }

    const exists = countries.some((country) => country.publicId === selectedCountryPublicId);
    if (!exists) {
      syncSelectedCountry(isDesktopViewport ? countries[0].publicId : "");
    }
  }, [countries, countriesLoading, isDesktopViewport, selectedCountryPublicId, syncSelectedCountry]);

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

  const visibleDestinationCount = useMemo(
    () => filteredDestinations.filter((destination) => destination.isPublished).length,
    [filteredDestinations]
  );
  const hiddenDestinationCount = Math.max(filteredDestinations.length - visibleDestinationCount, 0);

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
    <div className="animate-in fade-in space-y-5 duration-500 md:space-y-6">
      <div className="hidden md:block">
        <ListPageHeader
          title="Paises y destinos"
          subtitle="Organiza la oferta del sitio con una vista clara por pais y administra los destinos sin perder contexto."
          actions={
            canEdit ? (
              <>
                <Button variant="outline" onClick={openCreateCountryModal} className="gap-2 rounded-2xl">
                  <Plus className="h-4 w-4" />
                  Nuevo pais
                </Button>
                {selectedCountry ? (
                  <Button onClick={openCreateDestination} className="gap-2 rounded-2xl">
                    <Plus className="h-4 w-4" />
                    Nuevo destino
                  </Button>
                ) : null}
              </>
            ) : null
          }
        />
      </div>

      <div className="hidden md:grid md:grid-cols-[320px_minmax(0,1fr)] md:items-start md:gap-6">
        <aside className={`${cardClass} overflow-hidden md:sticky md:top-20`}>
          <div className="border-b border-slate-200/70 px-5 py-5 dark:border-slate-800/70">
            <div className="flex items-start justify-between gap-3">
              <div>
                <p className="text-[11px] font-semibold uppercase tracking-[0.2em] text-slate-400">Catalogo</p>
                <h2 className="mt-2 text-xl font-bold tracking-tight text-slate-900 dark:text-white">Paises</h2>
                <p className="mt-1 text-sm text-slate-500 dark:text-slate-400">
                  Selecciona un pais para ver y administrar sus destinos.
                </p>
              </div>
              <span className="rounded-full bg-slate-100 px-3 py-1 text-xs font-semibold text-slate-600 dark:bg-slate-800 dark:text-slate-200">
                {countries.length}
              </span>
            </div>

            <div className="relative mt-5">
              <Search className="pointer-events-none absolute left-4 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" />
              <input
                type="text"
                value={countrySearch}
                onChange={(event) => setCountrySearch(event.target.value)}
                placeholder="Filtrar paises..."
                className={`${inputClass} pl-11`}
              />
            </div>

            <div className="mt-5 grid grid-cols-2 gap-3">
              <MiniStatCard label="Paises" value={portfolioSummary.totalCountries} accent="sky" />
              <MiniStatCard label="Destinos" value={portfolioSummary.totalDestinations} />
            </div>
          </div>

          <div className="space-y-2 overflow-y-auto p-3 md:max-h-[calc(100vh-290px)]">
            {countriesLoading ? (
              <div className="flex h-44 items-center justify-center">
                <Loader2 className="h-6 w-6 animate-spin text-sky-600" />
              </div>
            ) : countries.length === 0 ? (
              <ListEmptyState
                icon={Building2}
                title="Todavia no hay paises"
                description="Crea el primero para empezar a organizar la oferta."
                compact
              />
            ) : (
              countries.map((country) => (
                <CountryRailItem
                  key={country.publicId}
                  country={country}
                  selected={country.publicId === selectedCountryPublicId}
                  onClick={() => syncSelectedCountry(country.publicId)}
                />
              ))
            )}
          </div>
        </aside>

        <section className="space-y-5">
          {!selectedCountry ? (
            <div className={`${cardClass} p-8`}>
              <ListEmptyState
                icon={Globe2}
                title="Selecciona un pais"
                description="Cuando elijas un pais, aqui veras un resumen claro y una grilla de destinos lista para trabajar."
              />
            </div>
          ) : (
            <>
              <DesktopCountryHero
                country={selectedCountry}
                canEdit={canEdit}
                canPublish={canPublish}
                onEdit={() => openEditCountryModal(selectedCountry)}
                onCreateDestination={openCreateDestination}
                onPreview={() => openCountryPreview(selectedCountry)}
                onCopy={() => copyCountryPublication(selectedCountry)}
              />

              <DestinationToolbarPanel
                searchValue={destinationSearch}
                onSearchChange={setDestinationSearch}
                filterValue={destinationStatusFilter}
                onFilterChange={setDestinationStatusFilter}
                filteredCount={filteredDestinations.length}
                visibleCount={visibleDestinationCount}
                hiddenCount={hiddenDestinationCount}
                canPublish={canPublish}
              />

              {destinationsLoading ? (
                <div className={`${cardClass} flex items-center justify-center px-4 py-16`}>
                  <Loader2 className="h-7 w-7 animate-spin text-sky-600" />
                </div>
              ) : filteredDestinations.length === 0 ? (
                <div className={`${cardClass} p-4`}>
                  <ListEmptyState
                    icon={MapPinned}
                    title={destinations.length === 0 ? "Todavia no hay destinos cargados" : "No encontramos destinos"}
                    description={
                      destinations.length === 0
                        ? "Crea el primer destino de este pais para empezar a cargar salidas y contenido."
                        : "Ajusta la busqueda o el filtro para ver otros resultados."
                    }
                    className="rounded-[24px] border border-slate-200 bg-white shadow-none dark:border-slate-800 dark:bg-slate-900/50"
                  />
                </div>
              ) : (
                <div className="grid gap-4 lg:grid-cols-2 2xl:grid-cols-3">
                  {filteredDestinations.map((destination) => (
                    <DestinationVisualCard
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

      <div className="space-y-4 md:hidden">
        {!selectedCountry ? (
          <MobileCountrySelectionScene
            countries={countries}
            countriesLoading={countriesLoading}
            countrySearch={countrySearch}
            onCountrySearchChange={setCountrySearch}
            onSelectCountry={syncSelectedCountry}
            portfolioSummary={portfolioSummary}
            canEdit={canEdit}
            onCreateCountry={openCreateCountryModal}
          />
        ) : (
          <MobileDestinationScene
            country={selectedCountry}
            destinations={filteredDestinations}
            allDestinations={destinations}
            loading={destinationsLoading}
            searchValue={destinationSearch}
            onSearchChange={setDestinationSearch}
            filterValue={destinationStatusFilter}
            onFilterChange={setDestinationStatusFilter}
            canEdit={canEdit}
            canPublish={canPublish}
            onBack={() => syncSelectedCountry("")}
            onEditCountry={() => openEditCountryModal(selectedCountry)}
            onCreateDestination={openCreateDestination}
            onPreviewCountry={() => openCountryPreview(selectedCountry)}
            onCopyCountry={() => copyCountryPublication(selectedCountry)}
            onEditDestination={(destinationPublicId) => openDestinationEditor(destinationPublicId)}
            onViewDestination={openDestinationPreview}
            onCopyDestination={copyDestinationPublication}
            onPublishDestination={handlePublish}
            onUnpublishDestination={handleUnpublish}
          />
        )}
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

function MobileCountrySelectionScene({
  countries,
  countriesLoading,
  countrySearch,
  onCountrySearchChange,
  onSelectCountry,
  portfolioSummary,
  canEdit,
  onCreateCountry,
}) {
  return (
    <div className="space-y-4">
      <div className={`${cardClass} overflow-hidden`}>
        <div className="flex items-center justify-between gap-3 px-5 py-5">
          <div className="min-w-0">
            <p className="text-[11px] font-semibold uppercase tracking-[0.2em] text-slate-400">Catalogo</p>
            <h1 className="mt-2 text-2xl font-bold tracking-tight text-slate-900 dark:text-white">Paises y destinos</h1>
          </div>
          {canEdit ? (
            <Button size="icon" onClick={onCreateCountry} className="h-11 w-11 rounded-2xl">
              <Plus className="h-5 w-5" />
            </Button>
          ) : null}
        </div>

        <div className="border-t border-slate-200/70 px-5 py-4 dark:border-slate-800/70">
          <div className="relative">
            <Search className="pointer-events-none absolute left-4 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" />
            <input
              type="text"
              value={countrySearch}
              onChange={(event) => onCountrySearchChange(event.target.value)}
              placeholder="Buscar por pais o region..."
              className={`${inputClass} border-transparent bg-slate-100/90 pl-11 dark:bg-slate-950`}
            />
          </div>

          <div className="mt-4 grid grid-cols-2 gap-3">
            <MiniStatCard label="Paises cargados" value={portfolioSummary.totalCountries} accent="sky" />
            <MiniStatCard label="Destinos totales" value={portfolioSummary.totalDestinations} />
          </div>
        </div>
      </div>

      <div className={`${cardClass} p-3`}>
        <div className="px-2 pb-3 pt-1">
          <h2 className="text-lg font-bold tracking-tight text-slate-900 dark:text-white">Destinos globales</h2>
          <p className="mt-1 text-sm text-slate-500 dark:text-slate-400">
            Entra por pais y luego continua con el detalle de destinos.
          </p>
        </div>

        {countriesLoading ? (
          <div className="flex h-44 items-center justify-center">
            <Loader2 className="h-6 w-6 animate-spin text-sky-600" />
          </div>
        ) : countries.length === 0 ? (
          <ListEmptyState
            icon={Building2}
            title="Todavia no hay paises"
            description="Crea el primero para empezar a organizar la oferta."
            className="rounded-[24px] border border-slate-200 bg-white shadow-none dark:border-slate-800 dark:bg-slate-900/50"
          />
        ) : (
          <div className="space-y-2">
            {countries.map((country) => (
              <CountryStackItem key={country.publicId} country={country} onClick={() => onSelectCountry(country.publicId)} />
            ))}
          </div>
        )}
      </div>
    </div>
  );
}

function MobileDestinationScene({
  country,
  destinations,
  allDestinations,
  loading,
  searchValue,
  onSearchChange,
  filterValue,
  onFilterChange,
  canEdit,
  canPublish,
  onBack,
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

  return (
    <div className="space-y-4">
      <div className={`${cardClass} overflow-hidden`}>
        <div className="flex items-center justify-between gap-3 px-4 py-4">
          <button
            type="button"
            onClick={onBack}
            className="flex h-11 w-11 items-center justify-center rounded-2xl bg-slate-100 text-slate-700 transition hover:bg-slate-200 dark:bg-slate-800 dark:text-slate-200 dark:hover:bg-slate-700"
          >
            <ArrowLeft className="h-5 w-5" />
          </button>

          <div className="min-w-0 flex-1">
            <p className="text-[11px] font-semibold uppercase tracking-[0.2em] text-slate-400">Pais seleccionado</p>
            <h1 className="truncate text-xl font-bold tracking-tight text-slate-900 dark:text-white">{country.name}</h1>
          </div>

          {canEdit ? (
            <Button size="icon" onClick={onCreateDestination} className="h-11 w-11 rounded-2xl">
              <Plus className="h-5 w-5" />
            </Button>
          ) : null}
        </div>

        <div className="border-t border-slate-200/70 px-4 py-4 dark:border-slate-800/70">
          <div className={`${mutedPanelClass} p-4`}>
            <div className="flex flex-wrap gap-2">
              <Badge tone="sky">{country.totalDestinations} destinos</Badge>
              <Badge tone="emerald">{country.publishedDestinations} visibles</Badge>
              <Badge tone="amber">{country.draftDestinations} en preparacion</Badge>
            </div>

            <div className="mt-4 grid grid-cols-2 gap-3">
              <MiniStatCard label="Resultados" value={destinations.length} accent="sky" />
              <MiniStatCard label="Visibles" value={visibleCount} accent="emerald" />
            </div>

            {(canEdit || canPublish) ? (
              <div className="mt-4 flex flex-wrap gap-2">
                {canEdit ? (
                  <Button variant="outline" size="sm" onClick={onEditCountry} className="rounded-xl">
                    Editar pais
                  </Button>
                ) : null}
                {canPublish ? (
                  <>
                    <Button variant="outline" size="sm" onClick={onPreviewCountry} className="rounded-xl">
                      Ver como cliente
                    </Button>
                    <Button variant="outline" size="sm" onClick={onCopyCountry} className="rounded-xl">
                      Copiar codigo
                    </Button>
                  </>
                ) : null}
              </div>
            ) : null}
          </div>

          <div className="mt-4 space-y-3">
            <div className="relative">
              <Search className="pointer-events-none absolute left-4 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" />
              <input
                type="text"
                value={searchValue}
                onChange={(event) => onSearchChange(event.target.value)}
                placeholder="Buscar destinos..."
                className={`${inputClass} border-transparent bg-slate-100/90 pl-11 dark:bg-slate-950`}
              />
            </div>

            {canPublish ? (
              <select
                value={filterValue}
                onChange={(event) => onFilterChange(event.target.value)}
                className={`${inputClass} border-transparent bg-slate-100/90 dark:bg-slate-950`}
              >
                <option value="all">Todos los estados</option>
                <option value="visible">Visibles en el sitio</option>
                <option value="hidden">En preparacion</option>
              </select>
            ) : null}
          </div>
        </div>
      </div>

      {loading ? (
        <div className={`${cardClass} flex items-center justify-center px-4 py-16`}>
          <Loader2 className="h-7 w-7 animate-spin text-sky-600" />
        </div>
      ) : destinations.length === 0 ? (
        <div className={`${cardClass} p-4`}>
          <ListEmptyState
            icon={MapPinned}
            title={allDestinations.length === 0 ? "Todavia no hay destinos cargados" : "No encontramos destinos"}
            description={
              allDestinations.length === 0
                ? "Crea el primer destino de este pais para empezar a cargar salidas y contenido."
                : "Ajusta la busqueda o el filtro para ver otros resultados."
            }
            className="rounded-[24px] border border-slate-200 bg-white shadow-none dark:border-slate-800 dark:bg-slate-900/50"
          />
        </div>
      ) : (
        <div className="space-y-4">
          {destinations.map((destination) => (
            <DestinationVisualCard
              key={destination.publicId}
              destination={destination}
              canEdit={canEdit}
              canPublish={canPublish}
              onEdit={() => onEditDestination(destination.publicId)}
              onView={() => onViewDestination(destination)}
              onCopy={() => onCopyDestination(destination)}
              onPublish={() => onPublishDestination(destination)}
              onUnpublish={() => onUnpublishDestination(destination)}
              compact
            />
          ))}
        </div>
      )}
    </div>
  );
}

function DesktopCountryHero({ country, canEdit, canPublish, onEdit, onCreateDestination, onPreview, onCopy }) {
  return (
    <div className={`${cardClass} overflow-hidden`}>
      <div className="px-6 py-6 lg:px-7">
        <div className="flex flex-col gap-6 xl:flex-row xl:items-start xl:justify-between">
          <div className="min-w-0 flex-1">
            <div className="flex items-center gap-2 text-[11px] font-semibold uppercase tracking-[0.2em] text-slate-400">
              <span>Destinos</span>
              <ChevronRight className="h-3 w-3" />
              <span className="truncate">{country.name}</span>
            </div>

            <h2 className="mt-4 text-3xl font-bold tracking-tight text-slate-950 dark:text-white">{country.name}</h2>
            <p className="mt-3 max-w-3xl text-sm leading-6 text-slate-500 dark:text-slate-400">
              Explora la oferta actual del pais, revisa rapidamente el estado comercial de cada destino y accede a sus acciones clave sin salir del contexto.
            </p>

            <div className="mt-5 flex flex-wrap gap-2">
              <Badge tone="sky">{country.totalDestinations} destinos cargados</Badge>
              <Badge tone="emerald">{country.publishedDestinations} visibles</Badge>
              <Badge tone="amber">{country.draftDestinations} en preparacion</Badge>
            </div>
          </div>

          {(canEdit || canPublish) ? (
            <div className="flex flex-wrap gap-2 xl:max-w-[360px] xl:justify-end">
              {canEdit ? (
                <>
                  <Button variant="outline" onClick={onEdit} className="gap-2 rounded-2xl">
                    <Pencil className="h-4 w-4" />
                    Editar pais
                  </Button>
                  <Button onClick={onCreateDestination} className="gap-2 rounded-2xl">
                    <Plus className="h-4 w-4" />
                    Nuevo destino
                  </Button>
                </>
              ) : null}
              {canPublish ? (
                <>
                  <Button variant="outline" onClick={onPreview} className="gap-2 rounded-2xl">
                    <Eye className="h-4 w-4" />
                    Ver como cliente
                  </Button>
                  <Button variant="outline" onClick={onCopy} className="gap-2 rounded-2xl">
                    <Copy className="h-4 w-4" />
                    Copiar codigo
                  </Button>
                </>
              ) : null}
            </div>
          ) : null}
        </div>
      </div>

      <div className="border-t border-slate-200/70 px-6 py-5 dark:border-slate-800/70 lg:px-7">
        <div className="grid gap-3 sm:grid-cols-3">
          <HeroMetricCard label="Destinos" value={country.totalDestinations} />
          <HeroMetricCard label="Visibles en el sitio" value={country.publishedDestinations} accent="emerald" />
          <HeroMetricCard label="En preparacion" value={country.draftDestinations} accent="amber" />
        </div>
      </div>
    </div>
  );
}

function DestinationToolbarPanel({
  searchValue,
  onSearchChange,
  filterValue,
  onFilterChange,
  filteredCount,
  visibleCount,
  hiddenCount,
  canPublish,
}) {
  return (
    <div className={`${cardClass} px-5 py-5 lg:px-6`}>
      <div className="flex flex-col gap-4 xl:flex-row xl:items-center xl:justify-between">
        <div className="min-w-0">
          <p className="text-[11px] font-semibold uppercase tracking-[0.2em] text-slate-400">Panel de destinos</p>
          <div className="mt-3 flex flex-wrap gap-2">
            <ToolbarChip label="Mostrando" value={filteredCount} tone="sky" />
            <ToolbarChip label="Visibles" value={visibleCount} tone="emerald" />
            <ToolbarChip label="En preparacion" value={hiddenCount} tone="amber" />
          </div>
        </div>

        <div className={`grid gap-3 ${canPublish ? "xl:min-w-[520px] md:grid-cols-[minmax(0,1fr)_220px]" : "xl:min-w-[360px]"}`}>
          <div className="relative">
            <Search className="pointer-events-none absolute left-4 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" />
            <input
              type="text"
              value={searchValue}
              onChange={(event) => onSearchChange(event.target.value)}
              placeholder="Buscar destino, titulo o tagline..."
              className={`${inputClass} bg-slate-50/80 pl-11 dark:bg-slate-950/60`}
            />
          </div>

          {canPublish ? (
            <select
              value={filterValue}
              onChange={(event) => onFilterChange(event.target.value)}
              className={`${inputClass} bg-slate-50/80 dark:bg-slate-950/60`}
            >
              <option value="all">Todos los estados</option>
              <option value="visible">Visibles en el sitio</option>
              <option value="hidden">En preparacion</option>
            </select>
          ) : null}
        </div>
      </div>
    </div>
  );
}

function CountryRailItem({ country, selected, onClick }) {
  const isVisible = country.publishedDestinations > 0;

  return (
    <button
      type="button"
      onClick={onClick}
      className={`group relative w-full overflow-hidden rounded-[24px] border px-4 py-4 text-left transition ${
        selected
          ? "border-sky-200 bg-sky-50/90 shadow-[0_20px_40px_rgba(14,165,233,0.12)] dark:border-sky-900/70 dark:bg-sky-950/20"
          : "border-slate-200/80 bg-white hover:border-slate-300 hover:bg-slate-50 dark:border-slate-800 dark:bg-slate-900/40 dark:hover:border-slate-700 dark:hover:bg-slate-900/70"
      }`}
    >
      <span
        className={`absolute inset-y-4 left-0 w-1 rounded-r-full transition ${
          selected ? "bg-sky-500" : "bg-transparent group-hover:bg-slate-200 dark:group-hover:bg-slate-700"
        }`}
      />

      <div className="flex items-start gap-3">
        <CountryAvatar name={country.name} selected={selected} />

        <div className="min-w-0 flex-1">
          <div className="flex flex-wrap items-center gap-2">
            <p className="min-w-0 flex-1 break-words text-sm font-semibold text-slate-900 dark:text-white">{country.name}</p>
            <Badge tone={isVisible ? "emerald" : "amber"}>
              {isVisible ? `${country.publishedDestinations} visibles` : "En preparacion"}
            </Badge>
          </div>

          <div className="mt-3 flex flex-wrap items-center gap-x-3 gap-y-2 text-xs text-slate-500 dark:text-slate-400">
            <span>{country.totalDestinations} destinos</span>
            <span>{country.draftDestinations} por completar</span>
          </div>
        </div>

        <ChevronRight
          className={`mt-1 h-4 w-4 shrink-0 transition ${
            selected ? "text-sky-500" : "text-slate-300 group-hover:translate-x-0.5 group-hover:text-slate-500 dark:text-slate-600"
          }`}
        />
      </div>
    </button>
  );
}

function CountryStackItem({ country, onClick }) {
  const isVisible = country.publishedDestinations > 0;

  return (
    <button
      type="button"
      onClick={onClick}
      className="group w-full rounded-[24px] border border-slate-200/80 bg-white px-4 py-4 text-left transition hover:border-slate-300 hover:bg-slate-50 dark:border-slate-800 dark:bg-slate-900/50 dark:hover:border-slate-700 dark:hover:bg-slate-900"
    >
      <div className="flex items-center gap-3">
        <CountryAvatar name={country.name} compact />

        <div className="min-w-0 flex-1">
          <div className="flex flex-wrap items-center gap-2">
            <p className="min-w-0 flex-1 break-words text-sm font-semibold text-slate-900 dark:text-white">{country.name}</p>
            <Badge tone={isVisible ? "emerald" : "amber"}>
              {isVisible ? `${country.publishedDestinations} visibles` : "En preparacion"}
            </Badge>
          </div>

          <p className="mt-1 break-words text-xs text-slate-500 dark:text-slate-400">
            {country.totalDestinations} destinos cargados
          </p>
        </div>

        <ChevronRight className="h-4 w-4 shrink-0 text-slate-300 transition group-hover:translate-x-0.5 group-hover:text-slate-500 dark:text-slate-600" />
      </div>
    </button>
  );
}

function CountryAvatar({ name, selected = false, compact = false }) {
  const initials = String(name || "PA")
    .split(/\s+/)
    .filter(Boolean)
    .slice(0, 2)
    .map((part) => part[0]?.toUpperCase() || "")
    .join("");

  return (
    <div
      className={`flex shrink-0 items-center justify-center rounded-[20px] font-semibold ${
        compact ? "h-11 w-11 text-sm" : "h-12 w-12 text-base"
      } ${
        selected
          ? "bg-sky-500 text-white shadow-[0_14px_30px_rgba(14,165,233,0.3)]"
          : "bg-slate-100 text-slate-700 dark:bg-slate-800 dark:text-slate-200"
      }`}
    >
      {initials || "PA"}
    </div>
  );
}

function MiniStatCard({ label, value, accent = "slate" }) {
  const accentClass =
    accent === "sky"
      ? "bg-sky-50 text-sky-900 ring-sky-100 dark:bg-sky-950/20 dark:text-sky-100 dark:ring-sky-900/60"
      : accent === "emerald"
        ? "bg-emerald-50 text-emerald-900 ring-emerald-100 dark:bg-emerald-950/20 dark:text-emerald-100 dark:ring-emerald-900/60"
        : accent === "amber"
          ? "bg-amber-50 text-amber-900 ring-amber-100 dark:bg-amber-950/20 dark:text-amber-100 dark:ring-amber-900/60"
          : "bg-slate-50 text-slate-900 ring-slate-200/80 dark:bg-slate-950/40 dark:text-white dark:ring-slate-800";

  return (
    <div className={`rounded-[22px] px-4 py-4 ring-1 ${accentClass}`}>
      <p className="text-[11px] font-semibold uppercase tracking-[0.18em] text-slate-500 dark:text-slate-400">{label}</p>
      <p className="mt-2 break-words text-2xl font-bold tracking-tight">{value}</p>
    </div>
  );
}

function DestinationVisualCard({
  destination,
  canEdit,
  canPublish,
  onEdit,
  onView,
  onCopy,
  onPublish,
  onUnpublish,
  compact = false,
}) {
  const heroImageUrl = destination.heroImageUrl ? buildAppUrl(destination.heroImageUrl) : null;
  const title = destination.title || destination.name;
  const secondaryLine =
    destination.title && destination.name && destination.title !== destination.name
      ? destination.name
      : destination.tagline || "";
  const stateLabel = destination.isPublished
    ? "Visible en el sitio"
    : destination.canPublish
      ? "Lista para mostrar"
      : destination.publishIssues?.length
        ? "Completar datos"
        : "En preparacion";
  const activeDeparturesLabel =
    destination.departureCount > destination.activeDepartureCount
      ? `${destination.activeDepartureCount} de ${destination.departureCount} activas`
      : `${destination.activeDepartureCount} activas`;
  const priceLabel =
    destination.fromPrice != null ? formatMoney(destination.fromPrice, destination.currency) : "Sin tarifa principal";

  return (
    <article className={`${cardClass} overflow-hidden`}>
      <div className={`relative ${compact ? "h-48" : "h-52"}`}>
        {heroImageUrl ? (
          <img src={heroImageUrl} alt={title} className="h-full w-full object-cover" />
        ) : (
          <div className="flex h-full w-full items-center justify-center bg-[radial-gradient(circle_at_top_left,_rgba(14,165,233,0.22),_transparent_35%),linear-gradient(135deg,rgba(15,23,42,0.95),rgba(30,41,59,0.84))] px-6 text-center">
            <div>
              <div className="mx-auto flex h-14 w-14 items-center justify-center rounded-2xl bg-white/10 text-white ring-1 ring-white/15">
                <ImagePlus className="h-6 w-6" />
              </div>
              <p className="mt-3 text-sm font-medium text-white">Agrega una imagen portada para destacar este destino</p>
            </div>
          </div>
        )}

        <div className="absolute inset-0 bg-gradient-to-t from-slate-950/80 via-slate-950/25 to-transparent" />

        <div className="absolute left-4 right-4 top-4 flex flex-wrap items-center justify-between gap-2">
          <div className="flex flex-wrap gap-2">
            {canPublish ? <DestinationStateBadge destination={destination} /> : null}
            {destination.publishIssues?.length && !destination.isPublished ? (
              <Badge tone="slate">{destination.publishIssues.length} pendientes</Badge>
            ) : null}
          </div>

          <span className="inline-flex items-center gap-2 rounded-full bg-slate-950/45 px-3 py-1.5 text-xs font-medium text-white backdrop-blur-sm">
            <Rocket className="h-3.5 w-3.5" />
            {activeDeparturesLabel}
          </span>
        </div>

        <div className="absolute bottom-4 left-4 right-4 flex flex-col gap-3 lg:flex-row lg:items-end lg:justify-between">
          <div className="min-w-0">
            <p className="text-[11px] font-semibold uppercase tracking-[0.2em] text-white/65">{destination.countryName}</p>
            <h3 className="mt-2 break-words text-2xl font-semibold tracking-tight text-white">{title}</h3>
            {secondaryLine ? <p className="mt-2 max-w-xl break-words text-sm text-white/80">{secondaryLine}</p> : null}
          </div>

          <div className="w-full max-w-[220px] rounded-[22px] bg-white/95 px-4 py-3 text-slate-900 shadow-lg shadow-slate-950/15 backdrop-blur-sm">
            <p className="text-[11px] font-semibold uppercase tracking-[0.18em] text-slate-500">Precio desde</p>
            <p className="mt-1 break-words text-lg font-semibold">{priceLabel}</p>
          </div>
        </div>
      </div>

      <div className={compact ? "space-y-4 p-4" : "space-y-4 p-5"}>
        <div className={`${mutedPanelClass} ${compact ? "p-3.5" : "p-4"}`}>
          <div className={`grid gap-3 ${compact ? "sm:grid-cols-2" : "xl:grid-cols-3"}`}>
            <DestinationMetricItem label="Proxima salida" value={formatLongDate(destination.nextDepartureDate)} />
            <DestinationMetricItem label="Salidas" value={activeDeparturesLabel} />
            <DestinationMetricItem label="Publicacion" value={stateLabel} />
          </div>

          {canPublish && !destination.isPublished && !destination.canPublish && destination.publishIssues?.length ? (
            <p className="mt-3 break-words text-xs text-amber-700 dark:text-amber-300">
              Completa los datos pendientes para poder publicarlo en el sitio.
            </p>
          ) : null}
        </div>

        {(canEdit || canPublish) ? (
          <div className="flex flex-wrap gap-2">
            {canEdit ? (
              <Button variant="outline" size="sm" onClick={onEdit} className="gap-2 rounded-xl">
                <Pencil className="h-4 w-4" />
                Editar
              </Button>
            ) : null}

            {canPublish ? (
              <>
                <Button variant="outline" size="sm" onClick={onView} className="gap-2 rounded-xl">
                  <Eye className="h-4 w-4" />
                  Vista previa
                </Button>
                <Button variant="outline" size="sm" onClick={onCopy} className="gap-2 rounded-xl">
                  <Copy className="h-4 w-4" />
                  Copiar codigo
                </Button>
                {destination.isPublished ? (
                  <Button variant="outline" size="sm" onClick={onUnpublish} className="gap-2 rounded-xl">
                    <Rocket className="h-4 w-4" />
                    Ocultar del sitio
                  </Button>
                ) : (
                  <Button size="sm" onClick={onPublish} disabled={!destination.canPublish} className="gap-2 rounded-xl">
                    <Rocket className="h-4 w-4" />
                    Mostrar en sitio
                  </Button>
                )}
              </>
            ) : null}
          </div>
        ) : null}
      </div>
    </article>
  );
}

function DestinationMetricItem({ label, value }) {
  return (
    <div className="rounded-[20px] border border-slate-200/80 bg-white/80 px-4 py-3 dark:border-slate-800 dark:bg-slate-900/60">
      <p className="text-[11px] font-semibold uppercase tracking-[0.16em] text-slate-500 dark:text-slate-400">{label}</p>
      <p className="mt-1 break-words text-sm font-medium text-slate-900 dark:text-white">{value}</p>
    </div>
  );
}

function HeroMetricCard({ label, value, accent = "sky" }) {
  const accentClass =
    accent === "emerald"
      ? "bg-emerald-500"
      : accent === "amber"
        ? "bg-amber-500"
        : "bg-sky-500";

  return (
    <div className="rounded-[22px] border border-slate-200/80 bg-slate-50/80 px-4 py-4 dark:border-slate-800 dark:bg-slate-950/40">
      <div className="flex items-center gap-2">
        <span className={`h-2.5 w-2.5 rounded-full ${accentClass}`} />
        <p className="text-[11px] font-semibold uppercase tracking-[0.18em] text-slate-500 dark:text-slate-400">{label}</p>
      </div>
      <p className="mt-3 break-words text-3xl font-bold tracking-tight text-slate-950 dark:text-white">{value}</p>
    </div>
  );
}

function ToolbarChip({ label, value, tone = "slate" }) {
  const toneClass =
    tone === "sky"
      ? "bg-sky-50 text-sky-800 ring-sky-100 dark:bg-sky-950/20 dark:text-sky-200 dark:ring-sky-900/60"
      : tone === "emerald"
        ? "bg-emerald-50 text-emerald-800 ring-emerald-100 dark:bg-emerald-950/20 dark:text-emerald-200 dark:ring-emerald-900/60"
        : tone === "amber"
          ? "bg-amber-50 text-amber-800 ring-amber-100 dark:bg-amber-950/20 dark:text-amber-200 dark:ring-amber-900/60"
          : "bg-slate-50 text-slate-800 ring-slate-200 dark:bg-slate-950/30 dark:text-slate-200 dark:ring-slate-800";

  return (
    <div className={`inline-flex items-center gap-2 rounded-full px-3 py-2 ring-1 ${toneClass}`}>
      <span className="text-[11px] font-semibold uppercase tracking-[0.16em]">{label}</span>
      <span className="text-sm font-semibold">{value}</span>
    </div>
  );
}

function DestinationStateBadge({ destination }) {
  if (destination.isPublished) {
    return <Badge tone="emerald">Visible</Badge>;
  }

  if (destination.canPublish) {
    return <Badge tone="sky">Lista para mostrar</Badge>;
  }

  return <Badge tone="amber">{destination.publishIssues?.length ? "Completar datos" : "En preparacion"}</Badge>;
}

function CountryModal({ open, form, saving, onChange, onClose, onSubmit }) {
  if (!open) {
    return null;
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-slate-950/55 p-4 backdrop-blur-sm">
      <div className={`${cardClass} w-full max-w-lg overflow-hidden`}>
        <div className="border-b border-slate-200/70 px-6 py-5 dark:border-slate-800/70">
          <p className="text-[11px] font-semibold uppercase tracking-[0.2em] text-slate-400">Catalogo</p>
          <h3 className="mt-2 text-2xl font-bold tracking-tight text-slate-900 dark:text-white">
            {form.publicId ? "Editar pais" : "Nuevo pais"}
          </h3>
          <p className="mt-2 text-sm text-slate-500 dark:text-slate-400">
            Crea o ajusta el pais para ordenar mejor la oferta y facilitar la navegacion por destinos.
          </p>
        </div>

        <form onSubmit={onSubmit} className="space-y-5 px-6 py-6">
          <label className="block space-y-2">
            <span className="text-sm font-medium text-slate-700 dark:text-slate-200">Nombre del pais</span>
            <input
              type="text"
              value={form.name}
              onChange={(event) => onChange(event.target.value)}
              placeholder="Ej. Republica Dominicana"
              className={inputClass}
              autoFocus
            />
          </label>

          <div className="flex flex-wrap justify-end gap-2 border-t border-slate-200/70 pt-5 dark:border-slate-800/70">
            <Button type="button" variant="outline" onClick={onClose} className="rounded-2xl">
              Cancelar
            </Button>
            <Button type="submit" disabled={saving} className="gap-2 rounded-2xl">
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
    tone === "sky" || tone === "blue"
      ? "bg-sky-100 text-sky-700 dark:bg-sky-900/30 dark:text-sky-300"
      : tone === "emerald"
        ? "bg-emerald-100 text-emerald-700 dark:bg-emerald-900/30 dark:text-emerald-300"
        : tone === "amber"
          ? "bg-amber-100 text-amber-700 dark:bg-amber-900/30 dark:text-amber-300"
          : "bg-slate-100 text-slate-700 dark:bg-slate-800 dark:text-slate-200";

  return <span className={`inline-flex max-w-full items-center rounded-full px-2.5 py-1 text-xs font-semibold ${toneClass}`}>{children}</span>;
}
