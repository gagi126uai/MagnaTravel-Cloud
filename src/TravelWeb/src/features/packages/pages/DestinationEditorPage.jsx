import { useEffect, useMemo, useState } from "react";
import { ArrowLeft, ImagePlus, Loader2, Plus, Trash2 } from "lucide-react";
import { useNavigate, useParams, useSearchParams } from "react-router-dom";
import { api, buildAppUrl } from "../../../api";
import { showConfirm, showError, showSuccess } from "../../../alerts";
import { hasPermission } from "../../../auth";
import { Button } from "../../../components/ui/button";
import { ListPageHeader } from "../../../components/ui/ListPageHeader";
import { DestinationDepartureDialog } from "../components/admin/DestinationDepartureDialog";
import { DestinationEditorSidebar } from "../components/admin/DestinationEditorSidebar";
import {
  buildDestinationPublicationSnippet,
  createDepartureDraft,
  createEmptyDestinationForm,
  formatLongDate,
  formatMoney,
  mapDestinationForm,
} from "../lib/publicationUtils";

const inputClass =
  "w-full rounded-md border border-slate-300 bg-white px-3 py-2 text-sm text-slate-900 outline-none transition focus:border-indigo-500 focus:ring-2 focus:ring-indigo-500/20 dark:border-slate-700 dark:bg-slate-950 dark:text-white";
const textareaClass = `${inputClass} min-h-[220px] resize-y`;

