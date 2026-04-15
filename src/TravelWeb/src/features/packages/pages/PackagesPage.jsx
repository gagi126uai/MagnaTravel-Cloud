import { useCallback, useEffect, useMemo, useState } from "react";
import { ChevronDown, Eye, EyeOff, Loader2, Plus, Search } from "lucide-react";
import { useNavigate, useSearchParams } from "react-router-dom";
import { api, buildAppUrl } from "../../../api";
import { showConfirm, showError, showSuccess } from "../../../alerts";
import { hasPermission } from "../../../auth";
import { Button } from "../../../components/ui/button";
import { ListPageHeader } from "../../../components/ui/ListPageHeader";
import { useDebounce } from "../../../hooks/useDebounce";
import { CountryModal } from "../components/admin/CountryModal";
import { PackagesCountrySidebar } from "../components/admin/PackagesCountrySidebar";
import { PackagesDestinationsPanel } from "../components/admin/PackagesDestinationsPanel";
import {
  buildCountryPublicationSnippet,
  buildDestinationPublicationSnippet,
  mapCountryForm,
} from "../lib/publicationUtils";

const inputClass =
  "w-full rounded-md border border-slate-300 bg-white px-3 py-2 text-sm text-slate-900 outline-none transition focus:border-indigo-500 focus:ring-2 focus:ring-indigo-500/20 dark:border-slate-700 dark:bg-slate-950 dark:text-white";

