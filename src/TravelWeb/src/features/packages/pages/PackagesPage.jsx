import { useCallback, useEffect, useMemo, useState } from "react";
import {
  CalendarDays,
  Copy,
  Eye,
  ImagePlus,
  Loader2,
  Pencil,
  Plus,
  Rocket,
  Save,
  Search,
  ShieldCheck,
  Tag,
  Trash2,
  UploadCloud,
  X,
} from "lucide-react";
import { api, buildAppUrl } from "../../../api";
import { showConfirm, showError, showSuccess } from "../../../alerts";
import { hasPermission } from "../../../auth";
import { ListPageHeader } from "../../../components/ui/ListPageHeader";
import { PaginationFooter } from "../../../components/ui/PaginationFooter";
import { useDebounce } from "../../../hooks/useDebounce";

const inputClass =
  "w-full rounded-2xl border border-slate-200 bg-slate-50 px-4 py-3 text-sm text-slate-900 outline-none transition focus:border-indigo-500 focus:bg-white dark:border-slate-700 dark:bg-slate-950 dark:text-white";
const textareaClass = `${inputClass} min-h-[148px] resize-y`;

const emptyForm = {
  publicId: null,
  title: "",
  slug: "",
  tagline: "",
  destination: "",
  generalInfo: "",
  departures: [],
  isPublished: false,
  canPublish: false,
  publishIssues: [],
  hasHeroImage: false,
  heroImageUrl: null,
  heroImageFileName: null,
  publicPagePath: "",
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

function buildSnippet(item) {
  const safeTitle = String(item.title || "Paquete").replace(/"/g, "&quot;");
  const safeSlug = sanitizeEmbedToken(item.slug || "paquete");
  const embedId = `mt-package-${safeSlug || "embed"}`;
  const baseSrc = buildAppUrl(item.publicPagePath || `/embed/packages/${item.slug}`);
  const srcUrl = new URL(baseSrc);
  srcUrl.searchParams.set("embedId", embedId);

  return `<iframe id="${embedId}" src="${srcUrl.toString()}" loading="lazy" scrolling="no" style="width:100%;min-height:640px;height:640px;border:0;display:block;overflow:hidden;" title="${safeTitle}"></iframe>
<script>
(function () {
  var iframe = document.getElementById(${JSON.stringify(embedId)});
  if (!iframe) return;

  var allowedOrigin = ${JSON.stringify(srcUrl.origin)};
  var expectedEmbedId = ${JSON.stringify(embedId)};
  var minHeight = 640;

  function applyHeight(height) {
    var parsed = Number(height || 0);
    if (!parsed || !isFinite(parsed)) return;

    var nextHeight = Math.max(minHeight, Math.min(5200, Math.round(parsed)));
    iframe.style.height = nextHeight + "px";
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

function mapDetailToForm(detail) {
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
    title: detail.title || "",
    slug: detail.slug || "",
    tagline: detail.tagline || "",
    destination: detail.destination || "",
    generalInfo: detail.generalInfo || "",
    departures,
    isPublished: Boolean(detail.isPublished),
    canPublish: Boolean(detail.canPublish),
    publishIssues: detail.publishIssues || [],
    hasHeroImage: Boolean(detail.hasHeroImage),
    heroImageUrl: detail.heroImageUrl || null,
    heroImageFileName: detail.heroImageFileName || null,
    publicPagePath: detail.publicPagePath || `/embed/packages/${detail.slug}`,
  };
}

export default function PackagesPage() {
  const canEdit = hasPermission("paquetes.edit");
  const canPublish = hasPermission("paquetes.publish");
  const [items, setItems] = useState([]);
  const [loading, setLoading] = useState(true);
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(25);
  const [pageState, setPageState] = useState({
    page: 1,
    pageSize: 25,
    totalCount: 0,
    totalPages: 0,
    hasPreviousPage: false,
    hasNextPage: false,
  });
  const [searchTerm, setSearchTerm] = useState("");
  const [statusFilter, setStatusFilter] = useState("all");
  const [modalOpen, setModalOpen] = useState(false);
  const [saving, setSaving] = useState(false);
  const [form, setForm] = useState(emptyForm);
  const [slugTouched, setSlugTouched] = useState(false);
  const [departureDraft, setDepartureDraft] = useState(createDepartureDraft(true));
  const [editingDepartureClientId, setEditingDepartureClientId] = useState(null);
  const [selectedImageFile, setSelectedImageFile] = useState(null);
  const [imagePreviewUrl, setImagePreviewUrl] = useState(null);
  const debouncedSearch = useDebounce(searchTerm, 300);

  const loadPackages = useCallback(async () => {
    setLoading(true);

    try {
      const params = new URLSearchParams({
        page: String(page),
        pageSize: String(pageSize),
        sortBy: "updatedAt",
        sortDir: "desc",
      });

      if (debouncedSearch.trim()) {
        params.set("search", debouncedSearch.trim());
      }

      if (statusFilter !== "all") {
        params.set("status", statusFilter);
      }

      const response = await api.get(`/packages?${params.toString()}`);
      setItems(response?.items || []);
      setPageState({
        page: response?.page || page,
        pageSize: response?.pageSize || pageSize,
        totalCount: response?.totalCount || 0,
        totalPages: response?.totalPages || 0,
        hasPreviousPage: Boolean(response?.hasPreviousPage),
        hasNextPage: Boolean(response?.hasNextPage),
      });
    } catch (error) {
      console.error("Error loading packages:", error);
      setItems([]);
      showError("No se pudieron cargar los paquetes.");
    } finally {
      setLoading(false);
    }
  }, [debouncedSearch, page, pageSize, statusFilter]);

  useEffect(() => {
    loadPackages();
  }, [loadPackages]);

  useEffect(() => {
    setPage(1);
  }, [debouncedSearch, pageSize, statusFilter]);

  useEffect(() => {
    if (!imagePreviewUrl || !imagePreviewUrl.startsWith("blob:")) {
      return undefined;
    }

    return () => URL.revokeObjectURL(imagePreviewUrl);
  }, [imagePreviewUrl]);

  const summary = useMemo(
    () =>
      items.reduce(
        (accumulator, item) => {
          accumulator.total += 1;
          if (item.isPublished) {
            accumulator.published += 1;
          } else {
            accumulator.draft += 1;
          }

          if (!item.canPublish) {
            accumulator.pending += 1;
          }

          return accumulator;
        },
        { total: 0, published: 0, draft: 0, pending: 0 }
      ),
    [items]
  );

  const departuresSorted = useMemo(
    () =>
      [...(form.departures || [])].sort((left, right) => {
        const leftDate = new Date(left.startDate);
        const rightDate = new Date(right.startDate);
        return leftDate - rightDate;
      }),
    [form.departures]
  );

  const effectiveSlug = slugify(form.slug || form.title);
  const currentSnippetItem = useMemo(
    () => ({
      title: form.title || "Paquete",
      slug: effectiveSlug,
      publicPagePath: effectiveSlug ? `/embed/packages/${effectiveSlug}` : "",
    }),
    [effectiveSlug, form.title]
  );
  const embedSnippet = effectiveSlug ? buildSnippet(currentSnippetItem) : "";

  function resetEditor(nextForm = emptyForm) {
    const safeForm = {
      ...emptyForm,
      ...nextForm,
      departures: [...(nextForm.departures || [])],
      publishIssues: [...(nextForm.publishIssues || [])],
    };

    setForm(safeForm);
    setSlugTouched(Boolean(safeForm.slug));
    setEditingDepartureClientId(null);
    setDepartureDraft(createDepartureDraft((safeForm.departures || []).length === 0));
    setSelectedImageFile(null);
    setImagePreviewUrl(safeForm.heroImageUrl || null);
  }

  function openCreateModal() {
    resetEditor(emptyForm);
    setModalOpen(true);
  }

  async function openEditModal(publicId) {
    try {
      const detail = await api.get(`/packages/${publicId}`);
      resetEditor(mapDetailToForm(detail));
      setModalOpen(true);
    } catch (error) {
      showError(error.message || "No se pudo abrir el paquete.");
    }
  }

  function closeModal() {
    setModalOpen(false);
    resetEditor(emptyForm);
  }

  function updateFormField(key, value) {
    setForm((previous) => {
      const next = { ...previous, [key]: value };
      if (key === "title" && !slugTouched) {
        next.slug = slugify(value);
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
    setDepartureDraft(createDepartureDraft((form.departures || []).length === 0));
  }

  function persistDeparture() {
    if (!departureDraft.startDate || !departureDraft.hotelName.trim() || !departureDraft.mealPlan.trim()) {
      showError("Completa fecha, hotel y regimen antes de guardar la salida.");
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

    setForm((previous) => {
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
      "La salida sera removida del paquete cuando guardes los cambios.",
      "Si, eliminar",
      "red"
    );

    if (!confirmed) {
      return;
    }

    setForm((previous) => {
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

  async function savePackage(event) {
    event.preventDefault();

    if (!form.title.trim()) {
      showError("El titulo del paquete es obligatorio.");
      return;
    }

    if (!effectiveSlug) {
      showError("El slug es obligatorio.");
      return;
    }

    setSaving(true);

    try {
      const payload = {
        title: form.title.trim(),
        slug: effectiveSlug,
        tagline: form.tagline.trim() || null,
        destination: form.destination.trim() || null,
        generalInfo: form.generalInfo.trim() || null,
        departures: form.departures.map((departure) => ({
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

      const response = form.publicId
        ? await api.put(`/packages/${form.publicId}`, payload)
        : await api.post("/packages", payload);

      if (selectedImageFile) {
        const imageFormData = new FormData();
        imageFormData.append("file", selectedImageFile);
        await api.post(`/packages/${response.publicId}/hero-image`, imageFormData);
      }

      showSuccess(form.publicId ? "Paquete actualizado." : "Paquete creado.");
      closeModal();
      await loadPackages();
    } catch (error) {
      console.error("Error saving package:", error);
      showError(error.message || "No se pudo guardar el paquete.");
    } finally {
      setSaving(false);
    }
  }

  async function handlePublish(item) {
    const confirmed = await showConfirm(
      "Publicar paquete",
      "El paquete quedara disponible en la ficha publica embebible.",
      "Si, publicar",
      "emerald"
    );
    if (!confirmed) {
      return;
    }

    try {
      await api.patch(`/packages/${item.publicId}/publish`);
      showSuccess("Paquete publicado.");
      await loadPackages();
    } catch (error) {
      showError(error.message || "No se pudo publicar el paquete.");
    }
  }

  async function handleUnpublish(item) {
    const confirmed = await showConfirm(
      "Despublicar paquete",
      "La ficha dejara de estar disponible desde el codigo embebido.",
      "Si, despublicar",
      "amber"
    );
    if (!confirmed) {
      return;
    }

    try {
      await api.patch(`/packages/${item.publicId}/unpublish`);
      showSuccess("Paquete despublicado.");
      await loadPackages();
    } catch (error) {
      showError(error.message || "No se pudo despublicar el paquete.");
    }
  }

  async function copySnippet(item) {
    try {
      await navigator.clipboard.writeText(buildSnippet(item));
      showSuccess("Codigo embebido copiado.");
    } catch {
      showError("No se pudo copiar el codigo embebido.");
    }
  }

  function previewPackage(item) {
    if (!item?.slug) {
      showError("Define un slug antes de abrir la ficha publica.");
      return;
    }

    window.open(buildAppUrl(item.publicPagePath || `/embed/packages/${item.slug}`), "_blank", "noopener,noreferrer");
  }

  return (
    <>
      <div className="space-y-6">
        <ListPageHeader
          title="Paquetes"
          subtitle="Administra las fichas publicas embebibles que se sincronizan con el ERP."
          actions={
            canEdit ? (
              <button
                type="button"
                onClick={openCreateModal}
                className="inline-flex items-center gap-2 rounded-xl bg-indigo-600 px-4 py-2.5 text-sm font-semibold text-white shadow-lg shadow-indigo-500/20 transition hover:bg-indigo-500"
              >
                <Plus className="h-4 w-4" />
                Nuevo paquete
              </button>
            ) : null
          }
        />

        <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
          <SummaryCard icon={Rocket} label="Publicados" value={summary.published} tone="emerald" />
          <SummaryCard icon={Tag} label="Borradores" value={summary.draft} tone="slate" />
          <SummaryCard icon={ShieldCheck} label="Pendientes" value={summary.pending} tone="amber" />
          <SummaryCard icon={CalendarDays} label="En esta vista" value={summary.total} tone="indigo" />
        </div>

        <div className="flex flex-col gap-3 rounded-2xl border border-slate-200 bg-white p-4 shadow-sm dark:border-slate-800 dark:bg-slate-900 md:flex-row md:items-center md:justify-between">
          <div className="relative flex-1">
            <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" />
            <input
              type="text"
              value={searchTerm}
              onChange={(event) => setSearchTerm(event.target.value)}
              placeholder="Buscar por titulo, slug o destino..."
              className="w-full rounded-xl border border-slate-200 bg-slate-50 py-2.5 pl-10 pr-4 text-sm text-slate-900 outline-none transition focus:border-indigo-500 focus:bg-white dark:border-slate-700 dark:bg-slate-950 dark:text-white"
            />
          </div>

          <select
            value={statusFilter}
            onChange={(event) => setStatusFilter(event.target.value)}
            className="rounded-xl border border-slate-200 bg-white px-4 py-2.5 text-sm font-medium text-slate-700 dark:border-slate-700 dark:bg-slate-950 dark:text-slate-200"
          >
            <option value="all">Todos</option>
            <option value="published">Publicados</option>
            <option value="draft">Borradores</option>
          </select>
        </div>

        <div className="overflow-hidden rounded-3xl border border-slate-200 bg-white shadow-xl shadow-slate-200/40 dark:border-slate-800 dark:bg-slate-900 dark:shadow-none">
          {loading ? (
            <div className="flex h-64 items-center justify-center">
              <Loader2 className="h-8 w-8 animate-spin text-indigo-500" />
            </div>
          ) : items.length === 0 ? (
            <div className="flex h-64 flex-col items-center justify-center gap-3 px-6 text-center">
              <div className="rounded-2xl bg-slate-100 p-4 text-slate-400 dark:bg-slate-800">
                <Tag className="h-8 w-8" />
              </div>
              <div>
                <p className="text-base font-semibold text-slate-900 dark:text-white">No hay paquetes cargados.</p>
                <p className="mt-1 text-sm text-slate-500 dark:text-slate-400">
                  Crea el primero para empezar a publicar fichas embebibles.
                </p>
              </div>
            </div>
          ) : (
            <div className="overflow-x-auto">
              <table className="min-w-full divide-y divide-slate-200 dark:divide-slate-800">
                <thead className="bg-slate-50/80 dark:bg-slate-950/40">
                  <tr className="text-left text-xs font-bold uppercase tracking-wider text-slate-500 dark:text-slate-400">
                    <th className="px-6 py-4">Paquete</th>
                    <th className="px-4 py-4">Estado</th>
                    <th className="px-4 py-4">Desde</th>
                    <th className="px-4 py-4">Salidas</th>
                    <th className="px-4 py-4">Slug</th>
                    <th className="px-6 py-4 text-right">Acciones</th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-slate-100 dark:divide-slate-800">
                  {items.map((item) => (
                    <tr key={item.publicId} className="align-top">
                      <td className="px-6 py-4">
                        <div className="flex gap-4">
                          <div className="h-20 w-28 overflow-hidden rounded-2xl bg-slate-100 dark:bg-slate-800">
                            {item.heroImageUrl ? (
                              <img src={item.heroImageUrl} alt={item.title} className="h-full w-full object-cover" />
                            ) : (
                              <div className="flex h-full items-center justify-center text-slate-400">
                                <ImagePlus className="h-5 w-5" />
                              </div>
                            )}
                          </div>
                          <div className="space-y-2">
                            <div>
                              <p className="text-sm font-semibold text-slate-900 dark:text-white">{item.title}</p>
                              <p className="text-xs text-slate-500 dark:text-slate-400">
                                {item.tagline || item.destination || "Sin bajada todavia"}
                              </p>
                            </div>
                            <div className="flex flex-wrap gap-2">
                              {item.destination ? (
                                <span className="rounded-full bg-slate-100 px-2.5 py-1 text-[11px] font-medium text-slate-600 dark:bg-slate-800 dark:text-slate-300">
                                  {item.destination}
                                </span>
                              ) : null}
                              {!item.hasHeroImage ? (
                                <span className="rounded-full bg-rose-50 px-2.5 py-1 text-[11px] font-medium text-rose-600 dark:bg-rose-900/20 dark:text-rose-300">
                                  Falta imagen
                                </span>
                              ) : null}
                            </div>
                            {item.publishIssues?.length > 0 ? (
                              <p className="max-w-md text-xs text-amber-600 dark:text-amber-300">
                                {item.publishIssues.join(" ")}
                              </p>
                            ) : null}
                          </div>
                        </div>
                      </td>
                      <td className="px-4 py-4">
                        <span
                          className={`inline-flex rounded-full px-2.5 py-1 text-xs font-semibold ${
                            item.isPublished
                              ? "bg-emerald-50 text-emerald-700 dark:bg-emerald-900/20 dark:text-emerald-300"
                              : "bg-slate-100 text-slate-700 dark:bg-slate-800 dark:text-slate-300"
                          }`}
                        >
                          {item.isPublished ? "Publicado" : "Borrador"}
                        </span>
                      </td>
                      <td className="px-4 py-4 text-sm font-semibold text-slate-900 dark:text-white">
                        {formatMoney(item.fromPrice, item.currency)}
                      </td>
                      <td className="px-4 py-4 text-sm text-slate-600 dark:text-slate-300">
                        {item.activeDepartureCount}/{item.departureCount} activas
                      </td>
                      <td className="px-4 py-4">
                        <code className="rounded bg-slate-100 px-2 py-1 text-xs text-slate-700 dark:bg-slate-800 dark:text-slate-200">
                          {item.slug}
                        </code>
                      </td>
                      <td className="px-6 py-4">
                        <div className="flex flex-wrap justify-end gap-2">
                          <ActionButton icon={Eye} label="Preview" onClick={() => previewPackage(item)} />
                          <ActionButton icon={Copy} label="Iframe" onClick={() => copySnippet(item)} />
                          {canEdit ? (
                            <ActionButton icon={Pencil} label="Editar" onClick={() => openEditModal(item.publicId)} />
                          ) : null}
                          {canPublish ? (
                            item.isPublished ? (
                              <ActionButton
                                icon={X}
                                label="Despublicar"
                                onClick={() => handleUnpublish(item)}
                                tone="amber"
                              />
                            ) : (
                              <ActionButton
                                icon={Rocket}
                                label="Publicar"
                                onClick={() => handlePublish(item)}
                                tone="emerald"
                                disabled={!item.canPublish}
                              />
                            )
                          ) : null}
                        </div>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}

          <div className="border-t border-slate-200 px-4 py-4 dark:border-slate-800">
            <PaginationFooter
              page={pageState.page}
              pageSize={pageState.pageSize}
              totalCount={pageState.totalCount}
              totalPages={pageState.totalPages}
              hasPreviousPage={pageState.hasPreviousPage}
              hasNextPage={pageState.hasNextPage}
              onPageChange={setPage}
              onPageSizeChange={setPageSize}
            />
          </div>
        </div>
      </div>

      <ModalShell
        open={modalOpen}
        title={form.publicId ? "Editar paquete" : "Nuevo paquete"}
        subtitle="Configura la ficha publica, la imagen principal y las salidas que luego se mostraran en el iframe."
        onClose={closeModal}
        footer={
          <>
            <button
              type="button"
              onClick={closeModal}
              className="rounded-xl border border-slate-200 px-4 py-2.5 text-sm font-semibold text-slate-600 transition hover:bg-slate-50 dark:border-slate-700 dark:text-slate-300 dark:hover:bg-slate-800"
            >
              Cancelar
            </button>
            {canEdit ? (
              <button
                type="submit"
                form="package-editor-form"
                disabled={saving}
                className="inline-flex items-center gap-2 rounded-xl bg-indigo-600 px-4 py-2.5 text-sm font-semibold text-white transition hover:bg-indigo-500 disabled:cursor-not-allowed disabled:opacity-60"
              >
                {saving ? <Loader2 className="h-4 w-4 animate-spin" /> : <Save className="h-4 w-4" />}
                Guardar paquete
              </button>
            ) : null}
          </>
        }
      >
        <form id="package-editor-form" onSubmit={savePackage} className="space-y-8">
          <div className="grid gap-4 lg:grid-cols-[minmax(0,1.8fr)_minmax(280px,1fr)]">
            <div className="space-y-4 rounded-3xl border border-slate-200 bg-slate-50/80 p-5 dark:border-slate-800 dark:bg-slate-950/60">
              <SectionTitle
                eyebrow="Ficha general"
                title="Lo primero que va a ver el sitio"
                description="Completa el titulo comercial, la bajada y el destino para armar la portada del paquete."
              />

              <div className="grid gap-4 md:grid-cols-2">
                <Field label="Titulo del paquete">
                  <input
                    type="text"
                    value={form.title}
                    onChange={(event) => updateFormField("title", event.target.value)}
                    placeholder="Punta Cana"
                    className={inputClass}
                    disabled={!canEdit}
                  />
                </Field>

                <Field label="Destino">
                  <input
                    type="text"
                    value={form.destination}
                    onChange={(event) => updateFormField("destination", event.target.value)}
                    placeholder="Caribe"
                    className={inputClass}
                    disabled={!canEdit}
                  />
                </Field>
              </div>

              <Field label="Bajada comercial">
                <input
                  type="text"
                  value={form.tagline}
                  onChange={(event) => updateFormField("tagline", event.target.value)}
                  placeholder="Vivi el Caribe"
                  className={inputClass}
                  disabled={!canEdit}
                />
              </Field>

              <Field label="Slug publico" hint="Se usa para la URL del iframe y debe ser unico.">
                <input
                  type="text"
                  value={form.slug}
                  onChange={(event) => {
                    setSlugTouched(true);
                    updateFormField("slug", event.target.value);
                  }}
                  placeholder="punta-cana-julio-2026"
                  className={inputClass}
                  disabled={!canEdit}
                />
              </Field>

              <div className="rounded-2xl border border-dashed border-indigo-200 bg-white p-4 dark:border-indigo-900/60 dark:bg-slate-900">
                <div className="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between">
                  <div>
                    <p className="text-sm font-semibold text-slate-900 dark:text-white">Preview tecnico</p>
                    <p className="mt-1 text-xs text-slate-500 dark:text-slate-400">
                      Copia el iframe listo para Hostinger o abre la ficha publica en una pestana nueva.
                    </p>
                  </div>
                  <div className="flex flex-wrap gap-2">
                    <ActionButton
                      icon={Eye}
                      label="Abrir ficha"
                      onClick={() => previewPackage(currentSnippetItem)}
                      disabled={!effectiveSlug}
                    />
                    <ActionButton
                      icon={Copy}
                      label="Copiar iframe"
                      onClick={() => copySnippet(currentSnippetItem)}
                      disabled={!effectiveSlug}
                    />
                  </div>
                </div>

                <div className="mt-4 rounded-2xl bg-slate-950 p-4 text-xs leading-6 text-slate-200">
                  <code className="whitespace-pre-wrap break-all">
                    {embedSnippet || "Completa al menos el titulo para generar el iframe."}
                  </code>
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
                  tone={form.isPublished ? "emerald" : "slate"}
                  value={form.isPublished ? "Publicado" : "Borrador"}
                />
                <InfoPill
                  label="Puede publicarse"
                  tone={form.canPublish ? "emerald" : "amber"}
                  value={form.canPublish ? "Si" : "Todavia no"}
                />
                <InfoPill
                  label="Slug publico"
                  tone="indigo"
                  value={effectiveSlug || "Pendiente"}
                />
              </div>

              <div className="rounded-2xl border border-slate-200 bg-slate-50 p-4 dark:border-slate-800 dark:bg-slate-950/60">
                <p className="text-sm font-semibold text-slate-900 dark:text-white">Pendientes de publicacion</p>
                {form.publishIssues?.length > 0 ? (
                  <ul className="mt-3 space-y-2 text-sm text-slate-600 dark:text-slate-300">
                    {form.publishIssues.map((issue) => (
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
              <SectionTitle
                eyebrow="Imagen principal"
                title="Hero del paquete"
                description="Esta imagen se muestra en la portada del embed. Reutiliza el flujo de uploads del ERP."
              />

              <div className="overflow-hidden rounded-3xl border border-slate-200 bg-slate-100 dark:border-slate-800 dark:bg-slate-950">
                {imagePreviewUrl ? (
                  <img src={imagePreviewUrl} alt={form.title || "Paquete"} className="h-72 w-full object-cover" />
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
                    <p className="mt-1 text-xs text-slate-500 dark:text-slate-400">
                      PNG, JPG o WEBP hasta 10 MB.
                    </p>
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
                Archivo actual: {selectedImageFile?.name || form.heroImageFileName || "Sin imagen cargada"}
              </p>
            </div>

            <div className="space-y-4 rounded-3xl border border-slate-200 bg-white p-5 dark:border-slate-800 dark:bg-slate-900">
              <SectionTitle
                eyebrow="Informacion general"
                title="Contenido editorial del paquete"
                description="Escribe el contenido que aparece en la pestana de informacion general del embed."
              />

              <Field label="Texto de la ficha" hint="Puedes usar saltos de linea.">
                <textarea
                  value={form.generalInfo}
                  onChange={(event) => updateFormField("generalInfo", event.target.value)}
                  placeholder="Incluye lo mas importante del viaje, que servicios contempla y aclaraciones comerciales."
                  className={textareaClass}
                  disabled={!canEdit}
                />
              </Field>
            </div>
          </div>

          <div className="space-y-4 rounded-3xl border border-slate-200 bg-white p-5 dark:border-slate-800 dark:bg-slate-900">
            <SectionTitle
              eyebrow="Salidas y tarifas"
              title="Arma las opciones que se mostraran en la tabla publica"
              description="Cada salida define fecha, hotel, regimen, base y tarifa. Debe existir una principal activa."
            />

            <div className="grid gap-4 xl:grid-cols-[minmax(0,1.3fr)_minmax(0,1fr)_minmax(0,1fr)]">
              <Field label="Fecha">
                <input
                  type="date"
                  value={departureDraft.startDate}
                  onChange={(event) => updateDepartureField("startDate", event.target.value)}
                  className={inputClass}
                  disabled={!canEdit}
                />
              </Field>

              <Field label="Noches">
                <input
                  type="number"
                  min="1"
                  value={departureDraft.nights}
                  onChange={(event) => updateDepartureField("nights", event.target.value)}
                  className={inputClass}
                  disabled={!canEdit}
                />
              </Field>

              <Field label="Tarifa">
                <input
                  type="number"
                  min="0"
                  step="0.01"
                  value={departureDraft.salePrice}
                  onChange={(event) => updateDepartureField("salePrice", event.target.value)}
                  className={inputClass}
                  disabled={!canEdit}
                />
              </Field>

              <Field label="Transporte">
                <input
                  type="text"
                  value={departureDraft.transportLabel}
                  onChange={(event) => updateDepartureField("transportLabel", event.target.value)}
                  className={inputClass}
                  disabled={!canEdit}
                />
              </Field>

              <Field label="Hotel">
                <input
                  type="text"
                  value={departureDraft.hotelName}
                  onChange={(event) => updateDepartureField("hotelName", event.target.value)}
                  className={inputClass}
                  disabled={!canEdit}
                />
              </Field>

              <Field label="Regimen">
                <input
                  type="text"
                  value={departureDraft.mealPlan}
                  onChange={(event) => updateDepartureField("mealPlan", event.target.value)}
                  className={inputClass}
                  disabled={!canEdit}
                />
              </Field>

              <Field label="Base">
                <input
                  type="text"
                  value={departureDraft.roomBase}
                  onChange={(event) => updateDepartureField("roomBase", event.target.value)}
                  className={inputClass}
                  disabled={!canEdit}
                />
              </Field>

              <Field label="Moneda">
                <input
                  type="text"
                  maxLength={3}
                  value={departureDraft.currency}
                  onChange={(event) => updateDepartureField("currency", event.target.value.toUpperCase())}
                  className={inputClass}
                  disabled={!canEdit}
                />
              </Field>

              <div className="grid gap-3 md:grid-cols-2 xl:col-span-3">
                <label className="flex items-center gap-3 rounded-2xl border border-slate-200 bg-slate-50 px-4 py-3 text-sm font-medium text-slate-700 dark:border-slate-700 dark:bg-slate-950 dark:text-slate-200">
                  <input
                    type="checkbox"
                    checked={departureDraft.isPrimary}
                    onChange={(event) => updateDepartureField("isPrimary", event.target.checked)}
                    className="h-4 w-4 rounded border-slate-300 text-indigo-600 focus:ring-indigo-500"
                    disabled={!canEdit}
                  />
                  Salida principal
                </label>

                <label className="flex items-center gap-3 rounded-2xl border border-slate-200 bg-slate-50 px-4 py-3 text-sm font-medium text-slate-700 dark:border-slate-700 dark:bg-slate-950 dark:text-slate-200">
                  <input
                    type="checkbox"
                    checked={departureDraft.isActive}
                    onChange={(event) => updateDepartureField("isActive", event.target.checked)}
                    className="h-4 w-4 rounded border-slate-300 text-indigo-600 focus:ring-indigo-500"
                    disabled={!canEdit}
                  />
                  Disponible para web
                </label>
              </div>
            </div>

            <div className="flex flex-wrap gap-2">
              {canEdit ? (
                <button
                  type="button"
                  onClick={persistDeparture}
                  className="inline-flex items-center gap-2 rounded-xl bg-slate-900 px-4 py-2.5 text-sm font-semibold text-white transition hover:bg-slate-800 dark:bg-white dark:text-slate-900 dark:hover:bg-slate-200"
                >
                  <Save className="h-4 w-4" />
                  {editingDepartureClientId ? "Actualizar salida" : "Agregar salida"}
                </button>
              ) : null}
              {editingDepartureClientId ? (
                <button
                  type="button"
                  onClick={clearDepartureEditor}
                  className="rounded-xl border border-slate-200 px-4 py-2.5 text-sm font-semibold text-slate-600 transition hover:bg-slate-50 dark:border-slate-700 dark:text-slate-300 dark:hover:bg-slate-800"
                >
                  Cancelar edicion
                </button>
              ) : null}
            </div>

            <div className="overflow-hidden rounded-3xl border border-slate-200 dark:border-slate-800">
              {departuresSorted.length === 0 ? (
                <div className="flex flex-col items-center justify-center gap-3 px-6 py-14 text-center">
                  <div className="rounded-2xl bg-slate-100 p-4 text-slate-400 dark:bg-slate-800">
                    <CalendarDays className="h-8 w-8" />
                  </div>
                  <div>
                    <p className="text-base font-semibold text-slate-900 dark:text-white">Todavia no hay salidas.</p>
                    <p className="mt-1 text-sm text-slate-500 dark:text-slate-400">
                      Agrega al menos una salida para poder publicar el paquete.
                    </p>
                  </div>
                </div>
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
                        <tr key={departure.clientId}>
                          <td className="px-4 py-3 text-sm font-medium text-slate-900 dark:text-white">
                            {formatDate(departure.startDate)}
                          </td>
                          <td className="px-4 py-3 text-sm text-slate-600 dark:text-slate-300">{departure.nights}</td>
                          <td className="px-4 py-3 text-sm text-slate-600 dark:text-slate-300">{departure.hotelName}</td>
                          <td className="px-4 py-3 text-sm text-slate-600 dark:text-slate-300">{departure.mealPlan}</td>
                          <td className="px-4 py-3 text-sm text-slate-600 dark:text-slate-300">{departure.roomBase}</td>
                          <td className="px-4 py-3 text-sm font-semibold text-slate-900 dark:text-white">
                            {formatMoney(departure.salePrice, departure.currency)}
                          </td>
                          <td className="px-4 py-3">
                            <div className="flex flex-wrap gap-2">
                              {departure.isPrimary ? (
                                <span className="rounded-full bg-indigo-50 px-2.5 py-1 text-[11px] font-semibold text-indigo-700 dark:bg-indigo-900/30 dark:text-indigo-300">
                                  Principal
                                </span>
                              ) : null}
                              <span
                                className={`rounded-full px-2.5 py-1 text-[11px] font-semibold ${
                                  departure.isActive
                                    ? "bg-emerald-50 text-emerald-700 dark:bg-emerald-900/20 dark:text-emerald-300"
                                    : "bg-slate-100 text-slate-700 dark:bg-slate-800 dark:text-slate-300"
                                }`}
                              >
                                {departure.isActive ? "Activa" : "Oculta"}
                              </span>
                            </div>
                          </td>
                          <td className="px-4 py-3">
                            <div className="flex justify-end gap-2">
                              {canEdit ? (
                                <>
                                  <ActionButton icon={Pencil} label="Editar" onClick={() => editDeparture(departure)} />
                                  <ActionButton
                                    icon={Trash2}
                                    label="Quitar"
                                    onClick={() => confirmRemoveDeparture(departure.clientId)}
                                    tone="rose"
                                  />
                                </>
                              ) : null}
                            </div>
                          </td>
                        </tr>
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
              <p className="text-xs font-bold uppercase tracking-[0.24em] text-indigo-600 dark:text-indigo-300">
                Catalogo publico
              </p>
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

          <div className="flex flex-wrap justify-end gap-3 border-t border-slate-200 px-6 py-5 dark:border-slate-800">
            {footer}
          </div>
        </div>
      </div>
    </div>
  );
}

function SectionTitle({ eyebrow, title, description }) {
  return (
    <div>
      {eyebrow ? (
        <p className="text-xs font-bold uppercase tracking-[0.24em] text-indigo-600 dark:text-indigo-300">{eyebrow}</p>
      ) : null}
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
    slate:
      "border-slate-200 bg-white text-slate-700 hover:bg-slate-50 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-200 dark:hover:bg-slate-800",
    emerald:
      "border-emerald-200 bg-emerald-50 text-emerald-700 hover:bg-emerald-100 dark:border-emerald-900/40 dark:bg-emerald-900/20 dark:text-emerald-300 dark:hover:bg-emerald-900/30",
    amber:
      "border-amber-200 bg-amber-50 text-amber-700 hover:bg-amber-100 dark:border-amber-900/40 dark:bg-amber-900/20 dark:text-amber-300 dark:hover:bg-amber-900/30",
    rose:
      "border-rose-200 bg-rose-50 text-rose-700 hover:bg-rose-100 dark:border-rose-900/40 dark:bg-rose-900/20 dark:text-rose-300 dark:hover:bg-rose-900/30",
  };

  return (
    <button
      type="button"
      onClick={onClick}
      disabled={disabled}
      className={`inline-flex items-center gap-2 rounded-xl border px-3 py-2 text-sm font-semibold transition disabled:cursor-not-allowed disabled:opacity-50 ${
        tones[tone] || tones.slate
      }`}
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
