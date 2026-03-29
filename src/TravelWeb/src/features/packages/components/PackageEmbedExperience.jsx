import { useEffect, useMemo, useRef, useState } from "react";
import {
  CalendarDays,
  ChevronRight,
  Loader2,
  Mail,
  Phone,
  Plane,
  Send,
  User,
} from "lucide-react";
import { showError, showSuccess } from "../../../alerts";

const leadFormInitialState = {
  fullName: "",
  phone: "",
  email: "",
  message: "",
  website: "",
};

function formatDate(value) {
  if (!value) {
    return "-";
  }

  const date = new Date(value);
  return Number.isNaN(date.getTime())
    ? "-"
    : date.toLocaleDateString("es-AR", {
        day: "numeric",
        month: "long",
        year: "numeric",
      });
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

function getElementHeight(element) {
  if (!element) {
    return 0;
  }

  return Math.max(
    element.scrollHeight ?? 0,
    element.offsetHeight ?? 0,
    Math.ceil(element.getBoundingClientRect?.().height ?? 0)
  );
}

function getEmbedDocumentHeight(contentElement) {
  if (typeof document === "undefined") {
    return 0;
  }

  const baseHeight = Math.max(
    getElementHeight(contentElement),
    getElementHeight(document.body?.firstElementChild),
    getElementHeight(document.body)
  );

  const modalPanel = document.querySelector("[data-embed-modal-panel]");
  if (!modalPanel) {
    return baseHeight;
  }

  return Math.max(baseHeight, getElementHeight(modalPanel) + 96);
}

export function PackageEmbedExperience({
  packageData,
  loading,
  embedKey,
  selector = null,
  onSubmitLead,
  loadingLabel = "Cargando paquete...",
  emptyTitle = "Paquete no disponible",
  emptyDescription = "Esta ficha todavia no esta publicada o ya no tiene una salida principal activa.",
}) {
  const isEmbedded = typeof window !== "undefined" && window.parent !== window;
  const [submitting, setSubmitting] = useState(false);
  const [activeTab, setActiveTab] = useState("departures");
  const [leadOpen, setLeadOpen] = useState(false);
  const [selectedDeparture, setSelectedDeparture] = useState(null);
  const [leadForm, setLeadForm] = useState(leadFormInitialState);
  const contentRef = useRef(null);
  const embedId = useMemo(() => {
    if (typeof window === "undefined") {
      return "";
    }

    return new URLSearchParams(window.location.search).get("embedId") || "";
  }, []);

  useEffect(() => {
    if (typeof document === "undefined") {
      return undefined;
    }

    const root = document.documentElement;
    const body = document.body;
    const previousRootClassName = root.className;
    const previousRootBackground = root.style.background;
    const previousBodyBackground = body.style.background;
    const previousBodyColor = body.style.color;

    root.classList.remove("dark");
    root.classList.add("light");
    root.style.background = "#eef6f3";
    body.style.background = "#eef6f3";
    body.style.color = "#0f172a";

    return () => {
      root.className = previousRootClassName;
      root.style.background = previousRootBackground;
      body.style.background = previousBodyBackground;
      body.style.color = previousBodyColor;
    };
  }, []);

  useEffect(() => {
    setLeadOpen(false);
    setSelectedDeparture(null);
    setLeadForm(leadFormInitialState);
  }, [packageData?.slug]);

  const primaryDeparture = packageData?.primaryDeparture || null;
  const departures = packageData?.departures || [];
  const departuresSorted = useMemo(
    () =>
      [...departures].sort((left, right) => {
        const leftDate = new Date(left.startDate);
        const rightDate = new Date(right.startDate);
        return leftDate - rightDate;
      }),
    [departures]
  );

  const priceLabel = useMemo(() => {
    if (!packageData) {
      return "-";
    }

    return formatMoney(packageData.fromPrice, packageData.currency);
  }, [packageData]);

  const dateFact = useMemo(() => {
    const firstAvailableDeparture = departuresSorted[0] || primaryDeparture;
    if (!firstAvailableDeparture) {
      return { label: "Fecha", value: "-" };
    }

    if (departuresSorted.length > 1) {
      return {
        label: "Fechas disponibles",
        value: `Desde el ${formatDate(firstAvailableDeparture.startDate)}`,
      };
    }

    return {
      label: "Fecha",
      value: formatDate(firstAvailableDeparture.startDate),
    };
  }, [departuresSorted, primaryDeparture]);

  useEffect(() => {
    if (typeof window === "undefined" || window.parent === window) {
      return undefined;
    }

    let animationFrameId = 0;
    const postHeight = () => {
      animationFrameId = 0;
      window.parent.postMessage(
        {
          type: "magnatravel:embed:resize",
          key: embedKey,
          slug: packageData?.slug,
          embedId,
          height: getEmbedDocumentHeight(contentRef.current),
        },
        "*"
      );
    };

    const scheduleHeightSync = () => {
      if (animationFrameId) {
        window.cancelAnimationFrame(animationFrameId);
      }

      animationFrameId = window.requestAnimationFrame(postHeight);
    };

    scheduleHeightSync();

    const resizeObserver =
      typeof ResizeObserver === "undefined"
        ? null
        : new ResizeObserver(() => {
            scheduleHeightSync();
          });

    if (resizeObserver) {
      resizeObserver.observe(document.body);
      resizeObserver.observe(document.documentElement);
    }

    const mutationObserver = new MutationObserver(() => {
      scheduleHeightSync();
    });

    mutationObserver.observe(document.body, {
      childList: true,
      subtree: true,
      attributes: true,
      characterData: true,
    });

    window.addEventListener("load", scheduleHeightSync);
    window.addEventListener("resize", scheduleHeightSync);

    return () => {
      if (animationFrameId) {
        window.cancelAnimationFrame(animationFrameId);
      }

      resizeObserver?.disconnect();
      mutationObserver.disconnect();
      window.removeEventListener("load", scheduleHeightSync);
      window.removeEventListener("resize", scheduleHeightSync);
    };
  }, [activeTab, embedId, embedKey, leadOpen, loading, packageData, selectedDeparture, submitting]);

  function openLeadModal(departure = null) {
    setSelectedDeparture(departure || primaryDeparture || null);
    setLeadForm(leadFormInitialState);
    setLeadOpen(true);
  }

  function closeLeadModal() {
    setLeadOpen(false);
    setLeadForm(leadFormInitialState);
  }

  function updateLeadField(key, value) {
    setLeadForm((previous) => ({ ...previous, [key]: value }));
  }

  async function submitLead(event) {
    event.preventDefault();

    if (!selectedDeparture?.publicId) {
      showError("Selecciona una fecha desde la tabla antes de enviar la consulta.");
      return;
    }

    if (!leadForm.fullName.trim()) {
      showError("El nombre es obligatorio.");
      return;
    }

    if (!leadForm.phone.trim()) {
      showError("El telefono es obligatorio.");
      return;
    }

    setSubmitting(true);

    try {
      await onSubmitLead({
        packageSlug: packageData?.slug || "",
        fullName: leadForm.fullName.trim(),
        phone: leadForm.phone.trim(),
        email: leadForm.email.trim() || null,
        message: leadForm.message.trim() || null,
        website: leadForm.website,
        departurePublicId: selectedDeparture?.publicId || null,
      });

      showSuccess("Recibimos tu consulta y un asesor te contactara pronto.");
      closeLeadModal();
    } catch (error) {
      showError(error.message || "No se pudo enviar la consulta.");
    } finally {
      setSubmitting(false);
    }
  }

  if (loading) {
    return (
      <div
        className={`flex items-center justify-center bg-[linear-gradient(180deg,#f6fbfa_0%,#eff7f5_100%)] px-4 ${
          isEmbedded ? "min-h-0 py-6" : "min-h-screen"
        }`}
      >
        <div className="flex items-center gap-3 rounded-3xl bg-white px-6 py-5 text-slate-700 shadow-xl shadow-slate-200/70">
          <Loader2 className="h-5 w-5 animate-spin text-teal-700" />
          {loadingLabel}
        </div>
      </div>
    );
  }

  if (!packageData || !primaryDeparture) {
    return (
      <div
        className={`flex items-center justify-center bg-[linear-gradient(180deg,#f6fbfa_0%,#eff7f5_100%)] px-4 ${
          isEmbedded ? "min-h-0 py-6" : "min-h-screen"
        }`}
      >
        <div className="w-full max-w-2xl rounded-[2rem] border border-white/80 bg-white px-8 py-10 text-center shadow-[0_24px_60px_-28px_rgba(15,23,42,0.35)]">
          {selector ? <div className="mx-auto mb-6 max-w-lg text-left">{selector}</div> : null}
          <p className="text-xs font-bold uppercase tracking-[0.26em] text-teal-700">Embed</p>
          <h1 className="mt-3 text-3xl font-bold tracking-tight text-slate-950">{emptyTitle}</h1>
          <p className="mt-3 text-sm leading-6 text-slate-500">{emptyDescription}</p>
        </div>
      </div>
    );
  }

  return (
    <>
      <div
        ref={contentRef}
        className={`bg-[radial-gradient(circle_at_top_left,rgba(111,210,214,0.18),transparent_28%),linear-gradient(180deg,#f6fbfa_0%,#eef6f3_100%)] px-3 py-4 sm:px-5 sm:py-6 ${
          isEmbedded ? "min-h-0" : "min-h-screen"
        }`}
      >
        <div className="mx-auto max-w-[1180px]">
          <div className="overflow-hidden rounded-[2rem] border border-white/80 bg-white shadow-[0_28px_70px_-34px_rgba(15,23,42,0.4)]">
            <div className="grid gap-6 px-4 py-4 sm:px-6 sm:py-6 lg:grid-cols-[minmax(0,1.55fr)_390px]">
              <div className="overflow-hidden rounded-[1.6rem] bg-slate-100">
                {packageData.heroImageUrl ? (
                  <img src={packageData.heroImageUrl} alt={packageData.title} className="h-full min-h-[320px] w-full object-cover" />
                ) : (
                  <div className="flex min-h-[320px] items-center justify-center bg-[linear-gradient(135deg,#7ed5d6_0%,#d7f0ea_100%)] text-center text-slate-700">
                    <div>
                      <p className="text-xs font-bold uppercase tracking-[0.3em]">MagnaTravel</p>
                      <p className="mt-3 text-3xl font-bold">{packageData.title}</p>
                    </div>
                  </div>
                )}
              </div>

              <aside className="flex flex-col">
                {selector ? <div className="mb-5">{selector}</div> : null}

                <div>
                  <p className="text-xs font-bold uppercase tracking-[0.28em] text-teal-700">
                    {packageData.destination || "Paquete destacado"}
                  </p>
                  <h1 className="mt-2 text-4xl font-black tracking-tight text-slate-950">{packageData.title}</h1>
                  <p className="mt-2 text-lg leading-7 text-slate-600">
                    {packageData.tagline || "Vivilo con fechas y tarifas listas para reservar."}
                  </p>
                </div>

                <div className="mt-6">
                  <p className="text-sm font-semibold uppercase tracking-[0.24em] text-slate-400">Desde</p>
                  <p className="mt-1 text-5xl font-black tracking-tight text-[#9fd7d2]">{priceLabel}</p>
                </div>

                <div className="mt-6 space-y-3">
                  <QuickFact label="Transporte" value={primaryDeparture.transportLabel} icon={Plane} />
                  <QuickFact label="Noches" value={`${primaryDeparture.nights} noches`} icon={CalendarDays} />
                  <QuickFact label="Regimen" value={primaryDeparture.mealPlan} icon={ChevronRight} />
                  <QuickFact label={dateFact.label} value={dateFact.value} icon={CalendarDays} />
                </div>

                <div className="mt-6 rounded-[1.2rem] border border-[#d8e8e5] bg-[#f5fbf8] px-4 py-4 text-sm text-slate-600">
                  Selecciona una fecha en la tabla de abajo para enviar la consulta con la salida elegida.
                </div>
              </aside>
            </div>

            <div className="border-t border-slate-200 px-4 sm:px-6">
              <div className="flex flex-wrap gap-2 pt-4">
                <TabButton
                  active={activeTab === "departures"}
                  onClick={() => setActiveTab("departures")}
                  label="Fechas y tarifas"
                />
                <TabButton
                  active={activeTab === "info"}
                  onClick={() => setActiveTab("info")}
                  label="Informacion general"
                />
              </div>
            </div>

            <div className="px-4 py-5 sm:px-6 sm:py-6">
              {activeTab === "departures" ? (
                <div className="space-y-4">
                  <div>
                    <h2 className="text-2xl font-bold tracking-tight text-slate-950">Fechas y tarifas disponibles</h2>
                    <p className="mt-1 text-sm text-slate-500">
                      Elige una salida y dejanos tus datos para que el equipo te contacte con el detalle comercial.
                    </p>
                  </div>

                  <div className="hidden overflow-hidden rounded-[1.4rem] border border-slate-200 md:block">
                    <table className="min-w-full divide-y divide-slate-200">
                      <thead className="bg-slate-50">
                        <tr className="text-left text-xs font-bold uppercase tracking-wider text-slate-500">
                          <th className="px-4 py-3">Fecha</th>
                          <th className="px-4 py-3">Noches</th>
                          <th className="px-4 py-3">Hotel</th>
                          <th className="px-4 py-3">Regimen</th>
                          <th className="px-4 py-3">Base</th>
                          <th className="px-4 py-3">Tarifa</th>
                          <th className="px-4 py-3 text-right">Accion</th>
                        </tr>
                      </thead>
                      <tbody className="divide-y divide-slate-100 bg-white">
                        {departures.map((departure) => (
                          <tr key={departure.publicId}>
                            <td className="px-4 py-4 text-sm font-medium text-slate-900">{formatDate(departure.startDate)}</td>
                            <td className="px-4 py-4 text-sm text-slate-600">{departure.nights}</td>
                            <td className="px-4 py-4 text-sm text-slate-600">{departure.hotelName}</td>
                            <td className="px-4 py-4 text-sm text-slate-600">{departure.mealPlan}</td>
                            <td className="px-4 py-4 text-sm text-slate-600">{departure.roomBase}</td>
                            <td className="px-4 py-4 text-sm font-bold text-slate-900">
                              {formatMoney(departure.salePrice, departure.currency)}
                            </td>
                            <td className="px-4 py-4 text-right">
                              <button
                                type="button"
                                onClick={() => openLeadModal(departure)}
                                className="inline-flex items-center rounded-full bg-[#0f4d5b] px-3.5 py-2 text-xs font-bold text-white transition hover:bg-[#0d4350]"
                              >
                                Solicitar reserva
                              </button>
                            </td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>

                  <div className="grid gap-3 md:hidden">
                    {departures.map((departure) => (
                      <div key={departure.publicId} className="rounded-[1.4rem] border border-slate-200 bg-white p-4 shadow-sm">
                        <div className="flex items-start justify-between gap-4">
                          <div>
                            <p className="text-xs font-bold uppercase tracking-[0.24em] text-teal-700">
                              {formatDate(departure.startDate)}
                            </p>
                            <p className="mt-2 text-lg font-bold text-slate-950">{departure.hotelName}</p>
                            <p className="mt-1 text-sm text-slate-500">
                              {departure.nights} noches · {departure.mealPlan} · {departure.roomBase}
                            </p>
                          </div>
                          <p className="text-base font-black text-slate-950">
                            {formatMoney(departure.salePrice, departure.currency)}
                          </p>
                        </div>

                        <button
                          type="button"
                          onClick={() => openLeadModal(departure)}
                          className="mt-4 inline-flex w-full items-center justify-center rounded-full bg-[#0f4d5b] px-4 py-3 text-sm font-bold text-white transition hover:bg-[#0d4350]"
                        >
                          Solicitar reserva
                        </button>
                      </div>
                    ))}
                  </div>
                </div>
              ) : (
                <div className="rounded-[1.6rem] border border-slate-200 bg-[linear-gradient(180deg,#fcfffe_0%,#f5fbf8_100%)] p-5 sm:p-6">
                  <h2 className="text-2xl font-bold tracking-tight text-slate-950">Informacion general</h2>
                  <p className="mt-4 whitespace-pre-line text-sm leading-7 text-slate-600">
                    {packageData.generalInfo || "Sin informacion adicional por el momento."}
                  </p>
                </div>
              )}
            </div>
          </div>
        </div>
      </div>

      <LeadModal
        open={leadOpen}
        packageTitle={packageData.title}
        departure={selectedDeparture}
        leadForm={leadForm}
        submitting={submitting}
        onClose={closeLeadModal}
        onChange={updateLeadField}
        onSubmit={submitLead}
      />
    </>
  );
}

function QuickFact({ label, value, icon: Icon }) {
  return (
    <div className="rounded-[1.2rem] bg-[#ede5de] px-4 py-3">
      <div className="flex items-start gap-3">
        <div className="rounded-full bg-white/70 p-2 text-[#0f4d5b]">
          <Icon className="h-4 w-4" />
        </div>
        <div>
          <p className="text-[11px] font-bold uppercase tracking-[0.22em] text-slate-500">{label}</p>
          <p className="mt-1 text-sm font-semibold text-slate-900">{value || "-"}</p>
        </div>
      </div>
    </div>
  );
}

function TabButton({ active, onClick, label }) {
  return (
    <button
      type="button"
      onClick={onClick}
      className={`rounded-t-2xl px-4 py-3 text-sm font-semibold transition ${
        active
          ? "border-b-2 border-[#0f4d5b] text-[#0f4d5b]"
          : "border-b-2 border-transparent text-slate-500 hover:text-slate-700"
      }`}
    >
      {label}
    </button>
  );
}

function LeadModal({ open, packageTitle, departure, leadForm, submitting, onClose, onChange, onSubmit }) {
  if (!open) {
    return null;
  }

  return (
    <div data-embed-modal-root className="fixed inset-0 z-50 overflow-y-auto bg-slate-950/60 p-4 backdrop-blur-sm">
      <div className="mx-auto flex min-h-full max-w-2xl items-center justify-center">
        <div
          data-embed-modal-panel
          className="w-full overflow-hidden rounded-[2rem] bg-white shadow-[0_30px_80px_-30px_rgba(15,23,42,0.55)]"
        >
          <div className="border-b border-slate-200 px-6 py-5">
            <p className="text-xs font-bold uppercase tracking-[0.26em] text-teal-700">Consulta web</p>
            <h2 className="mt-2 text-2xl font-bold tracking-tight text-slate-950">Solicitar informacion</h2>
            <p className="mt-2 text-sm text-slate-500">
              {packageTitle}
              {departure ? ` · ${formatDate(departure.startDate)} · ${formatMoney(departure.salePrice, departure.currency)}` : ""}
            </p>
          </div>

          <form onSubmit={onSubmit} className="space-y-4 px-6 py-6">
            <div className="grid gap-4 md:grid-cols-2">
              <Field label="Nombre y apellido" icon={User}>
                <input
                  type="text"
                  value={leadForm.fullName}
                  onChange={(event) => onChange("fullName", event.target.value)}
                  placeholder="Tu nombre"
                  className="w-full rounded-2xl border border-slate-200 bg-slate-50 px-4 py-3 text-sm text-slate-900 outline-none transition focus:border-teal-600 focus:bg-white"
                />
              </Field>

              <Field label="Telefono" icon={Phone}>
                <input
                  type="tel"
                  value={leadForm.phone}
                  onChange={(event) => onChange("phone", event.target.value)}
                  placeholder="+54 9 ..."
                  className="w-full rounded-2xl border border-slate-200 bg-slate-50 px-4 py-3 text-sm text-slate-900 outline-none transition focus:border-teal-600 focus:bg-white"
                />
              </Field>
            </div>

            <Field label="Email" icon={Mail}>
              <input
                type="email"
                value={leadForm.email}
                onChange={(event) => onChange("email", event.target.value)}
                placeholder="tu@email.com"
                className="w-full rounded-2xl border border-slate-200 bg-slate-50 px-4 py-3 text-sm text-slate-900 outline-none transition focus:border-teal-600 focus:bg-white"
              />
            </Field>

            <Field label="Comentario">
              <textarea
                value={leadForm.message}
                onChange={(event) => onChange("message", event.target.value)}
                placeholder="Si quieres, puedes contarnos mas sobre tu viaje."
                className="min-h-[120px] w-full rounded-2xl border border-slate-200 bg-slate-50 px-4 py-3 text-sm text-slate-900 outline-none transition focus:border-teal-600 focus:bg-white"
              />
            </Field>

            <input
              type="text"
              value={leadForm.website}
              onChange={(event) => onChange("website", event.target.value)}
              tabIndex={-1}
              autoComplete="off"
              className="sr-only"
              aria-hidden="true"
            />

            <div className="flex flex-col gap-3 pt-2 sm:flex-row sm:justify-end">
              <button
                type="button"
                onClick={onClose}
                className="rounded-full border border-slate-200 px-5 py-3 text-sm font-semibold text-slate-600 transition hover:bg-slate-50"
              >
                Cancelar
              </button>
              <button
                type="submit"
                disabled={submitting}
                className="inline-flex items-center justify-center gap-2 rounded-full bg-[#0f4d5b] px-6 py-3 text-sm font-bold text-white transition hover:bg-[#0d4350] disabled:cursor-not-allowed disabled:opacity-60"
              >
                {submitting ? <Loader2 className="h-4 w-4 animate-spin" /> : <Send className="h-4 w-4" />}
                Enviar consulta
              </button>
            </div>
          </form>
        </div>
      </div>
    </div>
  );
}

function Field({ label, icon: Icon, children }) {
  return (
    <label className="block space-y-2">
      <span className="flex items-center gap-2 text-sm font-semibold text-slate-700">
        {Icon ? <Icon className="h-4 w-4 text-teal-700" /> : null}
        {label}
      </span>
      {children}
    </label>
  );
}