export default function DestinationEditorPage() {
  const { publicId = "new" } = useParams();
  const [searchParams] = useSearchParams();
  const navigate = useNavigate();
  const canEdit = hasPermission("paquetes.edit");
  const canPublish = hasPermission("paquetes.publish");
  const isNew = publicId === "new";

  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [imageUploading, setImageUploading] = useState(false);
  const [imageInputKey, setImageInputKey] = useState(0);
  const [form, setForm] = useState(createEmptyDestinationForm(null));
  const [departureModalOpen, setDepartureModalOpen] = useState(false);
  const [departureDraft, setDepartureDraft] = useState(createDepartureDraft(true));
  const [editingDepartureId, setEditingDepartureId] = useState(null);

  useEffect(() => {
    let cancelled = false;

    async function loadCountryForNew() {
      const countryPublicId = searchParams.get("country");
      if (!countryPublicId) {
        showError("Selecciona un pais antes de crear un destino.");
        navigate("/packages", { replace: true });
        return;
      }

      try {
        const country = await api.get(`/countries/${countryPublicId}`);
        if (!cancelled) {
          setForm(createEmptyDestinationForm(country));
        }
      } catch (error) {
        if (!cancelled) {
          showError(error.message || "No pudimos cargar el pais seleccionado.");
          navigate("/packages", { replace: true });
        }
      } finally {
        if (!cancelled) {
          setLoading(false);
        }
      }
    }

    async function loadDestination() {
      try {
        const detail = await api.get(`/destinations/${publicId}`);
        if (!cancelled) {
          setForm(mapDestinationForm(detail));
        }
      } catch (error) {
        if (!cancelled) {
          showError(error.message || "No pudimos cargar el destino.");
          navigate("/packages", { replace: true });
        }
      } finally {
        if (!cancelled) {
          setLoading(false);
        }
      }
    }

    setLoading(true);
    if (isNew) {
      loadCountryForNew();
    } else {
      loadDestination();
    }

    return () => {
      cancelled = true;
    };
  }, [isNew, navigate, publicId, searchParams]);

  const nextDepartureDate = useMemo(() => {
    const source = form.departures.some((departure) => departure.isActive)
      ? form.departures.filter((departure) => departure.isActive)
      : form.departures;

    const ordered = [...source]
      .filter((departure) => departure?.startDate)
      .sort((left, right) => new Date(left.startDate).getTime() - new Date(right.startDate).getTime());

    return ordered[0]?.startDate || null;
  }, [form.departures]);

  const fromPrice = useMemo(() => {
    const active = form.departures.filter((departure) => departure.isActive);
    const source = active.length > 0 ? active : form.departures;
    const sorted = [...source].sort((left, right) => Number(left.salePrice || 0) - Number(right.salePrice || 0));
    return sorted[0] || null;
  }, [form.departures]);

  const departureSummary = useMemo(() => {
    const activeCount = form.departures.filter((departure) => departure.isActive).length;
    const primaryDeparture = form.departures.find((departure) => departure.isPrimary) || null;

    return {
      total: form.departures.length,
      active: activeCount,
      primaryDeparture,
    };
  }, [form.departures]);

  const publicationState = useMemo(() => {
    if (form.isPublished) {
      return { label: "Visible en el sitio", tone: "emerald" };
    }

    if (form.canPublish) {
      return { label: "Lista para mostrar", tone: "blue" };
    }

    return { label: "En preparacion", tone: "amber" };
  }, [form.canPublish, form.isPublished]);

  const backPath = form.countryPublicId ? `/packages?country=${form.countryPublicId}` : "/packages";

  function updateField(key, value) {
    setForm((current) => ({ ...current, [key]: value }));
  }

  function buildPayload() {
    return {
      countryPublicId: form.countryPublicId,
      name: form.name.trim(),
      title: form.title.trim(),
      tagline: form.tagline?.trim() || null,
      displayOrder: Number(form.displayOrder || 0),
      generalInfo: form.generalInfo?.trim() || null,
      departures: form.departures.map((departure) => ({
        publicId: departure.publicId || null,
        startDate: departure.startDate,
        nights: Number(departure.nights || 0),
        transportLabel: departure.transportLabel?.trim() || "",
        hotelName: departure.hotelName?.trim() || "",
        mealPlan: departure.mealPlan?.trim() || "",
        roomBase: departure.roomBase?.trim() || "",
        currency: departure.currency || "USD",
        salePrice: Number(departure.salePrice || 0),
        isPrimary: Boolean(departure.isPrimary),
        isActive: Boolean(departure.isActive),
      })),
    };
  }

  async function persistDestination(successMessage = "Cambios guardados.") {
    if (!form.countryPublicId) {
      showError("Selecciona un pais antes de guardar.");
      return null;
    }

    if (!form.name.trim()) {
      showError("Ingresa el nombre del destino.");
      return null;
    }

    if (!form.title.trim()) {
      showError("Ingresa el titulo comercial.");
      return null;
    }

    setSaving(true);
    try {
      const saved = isNew
        ? await api.post("/destinations", buildPayload())
        : await api.put(`/destinations/${form.publicId}`, buildPayload());

      setForm(mapDestinationForm(saved));
      showSuccess(isNew ? "Destino creado." : successMessage);

      if (isNew) {
        navigate(`/packages/destinations/${saved.publicId}?country=${saved.countryPublicId}`, { replace: true });
      }

      return saved;
    } catch (error) {
      showError(error.message || "No pudimos guardar el destino.");
      return null;
    } finally {
      setSaving(false);
    }
  }

  async function handleImageSelected(file) {
    if (!file) {
      return;
    }

    if (!form.publicId) {
      showError("Guarda primero el destino para poder cargar la imagen.");
      return;
    }

    const payload = new FormData();
    payload.append("file", file);

    setImageUploading(true);
    try {
      const updated = await api.post(`/destinations/${form.publicId}/hero-image`, payload);
      setForm(mapDestinationForm(updated));
      setImageInputKey((current) => current + 1);
      showSuccess("Imagen principal actualizada.");
    } catch (error) {
      showError(error.message || "No pudimos cargar la imagen.");
    } finally {
      setImageUploading(false);
    }
  }

  function openNewDeparture() {
    setEditingDepartureId(null);
    setDepartureDraft(createDepartureDraft(form.departures.length === 0));
    setDepartureModalOpen(true);
  }

  function openEditDeparture(departure) {
    setEditingDepartureId(departure.clientId);
    setDepartureDraft({ ...departure });
    setDepartureModalOpen(true);
  }

  function closeDepartureModal() {
    setDepartureModalOpen(false);
    setEditingDepartureId(null);
    setDepartureDraft(createDepartureDraft(false));
  }

  function saveDepartureDraft(event) {
    event.preventDefault();

    if (!departureDraft.startDate) {
      showError("Ingresa la fecha de salida.");
      return;
    }

    if (!departureDraft.hotelName.trim()) {
      showError("Ingresa el hotel.");
      return;
    }

    if (!departureDraft.transportLabel.trim()) {
      showError("Ingresa el transporte.");
      return;
    }

    if (!departureDraft.mealPlan.trim()) {
      showError("Ingresa el regimen.");
      return;
    }

    if (!departureDraft.roomBase.trim()) {
      showError("Ingresa la base.");
      return;
    }

    if (Number(departureDraft.nights || 0) <= 0) {
      showError("Las noches deben ser mayores a cero.");
      return;
    }

    if (Number(departureDraft.salePrice || 0) <= 0) {
      showError("La tarifa debe ser mayor a cero.");
      return;
    }

    setForm((current) => {
      const normalized = {
        ...departureDraft,
        clientId: editingDepartureId || departureDraft.clientId,
        nights: Number(departureDraft.nights || 0),
        salePrice: Number(departureDraft.salePrice || 0),
      };

      let nextDepartures = editingDepartureId
        ? current.departures.map((item) => (item.clientId === editingDepartureId ? normalized : item))
        : [...current.departures, normalized];

      if (normalized.isPrimary) {
        nextDepartures = nextDepartures.map((item) =>
          item.clientId === normalized.clientId ? { ...normalized, isPrimary: true } : { ...item, isPrimary: false }
        );
      }

      if (!nextDepartures.some((item) => item.isPrimary) && nextDepartures.length > 0) {
        nextDepartures = nextDepartures.map((item, index) => ({ ...item, isPrimary: index === 0 }));
      }

      nextDepartures = [...nextDepartures].sort(
        (left, right) => new Date(left.startDate).getTime() - new Date(right.startDate).getTime()
      );

      return { ...current, departures: nextDepartures };
    });

    closeDepartureModal();
  }

  async function removeDeparture(departure) {
    const confirmed = await showConfirm({
      title: "Quitar salida",
      eyebrow: "Salidas",
      text: "La salida se quitara de este formulario. Guarda el destino para confirmar el cambio.",
      confirmText: "Quitar salida",
      confirmColor: "red",
    });

    if (!confirmed) {
      return;
    }

    setForm((current) => {
      let nextDepartures = current.departures.filter((item) => item.clientId !== departure.clientId);

      if (!nextDepartures.some((item) => item.isPrimary) && nextDepartures.length > 0) {
        nextDepartures = nextDepartures.map((item, index) => ({ ...item, isPrimary: index === 0 }));
      }

      return { ...current, departures: nextDepartures };
    });
  }

  async function copyPublicationCode() {
    if (!form.publicPagePath) {
      showError("Guarda el destino antes de copiarlo para la web.");
      return;
    }

    try {
      await navigator.clipboard.writeText(buildDestinationPublicationSnippet(form));
      showSuccess("Codigo para la web copiado.");
    } catch {
      showError("No pudimos copiar el codigo para la web.");
    }
  }

  function openClientPreview() {
    if (!form.slug) {
      showError("Guarda el destino antes de verlo como cliente.");
      return;
    }

    const previewUrl = new URL(buildAppUrl(`/preview/packages/${form.slug}`));
    if (form.countrySlug) {
      previewUrl.searchParams.set("countrySlug", form.countrySlug);
    }

    window.open(previewUrl.toString(), "_blank", "noopener,noreferrer");
  }

  async function publishDestination() {
    if (!form.publicId) {
      showError("Guarda el destino antes de mostrarlo en el sitio.");
      return;
    }

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
      const updated = await api.patch(`/destinations/${form.publicId}/publish`);
      setForm(mapDestinationForm(updated));
      showSuccess("El destino ya esta visible en el sitio.");
    } catch (error) {
      showError(error.message || "No pudimos mostrar el destino en el sitio.");
    }
  }

  async function unpublishDestination() {
    if (!form.publicId) {
      return;
    }

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
      const updated = await api.patch(`/destinations/${form.publicId}/unpublish`);
      setForm(mapDestinationForm(updated));
      showSuccess("El destino dejo de mostrarse en el sitio.");
    } catch (error) {
      showError(error.message || "No pudimos retirar el destino del sitio.");
    }
  }

  async function handleSave() {
    await persistDestination("Cambios guardados.");
  }

  if (loading) {
    return (
      <div className="flex min-h-[50vh] items-center justify-center">
        <Loader2 className="h-7 w-7 animate-spin text-indigo-500" />
      </div>
    );
  }

  return (
    <div className="animate-in fade-in space-y-4 duration-500 md:space-y-6">
      <ListPageHeader
        title={isNew ? "Nuevo destino" : form.title || form.name || "Destino"}
        subtitle="Edita contenido, salidas y publicacion desde una sola pantalla."
        actions={
          <div className="flex flex-wrap gap-2">
            <Button type="button" variant="outline" onClick={() => navigate(backPath)} className="gap-2">
              <ArrowLeft className="h-4 w-4" />
              Volver
            </Button>
            {canEdit ? (
              <Button type="button" onClick={handleSave} disabled={saving} className="gap-2 xl:hidden">
                {saving ? <Loader2 className="h-4 w-4 animate-spin" /> : null}
                Guardar
              </Button>
            ) : null}
          </div>
        }
      />

      <div className="grid gap-4 xl:grid-cols-[minmax(0,1fr)_320px]">
        <div className="space-y-4">
          <section className="rounded-lg border border-slate-200 bg-white p-4 shadow-sm dark:border-slate-800 dark:bg-slate-900/50">
            <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
              <SummaryMetric label="Pais" value={form.countryName || "-"} />
              <SummaryMetric label="Estado" value={publicationState.label} tone={publicationState.tone} />
              <SummaryMetric label="Proxima salida" value={formatLongDate(nextDepartureDate)} />
              <SummaryMetric
                label="Precio desde"
                value={fromPrice ? formatMoney(fromPrice.salePrice, fromPrice.currency) : "-"}
              />
            </div>
          </section>

          <SectionCard
            title="Datos principales"
            description="Define el nombre comercial, el texto corto y la descripcion principal del destino."
          >
            <div className="grid gap-4 md:grid-cols-2">
              <Field label="Nombre del destino">
                <input
                  type="text"
                  value={form.name}
                  onChange={(event) => updateField("name", event.target.value)}
                  className={inputClass}
                  disabled={!canEdit}
                />
              </Field>

              <Field label="Titulo comercial">
                <input
                  type="text"
                  value={form.title}
                  onChange={(event) => updateField("title", event.target.value)}
                  className={inputClass}
                  disabled={!canEdit}
                />
              </Field>
            </div>

            <div className="grid gap-4">
              <Field label="Texto destacado">
                <input
                  type="text"
                  value={form.tagline}
                  onChange={(event) => updateField("tagline", event.target.value)}
                  className={inputClass}
                  placeholder="Frase corta que resuma la propuesta."
                  disabled={!canEdit}
                />
              </Field>

              <Field label="Descripcion para el sitio">
                <textarea
                  value={form.generalInfo}
                  onChange={(event) => updateField("generalInfo", event.target.value)}
                  className={textareaClass}
                  placeholder="Cuenta que incluye la propuesta, el estilo del viaje y la informacion relevante para el cliente."
                  disabled={!canEdit}
                />
              </Field>
            </div>
          </SectionCard>

          <SectionCard
            title="Imagen principal"
            description="Carga la portada del destino. Si todavia no guardaste el registro, primero crea el destino y despues sube la imagen."
          >
            <ImageUploadSection
              canEdit={canEdit}
              disabled={!form.publicId}
              uploading={imageUploading}
              imageUrl={form.heroImageUrl}
              imageName={form.heroImageFileName}
              imageInputKey={imageInputKey}
              title={form.title || form.name || "Destino"}
              onFileChange={(event) => handleImageSelected(event.target.files?.[0] || null)}
            />
          </SectionCard>

          <SectionCard
            title="Salidas"
            description="Gestiona fechas, hoteleria y tarifas desde el mismo editor."
            action={
              canEdit ? (
                <Button type="button" onClick={openNewDeparture} disabled={!form.publicId} className="gap-2">
                  <Plus className="h-4 w-4" />
                  Agregar salida
                </Button>
              ) : null
            }
          >
            {!form.publicId ? (
              <InfoNotice>
                Guarda el destino una vez para habilitar la carga de salidas y mantener el contexto del pais.
              </InfoNotice>
            ) : form.departures.length === 0 ? (
              <InfoNotice>Todavia no hay salidas cargadas para este destino.</InfoNotice>
            ) : (
              <div className="space-y-4">
                <div className="grid gap-3 md:grid-cols-3">
                  <SummaryMetric label="Total de salidas" value={departureSummary.total} />
                  <SummaryMetric label="Activas" value={departureSummary.active} tone="emerald" />
                  <SummaryMetric
                    label="Salida destacada"
                    value={formatLongDate(departureSummary.primaryDeparture?.startDate)}
                    tone="blue"
                  />
                </div>

                <div className="space-y-3">
                  {form.departures.map((departure) => (
                    <DepartureListItem
                      key={departure.clientId}
                      departure={departure}
                      canEdit={canEdit}
                      onEdit={() => openEditDeparture(departure)}
                      onRemove={() => removeDeparture(departure)}
                    />
                  ))}
                </div>
              </div>
            )}
          </SectionCard>
        </div>

        <DestinationEditorSidebar
          canEdit={canEdit}
          canPublish={canPublish}
          saving={saving}
          form={form}
          publicationState={publicationState}
          nextDepartureDate={nextDepartureDate}
          fromPrice={fromPrice}
          onBack={() => navigate(backPath)}
          onSave={handleSave}
          onDisplayOrderChange={(value) => updateField("displayOrder", value)}
          onPreview={openClientPreview}
          onCopy={copyPublicationCode}
          onPublish={publishDestination}
          onUnpublish={unpublishDestination}
        />
      </div>

      <DestinationDepartureDialog
        open={departureModalOpen}
        draft={departureDraft}
        editing={Boolean(editingDepartureId)}
        onChange={(key, value) => setDepartureDraft((current) => ({ ...current, [key]: value }))}
        onClose={closeDepartureModal}
        onSubmit={saveDepartureDraft}
      />
    </div>
  );
}