const emptyCountryForm = {
  publicId: null,
  name: "",
  isPublished: true,
  publishedAt: null,
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

    if (!selectedCountryPublicId) {
      syncSelectedCountry(countries[0].publicId);
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
        return destination.isPublished && destination.isCountryPublished;
      }

      if (destinationStatusFilter === "blocked") {
        return destination.isPublished && !destination.isCountryPublished;
      }

      return !destination.isPublished;
    });
  }, [canPublish, destinationSearch, destinationStatusFilter, destinations]);

  function applyCountryUpdate(updatedCountry) {
    setCountries((current) =>
      current.map((country) => (country.publicId === updatedCountry.publicId ? updatedCountry : country))
    );
    setDestinations((current) =>
      current.map((destination) =>
        destination.countryPublicId === updatedCountry.publicId
          ? { ...destination, isCountryPublished: updatedCountry.isPublished }
          : destination
      )
    );
  }

  function applyDestinationUpdate(updatedDestination) {
    setDestinations((current) => {
      const nextDestinations = current.map((item) =>
        item.publicId === updatedDestination.publicId ? updatedDestination : item
      );

      setCountries((currentCountries) =>
        currentCountries.map((country) => {
          if (country.publicId !== updatedDestination.countryPublicId) {
            return country;
          }

          const totalDestinations = nextDestinations.length;
          const publishedDestinations = nextDestinations.filter((item) => item.isPublished).length;

          return {
            ...country,
            totalDestinations,
            publishedDestinations,
            draftDestinations: Math.max(totalDestinations - publishedDestinations, 0),
          };
        })
      );

      return nextDestinations;
    });
  }

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
      applyDestinationUpdate(updated);
      showSuccess("El destino ya esta visible en el sitio.");
    } catch (error) {
      showError(error.message || "No pudimos mostrar el destino en el sitio.");
    }
  }

  async function handlePublishCountry(country) {
    const confirmed = await showConfirm({
      title: "Publicar pais",
      eyebrow: "Publicacion web",
      text: "El iframe del pais y los destinos publicados de este pais volveran a responder en el sitio sin cambiar cada destino.",
      confirmText: "Publicar pais",
      confirmColor: "emerald",
    });

    if (!confirmed) {
      return;
    }

    try {
      const updated = await api.patch(`/countries/${country.publicId}/publish`);
      applyCountryUpdate(updated);
      showSuccess("El pais volvio a estar visible en el sitio.");
    } catch (error) {
      showError(error.message || "No pudimos publicar el pais.");
    }
  }

  async function handleUnpublishCountry(country) {
    const confirmed = await showConfirm({
      title: "Retirar pais del sitio",
      eyebrow: "Publicacion web",
      text: "El iframe del pais y todos los destinos publicos directos de este pais dejaran de responder, pero cada destino conservara su estado propio.",
      confirmText: "Retirar pais",
      confirmColor: "amber",
    });

    if (!confirmed) {
      return;
    }

    try {
      const updated = await api.patch(`/countries/${country.publicId}/unpublish`);
      applyCountryUpdate(updated);
      showSuccess("El pais dejo de mostrarse en el sitio.");
    } catch (error) {
      showError(error.message || "No pudimos retirar el pais del sitio.");
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
      applyDestinationUpdate(updated);
      showSuccess("El destino dejo de mostrarse en el sitio.");
    } catch (error) {
      showError(error.message || "No pudimos retirar el destino del sitio.");
    }
  }

  return (
    <div className="animate-in fade-in space-y-4 duration-500 md:space-y-6">
      <ListPageHeader
        title="Paises y destinos"
        subtitle="Organiza el catalogo por pais, revisa destinos y controla su publicacion."
        actions={
          <div className="flex flex-wrap gap-2">
            {canEdit ? (
              <Button type="button" variant="outline" onClick={openCreateCountryModal} className="gap-2">
                <Plus className="h-4 w-4" />
                Nuevo pais
              </Button>
            ) : null}
            {canEdit && selectedCountry ? (
              <Button type="button" onClick={openCreateDestination} className="gap-2">
                <Plus className="h-4 w-4" />
                Nuevo destino
              </Button>
            ) : null}
          </div>
        }
      />

      <div className="grid gap-4 xl:grid-cols-[280px_minmax(0,1fr)]">
        <div className="hidden xl:block">
          <PackagesCountrySidebar
            countries={countries}
            countriesLoading={countriesLoading}
            countrySearch={countrySearch}
            onCountrySearchChange={setCountrySearch}
            selectedCountryPublicId={selectedCountryPublicId}
            onSelectCountry={syncSelectedCountry}
            selectedCountry={selectedCountry}
            portfolioSummary={portfolioSummary}
            canEdit={canEdit}
            onCreateCountry={openCreateCountryModal}
            onEditCountry={openEditCountryModal}
            canPublish={canPublish}
            onPublishCountry={handlePublishCountry}
            onUnpublishCountry={handleUnpublishCountry}
          />
        </div>

        <div className="space-y-4">
          <section className="rounded-lg border border-slate-200 bg-white p-4 shadow-sm dark:border-slate-800 dark:bg-slate-900/50 xl:hidden">
            <div className="flex items-start justify-between gap-3">
              <div className="min-w-0">
                <h2 className="text-base font-semibold text-slate-900 dark:text-white">Pais activo</h2>
                <p className="mt-1 text-sm text-slate-500 dark:text-slate-400">
                  Cambia el pais para revisar su lista de destinos.
                </p>
              </div>
              {canEdit ? (
                <Button type="button" size="sm" onClick={openCreateCountryModal} className="gap-2">
                  <Plus className="h-4 w-4" />
                  Nuevo
                </Button>
              ) : null}
            </div>

            <div className="mt-4 space-y-3">
              <div className="relative">
                <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" />
                <input
                  type="text"
                  value={countrySearch}
                  onChange={(event) => setCountrySearch(event.target.value)}
                  placeholder="Buscar pais..."
                  className={`${inputClass} pl-9`}
                />
              </div>

              {countriesLoading ? (
                <div className="flex h-20 items-center justify-center">
                  <Loader2 className="h-5 w-5 animate-spin text-indigo-500" />
                </div>
              ) : countries.length === 0 ? (
                <div className="rounded-md border border-dashed border-slate-300 px-4 py-6 text-center text-sm text-slate-500 dark:border-slate-700 dark:text-slate-400">
                  No hay paises cargados.
                </div>
              ) : (
                <>
                  <div className="relative">
                    <select
                      value={selectedCountryPublicId}
                      onChange={(event) => syncSelectedCountry(event.target.value)}
                      className={`${inputClass} appearance-none pr-10`}
                    >
                      {countries.map((country) => (
                        <option key={country.publicId} value={country.publicId}>
                          {country.name}
                        </option>
                      ))}
                    </select>
                    <ChevronDown className="pointer-events-none absolute right-3 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" />
                  </div>

                  {selectedCountry ? (
                    <>
                      <div className="flex flex-wrap gap-2">
                        <MobileStat label="Destinos" value={selectedCountry.totalDestinations} />
                        <MobileStat label="Visibles" value={selectedCountry.publishedDestinations} />
                        <MobileStat label="Borrador" value={selectedCountry.draftDestinations} />
                      </div>

                      <div className="flex flex-wrap gap-2">
                        {canPublish ? (
                          selectedCountry.isPublished ? (
                            <Button type="button" variant="outline" size="sm" onClick={() => handleUnpublishCountry(selectedCountry)} className="gap-2">
                              <EyeOff className="h-4 w-4" />
                              Retirar del sitio
                            </Button>
                          ) : (
                            <Button type="button" size="sm" onClick={() => handlePublishCountry(selectedCountry)} className="gap-2">
                              <Eye className="h-4 w-4" />
                              Publicar pais
                            </Button>
                          )
                        ) : null}
                        {canEdit ? (
                          <Button
                            type="button"
                            variant="outline"
                            size="sm"
                            onClick={() => openEditCountryModal(selectedCountry)}
                            className="gap-2"
                          >
                            Editar pais
                          </Button>
                        ) : null}
                        {canEdit ? (
                          <Button type="button" size="sm" onClick={openCreateDestination} className="gap-2">
                            <Plus className="h-4 w-4" />
                            Nuevo destino
                          </Button>
                        ) : null}
                      </div>
                    </>
                  ) : null}
                </>
              )}
            </div>
          </section>

          {!selectedCountry ? (
            <section className="rounded-lg border border-slate-200 bg-white px-4 py-12 text-center shadow-sm dark:border-slate-800 dark:bg-slate-900/50">
              {countriesLoading ? (
                <div className="flex justify-center">
                  <Loader2 className="h-6 w-6 animate-spin text-indigo-500" />
                </div>
              ) : (
                <>
                  <p className="text-sm font-medium text-slate-900 dark:text-white">No hay un pais seleccionado</p>
                  <p className="mt-1 text-sm text-slate-500 dark:text-slate-400">
                    Crea o selecciona un pais para empezar a administrar sus destinos.
                  </p>
                </>
              )}
            </section>
          ) : (
            <PackagesDestinationsPanel
              selectedCountry={selectedCountry}
              destinations={destinations}
              filteredDestinations={filteredDestinations}
              loading={destinationsLoading}
              searchValue={destinationSearch}
              onSearchChange={setDestinationSearch}
              filterValue={destinationStatusFilter}
              onFilterChange={setDestinationStatusFilter}
              canEdit={canEdit}
              canPublish={canPublish}
              onEditCountry={openEditCountryModal}
              onCreateDestination={openCreateDestination}
              onPublishCountry={handlePublishCountry}
              onUnpublishCountry={handleUnpublishCountry}
              onPreviewCountry={openCountryPreview}
              onCopyCountry={copyCountryPublication}
              onEditDestination={openDestinationEditor}
              onViewDestination={openDestinationPreview}
              onCopyDestination={copyDestinationPublication}
              onPublishDestination={handlePublish}
              onUnpublishDestination={handleUnpublish}
            />
          )}
        </div>
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

function MobileStat({ label, value }) {
  return (
    <span className="inline-flex items-center gap-2 rounded-md border border-slate-200 bg-slate-50 px-3 py-1.5 text-sm font-medium text-slate-700 dark:border-slate-800 dark:bg-slate-950/40 dark:text-slate-200">
      <span>{label}</span>
      <span className="rounded-md bg-white/70 px-1.5 py-0.5 text-xs font-semibold dark:bg-slate-900/40">{value}</span>
    </span>
  );
}
