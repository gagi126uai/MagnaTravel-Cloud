import { useEffect, useMemo, useState } from "react";
import { useNavigate, useParams, useSearchParams } from "react-router-dom";
import {
  ArrowLeft,
  Building2,
  CalendarDays,
  CheckCircle2,
  Copy,
  Eye,
  ImagePlus,
  Loader2,
  MapPinned,
  Plus,
  Rocket,
  Save,
  Trash2,
  UploadCloud,
} from "lucide-react";
import { api, buildAppUrl } from "../../../api";
import { showConfirm, showError, showSuccess } from "../../../alerts";
import { hasPermission } from "../../../auth";
import { Button } from "../../../components/ui/button";
import { ListPageHeader } from "../../../components/ui/ListPageHeader";
import {
  buildDestinationPublicationSnippet,
  createDepartureDraft,
  createEmptyDestinationForm,
  formatLongDate,
  formatMoney,
  mapDestinationForm,
} from "../lib/publicationUtils";

const inputClass =
  "w-full rounded-lg border border-slate-200 bg-white px-3 py-2.5 text-sm text-slate-900 outline-none transition focus:border-indigo-500 focus:ring-1 focus:ring-indigo-500 dark:border-slate-700 dark:bg-slate-900 dark:text-white";
const textareaClass = `${inputClass} min-h-[200px] resize-y`;

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
  const [currentStep, setCurrentStep] = useState(0);
  const [departureModalOpen, setDepartureModalOpen] = useState(false);
  const [departureDraft, setDepartureDraft] = useState(createDepartureDraft(true));
  const [editingDepartureId, setEditingDepartureId] = useState(null);

  const steps = useMemo(() => {
    const baseSteps = [
      { key: "details", label: "Datos del destino" },
      { key: "content", label: "Imagen y contenido" },
      { key: "departures", label: "Salidas" },
    ];

    if (canPublish) {
      baseSteps.push({ key: "publication", label: "Publicacion web" });
    }

    return baseSteps;
  }, [canPublish]);

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
  const currentStepKey = steps[currentStep]?.key || steps[0]?.key;

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

  async function persistDestination(successMessage = "Destino actualizado.") {
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
      showError("Completa primero los datos del destino para poder cargar la imagen.");
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
      text: "La salida se quitara de este destino.",
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

  async function handleContinue() {
    const saved = await persistDestination(currentStep === steps.length - 1 ? "Destino actualizado." : "Cambios guardados.");
    if (!saved) {
      return;
    }

    if (currentStep < steps.length - 1) {
      setCurrentStep((step) => Math.min(step + 1, steps.length - 1));
    }
  }

  function goToStep(index) {
    if (index === currentStep) {
      return;
    }

    if (!form.publicId && index > 0) {
      return;
    }

    setCurrentStep(index);
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
        subtitle="Completa la propuesta paso a paso para dejarla lista para el sitio."
        actions={
          <Button variant="outline" onClick={() => navigate(backPath)} className="gap-2">
            <ArrowLeft className="h-4 w-4" />
            Volver a destinos
          </Button>
        }
      />

      <div className="rounded-2xl border border-slate-200 bg-white p-5 shadow-sm dark:border-slate-800 dark:bg-slate-900/50">
        <div className="grid gap-3 md:grid-cols-4">
          <SummaryCard icon={Building2} label="Pais" value={form.countryName || "-"} />
          <SummaryCard icon={CheckCircle2} label="Estado" value={publicationState.label} tone={publicationState.tone} />
          <SummaryCard icon={CalendarDays} label="Proxima salida" value={formatLongDate(nextDepartureDate)} />
          <SummaryCard
            icon={MapPinned}
            label="Precio desde"
            value={fromPrice ? formatMoney(fromPrice.salePrice, fromPrice.currency) : "-"}
          />
        </div>
      </div>

      <div className="rounded-2xl border border-slate-200 bg-white shadow-sm dark:border-slate-800 dark:bg-slate-900/50">
        <div className="border-b border-slate-200 px-4 py-4 dark:border-slate-800">
          <div className="grid gap-2 sm:grid-cols-2 xl:grid-cols-4">
            {steps.map((step, index) => (
              <button
                key={step.key}
                type="button"
                onClick={() => goToStep(index)}
                disabled={!form.publicId && index > 0}
                className={`rounded-xl border px-4 py-3 text-left transition ${
                  currentStep === index
                    ? "border-indigo-500 bg-indigo-600 text-white shadow-sm"
                    : index < currentStep || form.publicId
                      ? "border-slate-200 bg-slate-50 text-slate-700 hover:bg-slate-100 dark:border-slate-800 dark:bg-slate-900 dark:text-slate-200 dark:hover:bg-slate-800"
                      : "border-slate-200 bg-slate-50 text-slate-400 dark:border-slate-800 dark:bg-slate-950/40 dark:text-slate-600"
                }`}
              >
                <span className="text-[11px] font-semibold uppercase tracking-[0.18em] opacity-80">Paso {index + 1}</span>
                <span className="mt-1 block text-sm font-semibold">{step.label}</span>
              </button>
            ))}
          </div>
        </div>

        <div className="p-5">
          {currentStepKey === "details" ? (
            <WizardSection
              title="Datos del destino"
              description="Define como se identifica esta propuesta dentro del pais y en el sitio."
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

                <Field label="Orden de aparicion">
                  <input
                    type="number"
                    min="0"
                    value={form.displayOrder}
                    onChange={(event) => updateField("displayOrder", event.target.value)}
                    className={inputClass}
                    disabled={!canEdit}
                  />
                </Field>
              </div>
            </WizardSection>
          ) : null}

          {currentStepKey === "content" ? (
            <WizardSection
              title="Imagen y contenido"
              description="Carga la imagen principal y el texto que vera el cliente."
            >
              <div className="grid gap-6 xl:grid-cols-[340px_minmax(0,1fr)]">
                <ImageUploadCard
                  canEdit={canEdit}
                  disabled={!form.publicId}
                  uploading={imageUploading}
                  imageUrl={form.heroImageUrl}
                  imageName={form.heroImageFileName}
                  imageInputKey={imageInputKey}
                  title={form.title || form.name || "Destino"}
                  onFileChange={(event) => handleImageSelected(event.target.files?.[0] || null)}
                />

                <div className="space-y-4">
                  <Field label="Texto destacado">
                    <input
                      type="text"
                      value={form.tagline}
                      onChange={(event) => updateField("tagline", event.target.value)}
                      className={inputClass}
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
              </div>
            </WizardSection>
          ) : null}

          {currentStepKey === "departures" ? (
            <WizardSection
              title="Salidas"
              description="Carga fechas, hoteles y tarifas para cada opcion disponible."
              action={
                canEdit ? (
                  <Button onClick={openNewDeparture} className="gap-2">
                    <Plus className="h-4 w-4" />
                    Agregar salida
                  </Button>
                ) : null
              }
            >
              {form.departures.length === 0 ? (
                <div className="rounded-xl border border-dashed border-slate-300 px-4 py-12 text-center text-sm text-slate-500 dark:border-slate-700 dark:text-slate-400">
                  Todavia no hay salidas cargadas para este destino.
                </div>
              ) : (
                <div className="space-y-4">
                  <div className="grid gap-3 md:grid-cols-3">
                    <InlineSummaryCard label="Total de salidas" value={departureSummary.total} />
                    <InlineSummaryCard label="Activas" value={departureSummary.active} tone="emerald" />
                    <InlineSummaryCard
                      label="Salida destacada"
                      value={formatLongDate(departureSummary.primaryDeparture?.startDate)}
                      tone="indigo"
                    />
                  </div>

                  <div className="space-y-3">
                    {form.departures.map((departure) => (
                      <DepartureListCard
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
            </WizardSection>
          ) : null}

          {currentStepKey === "publication" ? (
            <WizardSection
              title="Publicacion web"
              description="Revisa el estado del destino, mira la vista del cliente y controla su presencia en el sitio."
            >
              <div className="space-y-4">
                <div className="rounded-xl border border-slate-200 bg-slate-50 p-4 dark:border-slate-800 dark:bg-slate-950/40">
                  <div className="flex flex-col gap-4 lg:flex-row lg:items-center lg:justify-between">
                    <div>
                      <p className="text-sm font-semibold text-slate-900 dark:text-white">Estado actual</p>
                      <p className="mt-1 text-sm text-slate-500 dark:text-slate-400">
                        {form.isPublished
                          ? "Este destino ya esta visible para los clientes."
                          : "Todavia no esta visible en el sitio. Puedes revisarlo antes de mostrarlo."}
                      </p>
                    </div>
                    <StatusPill tone={publicationState.tone}>{publicationState.label}</StatusPill>
                  </div>
                </div>

                <div className="flex flex-wrap gap-2">
                  <Button variant="outline" onClick={openClientPreview} disabled={!form.slug} className="gap-2">
                    <Eye className="h-4 w-4" />
                    Ver como cliente
                  </Button>
                  <Button variant="outline" onClick={copyPublicationCode} disabled={!form.publicPagePath} className="gap-2">
                    <Copy className="h-4 w-4" />
                    Copiar para la web
                  </Button>
                </div>

                {form.publishIssues?.length > 0 ? (
                  <div className="rounded-xl border border-amber-200 bg-amber-50/80 p-4 dark:border-amber-900/40 dark:bg-amber-900/10">
                    <p className="text-sm font-semibold text-amber-800 dark:text-amber-200">
                      Antes de mostrar este destino en el sitio, falta completar:
                    </p>
                    <ul className="mt-2 list-disc space-y-2 pl-5 text-sm text-amber-700 dark:text-amber-300">
                      {form.publishIssues.map((issue) => (
                        <li key={issue}>{issue}</li>
                      ))}
                    </ul>
                  </div>
                ) : null}

                <div className="flex flex-wrap gap-2">
                  {form.isPublished ? (
                    <Button variant="outline" onClick={unpublishDestination} className="gap-2">
                      <Rocket className="h-4 w-4" />
                      Retirar del sitio
                    </Button>
                  ) : (
                    <Button onClick={publishDestination} disabled={!form.publicId || !form.canPublish} className="gap-2">
                      <Rocket className="h-4 w-4" />
                      Mostrar en el sitio
                    </Button>
                  )}
                </div>
              </div>
            </WizardSection>
          ) : null}
        </div>

        <div className="flex flex-col gap-3 border-t border-slate-200 px-5 py-4 sm:flex-row sm:items-center sm:justify-between dark:border-slate-800">
          <Button variant="outline" onClick={() => setCurrentStep((step) => Math.max(step - 1, 0))} disabled={currentStep === 0}>
            Anterior
          </Button>

          {canEdit ? (
            <Button onClick={handleContinue} disabled={saving} className="gap-2">
              {saving ? <Loader2 className="h-4 w-4 animate-spin" /> : currentStep === steps.length - 1 ? <Save className="h-4 w-4" /> : <UploadCloud className="h-4 w-4" />}
              {currentStep === steps.length - 1 ? "Guardar cambios" : "Guardar y continuar"}
            </Button>
          ) : null}
        </div>
      </div>

      <DepartureDialog
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

function WizardSection({ title, description, action, children }) {
  return (
    <section className="space-y-5">
      <div className="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between">
        <div>
          <h2 className="text-lg font-semibold text-slate-900 dark:text-white">{title}</h2>
          {description ? <p className="mt-1 text-sm text-slate-500 dark:text-slate-400">{description}</p> : null}
        </div>
        {action}
      </div>
      {children}
    </section>
  );
}

function SummaryCard({ icon: Icon, label, value, tone = "slate" }) {
  const toneClass =
    tone === "emerald"
      ? "bg-emerald-50 text-emerald-800 dark:bg-emerald-900/20 dark:text-emerald-200"
      : tone === "blue"
        ? "bg-blue-50 text-blue-800 dark:bg-blue-900/20 dark:text-blue-200"
        : tone === "amber"
          ? "bg-amber-50 text-amber-800 dark:bg-amber-900/20 dark:text-amber-200"
          : "bg-slate-50 text-slate-900 dark:bg-slate-950/40 dark:text-white";

  return (
    <div className={`rounded-xl border border-slate-200 p-4 dark:border-slate-800 ${toneClass}`}>
      <div className="flex items-center gap-3">
        <div className="rounded-full bg-white/80 p-2 shadow-sm dark:bg-slate-900/80">
          <Icon className="h-4 w-4" />
        </div>
        <div className="min-w-0">
          <p className="text-xs font-semibold uppercase tracking-[0.18em] text-slate-500 dark:text-slate-400">{label}</p>
          <p className="mt-1 truncate text-sm font-semibold">{value}</p>
        </div>
      </div>
    </div>
  );
}

function InlineSummaryCard({ label, value, tone = "slate" }) {
  const toneClass =
    tone === "emerald"
      ? "bg-emerald-50 text-emerald-800 dark:bg-emerald-900/20 dark:text-emerald-200"
      : tone === "indigo"
        ? "bg-indigo-50 text-indigo-800 dark:bg-indigo-900/20 dark:text-indigo-200"
        : "bg-slate-50 text-slate-900 dark:bg-slate-950/40 dark:text-white";

  return (
    <div className={`rounded-xl border border-slate-200 px-4 py-4 dark:border-slate-800 ${toneClass}`}>
      <p className="text-[11px] font-semibold uppercase tracking-[0.16em] text-slate-500 dark:text-slate-400">{label}</p>
      <p className="mt-2 break-words text-lg font-semibold">{value}</p>
    </div>
  );
}

function DepartureListCard({ departure, canEdit, onEdit, onRemove }) {
  return (
    <article className="rounded-2xl border border-slate-200 bg-white p-4 shadow-sm dark:border-slate-800 dark:bg-slate-900/50">
      <div className="flex flex-col gap-4 xl:flex-row xl:items-start xl:justify-between">
        <div className="min-w-0 flex-1">
          <div className="flex flex-wrap gap-2">
            {departure.isPrimary ? <StatusPill tone="indigo">Destacada</StatusPill> : null}
            <StatusPill tone={departure.isActive ? "emerald" : "amber"}>
              {departure.isActive ? "Visible" : "Oculta"}
            </StatusPill>
          </div>

          <h3 className="mt-3 text-lg font-semibold tracking-tight text-slate-900 dark:text-white">
            {formatLongDate(departure.startDate)}
          </h3>

          <p className="mt-1 text-sm text-slate-500 dark:text-slate-400">
            {departure.hotelName} / {departure.transportLabel}
          </p>

          <div className="mt-4 grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
            <DepartureMetaItem label="Noches" value={`${departure.nights}`} />
            <DepartureMetaItem label="Tarifa" value={formatMoney(departure.salePrice, departure.currency)} highlight />
            <DepartureMetaItem label="Regimen" value={departure.mealPlan} />
            <DepartureMetaItem label="Base" value={departure.roomBase} />
          </div>
        </div>

        {canEdit ? (
          <div className="flex flex-wrap gap-2 xl:max-w-[220px] xl:justify-end">
            <Button variant="outline" size="sm" onClick={onEdit}>
              Editar
            </Button>
            <Button variant="outline" size="sm" onClick={onRemove} className="gap-2">
              <Trash2 className="h-4 w-4" />
              Quitar
            </Button>
          </div>
        ) : null}
      </div>
    </article>
  );
}

function DepartureMetaItem({ label, value, highlight = false }) {
  return (
    <div className="rounded-xl border border-slate-200 bg-slate-50/70 px-4 py-3 dark:border-slate-800 dark:bg-slate-950/30">
      <p className="text-[11px] font-semibold uppercase tracking-[0.16em] text-slate-500 dark:text-slate-400">{label}</p>
      <p className={`mt-1 text-sm ${highlight ? "font-semibold text-slate-900 dark:text-white" : "text-slate-700 dark:text-slate-200"}`}>
        {value}
      </p>
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

function ImageUploadCard({
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
    <div className="space-y-4">
      <div className="overflow-hidden rounded-xl border border-slate-200 bg-slate-50 dark:border-slate-800 dark:bg-slate-950/40">
        {imageUrl ? (
          <img src={imageUrl} alt={title} className="h-64 w-full object-cover" />
        ) : (
          <div className="flex h-64 flex-col items-center justify-center gap-3 px-4 text-center text-slate-400">
            <ImagePlus className="h-8 w-8" />
            <p className="text-sm">Todavia no cargaste una imagen principal.</p>
          </div>
        )}
      </div>

      <label
        className={`block rounded-xl border border-dashed px-4 py-5 text-center transition ${
          disabled || !canEdit
            ? "cursor-not-allowed border-slate-200 bg-slate-50 text-slate-400 dark:border-slate-800 dark:bg-slate-950/40"
            : "cursor-pointer border-indigo-200 bg-indigo-50/50 text-slate-700 hover:border-indigo-300 hover:bg-indigo-50 dark:border-indigo-900/50 dark:bg-indigo-900/10 dark:text-slate-200"
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
          {uploading ? <Loader2 className="h-6 w-6 animate-spin text-indigo-500" /> : <UploadCloud className="h-6 w-6 text-indigo-500" />}
          <div className="space-y-1">
            <p className="text-sm font-semibold">
              {disabled ? "Completa primero los datos del destino" : "Seleccionar imagen principal"}
            </p>
            <p className="text-xs text-slate-500 dark:text-slate-400">
              {disabled
                ? "Cuando guardes el primer paso vas a poder cargar la imagen."
                : "La imagen se carga automaticamente apenas la seleccionas."}
            </p>
          </div>
        </div>
      </label>

      {imageName ? <p className="text-sm text-slate-500 dark:text-slate-400">Imagen actual: {imageName}</p> : null}
    </div>
  );
}

function StatusPill({ children, tone = "slate" }) {
  const tones = {
    slate: "bg-slate-100 text-slate-700 dark:bg-slate-800 dark:text-slate-200",
    emerald: "bg-emerald-100 text-emerald-700 dark:bg-emerald-900/30 dark:text-emerald-300",
    amber: "bg-amber-100 text-amber-700 dark:bg-amber-900/30 dark:text-amber-300",
    blue: "bg-blue-100 text-blue-700 dark:bg-blue-900/30 dark:text-blue-300",
    indigo: "bg-indigo-100 text-indigo-700 dark:bg-indigo-900/30 dark:text-indigo-300",
  };

  return <span className={`rounded-full px-2.5 py-1 text-xs font-semibold ${tones[tone] || tones.slate}`}>{children}</span>;
}

function DepartureDialog({ open, draft, editing, onChange, onClose, onSubmit }) {
  if (!open) {
    return null;
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-slate-950/60 p-4 backdrop-blur-sm">
      <div className="w-full max-w-3xl rounded-xl border border-slate-200 bg-white shadow-2xl dark:border-slate-800 dark:bg-slate-900">
        <div className="border-b border-slate-200 px-6 py-4 dark:border-slate-800">
          <h3 className="text-lg font-semibold text-slate-900 dark:text-white">
            {editing ? "Editar salida" : "Nueva salida"}
          </h3>
          <p className="mt-1 text-sm text-slate-500 dark:text-slate-400">
            Completa la informacion comercial para esta fecha.
          </p>
        </div>

        <form onSubmit={onSubmit} className="space-y-5 p-6">
          <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-4">
            <Field label="Fecha de salida">
              <input
                type="date"
                value={draft.startDate}
                onChange={(event) => onChange("startDate", event.target.value)}
                className={inputClass}
              />
            </Field>

            <Field label="Noches">
              <input
                type="number"
                min="1"
                value={draft.nights}
                onChange={(event) => onChange("nights", event.target.value)}
                className={inputClass}
              />
            </Field>

            <Field label="Moneda">
              <select value={draft.currency} onChange={(event) => onChange("currency", event.target.value)} className={inputClass}>
                <option value="USD">USD</option>
                <option value="ARS">ARS</option>
                <option value="EUR">EUR</option>
              </select>
            </Field>

            <Field label="Tarifa">
              <input
                type="number"
                min="0"
                step="0.01"
                value={draft.salePrice}
                onChange={(event) => onChange("salePrice", event.target.value)}
                className={inputClass}
              />
            </Field>
          </div>

          <div className="grid gap-4 md:grid-cols-2">
            <Field label="Hotel">
              <input
                type="text"
                value={draft.hotelName}
                onChange={(event) => onChange("hotelName", event.target.value)}
                className={inputClass}
              />
            </Field>

            <Field label="Transporte">
              <input
                type="text"
                value={draft.transportLabel}
                onChange={(event) => onChange("transportLabel", event.target.value)}
                className={inputClass}
              />
            </Field>

            <Field label="Regimen">
              <input
                type="text"
                value={draft.mealPlan}
                onChange={(event) => onChange("mealPlan", event.target.value)}
                className={inputClass}
              />
            </Field>

            <Field label="Base">
              <input
                type="text"
                value={draft.roomBase}
                onChange={(event) => onChange("roomBase", event.target.value)}
                className={inputClass}
              />
            </Field>
          </div>

          <div className="grid gap-3 md:grid-cols-2">
            <label className="flex items-center gap-3 rounded-xl border border-slate-200 px-4 py-3 dark:border-slate-800">
              <input
                type="checkbox"
                checked={draft.isPrimary}
                onChange={(event) => onChange("isPrimary", event.target.checked)}
                className="h-4 w-4 rounded border-slate-300 text-indigo-600 focus:ring-indigo-500"
              />
              <div>
                <p className="text-sm font-medium text-slate-900 dark:text-white">Salida destacada</p>
                <p className="text-xs text-slate-500 dark:text-slate-400">Se usa como referencia principal del destino.</p>
              </div>
            </label>

            <label className="flex items-center gap-3 rounded-xl border border-slate-200 px-4 py-3 dark:border-slate-800">
              <input
                type="checkbox"
                checked={draft.isActive}
                onChange={(event) => onChange("isActive", event.target.checked)}
                className="h-4 w-4 rounded border-slate-300 text-indigo-600 focus:ring-indigo-500"
              />
              <div>
                <p className="text-sm font-medium text-slate-900 dark:text-white">Visible en el sitio</p>
                <p className="text-xs text-slate-500 dark:text-slate-400">Si queda apagada, la salida no se muestra al cliente.</p>
              </div>
            </label>
          </div>

          <div className="flex justify-end gap-2 border-t border-slate-200 pt-4 dark:border-slate-800">
            <Button type="button" variant="outline" onClick={onClose}>
              Cancelar
            </Button>
            <Button type="submit">{editing ? "Guardar salida" : "Agregar salida"}</Button>
          </div>
        </form>
      </div>
    </div>
  );
}