function SectionCard({ title, description, action, children }) {
  return (
    <section className="rounded-lg border border-slate-200 bg-white p-4 shadow-sm dark:border-slate-800 dark:bg-slate-900/50">
      <div className="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between">
        <div>
          <h2 className="text-base font-semibold text-slate-900 dark:text-white">{title}</h2>
          {description ? <p className="mt-1 text-sm text-slate-500 dark:text-slate-400">{description}</p> : null}
        </div>
        {action}
      </div>

      <div className="mt-5 space-y-4">{children}</div>
    </section>
  );
}

function SummaryMetric({ label, value, tone = "slate" }) {
  const tones = {
    slate: "border-slate-200 bg-slate-50 text-slate-900 dark:border-slate-800 dark:bg-slate-950/40 dark:text-white",
    emerald:
      "border-emerald-200 bg-emerald-50 text-emerald-800 dark:border-emerald-900/40 dark:bg-emerald-900/10 dark:text-emerald-200",
    amber:
      "border-amber-200 bg-amber-50 text-amber-800 dark:border-amber-900/40 dark:bg-amber-900/10 dark:text-amber-200",
    blue: "border-blue-200 bg-blue-50 text-blue-800 dark:border-blue-900/40 dark:bg-blue-900/10 dark:text-blue-200",
  };

  return (
    <div className={`rounded-md border px-3 py-3 ${tones[tone] || tones.slate}`}>
      <p className="text-[11px] font-semibold uppercase tracking-wide opacity-80">{label}</p>
      <p className="mt-1 text-sm font-semibold">{value}</p>
    </div>
  );
}

