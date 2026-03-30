import { useCallback, useEffect, useMemo, useState } from "react";
import {
  Building2,
  CalendarDays,
  Copy,
  Eye,
  FolderTree,
  Globe2,
  ImagePlus,
  Loader2,
  MapPinned,
  Pencil,
  Plus,
  Rocket,
  Save,
  Search,
  ShieldCheck,
  Trash2,
  UploadCloud,
  X,
} from "lucide-react";
import { api, buildAppUrl } from "../../../api";
import { showConfirm, showError, showSuccess } from "../../../alerts";
import { hasPermission } from "../../../auth";
import { ListPageHeader } from "../../../components/ui/ListPageHeader";
import { useDebounce } from "../../../hooks/useDebounce";

const inputClass =
  "w-full rounded-2xl border border-slate-200 bg-slate-50 px-4 py-3 text-sm text-slate-900 outline-none transition focus:border-indigo-500 focus:bg-white dark:border-slate-700 dark:bg-slate-950 dark:text-white";
const textareaClass = `${inputClass} min-h-[148px] resize-y`;

const emptyCountryForm = {
  publicId: null,
  name: "",
  slug: "",
  totalDestinations: 0,
  publishedDestinations: 0,
  draftDestinations: 0,
  countryPagePath: "",
};

const emptyDestinationForm = {
  publicId: null,
  countryPublicId: "",
  countryName: "",
  countrySlug: "",
  name: "",
  title: "",
  slug: "",
  tagline: "",
  displayOrder: 0,
  generalInfo: "",
  departures: [],
  isPublished: false,
  canPublish: false,
  publishIssues: [],
  hasHeroImage: false,
  heroImageUrl: null,
  heroImageFileName: null,
  publicPagePath: "",
  countryPagePath: "",
};

function createClientId() {
  if (typeof crypto !== "undefined" && typeof crypto.randomUUID === "function") {
    return crypto.randomUUID();
  }

  return `${Date.now()}-${Math.random().toString(36).slice(2)}`;
}

function createDepartureDraft(isPrimary = false) {
  return {
    clientId: createClientId(),
    publicId: null,
    startDate: "",
    nights: 7,
    transportLabel: "Aereo",
    hotelName: "",
    mealPlan: "",
    roomBase: "Doble",
    currency: "USD",
    salePrice: "",
    isPrimary,
    isActive: true,
  };
}

function slugify(value) {
  return String(value || "")
    .toLowerCase()
    .trim()
    .replace(/[^a-z0-9]+/g, "-")
    .replace(/^-+|-+$/g, "")
    .replace(/--+/g, "-");
}

function formatDate(value) {
  if (!value) {
    return "-";
  }

  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? "-" : date.toLocaleDateString("es-AR");
}

function formatMoney(value, currency = "USD") {
  if (value === null || value === undefined || value === "") {
    return "-";
  }

  return `${currency} ${Number(value).toLocaleString("es-AR", {
    minimumFractionDigits: 0,
    maximumFractionDigits: 2,
  })}`;
}

function sanitizeEmbedToken(value) {
  return String(value || "")
    .toLowerCase()
    .replace(/[^a-z0-9_-]+/g, "-")
    .replace(/^-+|-+$/g, "")
    .slice(0, 48);
}

