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
  MapPin,
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
import { useDebounce } from "../../../hooks/useDebounce";
import {
  buildCountryPublicationSnippet,
  buildDestinationPublicationSnippet,
  formatLongDate,
  formatMoney,
  mapCountryForm,
} from "../lib/publicationUtils";

const inputClass =
  "w-full rounded-[14px] border border-[#e1e3e4] bg-white px-4 py-3 text-sm text-[#191c1d] outline-none transition placeholder:text-[#737780] focus:border-[#255dad] focus:ring-2 focus:ring-[#255dad]/15";
const cardClass =
  "rounded-[24px] border border-[#e7e8e9] bg-white shadow-[0_12px_30px_rgba(25,28,29,0.04)]";
const mutedPanelClass = "rounded-[18px] border border-[#e7e8e9] bg-[#f3f4f5]";
const pageCanvasClass =
  "overflow-hidden rounded-[30px] border border-[#e1e3e4] bg-[#f8f9fa] text-[#191c1d] shadow-[0_24px_60px_rgba(25,28,29,0.06)]";
const outlineButtonClass =
  "rounded-xl border-[#e1e3e4] bg-white text-[#001d44] hover:bg-[#f3f4f5] hover:text-[#001d44]";
const primaryButtonClass = "rounded-xl bg-[#001d44] text-white hover:bg-[#00326b]";

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
      <div className="hidden items-center justify-end gap-3 md:flex">
        {canEdit ? (
          <Button variant="outline" onClick={openCreateCountryModal} className={`gap-2 ${outlineButtonClass}`}>
            <Plus className="h-4 w-4" />
            Nuevo pais
          </Button>
        ) : null}
        {canEdit && selectedCountry ? (
          <Button onClick={openCreateDestination} className={`gap-2 ${primaryButtonClass}`}>
            <Plus className="h-4 w-4" />
            Nuevo destino
          </Button>
        ) : null}
      </div>

      <div className={`hidden md:block ${pageCanvasClass}`}>
        <div className="grid min-h-[760px] grid-cols-[300px_minmax(0,1fr)]">
          <aside className="flex min-h-0 flex-col border-r border-[#e7e8e9] bg-[#f3f4f5]">
            <div className="p-6">
              <div className="flex items-start justify-between gap-3">
                <div>
                  <h2 className="text-[26px] font-black tracking-tight text-[#001d44]">Paises</h2>
                  <p className="mt-2 max-w-[220px] text-sm leading-6 text-[#43474f]">
                    Selecciona un pais para explorar destinos sin salir del contexto.
                  </p>
                </div>
                <span className="rounded-full bg-white px-3 py-1 text-[11px] font-semibold uppercase tracking-[0.12em] text-[#48626e]">
                  {countries.length}
                </span>
              </div>

              <div className="relative mt-6">
                <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-[#737780]" />
                <input
                  type="text"
                  value={countrySearch}
                  onChange={(event) => setCountrySearch(event.target.value)}
                  placeholder="Filtrar paises..."
                  className={`${inputClass} bg-white pl-10`}
                />
              </div>
            </div>

            <div className="flex-1 space-y-1 overflow-y-auto px-4 pb-5">
              {countriesLoading ? (
                <div className="flex h-44 items-center justify-center">
                  <Loader2 className="h-6 w-6 animate-spin text-[#255dad]" />
                </div>
              ) : countries.length === 0 ? (
                <StitchEmptyState
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

          <section className="flex min-h-0 flex-col bg-[#f8f9fa]">
            {!selectedCountry ? (
              <div className="flex flex-1 items-center justify-center p-8">
                <StitchEmptyState
                  icon={Globe2}
                  title="Selecciona un pais"
                  description="Cuando elijas un pais, aqui veras un detalle limpio con sus destinos y acciones principales."
                />
              </div>
            ) : (
              <>
                <div className="border-b border-[#e7e8e9] px-8 pb-5 pt-8">
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
                </div>

                <div className="flex-1 overflow-y-auto p-8">
                  {destinationsLoading ? (
                    <div className="flex h-44 items-center justify-center">
                      <Loader2 className="h-7 w-7 animate-spin text-[#255dad]" />
                    </div>
                  ) : filteredDestinations.length === 0 ? (
                    <StitchEmptyState
                      icon={MapPinned}
                      title={destinations.length === 0 ? "Todavia no hay destinos cargados" : "No encontramos destinos"}
                      description={
                        destinations.length === 0
                          ? "Crea el primer destino de este pais para empezar a cargar salidas y contenido."
                          : "Ajusta la busqueda o el filtro para ver otros resultados."
                      }
                    />
                  ) : (
                    <div className="grid gap-6 xl:grid-cols-2 2xl:grid-cols-3">
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
                </div>
              </>
            )}
          </section>
        </div>
      </div>

      <div className={`space-y-4 md:hidden ${pageCanvasClass} p-4`}>
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
    <div className="space-y-5 text-[#191c1d]">
      <div className="px-1 pt-1">
        <div className="flex items-center justify-between gap-3">
          <div className="min-w-0">
            <h1 className="text-[26px] font-black tracking-tight text-[#001d44]">Paises y destinos</h1>
            <p className="mt-1 text-sm text-[#43474f]">Busca por pais y continua con el detalle de destinos.</p>
          </div>
          {canEdit ? (
            <Button size="icon" onClick={onCreateCountry} className={`h-11 w-11 ${primaryButtonClass}`}>
              <Plus className="h-5 w-5" />
            </Button>
          ) : null}
        </div>

        <div className="relative mt-4">
          <Search className="pointer-events-none absolute left-4 top-1/2 h-4 w-4 -translate-y-1/2 text-[#737780]" />
          <input
            type="text"
            value={countrySearch}
            onChange={(event) => onCountrySearchChange(event.target.value)}
            placeholder="Buscar por pais o region..."
            className={`${inputClass} bg-[#f3f4f5] pl-11`}
          />
        </div>
      </div>

      <div className="grid grid-cols-2 gap-3">
        <MiniStatCard label="Paises activos" value={portfolioSummary.totalCountries} accent="sky" />
        <MiniStatCard label="Total destinos" value={portfolioSummary.totalDestinations} />
      </div>

      <div>
        <div className="px-1 pb-3">
          <h2 className="text-lg font-bold tracking-tight text-[#191c1d]">Destinos Globales</h2>
        </div>

        {countriesLoading ? (
          <div className="flex h-40 items-center justify-center">
            <Loader2 className="h-6 w-6 animate-spin text-[#255dad]" />
          </div>
        ) : countries.length === 0 ? (
          <StitchEmptyState
            icon={Building2}
            title="Todavia no hay paises"
            description="Crea el primero para empezar a organizar la oferta."
            compact
          />
        ) : (
          <div className="space-y-2.5">
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
    <div className="space-y-4 text-[#191c1d]">
      <div className={`${cardClass} overflow-hidden`}>
        <div className="flex items-center justify-between gap-3 px-4 py-4">
          <button
            type="button"
            onClick={onBack}
            className="flex h-11 w-11 items-center justify-center rounded-2xl bg-[#f3f4f5] text-[#001d44] transition hover:bg-[#e7e8e9]"
          >
            <ArrowLeft className="h-5 w-5" />
          </button>

          <div className="min-w-0 flex-1">
            <p className="text-[10px] font-semibold uppercase tracking-[0.2em] text-[#737780]">Region activa</p>
            <h1 className="truncate text-[28px] font-black tracking-tight text-[#001d44]">{country.name}</h1>
            <p className="text-xs text-[#737780]">{country.totalDestinations} destinos activos</p>
          </div>

          {canEdit ? (
            <Button size="icon" onClick={onCreateDestination} className={`h-11 w-11 ${primaryButtonClass}`}>
              <Plus className="h-5 w-5" />
            </Button>
          ) : null}
        </div>

        <div className="border-t border-[#e7e8e9] px-4 py-4">
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
                  <Button variant="outline" size="sm" onClick={onEditCountry} className={outlineButtonClass}>
                    Editar pais
                  </Button>
                ) : null}
                {canPublish ? (
                  <>
                    <Button variant="outline" size="sm" onClick={onPreviewCountry} className={outlineButtonClass}>
                      Ver como cliente
                    </Button>
                    <Button variant="outline" size="sm" onClick={onCopyCountry} className={outlineButtonClass}>
                      Copiar codigo
                    </Button>
                  </>
                ) : null}
              </div>
            ) : null}
          </div>

          <div className="mt-4 space-y-3">
            <div className="relative">
              <Search className="pointer-events-none absolute left-4 top-1/2 h-4 w-4 -translate-y-1/2 text-[#737780]" />
              <input
                type="text"
                value={searchValue}
                onChange={(event) => onSearchChange(event.target.value)}
                placeholder="Buscar destinos..."
                className={`${inputClass} bg-[#f3f4f5] pl-11`}
              />
            </div>

            {canPublish ? (
              <select
                value={filterValue}
                onChange={(event) => onFilterChange(event.target.value)}
                className={`${inputClass} bg-[#f3f4f5]`}
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
          <Loader2 className="h-7 w-7 animate-spin text-[#255dad]" />
        </div>
      ) : destinations.length === 0 ? (
        <div className={`${cardClass} p-4`}>
          <StitchEmptyState
            icon={MapPinned}
            title={allDestinations.length === 0 ? "Todavia no hay destinos cargados" : "No encontramos destinos"}
            description={
              allDestinations.length === 0
                ? "Crea el primer destino de este pais para empezar a cargar salidas y contenido."
                : "Ajusta la busqueda o el filtro para ver otros resultados."
            }
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
    <div className="flex flex-col gap-6 xl:flex-row xl:items-start xl:justify-between">
      <div className="min-w-0 flex-1">
        <div className="flex items-center gap-2 text-[11px] font-semibold uppercase tracking-[0.22em] text-[#737780]">
          <span>Destinos</span>
          <ChevronRight className="h-3 w-3" />
          <span className="truncate">{country.name}</span>
        </div>

        <h2 className="mt-3 text-5xl font-black tracking-tight text-[#001d44]">{country.name}</h2>
        <p className="mt-3 max-w-2xl text-sm leading-7 text-[#43474f]">
          Explora la oferta actual del pais, revisa rapidamente el estado comercial de cada destino y accede a sus acciones clave sin salir del contexto.
        </p>

        <div className="mt-5 flex flex-wrap gap-2">
          <Badge tone="sky">{country.totalDestinations} destinos</Badge>
          <Badge tone="emerald">{country.publishedDestinations} visibles</Badge>
          <Badge tone="amber">{country.draftDestinations} en preparacion</Badge>
        </div>
      </div>

      {(canEdit || canPublish) ? (
        <div className="flex max-w-[360px] flex-wrap justify-end gap-3">
          {canEdit ? (
            <>
              <Button variant="outline" onClick={onEdit} className={`gap-2 ${outlineButtonClass}`}>
                <Pencil className="h-4 w-4" />
                Editar pais
              </Button>
              <Button onClick={onCreateDestination} className={`gap-2 ${primaryButtonClass}`}>
                <Plus className="h-4 w-4" />
                Nuevo destino
              </Button>
            </>
          ) : null}
          {canPublish ? (
            <>
              <Button variant="outline" onClick={onPreview} className={`gap-2 ${outlineButtonClass}`}>
                <Eye className="h-4 w-4" />
                Ver como cliente
              </Button>
              <Button variant="outline" onClick={onCopy} className={`gap-2 ${outlineButtonClass}`}>
                <Copy className="h-4 w-4" />
                Copiar codigo
              </Button>
            </>
          ) : null}
        </div>
      ) : null}
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
    <div className="mt-8 flex flex-col gap-4">
      <div className="flex flex-col gap-4 xl:flex-row xl:items-end xl:justify-between">
        <div className="flex min-w-0 flex-wrap items-center gap-6">
          <button type="button" className="relative pb-3 text-sm font-bold text-[#001d44]">
            Destinos
            <span className="absolute -right-5 top-0 inline-flex h-5 min-w-5 items-center justify-center rounded-full bg-[#001d44] px-1.5 text-[10px] font-bold text-white">
              {filteredCount}
            </span>
            <span className="absolute bottom-0 left-0 h-0.5 w-full rounded-full bg-[#255dad]" />
          </button>
          <span className="pb-3 text-sm font-semibold text-[#737780]">Visibles {visibleCount}</span>
          <span className="pb-3 text-sm font-semibold text-[#737780]">En preparacion {hiddenCount}</span>
        </div>

        <div className={`grid gap-3 ${canPublish ? "xl:min-w-[480px] md:grid-cols-[minmax(0,1fr)_220px]" : "xl:min-w-[320px]"}`}>
          <div className="relative">
            <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-[#737780]" />
            <input
              type="text"
              value={searchValue}
              onChange={(event) => onSearchChange(event.target.value)}
              placeholder="Buscar destinos..."
              className={`${inputClass} bg-white pl-10`}
            />
          </div>

          {canPublish ? (
            <select
              value={filterValue}
              onChange={(event) => onFilterChange(event.target.value)}
              className={`${inputClass} bg-white`}
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
  const countLabel = `${country.totalDestinations} Dest.`;

  return (
    <button
      type="button"
      onClick={onClick}
      className={`group relative w-full overflow-hidden rounded-[18px] px-4 py-4 text-left transition ${
        selected ? "bg-white shadow-[0_10px_30px_rgba(25,28,29,0.06)]" : "bg-transparent hover:bg-white/80"
      }`}
    >
      <span className={`absolute inset-y-4 left-0 w-1 rounded-r-full ${selected ? "bg-[#255dad]" : "bg-transparent"}`} />

      <div className="flex items-start gap-3">
        <CountryAvatar name={country.name} selected={selected} />

        <div className="min-w-0 flex-1">
          <div className="flex items-center justify-between gap-3">
            <div className="min-w-0">
              <p className="break-words text-sm font-bold text-[#001d44]">{country.name}</p>
              <p className="mt-0.5 text-[11px] text-[#737780]">
                {country.publishedDestinations} visibles · {country.draftDestinations} borrador
              </p>
            </div>
            <span className="shrink-0 rounded-full bg-[#cbe7f5] px-2 py-0.5 text-[10px] font-bold text-[#304a55]">{countLabel}</span>
          </div>
        </div>

        <ChevronRight className={`mt-1 h-4 w-4 shrink-0 ${selected ? "text-[#255dad]" : "text-[#c3c6d1] group-hover:translate-x-0.5 group-hover:text-[#001d44]"}`} />
      </div>
    </button>
  );
}

function CountryStackItem({ country, onClick }) {
  return (
    <button
      type="button"
      onClick={onClick}
      className="group w-full rounded-[18px] border border-[#e7e8e9] bg-white px-4 py-4 text-left transition hover:bg-[#f3f4f5]"
    >
      <div className="flex items-center gap-3">
        <CountryAvatar name={country.name} compact />

        <div className="min-w-0 flex-1">
          <p className="break-words text-sm font-bold text-[#001d44]">{country.name}</p>
          <p className="mt-1 break-words text-xs text-[#737780]">{country.totalDestinations} destinos · {country.publishedDestinations} visibles</p>
        </div>

        <ChevronRight className="h-4 w-4 shrink-0 text-[#c3c6d1] transition group-hover:translate-x-0.5 group-hover:text-[#001d44]" />
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
          ? "bg-[#255dad] text-white shadow-[0_14px_30px_rgba(37,93,173,0.2)]"
          : "bg-white text-[#304a55]"
      }`}
    >
      {initials || "PA"}
    </div>
  );
}

function MiniStatCard({ label, value, accent = "slate" }) {
  const accentClass =
    accent === "sky"
      ? "border-l-[#255dad]"
      : accent === "emerald"
        ? "border-l-[#0f766e]"
        : accent === "amber"
          ? "border-l-[#d8885c]"
          : "border-l-[#c3c6d1]";

  return (
    <div className={`rounded-[18px] border border-[#e7e8e9] border-l-2 bg-white px-4 py-4 shadow-[0_6px_18px_rgba(25,28,29,0.03)] ${accentClass}`}>
      <p className="text-[10px] font-semibold uppercase tracking-[0.18em] text-[#737780]">{label}</p>
      <p className="mt-2 break-words text-2xl font-black tracking-tight text-[#001d44]">{value}</p>
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
      : destination.tagline || destination.countryName || "";
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
    <article className="group flex h-full flex-col overflow-hidden rounded-[20px] border border-[#e7e8e9] bg-white shadow-[0_10px_30px_rgba(25,28,29,0.04)] transition-all duration-200 hover:-translate-y-0.5 hover:shadow-[0_20px_40px_rgba(25,28,29,0.06)]">
      <div className={`relative overflow-hidden ${compact ? "h-40" : "h-48"}`}>
        {heroImageUrl ? (
          <img src={heroImageUrl} alt={title} className="h-full w-full object-cover transition-transform duration-500 group-hover:scale-105" />
        ) : (
          <div className="flex h-full w-full items-center justify-center bg-[linear-gradient(135deg,#d7e2ff_0%,#cbe7f5_100%)] px-6 text-center">
            <div>
              <div className="mx-auto flex h-14 w-14 items-center justify-center rounded-2xl bg-white text-[#255dad] shadow-sm">
                <ImagePlus className="h-6 w-6" />
              </div>
              <p className="mt-3 text-sm font-medium text-[#001d44]">Agrega una imagen de portada para destacar este destino</p>
            </div>
          </div>
        )}

        <div className="absolute left-4 top-4">
          {canPublish ? <DestinationStateBadge destination={destination} /> : null}
        </div>
      </div>

      <div className={compact ? "flex flex-1 flex-col p-4" : "flex flex-1 flex-col p-5"}>
        <div className="flex items-start justify-between gap-4">
          <div className="min-w-0 flex-1">
            <h3 className="break-words text-[22px] font-black tracking-tight text-[#001d44]">{title}</h3>
            <p className="mt-1 flex items-center gap-1.5 text-xs text-[#43474f]">
              <MapPin className="h-3.5 w-3.5 shrink-0" />
              <span className="truncate">{secondaryLine || destination.countryName}</span>
            </p>
          </div>

          <div className="shrink-0 text-right">
            <p className="text-[10px] font-bold uppercase tracking-[0.18em] text-[#737780]">Desde</p>
            <p className="mt-1 text-xl font-black tracking-tight text-[#255dad]">{priceLabel}</p>
          </div>
        </div>

        <div className={`${mutedPanelClass} mt-4 grid grid-cols-2 gap-4 p-3`}>
          <div>
            <DestinationMetricItem label="Proxima salida" value={formatLongDate(destination.nextDepartureDate)} />
          </div>
          <div>
            <DestinationMetricItem label="Paquetes" value={activeDeparturesLabel} />
          </div>
        </div>

        <div className="mt-3 flex items-center justify-between gap-3 text-xs text-[#737780]">
          <span>{stateLabel}</span>
          {destination.publishIssues?.length && !destination.isPublished ? <span>{destination.publishIssues.length} pendientes</span> : null}
        </div>

        {canPublish && !destination.isPublished && !destination.canPublish && destination.publishIssues?.length ? (
          <p className="mt-2 break-words text-xs text-[#93000a]">Completa los datos pendientes para poder publicarlo.</p>
        ) : null}

        {(canEdit || canPublish) ? (
          <div className={`mt-4 ${compact ? "grid grid-cols-2 gap-2" : "flex flex-wrap gap-2"}`}>
            {canEdit ? (
              <Button variant="outline" size="sm" onClick={onEdit} className={`gap-2 justify-start text-xs ${outlineButtonClass}`}>
                <Pencil className="h-4 w-4" />
                Editar
              </Button>
            ) : null}

            {canPublish ? (
              <>
                <Button variant="outline" size="sm" onClick={onView} className={`gap-2 justify-start text-xs ${outlineButtonClass}`}>
                  <Eye className="h-4 w-4" />
                  Vista previa
                </Button>
                <Button variant="outline" size="sm" onClick={onCopy} className={`gap-2 justify-start text-xs ${outlineButtonClass}`}>
                  <Copy className="h-4 w-4" />
                  Copiar
                </Button>
                {destination.isPublished ? (
                  <Button variant="outline" size="sm" onClick={onUnpublish} className={`gap-2 justify-start text-xs ${outlineButtonClass}`}>
                    <Rocket className="h-4 w-4" />
                    Ocultar
                  </Button>
                ) : (
                  <Button size="sm" onClick={onPublish} disabled={!destination.canPublish} className={`gap-2 justify-start text-xs ${primaryButtonClass}`}>
                    <Rocket className="h-4 w-4" />
                    Publicar
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
    <div>
      <p className="text-[10px] font-semibold uppercase tracking-[0.16em] text-[#737780]">{label}</p>
      <p className="mt-1 text-sm font-bold text-[#001d44]">{value}</p>
    </div>
  );
}

function DestinationStateBadge({ destination }) {
  if (destination.isPublished) {
    return <Badge tone="emerald">Visible</Badge>;
  }

  if (destination.canPublish) {
    return <Badge tone="sky">Lista</Badge>;
  }

  return <Badge tone="amber">{destination.publishIssues?.length ? "Completar" : "Borrador"}</Badge>;
}

function StitchEmptyState({ icon: Icon, title, description, compact = false }) {
  return (
    <div className={`flex flex-col items-center justify-center text-center ${compact ? "px-4 py-8" : "px-6 py-14"}`}>
      <div className="mb-4 flex h-14 w-14 items-center justify-center rounded-2xl bg-[#f3f4f5] text-[#255dad]">
        <Icon className="h-6 w-6" />
      </div>
      <p className="text-base font-bold tracking-tight text-[#001d44]">{title}</p>
      {description ? <p className="mt-2 max-w-md text-sm leading-6 text-[#43474f]">{description}</p> : null}
    </div>
  );
}

function CountryModal({ open, form, saving, onChange, onClose, onSubmit }) {
  if (!open) {
    return null;
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-slate-950/55 p-4 backdrop-blur-sm">
      <div className={`${cardClass} w-full max-w-lg overflow-hidden`}>
        <div className="border-b border-[#e7e8e9] px-6 py-5">
          <p className="text-[11px] font-semibold uppercase tracking-[0.2em] text-[#737780]">Catalogo</p>
          <h3 className="mt-2 text-2xl font-black tracking-tight text-[#001d44]">{form.publicId ? "Editar pais" : "Nuevo pais"}</h3>
          <p className="mt-2 text-sm text-[#43474f]">
            Crea o ajusta el pais para ordenar mejor la oferta y facilitar la navegacion por destinos.
          </p>
        </div>

        <form onSubmit={onSubmit} className="space-y-5 px-6 py-6">
          <label className="block space-y-2">
            <span className="text-sm font-medium text-[#43474f]">Nombre del pais</span>
            <input
              type="text"
              value={form.name}
              onChange={(event) => onChange(event.target.value)}
              placeholder="Ej. Republica Dominicana"
              className={inputClass}
              autoFocus
            />
          </label>

          <div className="flex flex-wrap justify-end gap-2 border-t border-[#e7e8e9] pt-5">
            <Button type="button" variant="outline" onClick={onClose} className={outlineButtonClass}>
              Cancelar
            </Button>
            <Button type="submit" disabled={saving} className={`gap-2 ${primaryButtonClass}`}>
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
      ? "bg-[#d7e2ff] text-[#00458f]"
      : tone === "emerald"
        ? "bg-[#d9f3ea] text-[#0f766e]"
        : tone === "amber"
          ? "bg-[#ffdbca] text-[#723610]"
          : "bg-[#edeeef] text-[#43474f]";

  return <span className={`inline-flex max-w-full items-center rounded-full px-3 py-1 text-[10px] font-bold uppercase tracking-[0.14em] ${toneClass}`}>{children}</span>;
}