function Field({ label, children }) {
  return (
    <label className="block space-y-2">
      <span className="text-sm font-medium text-slate-700 dark:text-slate-200">{label}</span>
      {children}
    </label>
  );
}

function ImageUploadSection({
  canEdit,
  disabled,
  uploading,
  imageUrl,
  imageName,
  imageInputKey,
  title,
  onFileChange,
}) {
  return (
    <div className="grid gap-4 lg:grid-cols-[320px_minmax(0,1fr)]">
      <div className="overflow-hidden rounded-md border border-slate-200 bg-slate-50 dark:border-slate-800 dark:bg-slate-950/40">
        {imageUrl ? (
          <img src={imageUrl} alt={title} className="h-64 w-full object-cover" />
        ) : (
          <div className="flex h-64 flex-col items-center justify-center gap-3 px-4 text-center text-slate-400">
            <ImagePlus className="h-8 w-8" />
            <p className="text-sm">Todavia no cargaste una imagen principal.</p>
          </div>
        )}
      </div>

      <div className="space-y-4">
        <div className="rounded-md border border-slate-200 bg-slate-50 px-4 py-4 dark:border-slate-800 dark:bg-slate-950/40">
          <p className="text-sm font-medium text-slate-900 dark:text-white">
            {disabled ? "Guarda el destino para habilitar la carga de imagen." : "Selecciona la portada principal del destino."}
          </p>
          <p className="mt-1 text-sm text-slate-500 dark:text-slate-400">
            Acepta archivos `.png`, `.jpg`, `.jpeg` y `.webp`. La imagen se publica automaticamente cuando termina la carga.
          </p>
        </div>

        <label
          className={`block rounded-md border border-dashed px-4 py-6 text-center transition ${
            disabled || !canEdit
              ? "cursor-not-allowed border-slate-200 bg-slate-50 text-slate-400 dark:border-slate-800 dark:bg-slate-950/40"
              : "cursor-pointer border-indigo-300 bg-indigo-50/50 text-slate-700 hover:border-indigo-400 hover:bg-indigo-50 dark:border-indigo-900/50 dark:bg-indigo-900/10 dark:text-slate-200"
          }`}
        >
          <input
            key={imageInputKey}
            type="file"
            accept="image/png,image/jpeg,image/webp"
            onChange={onFileChange}
            className="hidden"
            disabled={disabled || !canEdit || uploading}
          />
          <div className="flex flex-col items-center gap-3">
            {uploading ? <Loader2 className="h-6 w-6 animate-spin text-indigo-500" /> : <ImagePlus className="h-6 w-6 text-indigo-500" />}
            <div className="space-y-1">
              <p className="text-sm font-semibold">
                {disabled ? "Pendiente de primer guardado" : "Seleccionar imagen principal"}
              </p>
              <p className="text-xs text-slate-500 dark:text-slate-400">
                {disabled ? "Cuando el destino exista ya podras cargar la portada." : "Haz clic aqui para buscar la imagen en tu equipo."}
              </p>
            </div>
          </div>
        </label>

        {imageName ? <p className="text-sm text-slate-500 dark:text-slate-400">Imagen actual: {imageName}</p> : null}
      </div>
    </div>
  );
}

