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
  Plus,
  Rocket,
  Save,
  Trash2,
  UploadCloud,
  X,
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
const textareaClass = `${inputClass} min-h-[180px] resize-y`;

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
  const [imageFile, setImageFile] = useState(null);
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

  const backPath = form.countryPublicId ? `/packages?country=${form.countryPublicId}` : "/packages";

  function updateField(key, value) {
    setForm((current) => ({ ...current, [key]: value }));
  }

  async function saveDestination(event) {
    event.preventDefault();

    if (!form.countryPublicId) {
      showError("Selecciona un pais antes de guardar.");
      return;
    }

    if (!form.name.trim()) {
      showError("Ingresa el nombre del destino.");
      return;
    }

    if (!form.title.trim()) {
      showError("Ingresa el titulo comercial.");
      return;
    }

    setSaving(true);
    try {
      const payload = {
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

      const saved = isNew
        ? await api.post("/destinations", payload)
        : await api.put(`/destinations/${form.publicId}`, payload);

      setForm(mapDestinationForm(saved));
      showSuccess(isNew ? "Destino creado." : "Destino actualizado.");

      if (isNew) {
        navigate(`/packages/destinations/${saved.publicId}?country=${saved.countryPublicId}`, { replace: true });
      }
    } catch (error) {
      showError(error.message || "No pudimos guardar el destino.");
    } finally {
      setSaving(false);
    }
  }

  async function uploadImage() {
    if (!form.publicId) {
      showError("Guarda el destino antes de cargar la imagen.");
      return;
    }

    if (!imageFile) {
      showError("Selecciona una imagen para continuar.");
      return;
    }

    const payload = new FormData();
    payload.append("file", imageFile);

    setImageUploading(true);
    try {
      const updated = await api.post(`/destinations/${form.publicId}/hero-image`, payload);
      setForm(mapDestinationForm(updated));
      setImageFile(null);
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
        clientId: editingDepartureId || departureDraft.clientId || `${Date.now()}`,
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
      text: "La salida se quitara del destino actual.",
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
      showError("Guarda el destino antes de copiar el codigo para la web.");
      return;
    }

    try {
      await navigator.clipboard.writeText(buildDestinationPublicationSnippet(form));
      showSuccess("Codigo para la web copiado.");
    } catch {
      showError("No pudimos copiar el codigo para la web.");
    }
  }

  function openClientView() {
    if (!form.publicPagePath) {
      showError("Guarda el destino antes de verlo como cliente.");
      return;
    }

    window.open(buildAppUrl(form.publicPagePath), "_blank", "noopener,noreferrer");
  }

  async function publishDestination() {
    if (!form.publicId) {
      showError("Guarda el destino antes de mostrarlo en la web.");
      return;
    }

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
      const updated = await api.patch(`/destinations/${form.publicId}/publish`);
      setForm(mapDestinationForm(updated));
      showSuccess("El destino ya esta visible en la web.");
    } catch (error) {
      showError(error.message || "No pudimos mostrar el destino en la web.");
    }
  }

  async function unpublishDestination() {
    if (!form.publicId) {
      return;
    }

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
      const updated = await api.patch(`/destinations/${form.publicId}/unpublish`);
      setForm(mapDestinationForm(updated));
      showSuccess("El destino dejo de mostrarse en la web.");
    } catch (error) {
      showError(error.message || "No pudimos retirar el destino de la web.");
    }
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
        subtitle="Completa la propuesta comercial, organiza las salidas y revisa la version que vera el cliente."
        actions={
          <div className="flex flex-wrap gap-2">
            <Button variant="outline" onClick={() => navigate(backPath)} className="gap-2">
              <ArrowLeft className="h-4 w-4" />
              Volver
            </Button>
            {canEdit ? (
              <Button onClick={saveDestination} disabled={saving} className="gap-2">
                {saving ? <Loader2 className="h-4 w-4 animate-spin" /> : <Save className="h-4 w-4" />}
                Guardar
              </Button>
            ) : null}
          </div>
        }
      />

      <div className="grid gap-6 xl:grid-cols-[minmax(0,1fr)_320px]">
        <div className="space-y-6">
          <SectionCard
            title="Datos del destino"
            description="Aqui defines el nombre comercial, el nombre visible dentro del pais y el orden de aparicion."
          >
            <form onSubmit={saveDestination} className="grid gap-4 md:grid-cols-2">
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

              <Field label="Texto destacado">
                <input
                  type="text"
                  value={form.tagline}
                  onChange={(event) => updateField("tagline", event.target.value)}
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
            </form>
          </SectionCard>

          <SectionCard
            title="Imagen principal"
            description="Esta imagen se muestra en la portada del destino dentro del sitio."
          >
            <div className="grid gap-5 lg:grid-cols-[280px_minmax(0,1fr)]">
              <div className="overflow-hidden rounded-xl border border-slate-200 bg-slate-50 dark:border-slate-800 dark:bg-slate-950/40">
                {form.heroImageUrl ? (
                  <img src={form.heroImageUrl} alt={form.title || form.name} className="h-56 w-full object-cover" />
                ) : (
                  <div className="flex h-56 flex-col items-center justify-center gap-3 px-4 text-center text-slate-400">
                    <ImagePlus className="h-8 w-8" />
                    <p className="text-sm">Todavia no cargaste una imagen principal.</p>
                  </div>
                )}
              </div>

              <div className="space-y-4">
                <p className="text-sm text-slate-500 dark:text-slate-400">
                  Usa una imagen horizontal y de buena calidad para que el destino se vea prolijo en el sitio.
                </p>

                <input
                  key={imageInputKey}
                  type="file"
                  accept="image/png,image/jpeg,image/webp"
                  onChange={(event) => setImageFile(event.target.files?.[0] || null)}
                  className={inputClass}
                  disabled={!canEdit || !form.publicId}
                />

                {!form.publicId ? (
                  <p className="text-sm text-amber-600 dark:text-amber-300">
                    Guarda el destino primero para poder cargar la imagen.
                  </p>
                ) : null}

                {imageFile ? (
                  <p className="text-sm text-slate-500 dark:text-slate-400">Archivo seleccionado: {imageFile.name}</p>
                ) : form.heroImageFileName ? (
                  <p className="text-sm text-slate-500 dark:text-slate-400">Imagen actual: {form.heroImageFileName}</p>
                ) : null}

                {canEdit ? (
                  <Button onClick={uploadImage} disabled={!imageFile || imageUploading || !form.publicId} className="gap-2">
                    {imageUploading ? <Loader2 className="h-4 w-4 animate-spin" /> : <UploadCloud className="h-4 w-4" />}
                    Cargar imagen
                  </Button>
                ) : null}
              </div>
            </div>
          </SectionCard>

          <SectionCard
            title="Descripcion para la web"
            description="Este contenido aparece en la seccion informativa que vera el cliente."
          >
            <textarea
              value={form.generalInfo}
              onChange={(event) => updateField("generalInfo", event.target.value)}
              className={textareaClass}
              placeholder="Describe lo incluido, el estilo del viaje y la informacion relevante para el cliente."
              disabled={!canEdit}
            />
          </SectionCard>

          <SectionCard
            title="Salidas"
            description="Carga las fechas disponibles, el hotel, la tarifa y el estado de cada salida."
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
              <div className="rounded-xl border border-dashed border-slate-300 px-4 py-10 text-center text-sm text-slate-500 dark:border-slate-700 dark:text-slate-400">
                Todavia no hay salidas cargadas para este destino.
              </div>
            ) : (
              <div className="overflow-x-auto">
                <table className="min-w-full divide-y divide-slate-200 dark:divide-slate-800">
                  <thead className="bg-slate-50 dark:bg-slate-950/40">
                    <tr className="text-left text-xs font-semibold uppercase tracking-wide text-slate-500 dark:text-slate-400">
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
                    {form.departures.map((departure) => (
                      <tr key={departure.clientId}>
                        <td className="px-4 py-3 text-sm font-medium text-slate-900 dark:text-white">
                          {formatLongDate(departure.startDate)}
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
                            {departure.isPrimary ? <StatusPill tone="indigo">Salida destacada</StatusPill> : null}
                            <StatusPill tone={departure.isActive ? "emerald" : "amber"}>
                              {departure.isActive ? "Visible" : "Oculta"}
                            </StatusPill>
                          </div>
                        </td>
                        <td className="px-4 py-3">
                          <div className="flex flex-wrap justify-end gap-2">
                            {canEdit ? (
                              <>
                                <Button variant="outline" size="sm" onClick={() => openEditDeparture(departure)}>
                                  Editar
                                </Button>
                                <Button variant="outline" size="sm" onClick={() => removeDeparture(departure)} className="gap-2">
                                  <Trash2 className="h-4 w-4" />
                                  Quitar
                                </Button>
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
          </SectionCard>
        </div>

        <div className="space-y-6">
          <SectionCard title="Resumen" description="Repasa la informacion principal del destino.">
            <div className="space-y-3">
              <SummaryItem icon={Building2} label="Pais" value={form.countryName || "-"} />
              <SummaryItem icon={CalendarDays} label="Proxima salida" value={formatLongDate(nextDepartureDate)} />
              <SummaryItem
                icon={CheckCircle2}
                label="Precio desde"
                value={fromPrice ? formatMoney(fromPrice.salePrice, fromPrice.currency) : "-"}
              />
            </div>
          </SectionCard>

          {canPublish ? (
            <SectionCard
              title="Publicacion web"
              description="Desde aqui controlas la presencia del destino en el sitio."
            >
              <div className="space-y-4">
                <div className="flex items-center justify-between gap-3">
                  <div>
                    <p className="text-sm font-medium text-slate-700 dark:text-slate-200">Estado actual</p>
                    <p className="mt-1 text-sm text-slate-500 dark:text-slate-400">
                      {form.isPublished ? "El destino esta visible para los clientes." : "El destino todavia no se muestra en la web."}
                    </p>
                  </div>
                  <StatusPill tone={form.isPublished ? "emerald" : "amber"}>
                    {form.isPublished ? "Visible" : "No visible"}
                  </StatusPill>
                </div>

                <div className="flex flex-wrap gap-2">
                  <Button variant="outline" onClick={openClientView} disabled={!form.publicPagePath} className="gap-2">
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
                      Para mostrar este destino en la web falta:
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
                      Retirar de la web
                    </Button>
                  ) : (
                    <Button onClick={publishDestination} disabled={!form.publicId || !form.canPublish} className="gap-2">
                      <Rocket className="h-4 w-4" />
                      Mostrar en la web
                    </Button>
                  )}
                </div>
              </div>
            </SectionCard>
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

function SectionCard({ title, description, action, children }) {
  return (
    <section className="rounded-xl border border-slate-200 bg-white p-5 shadow-sm dark:border-slate-800 dark:bg-slate-900/50">
      <div className="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between">
        <div>
          <h2 className="text-base font-semibold text-slate-900 dark:text-white">{title}</h2>
          {description ? <p className="mt-1 text-sm text-slate-500 dark:text-slate-400">{description}</p> : null}
        </div>
        {action}
      </div>
      <div className="mt-5">{children}</div>
    </section>
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

function SummaryItem({ icon: Icon, label, value }) {
  return (
    <div className="flex items-center gap-3 rounded-lg border border-slate-200 bg-slate-50 px-3 py-3 dark:border-slate-800 dark:bg-slate-950/40">
      <div className="rounded-full bg-white p-2 text-slate-500 shadow-sm dark:bg-slate-900 dark:text-slate-300">
        <Icon className="h-4 w-4" />
      </div>
      <div>
        <p className="text-xs font-semibold uppercase tracking-wide text-slate-500 dark:text-slate-400">{label}</p>
        <p className="text-sm font-medium text-slate-900 dark:text-white">{value}</p>
      </div>
    </div>
  );
}

function StatusPill({ children, tone = "slate" }) {
  const tones = {
    slate: "bg-slate-100 text-slate-700 dark:bg-slate-800 dark:text-slate-200",
    emerald: "bg-emerald-100 text-emerald-700 dark:bg-emerald-900/30 dark:text-emerald-300",
    amber: "bg-amber-100 text-amber-700 dark:bg-amber-900/30 dark:text-amber-300",
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
        <div className="flex items-center justify-between border-b border-slate-200 px-6 py-4 dark:border-slate-800">
          <div>
            <h3 className="text-lg font-semibold text-slate-900 dark:text-white">
              {editing ? "Editar salida" : "Nueva salida"}
            </h3>
            <p className="mt-1 text-sm text-slate-500 dark:text-slate-400">
              Completa la fecha, el alojamiento y la tarifa para esta salida.
            </p>
          </div>
          <button
            type="button"
            onClick={onClose}
            className="rounded-full p-2 text-slate-400 transition hover:bg-slate-100 hover:text-slate-600 dark:hover:bg-slate-800 dark:hover:text-slate-200"
          >
            <X className="h-5 w-5" />
          </button>
        </div>

        <form onSubmit={onSubmit} className="space-y-4 p-6">
          <div className="grid gap-4 md:grid-cols-2 lg:grid-cols-4">
            <Field label="Fecha">
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
                min="1"
                step="0.01"
                value={draft.salePrice}
                onChange={(event) => onChange("salePrice", event.target.value)}
                className={inputClass}
              />
            </Field>
          </div>

          <div className="grid gap-4 md:grid-cols-2">
            <Field label="Transporte">
              <input
                type="text"
                value={draft.transportLabel}
                onChange={(event) => onChange("transportLabel", event.target.value)}
                className={inputClass}
              />
            </Field>
            <Field label="Hotel">
              <input
                type="text"
                value={draft.hotelName}
                onChange={(event) => onChange("hotelName", event.target.value)}
                className={inputClass}
              />
            </Field>
          </div>

          <div className="grid gap-4 md:grid-cols-2">
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
            <label className="flex items-center gap-3 rounded-lg border border-slate-200 px-3 py-3 text-sm text-slate-700 dark:border-slate-800 dark:text-slate-200">
              <input
                type="checkbox"
                checked={Boolean(draft.isPrimary)}
                onChange={(event) => onChange("isPrimary", event.target.checked)}
              />
              Marcar como salida destacada
            </label>

            <label className="flex items-center gap-3 rounded-lg border border-slate-200 px-3 py-3 text-sm text-slate-700 dark:border-slate-800 dark:text-slate-200">
              <input
                type="checkbox"
                checked={Boolean(draft.isActive)}
                onChange={(event) => onChange("isActive", event.target.checked)}
              />
              Mostrar esta salida en la web
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