function buildSnippet(item, { embedKind = "destination" } = {}) {
  const safeTitle = String(item.title || "Destino").replace(/"/g, "&quot;");
  const safeSlug = sanitizeEmbedToken(item.slug || "embed");
  const embedId = `mt-${embedKind}-${safeSlug || "embed"}`;
  const fallbackPath =
    embedKind === "country" ? `/embed/countries/${item.slug || "pais"}` : `/embed/packages/${item.slug || "destino"}`;
  const baseSrc = buildAppUrl(item.publicPagePath || fallbackPath);
  const srcUrl = new URL(baseSrc);
  srcUrl.searchParams.set("embedId", embedId);

  return `<iframe id="${embedId}" src="${srcUrl.toString()}" loading="lazy" scrolling="no" style="width:100%;min-height:320px;height:320px;border:0;display:block;overflow:hidden;" title="${safeTitle}"></iframe>
<script>
(function () {
  var iframe = document.getElementById(${JSON.stringify(embedId)});
  if (!iframe) return;
  var allowedOrigin = ${JSON.stringify(srcUrl.origin)};
  var expectedEmbedId = ${JSON.stringify(embedId)};
  var minHeight = 320;
  function applyHeight(height) {
    var parsed = Number(height || 0);
    if (!parsed || !isFinite(parsed)) return;
    iframe.style.height = Math.max(minHeight, Math.min(5200, Math.round(parsed))) + "px";
  }
  function handleMessage(event) {
    if (event.origin !== allowedOrigin) return;
    var data = event.data || {};
    if (data.type !== "magnatravel:embed:resize") return;
    if (data.embedId && data.embedId !== expectedEmbedId) return;
    applyHeight(data.height);
  }
  window.addEventListener("message", handleMessage, false);
})();
</script>`;
}

function buildDestinationSnippet(item) {
  return buildSnippet(
    {
      title: item.title || item.name || "Destino",
      slug: item.slug || "destino",
      publicPagePath: item.publicPagePath || `/embed/packages/${item.slug}`,
    },
    { embedKind: "destination" }
  );
}

function buildCountrySnippet(item) {
  return buildSnippet(
    {
      title: item.name ? `Destinos en ${item.name}` : "Destinos",
      slug: item.slug || "pais",
      publicPagePath: item.countryPagePath || `/embed/countries/${item.slug}`,
    },
    { embedKind: "country" }
  );
}

function mapCountryToForm(country) {
  return {
    publicId: country.publicId,
    name: country.name || "",
    slug: country.slug || "",
    totalDestinations: country.totalDestinations || 0,
    publishedDestinations: country.publishedDestinations || 0,
    draftDestinations: country.draftDestinations || 0,
    countryPagePath: country.countryPagePath || `/embed/countries/${country.slug}`,
  };
}

function mapDestinationDetailToForm(detail) {
  const departures = (detail.departures || []).map((departure) => ({
    clientId: departure.publicId || createClientId(),
    publicId: departure.publicId,
    startDate: departure.startDate?.split("T")[0] || "",
    nights: departure.nights ?? 0,
    transportLabel: departure.transportLabel || "",
    hotelName: departure.hotelName || "",
    mealPlan: departure.mealPlan || "",
    roomBase: departure.roomBase || "",
    currency: departure.currency || "USD",
    salePrice: departure.salePrice ?? "",
    isPrimary: Boolean(departure.isPrimary),
    isActive: Boolean(departure.isActive),
  }));

  return {
    publicId: detail.publicId,
    countryPublicId: detail.countryPublicId || "",
    countryName: detail.countryName || "",
    countrySlug: detail.countrySlug || "",
    name: detail.name || "",
    title: detail.title || "",
    slug: detail.slug || "",
    tagline: detail.tagline || "",
    displayOrder: detail.displayOrder ?? 0,
    generalInfo: detail.generalInfo || "",
    departures,
    isPublished: Boolean(detail.isPublished),
    canPublish: Boolean(detail.canPublish),
    publishIssues: detail.publishIssues || [],
    hasHeroImage: Boolean(detail.hasHeroImage),
    heroImageUrl: detail.heroImageUrl || null,
    heroImageFileName: detail.heroImageFileName || null,
    publicPagePath: detail.publicPagePath || `/embed/packages/${detail.slug}`,
    countryPagePath: detail.countryPagePath || (detail.countrySlug ? `/embed/countries/${detail.countrySlug}` : ""),
  };
}

function createEmptyDestinationForm(country) {
  return {
    ...emptyDestinationForm,
    countryPublicId: country?.publicId || "",
    countryName: country?.name || "",
    countrySlug: country?.slug || "",
    countryPagePath: country?.countryPagePath || (country?.slug ? `/embed/countries/${country.slug}` : ""),
  };
}

export default function PackagesPage() {
  const canEdit = hasPermission("paquetes.edit");
  const canPublish = hasPermission("paquetes.publish");
  const [countries, setCountries] = useState([]);
  const [countriesLoading, setCountriesLoading] = useState(true);
  const [countrySearch, setCountrySearch] = useState("");
  const [selectedCountryPublicId, setSelectedCountryPublicId] = useState("");
  const [destinations, setDestinations] = useState([]);
  const [destinationsLoading, setDestinationsLoading] = useState(false);
  const [destinationSearch, setDestinationSearch] = useState("");
  const [destinationStatusFilter, setDestinationStatusFilter] = useState("all");
  const [countryModalOpen, setCountryModalOpen] = useState(false);
  const [countrySaving, setCountrySaving] = useState(false);
  const [countryForm, setCountryForm] = useState(emptyCountryForm);
  const [countrySlugTouched, setCountrySlugTouched] = useState(false);
  const [destinationModalOpen, setDestinationModalOpen] = useState(false);
  const [destinationSaving, setDestinationSaving] = useState(false);
  const [destinationForm, setDestinationForm] = useState(emptyDestinationForm);
  const [slugTouched, setSlugTouched] = useState(false);
  const [departureDraft, setDepartureDraft] = useState(createDepartureDraft(true));
  const [editingDepartureClientId, setEditingDepartureClientId] = useState(null);
  const [selectedImageFile, setSelectedImageFile] = useState(null);
  const [imagePreviewUrl, setImagePreviewUrl] = useState(null);
  const debouncedCountrySearch = useDebounce(countrySearch, 300);

  const selectedCountry = useMemo(
    () => countries.find((country) => country.publicId === selectedCountryPublicId) || null,
    [countries, selectedCountryPublicId]
  );

  const summary = useMemo(
    () =>
      countries.reduce(
        (accumulator, country) => {
          accumulator.countries += 1;
          accumulator.destinations += country.totalDestinations || 0;
          accumulator.published += country.publishedDestinations || 0;
          accumulator.draft += country.draftDestinations || 0;
          return accumulator;
        },
        { countries: 0, destinations: 0, published: 0, draft: 0 }
      ),
    [countries]
  );

  const departuresSorted = useMemo(
    () =>
      [...(destinationForm.departures || [])].sort((left, right) => {
        const leftDate = new Date(left.startDate);
        const rightDate = new Date(right.startDate);
        return leftDate - rightDate;
      }),
    [destinationForm.departures]
  );

  const filteredDestinations = useMemo(() => {
    return destinations.filter((destination) => {
      const matchesSearch = !destinationSearch.trim()
        ? true
        : `${destination.title} ${destination.name} ${destination.slug}`.toLowerCase().includes(destinationSearch.trim().toLowerCase());

      const matchesStatus =
        destinationStatusFilter === "all"
          ? true
          : destinationStatusFilter === "published"
            ? destination.isPublished
            : !destination.isPublished;

      return matchesSearch && matchesStatus;
    });
  }, [destinationSearch, destinationStatusFilter, destinations]);

  const effectiveCountrySlug = slugify(countryForm.slug || countryForm.name);
  const effectiveDestinationSlug = slugify(destinationForm.slug || destinationForm.title || destinationForm.name);

  const currentCountrySnippetItem = useMemo(() => {
    if (destinationForm.countrySlug) {
      return {
        name: destinationForm.countryName || "Destino",
        slug: destinationForm.countrySlug,
        countryPagePath: destinationForm.countryPagePath || `/embed/countries/${destinationForm.countrySlug}`,
      };
    }

    if (!selectedCountry) {
      return null;
    }

    return {
      name: selectedCountry.name,
      slug: selectedCountry.slug,
      countryPagePath: selectedCountry.countryPagePath || `/embed/countries/${selectedCountry.slug}`,
    };
  }, [destinationForm.countryName, destinationForm.countryPagePath, destinationForm.countrySlug, selectedCountry]);

  const currentDestinationSnippetItem = useMemo(
    () => ({
      title: destinationForm.title || destinationForm.name || "Destino",
      name: destinationForm.name || destinationForm.title || "Destino",
      slug: effectiveDestinationSlug,
      publicPagePath: effectiveDestinationSlug ? `/embed/packages/${effectiveDestinationSlug}` : "",
    }),
    [destinationForm.name, destinationForm.title, effectiveDestinationSlug]
  );

  const destinationEmbedSnippet = effectiveDestinationSlug ? buildDestinationSnippet(currentDestinationSnippetItem) : "";
  const countryEmbedSnippet = currentCountrySnippetItem?.slug ? buildCountrySnippet(currentCountrySnippetItem) : "";

  const loadCountries = useCallback(
    async (preferredCountryPublicId = null) => {
      setCountriesLoading(true);

      try {
        const params = new URLSearchParams();
        if (debouncedCountrySearch.trim()) {
          params.set("search", debouncedCountrySearch.trim());
        }

        const response = await api.get(`/countries${params.toString() ? `?${params.toString()}` : ""}`);
        const items = response || [];
        setCountries(items);

        setSelectedCountryPublicId((currentValue) => {
          if (preferredCountryPublicId && items.some((item) => item.publicId === preferredCountryPublicId)) {
            return preferredCountryPublicId;
          }
          if (currentValue && items.some((item) => item.publicId === currentValue)) {
            return currentValue;
          }
          return items[0]?.publicId || "";
        });
      } catch (error) {
        console.error("Error loading countries:", error);
        setCountries([]);
        setSelectedCountryPublicId("");
        showError("No se pudieron cargar los países.");
      } finally {
        setCountriesLoading(false);
      }
    },
    [debouncedCountrySearch]
  );

  const loadDestinations = useCallback(async (countryPublicId) => {
    if (!countryPublicId) {
      setDestinations([]);
      return;
    }

    setDestinationsLoading(true);
    try {
      const response = await api.get(`/countries/${countryPublicId}/destinations`);
      setDestinations(response || []);
    } catch (error) {
      console.error("Error loading destinations:", error);
      setDestinations([]);
      showError("No se pudieron cargar los destinos del país.");
    } finally {
      setDestinationsLoading(false);
    }
  }, []);

  useEffect(() => {
    loadCountries();
  }, [loadCountries]);

  useEffect(() => {
    if (!selectedCountryPublicId) {
      setDestinations([]);
      return;
    }

    loadDestinations(selectedCountryPublicId);
  }, [loadDestinations, selectedCountryPublicId]);

  useEffect(() => {
    if (!imagePreviewUrl || !imagePreviewUrl.startsWith("blob:")) {
      return undefined;
    }

    return () => URL.revokeObjectURL(imagePreviewUrl);
  }, [imagePreviewUrl]);

  function resetCountryEditor(nextForm = emptyCountryForm) {
    const safeForm = { ...emptyCountryForm, ...nextForm };
    setCountryForm(safeForm);
    setCountrySlugTouched(Boolean(safeForm.slug));
  }

  function resetDestinationEditor(nextForm = createEmptyDestinationForm(selectedCountry)) {
    const safeForm = {
      ...emptyDestinationForm,
      ...nextForm,
      departures: [...(nextForm.departures || [])],
      publishIssues: [...(nextForm.publishIssues || [])],
    };

    setDestinationForm(safeForm);
    setSlugTouched(Boolean(safeForm.slug));
    setEditingDepartureClientId(null);
    setDepartureDraft(createDepartureDraft((safeForm.departures || []).length === 0));
    setSelectedImageFile(null);
    setImagePreviewUrl(safeForm.heroImageUrl || null);
  }

  function openCreateCountryModal() {
    resetCountryEditor(emptyCountryForm);
    setCountryModalOpen(true);
  }

  function openEditCountryModal(country) {
    resetCountryEditor(mapCountryToForm(country));
    setCountryModalOpen(true);
  }

  function closeCountryModal() {
    setCountryModalOpen(false);
    resetCountryEditor(emptyCountryForm);
  }

  function openCreateDestinationModal() {
    if (!selectedCountry) {
      showError("Selecciona un país antes de crear un destino.");
      return;
    }

    resetDestinationEditor(createEmptyDestinationForm(selectedCountry));
    setDestinationModalOpen(true);
  }

  async function openEditDestinationModal(publicId) {
    try {
      const detail = await api.get(`/destinations/${publicId}`);
      resetDestinationEditor(mapDestinationDetailToForm(detail));
      setDestinationModalOpen(true);
    } catch (error) {
      showError(error.message || "No se pudo abrir el destino.");
    }
  }

  function closeDestinationModal() {
    setDestinationModalOpen(false);
    resetDestinationEditor(createEmptyDestinationForm(selectedCountry));
  }

  function updateCountryField(key, value) {
    setCountryForm((previous) => {
      const next = { ...previous, [key]: value };
      if (key === "name" && !countrySlugTouched) {
        next.slug = slugify(value);
        next.countryPagePath = next.slug ? `/embed/countries/${next.slug}` : "";
      }

      if (key === "slug") {
        next.countryPagePath = value ? `/embed/countries/${slugify(value)}` : "";
      }

      return next;
    });
  }

  function updateDestinationField(key, value) {
    setDestinationForm((previous) => {
      const next = { ...previous, [key]: value };

      if (key === "title" && !slugTouched) {
        next.slug = slugify(value);
        next.publicPagePath = next.slug ? `/embed/packages/${next.slug}` : "";
      }

      if (key === "slug") {
        next.publicPagePath = value ? `/embed/packages/${slugify(value)}` : "";
      }

      return next;
    });
  }

  function updateDepartureField(key, value) {
    setDepartureDraft((previous) => ({ ...previous, [key]: value }));
  }

  function handleImageChange(event) {
    const file = event.target.files?.[0];
    if (!file) {
      return;
    }

    setSelectedImageFile(file);
    setImagePreviewUrl(URL.createObjectURL(file));
  }

  function clearDepartureEditor() {
    setEditingDepartureClientId(null);
    setDepartureDraft(createDepartureDraft((destinationForm.departures || []).length === 0));
  }

  function persistDeparture() {
    if (!departureDraft.startDate || !departureDraft.hotelName.trim() || !departureDraft.mealPlan.trim()) {
      showError("Completa fecha, hotel y régimen antes de guardar la salida.");
      return;
    }

    if (!departureDraft.transportLabel.trim() || !departureDraft.roomBase.trim()) {
      showError("Completa transporte y base antes de guardar la salida.");
      return;
    }

    if (Number(departureDraft.nights) <= 0 || Number(departureDraft.salePrice) <= 0) {
      showError("Las noches y la tarifa deben ser mayores a cero.");
      return;
    }

    setDestinationForm((previous) => {
      const normalized = {
        ...departureDraft,
        nights: Number(departureDraft.nights),
        salePrice: Number(departureDraft.salePrice),
        currency: (departureDraft.currency || "USD").toUpperCase().slice(0, 3),
      };

      let nextDepartures = [...previous.departures];
      const editedIndex = nextDepartures.findIndex((item) => item.clientId === editingDepartureClientId);

      if (editedIndex >= 0) {
        nextDepartures[editedIndex] = normalized;
      } else {
        nextDepartures.push(normalized);
      }

      if (normalized.isPrimary) {
        nextDepartures = nextDepartures.map((item) => ({
          ...item,
          isPrimary: item.clientId === normalized.clientId,
        }));
      }

      if (!nextDepartures.some((item) => item.isPrimary) && nextDepartures.length > 0) {
        nextDepartures = nextDepartures.map((item, index) => ({
          ...item,
          isPrimary: index === 0,
        }));
      }

      return { ...previous, departures: nextDepartures };
    });

    clearDepartureEditor();
  }

  function editDeparture(departure) {
    setEditingDepartureClientId(departure.clientId);
    setDepartureDraft({ ...departure });
  }

  async function confirmRemoveDeparture(clientId) {
    const confirmed = await showConfirm(
      "Eliminar salida",
      "La salida será removida del destino cuando guardes los cambios.",
      "Sí, eliminar",
      "red"
    );

    if (!confirmed) {
      return;
    }

    setDestinationForm((previous) => {
      let nextDepartures = previous.departures.filter((item) => item.clientId !== clientId);
      if (nextDepartures.length > 0 && !nextDepartures.some((item) => item.isPrimary)) {
        nextDepartures = nextDepartures.map((item, index) => ({
          ...item,
          isPrimary: index === 0,
        }));
      }

      return { ...previous, departures: nextDepartures };
    });

    if (editingDepartureClientId === clientId) {
      clearDepartureEditor();
    }
  }

  async function saveCountry(event) {
    event.preventDefault();

    if (!countryForm.name.trim()) {
      showError("El nombre del país es obligatorio.");
      return;
    }

    if (!effectiveCountrySlug) {
      showError("El slug del país es obligatorio.");
      return;
    }

    setCountrySaving(true);
    try {
      const payload = {
        name: countryForm.name.trim(),
        slug: effectiveCountrySlug,
      };

      const response = countryForm.publicId
        ? await api.put(`/countries/${countryForm.publicId}`, payload)
        : await api.post("/countries", payload);

      showSuccess(countryForm.publicId ? "País actualizado." : "País creado.");
      closeCountryModal();
      await loadCountries(response.publicId);
    } catch (error) {
      console.error("Error saving country:", error);
      showError(error.message || "No se pudo guardar el país.");
    } finally {
      setCountrySaving(false);
    }
  }

  async function saveDestination(event) {
    event.preventDefault();

    if (!destinationForm.countryPublicId) {
      showError("El destino debe pertenecer a un país.");
      return;
    }

    if (!destinationForm.name.trim()) {
      showError("El nombre del destino es obligatorio.");
      return;
    }

    if (!destinationForm.title.trim()) {
      showError("El título comercial es obligatorio.");
      return;
    }

    if (!effectiveDestinationSlug) {
      showError("El slug del destino es obligatorio.");
      return;
    }

    setDestinationSaving(true);
    try {
      const payload = {
        countryPublicId: destinationForm.countryPublicId,
        name: destinationForm.name.trim(),
        title: destinationForm.title.trim(),
        slug: effectiveDestinationSlug,
        tagline: destinationForm.tagline.trim() || null,
        displayOrder: Number(destinationForm.displayOrder || 0),
        generalInfo: destinationForm.generalInfo.trim() || null,
        departures: destinationForm.departures.map((departure) => ({
          publicId: departure.publicId || null,
          startDate: new Date(`${departure.startDate}T00:00:00`).toISOString(),
          nights: Number(departure.nights),
          transportLabel: departure.transportLabel.trim(),
          hotelName: departure.hotelName.trim(),
          mealPlan: departure.mealPlan.trim(),
          roomBase: departure.roomBase.trim(),
          currency: (departure.currency || "USD").toUpperCase().slice(0, 3),
          salePrice: Number(departure.salePrice),
          isPrimary: Boolean(departure.isPrimary),
          isActive: Boolean(departure.isActive),
        })),
      };

      const response = destinationForm.publicId
        ? await api.put(`/destinations/${destinationForm.publicId}`, payload)
        : await api.post("/destinations", payload);

      if (selectedImageFile) {
        const imageFormData = new FormData();
        imageFormData.append("file", selectedImageFile);
        await api.post(`/destinations/${response.publicId}/hero-image`, imageFormData);
      }

      showSuccess(destinationForm.publicId ? "Destino actualizado." : "Destino creado.");
      closeDestinationModal();
      await Promise.all([
        loadCountries(destinationForm.countryPublicId),
        loadDestinations(destinationForm.countryPublicId),
      ]);
    } catch (error) {
      console.error("Error saving destination:", error);
      showError(error.message || "No se pudo guardar el destino.");
    } finally {
      setDestinationSaving(false);
    }
  }

  async function handlePublish(destination) {
    const confirmed = await showConfirm(
      "Publicar destino",
      "El destino quedará disponible en la ficha pública embebible.",
      "Sí, publicar",
      "emerald"
    );
    if (!confirmed) {
      return;
    }

    try {
      await api.patch(`/destinations/${destination.publicId}/publish`);
      showSuccess("Destino publicado.");
      await Promise.all([loadCountries(selectedCountryPublicId), loadDestinations(selectedCountryPublicId)]);
    } catch (error) {
      showError(error.message || "No se pudo publicar el destino.");
    }
  }

  async function handleUnpublish(destination) {
    const confirmed = await showConfirm(
      "Despublicar destino",
      "La ficha dejará de estar disponible desde el código embebido.",
      "Sí, despublicar",
      "amber"
    );
    if (!confirmed) {
      return;
    }

    try {
      await api.patch(`/destinations/${destination.publicId}/unpublish`);
      showSuccess("Destino despublicado.");
      await Promise.all([loadCountries(selectedCountryPublicId), loadDestinations(selectedCountryPublicId)]);
    } catch (error) {
      showError(error.message || "No se pudo despublicar el destino.");
    }
  }

  async function copyDestinationSnippet(item) {
    try {
      await navigator.clipboard.writeText(buildDestinationSnippet(item));
      showSuccess("Iframe por destino copiado.");
    } catch {
      showError("No se pudo copiar el código embebido.");
    }
  }

  async function copyCountrySnippet(item) {
    if (!item?.slug) {
      showError("Completa el slug del país para generar ese iframe.");
      return;
    }

    try {
      await navigator.clipboard.writeText(buildCountrySnippet(item));
      showSuccess("Iframe por país copiado.");
    } catch {
      showError("No se pudo copiar el código embebido.");
    }
  }

  function previewDestination(item) {
    if (!item?.slug) {
      showError("Define un slug antes de abrir la ficha pública.");
      return;
    }

    window.open(buildAppUrl(item.publicPagePath || `/embed/packages/${item.slug}`), "_blank", "noopener,noreferrer");
  }

  function previewCountry(item) {
    if (!item?.slug) {
      showError("Completa el slug del país antes de abrir el iframe por país.");
      return;
    }

    window.open(buildAppUrl(item.countryPagePath || `/embed/countries/${item.slug}`), "_blank", "noopener,noreferrer");
  }

  return (
    <>
      <div className="space-y-6">
        <ListPageHeader
          title="Países y destinos"
          subtitle="Organiza los países del sitio y dentro de cada uno arma los destinos con sus salidas embebibles."
          actions={
            <div className="flex flex-wrap gap-2">
              {canEdit ? (
                <button
                  type="button"
                  onClick={openCreateCountryModal}
                  className="inline-flex items-center gap-2 rounded-xl bg-indigo-600 px-4 py-2.5 text-sm font-semibold text-white shadow-lg shadow-indigo-500/20 transition hover:bg-indigo-500"
                >
                  <Plus className="h-4 w-4" />
                  Nuevo país
                </button>
              ) : null}
              {canEdit ? (
                <button
                  type="button"
                  onClick={openCreateDestinationModal}
                  disabled={!selectedCountry}
                  className="inline-flex items-center gap-2 rounded-xl border border-slate-200 bg-white px-4 py-2.5 text-sm font-semibold text-slate-700 transition hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-50 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-200 dark:hover:bg-slate-800"
                >
                  <MapPinned className="h-4 w-4" />
                  Nuevo destino
                </button>
              ) : null}
            </div>
          }
        />

        <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
          <SummaryCard icon={Building2} label="Países" value={summary.countries} tone="indigo" />
          <SummaryCard icon={FolderTree} label="Destinos" value={summary.destinations} tone="slate" />
          <SummaryCard icon={Rocket} label="Publicados" value={summary.published} tone="emerald" />
          <SummaryCard icon={ShieldCheck} label="Borradores" value={summary.draft} tone="amber" />
        </div>

        <div className="grid gap-6 xl:grid-cols-[360px_minmax(0,1fr)]">
          <div className="space-y-4 rounded-3xl border border-slate-200 bg-white p-5 shadow-sm dark:border-slate-800 dark:bg-slate-900">
            <SectionTitle
              eyebrow="Países"
              title="Base de organización"
              description="Cada país agrupa destinos y genera su propio iframe con dropdown."
            />

            <div className="relative">
              <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" />
              <input
                type="text"
                value={countrySearch}
                onChange={(event) => setCountrySearch(event.target.value)}
                placeholder="Buscar por país o slug..."
                className="w-full rounded-xl border border-slate-200 bg-slate-50 py-2.5 pl-10 pr-4 text-sm text-slate-900 outline-none transition focus:border-indigo-500 focus:bg-white dark:border-slate-700 dark:bg-slate-950 dark:text-white"
              />
            </div>

            <div className="space-y-3">
              {countriesLoading ? (
                <div className="flex h-56 items-center justify-center">
                  <Loader2 className="h-7 w-7 animate-spin text-indigo-500" />
                </div>
              ) : countries.length === 0 ? (
                <EmptyState
                  icon={Building2}
                  title="Todavía no hay países."
                  description="Crea el primero para empezar a ordenar destinos y iframes."
                />
              ) : (
                countries.map((country) => (
                  <CountryCard
                    key={country.publicId}
                    country={country}
                    selected={country.publicId === selectedCountryPublicId}
                    onSelect={() => setSelectedCountryPublicId(country.publicId)}
                    onEdit={() => openEditCountryModal(country)}
                    onPreview={() => previewCountry(country)}
                    onCopy={() => copyCountrySnippet(country)}
                    canEdit={canEdit}
                  />
                ))
              )}
            </div>
          </div>

          <div className="space-y-4 rounded-3xl border border-slate-200 bg-white p-5 shadow-sm dark:border-slate-800 dark:bg-slate-900">
            {selectedCountry ? (
              <>
                <div className="flex flex-col gap-4 xl:flex-row xl:items-start xl:justify-between">
                  <div>
                    <p className="text-xs font-bold uppercase tracking-[0.24em] text-indigo-600 dark:text-indigo-300">Destino real</p>
                    <h2 className="mt-2 text-2xl font-bold tracking-tight text-slate-950 dark:text-white">{selectedCountry.name}</h2>
                    <p className="mt-2 text-sm text-slate-500 dark:text-slate-400">
                      Administra los destinos de este país y copia el iframe agrupado con dropdown.
                    </p>
                    <div className="mt-3 flex flex-wrap gap-2">
                      <span className="rounded-full bg-slate-100 px-2.5 py-1 text-[11px] font-semibold text-slate-700 dark:bg-slate-800 dark:text-slate-200">
                        Slug: {selectedCountry.slug}
                      </span>
                      <span className="rounded-full bg-indigo-50 px-2.5 py-1 text-[11px] font-semibold text-indigo-700 dark:bg-indigo-900/30 dark:text-indigo-200">
                        {selectedCountry.totalDestinations} destinos
                      </span>
                    </div>
                  </div>

                  <div className="flex flex-wrap gap-2">
                    <ActionButton icon={Eye} label="Preview país" onClick={() => previewCountry(selectedCountry)} />
                    <ActionButton icon={Copy} label="Iframe país" onClick={() => copyCountrySnippet(selectedCountry)} />
                    {canEdit ? <ActionButton icon={Pencil} label="Editar país" onClick={() => openEditCountryModal(selectedCountry)} /> : null}
                  </div>
                </div>

                <div className="grid gap-3 rounded-2xl border border-slate-200 bg-slate-50/70 p-4 dark:border-slate-800 dark:bg-slate-950/50 lg:grid-cols-[minmax(0,1fr)_220px]">
                  <div className="relative">
                    <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" />
                    <input
                      type="text"
                      value={destinationSearch}
                      onChange={(event) => setDestinationSearch(event.target.value)}
                      placeholder="Buscar por título, destino o slug..."
                      className="w-full rounded-xl border border-slate-200 bg-white py-2.5 pl-10 pr-4 text-sm text-slate-900 outline-none transition focus:border-indigo-500 dark:border-slate-700 dark:bg-slate-900 dark:text-white"
                    />
                  </div>

                  <select
                    value={destinationStatusFilter}
                    onChange={(event) => setDestinationStatusFilter(event.target.value)}
                    className="rounded-xl border border-slate-200 bg-white px-4 py-2.5 text-sm font-medium text-slate-700 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-200"
                  >
                    <option value="all">Todos</option>
                    <option value="published">Publicados</option>
                    <option value="draft">Borradores</option>
                  </select>
                </div>

                <div className="overflow-hidden rounded-3xl border border-slate-200 dark:border-slate-800">
                  {destinationsLoading ? (
                    <div className="flex h-64 items-center justify-center">
                      <Loader2 className="h-8 w-8 animate-spin text-indigo-500" />
                    </div>
                  ) : filteredDestinations.length === 0 ? (
                    <EmptyState
                      icon={MapPinned}
                      title={destinations.length === 0 ? "Todavía no hay destinos." : "No hubo coincidencias."}
                      description={
                        destinations.length === 0
                          ? "Crea el primer destino dentro de este país para armar sus salidas públicas."
                          : "Ajusta los filtros para volver a ver los destinos de este país."
                      }
                    />
                  ) : (
                    <div className="overflow-x-auto">
                      <table className="min-w-full divide-y divide-slate-200 dark:divide-slate-800">
                        <thead className="bg-slate-50/80 dark:bg-slate-950/40">
                          <tr className="text-left text-xs font-bold uppercase tracking-wider text-slate-500 dark:text-slate-400">
                            <th className="px-6 py-4">Destino</th>
                            <th className="px-4 py-4">Estado</th>
                            <th className="px-4 py-4">Desde</th>
                            <th className="px-4 py-4">Salidas</th>
                            <th className="px-4 py-4">Slug</th>
                            <th className="px-6 py-4 text-right">Acciones</th>
                          </tr>
                        </thead>
                        <tbody className="divide-y divide-slate-100 dark:divide-slate-800">
                          {filteredDestinations.map((destination) => (
                            <DestinationRow
                              key={destination.publicId}
                              destination={destination}
                              canEdit={canEdit}
                              canPublish={canPublish}
                              onPreview={() => previewDestination(destination)}
                              onCopy={() => copyDestinationSnippet(destination)}
                              onEdit={() => openEditDestinationModal(destination.publicId)}
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
            ) : (
              <EmptyState
                icon={Globe2}
                title="Selecciona un país"
                description="Desde aquí vas a administrar sus destinos, sus salidas y los iframes agrupados."
              />
            )}
          </div>
        </div>
      </div>

      <ModalShell
        open={countryModalOpen}
        title={countryForm.publicId ? "Editar pais" : "Nuevo pais"}
        subtitle="El pais agrupa destinos y define el iframe con dropdown del sitio."
        onClose={closeCountryModal}
        footer={
          <>
            <button
              type="button"
              onClick={closeCountryModal}
              className="rounded-xl border border-slate-200 px-4 py-2.5 text-sm font-semibold text-slate-600 transition hover:bg-slate-50 dark:border-slate-700 dark:text-slate-300 dark:hover:bg-slate-800"
            >
              Cancelar
            </button>
            {canEdit ? (
              <button
                type="submit"
                form="country-editor-form"
                disabled={countrySaving}
                className="inline-flex items-center gap-2 rounded-xl bg-indigo-600 px-4 py-2.5 text-sm font-semibold text-white transition hover:bg-indigo-500 disabled:cursor-not-allowed disabled:opacity-60"
              >
                {countrySaving ? <Loader2 className="h-4 w-4 animate-spin" /> : <Save className="h-4 w-4" />}
                Guardar pais
              </button>
            ) : null}
          </>
        }
      >
        <form id="country-editor-form" onSubmit={saveCountry} className="space-y-6">
          <div className="grid gap-4 md:grid-cols-2">
            <Field label="Nombre del pais">
              <input
                type="text"
                value={countryForm.name}
                onChange={(event) => updateCountryField("name", event.target.value)}
                placeholder="Republica Dominicana"
                className={inputClass}
                disabled={!canEdit}
              />
            </Field>

            <Field label="Slug del pais" hint="Se usa para /embed/countries/...">
              <input
                type="text"
                value={countryForm.slug}
                onChange={(event) => {
                  setCountrySlugTouched(true);
                  updateCountryField("slug", event.target.value);
                }}
                placeholder="republica-dominicana"
                className={inputClass}
                disabled={!canEdit}
              />
            </Field>
          </div>

          <SnippetPreview
            title="Iframe por pais"
            subtitle="Este iframe muestra el dropdown de destinos de este pais."
            snippet={effectiveCountrySlug ? buildCountrySnippet({ name: countryForm.name, slug: effectiveCountrySlug }) : ""}
            emptyMessage="Completa el nombre del pais para generar el iframe."
            onPreview={() => previewCountry({ name: countryForm.name, slug: effectiveCountrySlug })}
            onCopy={() => copyCountrySnippet({ name: countryForm.name, slug: effectiveCountrySlug })}
            disabled={!effectiveCountrySlug}
          />
        </form>
      </ModalShell>

      <ModalShell
        open={destinationModalOpen}
        title={destinationForm.publicId ? "Editar destino" : "Nuevo destino"}
        subtitle="Configura la ficha publica, la imagen principal y las salidas que luego se mostraran en el iframe."
        onClose={closeDestinationModal}
        footer={
          <>
            <button
              type="button"
              onClick={closeDestinationModal}
              className="rounded-xl border border-slate-200 px-4 py-2.5 text-sm font-semibold text-slate-600 transition hover:bg-slate-50 dark:border-slate-700 dark:text-slate-300 dark:hover:bg-slate-800"
            >
              Cancelar
            </button>
            {canEdit ? (
              <button
                type="submit"
                form="destination-editor-form"
                disabled={destinationSaving}
                className="inline-flex items-center gap-2 rounded-xl bg-indigo-600 px-4 py-2.5 text-sm font-semibold text-white transition hover:bg-indigo-500 disabled:cursor-not-allowed disabled:opacity-60"
              >
                {destinationSaving ? <Loader2 className="h-4 w-4 animate-spin" /> : <Save className="h-4 w-4" />}
                Guardar destino
              </button>
            ) : null}
          </>
        }
      >
        <form id="destination-editor-form" onSubmit={saveDestination} className="space-y-8">
          <div className="grid gap-4 lg:grid-cols-[minmax(0,1.8fr)_minmax(280px,1fr)]">
            <div className="space-y-4 rounded-3xl border border-slate-200 bg-slate-50/80 p-5 dark:border-slate-800 dark:bg-slate-950/60">
              <SectionTitle
                eyebrow="Ficha general"
                title="Lo primero que va a ver el sitio"
                description="Cada destino vive dentro de un pais y mantiene su iframe publico individual."
              />

              <div className="grid gap-4 md:grid-cols-2">
                <Field label="Nombre del destino" hint="Es el nombre visible dentro del dropdown del pais.">
                  <input
                    type="text"
                    value={destinationForm.name}
                    onChange={(event) => updateDestinationField("name", event.target.value)}
                    placeholder="Punta Cana"
                    className={inputClass}
                    disabled={!canEdit}
                  />
                </Field>

                <Field label="Titulo comercial">
                  <input
                    type="text"
                    value={destinationForm.title}
                    onChange={(event) => updateDestinationField("title", event.target.value)}
                    placeholder="Punta Cana All Inclusive"
                    className={inputClass}
                    disabled={!canEdit}
                  />
                </Field>
              </div>

              <div className="grid gap-4 md:grid-cols-[minmax(0,1fr)_180px]">
                <Field label="Slug publico" hint="Se usa para la URL del iframe del destino y debe ser unico.">
                  <input
                    type="text"
                    value={destinationForm.slug}
                    onChange={(event) => {
                      setSlugTouched(true);
                      updateDestinationField("slug", event.target.value);
                    }}
                    placeholder="punta-cana"
                    className={inputClass}
                    disabled={!canEdit}
                  />
                </Field>

                <Field label="Orden destino" hint="Menor numero, mas arriba en el pais.">
                  <input
                    type="number"
                    min="0"
                    value={destinationForm.displayOrder}
                    onChange={(event) => updateDestinationField("displayOrder", event.target.value)}
                    className={inputClass}
                    disabled={!canEdit}
                  />
                </Field>
              </div>

              <Field label="Bajada comercial">
                <input
                  type="text"
                  value={destinationForm.tagline}
                  onChange={(event) => updateDestinationField("tagline", event.target.value)}
                  placeholder="Vivi el Caribe"
                  className={inputClass}
                  disabled={!canEdit}
                />
              </Field>

              <div className="grid gap-3 md:grid-cols-2">
                <InfoPill label="Pais asociado" tone="indigo" value={destinationForm.countryName || "Pendiente"} />
                <InfoPill label="Slug de pais" tone={destinationForm.countrySlug ? "indigo" : "amber"} value={destinationForm.countrySlug || "Pendiente"} />
              </div>

              <div className="rounded-2xl border border-dashed border-indigo-200 bg-white p-4 dark:border-indigo-900/60 dark:bg-slate-900">
                <div>
                  <p className="text-sm font-semibold text-slate-900 dark:text-white">Previews tecnicos</p>
                  <p className="mt-1 text-xs text-slate-500 dark:text-slate-400">
                    Mantienes el iframe individual del destino y, a la vez, el iframe agrupado del pais.
                  </p>
                </div>

                <div className="mt-4 grid gap-4 xl:grid-cols-2">
                  <SnippetPreview
                    title="Iframe por destino"
                    subtitle="Ficha publica real del destino."
                    snippet={destinationEmbedSnippet}
                    emptyMessage="Completa al menos el titulo para generar el iframe del destino."
                    onPreview={() => previewDestination(currentDestinationSnippetItem)}
                    onCopy={() => copyDestinationSnippet(currentDestinationSnippetItem)}
                    disabled={!effectiveDestinationSlug}
                  />
                  <SnippetPreview
                    title="Iframe por pais"
                    subtitle="Agrupa destinos del pais con dropdown."
                    snippet={countryEmbedSnippet}
                    emptyMessage="El pais asociado todavia no tiene slug disponible."
                    onPreview={() => previewCountry(currentCountrySnippetItem)}
                    onCopy={() => copyCountrySnippet(currentCountrySnippetItem)}
                    disabled={!currentCountrySnippetItem?.slug}
                  />
                </div>
              </div>
            </div>

            <div className="space-y-4 rounded-3xl border border-slate-200 bg-white p-5 dark:border-slate-800 dark:bg-slate-900">
              <SectionTitle
                eyebrow="Publicacion"
                title="Checklist de salida"
                description="La publicacion se habilita cuando la ficha queda completa y lista para embebido."
              />

              <div className="space-y-3">
                <InfoPill
                  label="Estado actual"
                  tone={destinationForm.isPublished ? "emerald" : "slate"}
                  value={destinationForm.isPublished ? "Publicado" : "Borrador"}
                />
                <InfoPill
                  label="Puede publicarse"
                  tone={destinationForm.canPublish ? "emerald" : "amber"}
                  value={destinationForm.canPublish ? "Si" : "Todavia no"}
                />
                <InfoPill label="Slug publico" tone="indigo" value={effectiveDestinationSlug || "Pendiente"} />
              </div>

              <div className="rounded-2xl border border-slate-200 bg-slate-50 p-4 dark:border-slate-800 dark:bg-slate-950/60">
                <p className="text-sm font-semibold text-slate-900 dark:text-white">Pendientes de publicacion</p>
                {destinationForm.publishIssues?.length > 0 ? (
                  <ul className="mt-3 space-y-2 text-sm text-slate-600 dark:text-slate-300">
                    {destinationForm.publishIssues.map((issue) => (
                      <li key={issue} className="rounded-xl bg-white px-3 py-2 dark:bg-slate-900">
                        {issue}
                      </li>
                    ))}
                  </ul>
                ) : (
                  <p className="mt-2 text-sm text-emerald-600 dark:text-emerald-300">
                    Todo listo para publicar cuando quieras.
                  </p>
                )}
              </div>
            </div>
          </div>

          <div className="grid gap-6 lg:grid-cols-[minmax(320px,440px)_minmax(0,1fr)]">
            <div className="space-y-4 rounded-3xl border border-slate-200 bg-white p-5 dark:border-slate-800 dark:bg-slate-900">
              <SectionTitle eyebrow="Imagen principal" title="Hero del destino" description="Esta imagen se muestra en la portada del embed." />

              <div className="overflow-hidden rounded-3xl border border-slate-200 bg-slate-100 dark:border-slate-800 dark:bg-slate-950">
                {imagePreviewUrl ? (
                  <img src={imagePreviewUrl} alt={destinationForm.title || "Destino"} className="h-72 w-full object-cover" />
                ) : (
                  <div className="flex h-72 flex-col items-center justify-center gap-3 text-slate-400">
                    <ImagePlus className="h-10 w-10" />
                    <p className="text-sm font-medium">Sin imagen principal</p>
                  </div>
                )}
              </div>

              <div className="rounded-2xl border border-dashed border-slate-300 bg-slate-50 p-4 dark:border-slate-700 dark:bg-slate-950/60">
                <label className="flex cursor-pointer flex-col items-center justify-center gap-3 text-center">
                  <div className="rounded-2xl bg-indigo-50 p-3 text-indigo-600 dark:bg-indigo-900/30 dark:text-indigo-300">
                    <UploadCloud className="h-6 w-6" />
                  </div>
                  <div>
                    <p className="text-sm font-semibold text-slate-900 dark:text-white">Subir imagen hero</p>
                    <p className="mt-1 text-xs text-slate-500 dark:text-slate-400">PNG, JPG o WEBP hasta 10 MB.</p>
                  </div>
                  <input
                    type="file"
                    accept="image/png,image/jpeg,image/webp"
                    onChange={handleImageChange}
                    className="hidden"
                  />
                </label>
              </div>

              <p className="text-xs text-slate-500 dark:text-slate-400">
                Archivo actual: {selectedImageFile?.name || destinationForm.heroImageFileName || "Sin imagen cargada"}
              </p>
            </div>

            <div className="space-y-4 rounded-3xl border border-slate-200 bg-white p-5 dark:border-slate-800 dark:bg-slate-900">
              <SectionTitle eyebrow="Informacion general" title="Contenido editorial del destino" description="Escribe el contenido que aparece en la pestana de informacion general del embed." />

              <Field label="Texto de la ficha" hint="Puedes usar saltos de linea.">
                <textarea
                  value={destinationForm.generalInfo}
                  onChange={(event) => updateDestinationField("generalInfo", event.target.value)}
                  placeholder="Incluye lo mas importante del viaje, que servicios contempla y aclaraciones comerciales."
                  className={textareaClass}
                  disabled={!canEdit}
                />
              </Field>
            </div>
          </div>

          <div className="space-y-4 rounded-3xl border border-slate-200 bg-white p-5 dark:border-slate-800 dark:bg-slate-900">
            <SectionTitle eyebrow="Salidas y tarifas" title="Arma las opciones que se mostraran en la tabla publica" description="Cada salida define fecha, hotel, regimen, base y tarifa. Debe existir una principal activa." />

            <div className="grid gap-4 xl:grid-cols-[minmax(0,1.3fr)_minmax(0,1fr)_minmax(0,1fr)]">
              <Field label="Fecha">
                <input type="date" value={departureDraft.startDate} onChange={(event) => updateDepartureField("startDate", event.target.value)} className={inputClass} disabled={!canEdit} />
              </Field>
              <Field label="Noches">
                <input type="number" min="1" value={departureDraft.nights} onChange={(event) => updateDepartureField("nights", event.target.value)} className={inputClass} disabled={!canEdit} />
              </Field>
              <Field label="Tarifa">
                <input type="number" min="0" step="0.01" value={departureDraft.salePrice} onChange={(event) => updateDepartureField("salePrice", event.target.value)} className={inputClass} disabled={!canEdit} />
              </Field>
              <Field label="Transporte">
                <input type="text" value={departureDraft.transportLabel} onChange={(event) => updateDepartureField("transportLabel", event.target.value)} className={inputClass} disabled={!canEdit} />
              </Field>
              <Field label="Hotel">
                <input type="text" value={departureDraft.hotelName} onChange={(event) => updateDepartureField("hotelName", event.target.value)} className={inputClass} disabled={!canEdit} />
              </Field>
              <Field label="Regimen">
                <input type="text" value={departureDraft.mealPlan} onChange={(event) => updateDepartureField("mealPlan", event.target.value)} className={inputClass} disabled={!canEdit} />
              </Field>
              <Field label="Base">
                <input type="text" value={departureDraft.roomBase} onChange={(event) => updateDepartureField("roomBase", event.target.value)} className={inputClass} disabled={!canEdit} />
              </Field>
              <Field label="Moneda">
                <input type="text" maxLength={3} value={departureDraft.currency} onChange={(event) => updateDepartureField("currency", event.target.value.toUpperCase())} className={inputClass} disabled={!canEdit} />
              </Field>

              <div className="grid gap-3 md:grid-cols-2 xl:col-span-3">
                <label className="flex items-center gap-3 rounded-2xl border border-slate-200 bg-slate-50 px-4 py-3 text-sm font-medium text-slate-700 dark:border-slate-700 dark:bg-slate-950 dark:text-slate-200">
                  <input type="checkbox" checked={departureDraft.isPrimary} onChange={(event) => updateDepartureField("isPrimary", event.target.checked)} className="h-4 w-4 rounded border-slate-300 text-indigo-600 focus:ring-indigo-500" disabled={!canEdit} />
                  Salida principal
                </label>
                <label className="flex items-center gap-3 rounded-2xl border border-slate-200 bg-slate-50 px-4 py-3 text-sm font-medium text-slate-700 dark:border-slate-700 dark:bg-slate-950 dark:text-slate-200">
                  <input type="checkbox" checked={departureDraft.isActive} onChange={(event) => updateDepartureField("isActive", event.target.checked)} className="h-4 w-4 rounded border-slate-300 text-indigo-600 focus:ring-indigo-500" disabled={!canEdit} />
                  Disponible para web
                </label>
              </div>
            </div>

            <div className="flex flex-wrap gap-2">
              {canEdit ? (
                <button type="button" onClick={persistDeparture} className="inline-flex items-center gap-2 rounded-xl bg-slate-900 px-4 py-2.5 text-sm font-semibold text-white transition hover:bg-slate-800 dark:bg-white dark:text-slate-900 dark:hover:bg-slate-200">
                  <Save className="h-4 w-4" />
                  {editingDepartureClientId ? "Actualizar salida" : "Agregar salida"}
                </button>
              ) : null}
              {editingDepartureClientId ? (
                <button type="button" onClick={clearDepartureEditor} className="rounded-xl border border-slate-200 px-4 py-2.5 text-sm font-semibold text-slate-600 transition hover:bg-slate-50 dark:border-slate-700 dark:text-slate-300 dark:hover:bg-slate-800">
                  Cancelar edicion
                </button>
              ) : null}
            </div>

            <div className="overflow-hidden rounded-3xl border border-slate-200 dark:border-slate-800">
              {departuresSorted.length === 0 ? (
                <EmptyState icon={CalendarDays} title="Todavia no hay salidas." description="Agrega al menos una salida para poder publicar el destino." />
              ) : (
                <div className="overflow-x-auto">
                  <table className="min-w-full divide-y divide-slate-200 dark:divide-slate-800">
                    <thead className="bg-slate-50 dark:bg-slate-950/50">
                      <tr className="text-left text-xs font-bold uppercase tracking-wider text-slate-500 dark:text-slate-400">
                        <th className="px-4 py-3">Fecha</th>
                        <th className="px-4 py-3">Noches</th>
                        <th className="px-4 py-3">Hotel</th>
                        <th className="px-4 py-3">Regimen</th>
                        <th className="px-4 py-3">Base</th>
                        <th className="px-4 py-3">Tarifa</th>
                        <th className="px-4 py-3">Estado</th>
                        <th className="px-4 py-3 text-right">Acciones</th>
                      </tr>
                    </thead>
                    <tbody className="divide-y divide-slate-100 dark:divide-slate-800">
                      {departuresSorted.map((departure) => (
                        <DepartureRow key={departure.clientId} departure={departure} canEdit={canEdit} onEdit={() => editDeparture(departure)} onRemove={() => confirmRemoveDeparture(departure.clientId)} />
                      ))}
                    </tbody>
                  </table>
                </div>
              )}
            </div>
          </div>
        </form>
      </ModalShell>
    </>
  );
}

function CountryCard({ country, selected, onSelect, onEdit, onPreview, onCopy, canEdit }) {
  return (
    <div
      className={`cursor-pointer rounded-2xl border p-4 transition ${
        selected
          ? "border-indigo-300 bg-indigo-50/70 shadow-sm dark:border-indigo-800 dark:bg-indigo-950/30"
          : "border-slate-200 bg-white hover:border-slate-300 dark:border-slate-800 dark:bg-slate-900 dark:hover:border-slate-700"
      }`}
      onClick={onSelect}
      role="button"
      tabIndex={0}
      onKeyDown={(event) => {
        if (event.key === "Enter" || event.key === " ") {
          event.preventDefault();
          onSelect();
        }
      }}
    >
      <div className="flex items-start justify-between gap-3">
        <div>
          <p className="text-sm font-semibold text-slate-900 dark:text-white">{country.name}</p>
          <p className="mt-1 text-xs text-slate-500 dark:text-slate-400">{country.slug}</p>
        </div>
        <div className="flex gap-2" onClick={(event) => event.stopPropagation()}>
          <ActionButton icon={Eye} label="Abrir" onClick={onPreview} />
          <ActionButton icon={Copy} label="Iframe" onClick={onCopy} />
          {canEdit ? <ActionButton icon={Pencil} label="Editar" onClick={onEdit} /> : null}
        </div>
      </div>

      <div className="mt-4 flex flex-wrap gap-2">
        <span className="rounded-full bg-slate-100 px-2.5 py-1 text-[11px] font-semibold text-slate-700 dark:bg-slate-800 dark:text-slate-200">
          {country.totalDestinations} destinos
        </span>
        <span className="rounded-full bg-emerald-50 px-2.5 py-1 text-[11px] font-semibold text-emerald-700 dark:bg-emerald-900/20 dark:text-emerald-300">
          {country.publishedDestinations} publicados
        </span>
        <span className="rounded-full bg-amber-50 px-2.5 py-1 text-[11px] font-semibold text-amber-700 dark:bg-amber-900/20 dark:text-amber-300">
          {country.draftDestinations} borradores
        </span>
      </div>
    </div>
  );
}

function DestinationRow({ destination, canEdit, canPublish, onPreview, onCopy, onEdit, onPublish, onUnpublish }) {
  return (
    <tr className="align-top">
      <td className="px-6 py-4">
        <div className="flex gap-4">
          <div className="h-20 w-28 overflow-hidden rounded-2xl bg-slate-100 dark:bg-slate-800">
            {destination.heroImageUrl ? (
              <img src={destination.heroImageUrl} alt={destination.title} className="h-full w-full object-cover" />
            ) : (
              <div className="flex h-full items-center justify-center text-slate-400">
                <ImagePlus className="h-5 w-5" />
              </div>
            )}
          </div>
          <div className="space-y-2">
            <div>
              <p className="text-sm font-semibold text-slate-900 dark:text-white">{destination.title}</p>
              <p className="text-xs text-slate-500 dark:text-slate-400">{destination.tagline || destination.name || "Sin bajada todavia"}</p>
            </div>
            <div className="flex flex-wrap gap-2">
              <span className="rounded-full bg-slate-100 px-2.5 py-1 text-[11px] font-medium text-slate-600 dark:bg-slate-800 dark:text-slate-300">
                {destination.name}
              </span>
              <span className="rounded-full bg-indigo-50 px-2.5 py-1 text-[11px] font-medium text-indigo-700 dark:bg-indigo-900/30 dark:text-indigo-300">
                Orden {destination.displayOrder ?? 0}
              </span>
              {!destination.hasHeroImage ? (
                <span className="rounded-full bg-rose-50 px-2.5 py-1 text-[11px] font-medium text-rose-600 dark:bg-rose-900/20 dark:text-rose-300">
                  Falta imagen
                </span>
              ) : null}
            </div>
            {destination.publishIssues?.length > 0 ? (
              <p className="max-w-md text-xs text-amber-600 dark:text-amber-300">{destination.publishIssues.join(" ")}</p>
            ) : null}
          </div>
        </div>
      </td>
      <td className="px-4 py-4">
        <span className={`inline-flex rounded-full px-2.5 py-1 text-xs font-semibold ${destination.isPublished ? "bg-emerald-50 text-emerald-700 dark:bg-emerald-900/20 dark:text-emerald-300" : "bg-slate-100 text-slate-700 dark:bg-slate-800 dark:text-slate-300"}`}>
          {destination.isPublished ? "Publicado" : "Borrador"}
        </span>
      </td>
      <td className="px-4 py-4 text-sm font-semibold text-slate-900 dark:text-white">{formatMoney(destination.fromPrice, destination.currency)}</td>
      <td className="px-4 py-4 text-sm text-slate-600 dark:text-slate-300">{destination.activeDepartureCount}/{destination.departureCount} activas</td>
      <td className="px-4 py-4">
        <code className="rounded bg-slate-100 px-2 py-1 text-xs text-slate-700 dark:bg-slate-800 dark:text-slate-200">{destination.slug}</code>
      </td>
      <td className="px-6 py-4">
        <div className="flex flex-wrap justify-end gap-2">
          <ActionButton icon={Eye} label="Preview destino" onClick={onPreview} />
          <ActionButton icon={Copy} label="Iframe destino" onClick={onCopy} />
          {canEdit ? <ActionButton icon={Pencil} label="Editar" onClick={onEdit} /> : null}
          {canPublish ? (
            destination.isPublished ? (
              <ActionButton icon={X} label="Despublicar" onClick={onUnpublish} tone="amber" />
            ) : (
              <ActionButton icon={Rocket} label="Publicar" onClick={onPublish} tone="emerald" disabled={!destination.canPublish} />
            )
          ) : null}
        </div>
      </td>
    </tr>
  );
}

function DepartureRow({ departure, canEdit, onEdit, onRemove }) {
  return (
    <tr>
      <td className="px-4 py-3 text-sm font-medium text-slate-900 dark:text-white">{formatDate(departure.startDate)}</td>
      <td className="px-4 py-3 text-sm text-slate-600 dark:text-slate-300">{departure.nights}</td>
      <td className="px-4 py-3 text-sm text-slate-600 dark:text-slate-300">{departure.hotelName}</td>
      <td className="px-4 py-3 text-sm text-slate-600 dark:text-slate-300">{departure.mealPlan}</td>
      <td className="px-4 py-3 text-sm text-slate-600 dark:text-slate-300">{departure.roomBase}</td>
      <td className="px-4 py-3 text-sm font-semibold text-slate-900 dark:text-white">{formatMoney(departure.salePrice, departure.currency)}</td>
      <td className="px-4 py-3">
        <div className="flex flex-wrap gap-2">
          {departure.isPrimary ? (
            <span className="rounded-full bg-indigo-50 px-2.5 py-1 text-[11px] font-semibold text-indigo-700 dark:bg-indigo-900/30 dark:text-indigo-300">
              Principal
            </span>
          ) : null}
          <span className={`rounded-full px-2.5 py-1 text-[11px] font-semibold ${departure.isActive ? "bg-emerald-50 text-emerald-700 dark:bg-emerald-900/20 dark:text-emerald-300" : "bg-slate-100 text-slate-700 dark:bg-slate-800 dark:text-slate-300"}`}>
            {departure.isActive ? "Activa" : "Oculta"}
          </span>
        </div>
      </td>
      <td className="px-4 py-3">
        <div className="flex justify-end gap-2">
          {canEdit ? (
            <>
              <ActionButton icon={Pencil} label="Editar" onClick={onEdit} />
              <ActionButton icon={Trash2} label="Quitar" onClick={onRemove} tone="rose" />
            </>
          ) : null}
        </div>
      </td>
    </tr>
  );
}

function EmptyState({ icon: Icon, title, description }) {
  return (
    <div className="flex min-h-[220px] flex-col items-center justify-center gap-3 px-6 py-10 text-center">
      <div className="rounded-2xl bg-slate-100 p-4 text-slate-400 dark:bg-slate-800">
        <Icon className="h-8 w-8" />
      </div>
      <div>
        <p className="text-base font-semibold text-slate-900 dark:text-white">{title}</p>
        <p className="mt-1 text-sm text-slate-500 dark:text-slate-400">{description}</p>
      </div>
    </div>
  );
}

function ModalShell({ open, title, subtitle, onClose, children, footer }) {
  if (!open) {
    return null;
  }

  return (
    <div className="fixed inset-0 z-50 overflow-y-auto bg-slate-950/65 p-4 backdrop-blur-sm">
      <div className="mx-auto flex min-h-full max-w-7xl items-center justify-center">
        <div className="w-full overflow-hidden rounded-[2rem] border border-slate-200 bg-white shadow-[0_30px_80px_-28px_rgba(15,23,42,0.45)] dark:border-slate-800 dark:bg-slate-900">
          <div className="flex items-start justify-between gap-4 border-b border-slate-200 px-6 py-5 dark:border-slate-800">
            <div>
              <p className="text-xs font-bold uppercase tracking-[0.24em] text-indigo-600 dark:text-indigo-300">Paises y destinos</p>
              <h2 className="mt-2 text-2xl font-bold tracking-tight text-slate-950 dark:text-white">{title}</h2>
              {subtitle ? <p className="mt-1 max-w-3xl text-sm text-slate-500 dark:text-slate-400">{subtitle}</p> : null}
            </div>

            <button
              type="button"
              onClick={onClose}
              className="rounded-2xl border border-slate-200 p-2 text-slate-500 transition hover:bg-slate-50 hover:text-slate-700 dark:border-slate-700 dark:text-slate-400 dark:hover:bg-slate-800 dark:hover:text-slate-200"
            >
              <X className="h-5 w-5" />
            </button>
          </div>

          <div className="max-h-[calc(100vh-10rem)] overflow-y-auto px-6 py-6">{children}</div>
          <div className="flex flex-wrap justify-end gap-3 border-t border-slate-200 px-6 py-5 dark:border-slate-800">{footer}</div>
        </div>
      </div>
    </div>
  );
}

function SectionTitle({ eyebrow, title, description }) {
  return (
    <div>
      {eyebrow ? <p className="text-xs font-bold uppercase tracking-[0.24em] text-indigo-600 dark:text-indigo-300">{eyebrow}</p> : null}
      <h3 className="mt-2 text-lg font-semibold text-slate-950 dark:text-white">{title}</h3>
      {description ? <p className="mt-1 text-sm text-slate-500 dark:text-slate-400">{description}</p> : null}
    </div>
  );
}

function Field({ label, hint, children }) {
  return (
    <label className="block space-y-2">
      <span className="text-sm font-semibold text-slate-700 dark:text-slate-200">{label}</span>
      {children}
      {hint ? <span className="block text-xs text-slate-500 dark:text-slate-400">{hint}</span> : null}
    </label>
  );
}

function SnippetPreview({ title, subtitle, snippet, emptyMessage, onPreview, onCopy, disabled }) {
  return (
    <div className="rounded-2xl border border-slate-200 bg-slate-50/70 p-4 dark:border-slate-800 dark:bg-slate-950/60">
      <div className="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between">
        <div>
          <p className="text-sm font-semibold text-slate-900 dark:text-white">{title}</p>
          <p className="mt-1 text-xs text-slate-500 dark:text-slate-400">{subtitle}</p>
        </div>
        <div className="flex flex-wrap gap-2">
          <ActionButton icon={Eye} label="Abrir" onClick={onPreview} disabled={disabled} />
          <ActionButton icon={Copy} label="Copiar" onClick={onCopy} disabled={disabled} />
        </div>
      </div>

      <div className="mt-4 rounded-2xl bg-slate-950 p-4 text-xs leading-6 text-slate-200">
        <code className="whitespace-pre-wrap break-all">{snippet || emptyMessage}</code>
      </div>
    </div>
  );
}

function SummaryCard({ icon: Icon, label, value, tone = "slate" }) {
  const tones = {
    slate: "bg-slate-50 text-slate-700 dark:bg-slate-900 dark:text-slate-200",
    indigo: "bg-indigo-50 text-indigo-700 dark:bg-indigo-900/30 dark:text-indigo-200",
    emerald: "bg-emerald-50 text-emerald-700 dark:bg-emerald-900/30 dark:text-emerald-200",
    amber: "bg-amber-50 text-amber-700 dark:bg-amber-900/30 dark:text-amber-200",
  };

  return (
    <div className="rounded-3xl border border-slate-200 bg-white p-5 shadow-sm dark:border-slate-800 dark:bg-slate-900">
      <div className="flex items-start justify-between gap-4">
        <div>
          <p className="text-sm font-medium text-slate-500 dark:text-slate-400">{label}</p>
          <p className="mt-2 text-3xl font-bold tracking-tight text-slate-950 dark:text-white">{value}</p>
        </div>
        <div className={`rounded-2xl p-3 ${tones[tone] || tones.slate}`}>
          <Icon className="h-5 w-5" />
        </div>
      </div>
    </div>
  );
}

function ActionButton({ icon: Icon, label, onClick, tone = "slate", disabled = false }) {
  const tones = {
    slate: "border-slate-200 bg-white text-slate-700 hover:bg-slate-50 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-200 dark:hover:bg-slate-800",
    emerald: "border-emerald-200 bg-emerald-50 text-emerald-700 hover:bg-emerald-100 dark:border-emerald-900/40 dark:bg-emerald-900/20 dark:text-emerald-300 dark:hover:bg-emerald-900/30",
    amber: "border-amber-200 bg-amber-50 text-amber-700 hover:bg-amber-100 dark:border-amber-900/40 dark:bg-amber-900/20 dark:text-amber-300 dark:hover:bg-amber-900/30",
    rose: "border-rose-200 bg-rose-50 text-rose-700 hover:bg-rose-100 dark:border-rose-900/40 dark:bg-rose-900/20 dark:text-rose-300 dark:hover:bg-rose-900/30",
  };

  return (
    <button
      type="button"
      onClick={onClick}
      disabled={disabled}
      className={`inline-flex items-center gap-2 rounded-xl border px-3 py-2 text-sm font-semibold transition disabled:cursor-not-allowed disabled:opacity-50 ${tones[tone] || tones.slate}`}
    >
      <Icon className="h-4 w-4" />
      {label}
    </button>
  );
}

function InfoPill({ label, value, tone = "slate" }) {
  const tones = {
    slate: "bg-slate-100 text-slate-700 dark:bg-slate-800 dark:text-slate-200",
    indigo: "bg-indigo-50 text-indigo-700 dark:bg-indigo-900/30 dark:text-indigo-200",
    emerald: "bg-emerald-50 text-emerald-700 dark:bg-emerald-900/20 dark:text-emerald-300",
    amber: "bg-amber-50 text-amber-700 dark:bg-amber-900/20 dark:text-amber-300",
  };

  return (
    <div className={`rounded-2xl px-4 py-3 ${tones[tone] || tones.slate}`}>
      <p className="text-[11px] font-bold uppercase tracking-[0.22em] opacity-80">{label}</p>
      <p className="mt-1 text-sm font-semibold">{value}</p>
    </div>
  );
}