function DepartureListItem({ departure, canEdit, onEdit, onRemove }) {
  return (
    <article className="rounded-md border border-slate-200 bg-slate-50/80 p-4 dark:border-slate-800 dark:bg-slate-950/30">
      <div className="flex flex-col gap-4 lg:flex-row lg:items-start lg:justify-between">
        <div className="min-w-0 flex-1">
          <div className="flex flex-wrap gap-2">
            {departure.isPrimary ? <StatusPill tone="blue">Destacada</StatusPill> : null}
            <StatusPill tone={departure.isActive ? "emerald" : "amber"}>
              {departure.isActive ? "Visible" : "Oculta"}
            </StatusPill>
          </div>

          <h3 className="mt-3 text-base font-semibold text-slate-900 dark:text-white">{formatLongDate(departure.startDate)}</h3>
          <p className="mt-1 text-sm text-slate-500 dark:text-slate-400">
            {departure.hotelName} · {departure.transportLabel}
          </p>

          <div className="mt-4 grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
            <DepartureMeta label="Noches" value={`${departure.nights}`} />
            <DepartureMeta label="Tarifa" value={formatMoney(departure.salePrice, departure.currency)} highlight />
            <DepartureMeta label="Regimen" value={departure.mealPlan} />
            <DepartureMeta label="Base" value={departure.roomBase} />
          </div>
        </div>

        {canEdit ? (
          <div className="flex flex-wrap gap-2">
            <Button type="button" variant="outline" size="sm" onClick={onEdit}>
              Editar
            </Button>
            <Button type="button" variant="outline" size="sm" onClick={onRemove} className="gap-2">
              <Trash2 className="h-4 w-4" />
              Quitar
            </Button>
          </div>
        ) : null}
      </div>
    </article>
  );
}

