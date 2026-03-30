import { buildAppUrl } from "../../../api";

export function createClientId() {
  if (typeof crypto !== "undefined" && typeof crypto.randomUUID === "function") {
    return crypto.randomUUID();
  }

  return `${Date.now()}-${Math.random().toString(36).slice(2)}`;
}

export function createDepartureDraft(isPrimary = false) {
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

export function formatMoney(value, currency = "USD") {
  if (value === null || value === undefined || value === "") {
    return "-";
  }

  return `${currency} ${Number(value).toLocaleString("es-AR", {
    minimumFractionDigits: 0,
    maximumFractionDigits: 2,
  })}`;
}

export function formatShortDate(value) {
  if (!value) {
    return "-";
  }

  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? "-" : date.toLocaleDateString("es-AR");
}

export function formatLongDate(value) {
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

function sanitizeEmbedToken(value) {
  return String(value || "")
    .toLowerCase()
    .replace(/[^a-z0-9_-]+/g, "-")
    .replace(/^-+|-+$/g, "")
    .slice(0, 48);
}

function buildPublicationSnippet(item, { publicationType }) {
  const safeTitle = String(item.title || item.name || "Destino").replace(/"/g, "&quot;");
  const safeSlug = sanitizeEmbedToken(item.slug || publicationType);
  const embedId = `mt-${publicationType}-${safeSlug || publicationType}`;
  const fallbackPath =
    publicationType === "country"
      ? `/embed/countries/${item.slug || "pais"}`
      : `/embed/packages/${item.slug || "destino"}`;

  const baseSrc = buildAppUrl(item.publicPagePath || item.countryPagePath || fallbackPath);
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

export function buildCountryPublicationSnippet(country) {
  return buildPublicationSnippet(country, { publicationType: "country" });
}

export function buildDestinationPublicationSnippet(destination) {
  return buildPublicationSnippet(destination, { publicationType: "destination" });
}

export function mapCountryForm(country) {
  return {
    publicId: country?.publicId || null,
    name: country?.name || "",
  };
}

export function mapDestinationForm(detail) {
  return {
    publicId: detail?.publicId || null,
    countryPublicId: detail?.countryPublicId || "",
    countryName: detail?.countryName || "",
    countrySlug: detail?.countrySlug || "",
    name: detail?.name || "",
    title: detail?.title || "",
    slug: detail?.slug || "",
    tagline: detail?.tagline || "",
    displayOrder: detail?.displayOrder ?? 0,
    generalInfo: detail?.generalInfo || "",
    departures: (detail?.departures || []).map((departure) => ({
      clientId: departure.publicId || createClientId(),
      publicId: departure.publicId || null,
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
    })),
    isPublished: Boolean(detail?.isPublished),
    canPublish: Boolean(detail?.canPublish),
    publishIssues: detail?.publishIssues || [],
    hasHeroImage: Boolean(detail?.hasHeroImage),
    heroImageUrl: detail?.heroImageUrl || null,
    heroImageFileName: detail?.heroImageFileName || null,
    fromPrice: detail?.fromPrice ?? null,
    currency: detail?.currency || "USD",
    publicPagePath: detail?.publicPagePath || "",
    countryPagePath: detail?.countryPagePath || "",
  };
}

export function createEmptyDestinationForm(country) {
  return {
    publicId: null,
    countryPublicId: country?.publicId || "",
    countryName: country?.name || "",
    countrySlug: country?.slug || "",
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
    fromPrice: null,
    currency: "USD",
    publicPagePath: "",
    countryPagePath: country?.countryPagePath || "",
  };
}

export function getNextDepartureDate(departures = []) {
  const ordered = [...departures]
    .filter((departure) => departure?.startDate)
    .sort((a, b) => new Date(a.startDate).getTime() - new Date(b.startDate).getTime());

  return ordered[0]?.startDate || null;
}
