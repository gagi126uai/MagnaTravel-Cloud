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

  const filteredDestinations = useMemo(() => {
    return destinations.filter((destination) => {
      const searchValue = destinationSearch.trim().toLowerCase();
      const matchesSearch =
        !searchValue ||
        `${destination.title || ""} ${destination.name || ""}`.toLowerCase().includes(searchValue);

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

  function openCountryOnWeb(country) {
    if (!country?.countryPagePath) {
      showError("El pais todavia no esta listo para la web.");
      return;
    }

    window.open(buildAppUrl(country.countryPagePath), "_blank", "noopener,noreferrer");
  }

  function openDestinationOnWeb(destination) {
    if (!destination?.publicPagePath) {
      showError("Guarda el destino para verlo como cliente.");
      return;
    }

    window.open(buildAppUrl(destination.publicPagePath), "_blank", "noopener,noreferrer");
  }

  async function copyCountryPublication(country) {
    if (!country?.countryPagePath) {
      showError("El pais todavia no esta listo para la web.");
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
      showError("Guarda el destino para copiar su codigo.");
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
      title: "Mostrar destino en la web",
      eyebrow: "Publicacion web",
      text: "El destino quedara visible para los clientes dentro del sitio.",
      confirmText: "Mostrar en la web",
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
      showSuccess("El destino ya esta visible en la web.");
    } catch (error) {
      showError(error.message || "No pudimos mostrar el destino en la web.");
    }
  }

  async function handleUnpublish(destination) {
    const confirmed = await showConfirm({
      title: "Retirar destino de la web",
      eyebrow: "Publicacion web",
      text: "El destino dejara de mostrarse para los clientes, pero seguira disponible dentro del ERP.",
      confirmText: "Retirar de la web",
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
      showSuccess("El destino dejo de mostrarse en la web.");
    } catch (error) {
      showError(error.message || "No pudimos retirar el destino de la web.");
    }
  }

  return (
    <div className="animate-in fade-in space-y-4 duration-500 md:space-y-6">
      <ListPageHeader
        title="Paises y destinos"
        subtitle="Organiza la oferta por pais, carga destinos y deja listo el contenido comercial del sitio."
        actions={
          canEdit ? (
            <Button onClick={openCreateCountryModal} className="gap-2">
              <Plus className="h-4 w-4" />
              Nuevo pais
            </Button>
          ) : null
        }
      />

      <div className="grid gap-6 xl:grid-cols-[320px_minmax(0,1fr)]">
        <section className="rounded-xl border border-slate-200 bg-white p-4 shadow-sm dark:border-slate-800 dark:bg-slate-900/50">
          <div className="flex items-center justify-between gap-3">
            <div>
              <h2 className="text-sm font-semibold text-slate-900 dark:text-white">Paises</h2>
              <p className="text-sm text-slate-500 dark:text-slate-400">Selecciona un pais para administrar sus destinos.</p>
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

          <div className="mt-4 space-y-2">
            {countriesLoading ? (
              <div className="flex h-48 items-center justify-center">
                <Loader2 className="h-6 w-6 animate-spin text-indigo-500" />
              </div>
            ) : countries.length === 0 ? (
              <EmptyState
                icon={Building2}
                title="Todavia no hay paises cargados"
                description="Crea el primero para empezar a organizar la oferta."
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
        </section>

        <section className="space-y-4">
          {!selectedCountry ? (
            <div className="rounded-xl border border-slate-200 bg-white p-10 shadow-sm dark:border-slate-800 dark:bg-slate-900/50">
              <EmptyState
                icon={Globe2}
                title="Selecciona un pais"
                description="Desde aqui vas a ver sus destinos, las salidas y la informacion comercial."
              />
            </div>
          ) : (
            <>
              <div className="rounded-xl border border-slate-200 bg-white p-5 shadow-sm dark:border-slate-800 dark:bg-slate-900/50">
                <div className="flex flex-col gap-4 lg:flex-row lg:items-start lg:justify-between">
                  <div className="space-y-3">
                    <div>
                      <h2 className="text-2xl font-bold tracking-tight text-slate-900 dark:text-white">{selectedCountry.name}</h2>
                      <p className="mt-1 text-sm text-slate-500 dark:text-slate-400">
                        {selectedCountry.totalDestinations} destinos cargados, {selectedCountry.publishedDestinations} visibles en el sitio y {selectedCountry.draftDestinations} en preparacion.
                      </p>
                    </div>

                    <div className="flex flex-wrap gap-2">
                      <SummaryPill label="Destinos" value={selectedCountry.totalDestinations} />
                      <SummaryPill label="Visibles" value={selectedCountry.publishedDestinations} tone="emerald" />
                      <SummaryPill label="En preparacion" value={selectedCountry.draftDestinations} tone="amber" />
                    </div>
                  </div>

                  <div className="flex flex-wrap gap-2">
                    {canEdit ? (
                      <>
                        <Button variant="outline" onClick={() => openEditCountryModal(selectedCountry)} className="gap-2">
                          <Pencil className="h-4 w-4" />
                          Editar pais
                        </Button>
                        <Button onClick={openCreateDestination} className="gap-2">
                          <Plus className="h-4 w-4" />
                          Nuevo destino
                        </Button>
                      </>
                    ) : null}
                  </div>
                </div>

                {canPublish ? (
                  <div className="mt-5 rounded-xl border border-slate-200 bg-slate-50/70 p-4 dark:border-slate-800 dark:bg-slate-950/40">
                    <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
                      <div>
                        <p className="text-sm font-semibold text-slate-900 dark:text-white">Publicacion web</p>
                        <p className="text-sm text-slate-500 dark:text-slate-400">
                          Comparte esta seleccion de destinos en el sitio y revisa la experiencia que vera el cliente.
                        </p>
                      </div>
                      <div className="flex flex-wrap gap-2">
                        <Button variant="outline" onClick={() => openCountryOnWeb(selectedCountry)} className="gap-2">
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
                      <option value="visible">Visibles en la web</option>
                      <option value="hidden">En preparacion</option>
                    </select>
                  ) : null
                }
              />

              <div className="overflow-hidden rounded-xl border border-slate-200 bg-white shadow-sm dark:border-slate-800 dark:bg-slate-900/50">
                {destinationsLoading ? (
                  <div className="flex h-64 items-center justify-center">
                    <Loader2 className="h-7 w-7 animate-spin text-indigo-500" />
                  </div>
                ) : filteredDestinations.length === 0 ? (
                  <EmptyState
                    icon={MapPinned}
                    title={destinations.length === 0 ? "Todavia no hay destinos cargados" : "No encontramos destinos"}
                    description={
                      destinations.length === 0
                        ? "Crea el primer destino de este pais para empezar a cargar salidas y contenido."
                        : "Ajusta la busqueda o el filtro para ver otros resultados."
                    }
                  />
                ) : (
                  <div className="overflow-x-auto">
                    <table className="min-w-full divide-y divide-slate-200 dark:divide-slate-800">
                      <thead className="bg-slate-50 dark:bg-slate-950/40">
                        <tr className="text-left text-xs font-semibold uppercase tracking-wide text-slate-500 dark:text-slate-400">
                          <th className="px-5 py-3">Destino</th>
                          {canPublish ? <th className="px-4 py-3">Estado en la web</th> : null}
                          <th className="px-4 py-3">Precio desde</th>
                          <th className="px-4 py-3">Proxima salida</th>
                          <th className="px-4 py-3">Salidas activas</th>
                          <th className="px-5 py-3 text-right">Acciones</th>
                        </tr>
                      </thead>
                      <tbody className="divide-y divide-slate-100 dark:divide-slate-800">
                        {filteredDestinations.map((destination) => (
                          <DestinationRow
                            key={destination.publicId}
                            destination={destination}
                            canEdit={canEdit}
                            canPublish={canPublish}
                            onEdit={() => openDestinationEditor(destination.publicId)}
                            onView={() => openDestinationOnWeb(destination)}
                            onCopy={() => copyDestinationPublication(destination)}
                            onPublish={() => handlePublish(destination)}
                            onUnpublish={() => handleUnpublish(destination)}
                          />
                        ))}
                      </tbody>
                    </table>
                  </div>
                )}
              </div>
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
  return (
    <button
      type="button"
      onClick={onClick}
      className={`w-full rounded-xl border px-4 py-3 text-left transition ${
        selected
          ? "border-indigo-200 bg-indigo-50/60 shadow-sm dark:border-indigo-900/60 dark:bg-indigo-900/20"
          : "border-slate-200 bg-white hover:border-slate-300 hover:bg-slate-50 dark:border-slate-800 dark:bg-slate-900 dark:hover:border-slate-700 dark:hover:bg-slate-800"
      }`}
    >
      <div className="flex items-start justify-between gap-3">
        <div className="min-w-0">
          <p className="truncate text-sm font-semibold text-slate-900 dark:text-white">{country.name}</p>
          <p className="mt-1 text-xs text-slate-500 dark:text-slate-400">
            {country.totalDestinations} destinos cargados
          </p>
        </div>
        <span
          className={`rounded-full px-2 py-1 text-[11px] font-semibold ${
            country.publishedDestinations > 0
              ? "bg-emerald-100 text-emerald-700 dark:bg-emerald-900/30 dark:text-emerald-300"
              : "bg-amber-100 text-amber-700 dark:bg-amber-900/30 dark:text-amber-300"
          }`}
        >
          {country.publishedDestinations > 0 ? `${country.publishedDestinations} visibles` : "En preparacion"}
        </span>
      </div>
    </button>
  );
}

function DestinationRow({ destination, canEdit, canPublish, onEdit, onView, onCopy, onPublish, onUnpublish }) {
  const publicationTone = destination.isPublished
    ? "bg-emerald-100 text-emerald-700 dark:bg-emerald-900/30 dark:text-emerald-300"
    : destination.canPublish
      ? "bg-blue-100 text-blue-700 dark:bg-blue-900/30 dark:text-blue-300"
      : "bg-amber-100 text-amber-700 dark:bg-amber-900/30 dark:text-amber-300";

  const publicationLabel = destination.isPublished
    ? "Visible"
    : destination.canPublish
      ? "Lista para mostrar"
      : "En preparacion";

  return (
    <tr>
      <td className="px-5 py-4">
        <div className="space-y-1">
          <p className="text-sm font-semibold text-slate-900 dark:text-white">{destination.title || destination.name}</p>
          {destination.title && destination.name && destination.title !== destination.name ? (
            <p className="text-xs text-slate-500 dark:text-slate-400">{destination.name}</p>
          ) : destination.tagline ? (
            <p className="text-xs text-slate-500 dark:text-slate-400">{destination.tagline}</p>
          ) : null}
        </div>
      </td>
      {canPublish ? (
        <td className="px-4 py-4">
          <span className={`rounded-full px-2.5 py-1 text-xs font-semibold ${publicationTone}`}>{publicationLabel}</span>
        </td>
      ) : null}
      <td className="px-4 py-4 text-sm font-semibold text-slate-900 dark:text-white">{formatMoney(destination.fromPrice, destination.currency)}</td>
      <td className="px-4 py-4 text-sm text-slate-600 dark:text-slate-300">{formatLongDate(destination.nextDepartureDate)}</td>
      <td className="px-4 py-4 text-sm text-slate-600 dark:text-slate-300">
        {destination.activeDepartureCount} activas{destination.departureCount > destination.activeDepartureCount ? ` de ${destination.departureCount}` : ""}
      </td>
      <td className="px-5 py-4">
        <div className="flex flex-wrap justify-end gap-2">
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
                Ver como cliente
              </Button>
              <Button variant="outline" size="sm" onClick={onCopy} className="gap-2">
                <Copy className="h-4 w-4" />
                Copiar para la web
              </Button>
              {destination.isPublished ? (
                <Button variant="outline" size="sm" onClick={onUnpublish} className="gap-2">
                  <Rocket className="h-4 w-4" />
                  Retirar
                </Button>
              ) : destination.canPublish ? (
                <Button size="sm" onClick={onPublish} className="gap-2">
                  <Rocket className="h-4 w-4" />
                  Mostrar
                </Button>
              ) : null}
            </>
          ) : null}
        </div>
      </td>
    </tr>
  );
}

function SummaryPill({ label, value, tone = "slate" }) {
  const styles = {
    slate: "bg-slate-100 text-slate-700 dark:bg-slate-800 dark:text-slate-200",
    emerald: "bg-emerald-100 text-emerald-700 dark:bg-emerald-900/30 dark:text-emerald-300",
    amber: "bg-amber-100 text-amber-700 dark:bg-amber-900/30 dark:text-amber-300",
  };

  return (
    <span className={`rounded-full px-3 py-1 text-xs font-semibold ${styles[tone] || styles.slate}`}>
      {label}: {value}
    </span>
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
          <h3 className="text-lg font-semibold text-slate-900 dark:text-white">
            {form.publicId ? "Editar pais" : "Nuevo pais"}
          </h3>
          <p className="mt-1 text-sm text-slate-500 dark:text-slate-400">
            Crea un pais para agrupar destinos y mantener la oferta ordenada.
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

function EmptyState({ icon: Icon, title, description }) {
  return (
    <div className="flex min-h-[220px] flex-col items-center justify-center gap-3 px-6 py-10 text-center">
      <div className="rounded-full bg-slate-100 p-4 text-slate-400 dark:bg-slate-800">
        <Icon className="h-7 w-7" />
      </div>
      <div className="space-y-1">
        <p className="text-base font-semibold text-slate-900 dark:text-white">{title}</p>
        <p className="text-sm text-slate-500 dark:text-slate-400">{description}</p>
      </div>
    </div>
  );
}