function DepartureMeta({ label, value, highlight = false }) {
  return (
    <div className="rounded-md border border-slate-200 bg-white px-3 py-3 dark:border-slate-800 dark:bg-slate-900/50">
      <p className="text-[11px] font-semibold uppercase tracking-wide text-slate-500 dark:text-slate-400">{label}</p>
      <p className={`mt-1 text-sm ${highlight ? "font-semibold text-slate-900 dark:text-white" : "text-slate-700 dark:text-slate-200"}`}>
        {value}
      </p>
    </div>
  );
}

function StatusPill({ children, tone = "slate" }) {
  const tones = {
    slate: "bg-slate-100 text-slate-700 dark:bg-slate-800 dark:text-slate-200",
    emerald: "bg-emerald-100 text-emerald-700 dark:bg-emerald-900/30 dark:text-emerald-300",
    amber: "bg-amber-100 text-amber-700 dark:bg-amber-900/30 dark:text-amber-300",
    blue: "bg-blue-100 text-blue-700 dark:bg-blue-900/30 dark:text-blue-300",
  };

  return <span className={`rounded-md px-2 py-1 text-xs font-semibold ${tones[tone] || tones.slate}`}>{children}</span>;
}

function InfoNotice({ children }) {
  return (
    <div className="rounded-md border border-dashed border-slate-300 px-4 py-8 text-center text-sm text-slate-500 dark:border-slate-700 dark:text-slate-400">
      {children}
    </div>
  );
}
