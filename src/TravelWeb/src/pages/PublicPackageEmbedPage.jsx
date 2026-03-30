import { useEffect, useMemo, useState } from "react";
import { MapPinned } from "lucide-react";
import { useLocation, useNavigate, useParams } from "react-router-dom";
import { api } from "../api";
import { PackageEmbedExperience } from "../features/packages/components/PackageEmbedExperience";

export default function PublicPackageEmbedPage({ preview = false }) {
  const { slug = "" } = useParams();
  const location = useLocation();
  const navigate = useNavigate();
  const [loading, setLoading] = useState(true);
  const [packageData, setPackageData] = useState(null);
  const [countryData, setCountryData] = useState(null);
  const countrySlug = useMemo(() => new URLSearchParams(location.search).get("countrySlug") || "", [location.search]);

  useEffect(() => {
    let cancelled = false;

    async function loadPackage() {
      setLoading(true);

      try {
        const response = await api.get(
          preview ? `/destinations/preview/by-slug/${slug}` : `/public/packages/${slug}`
        );
        if (!cancelled) {
          setPackageData(response);
        }
      } catch (error) {
        if (!cancelled) {
          setPackageData(null);
        }
      } finally {
        if (!cancelled) {
          setLoading(false);
        }
      }
    }

    if (slug) {
      loadPackage();
    } else {
      setPackageData(null);
      setLoading(false);
    }

    return () => {
      cancelled = true;
    };
  }, [slug]);

  useEffect(() => {
    let cancelled = false;

    async function loadCountry() {
      try {
        const response = await api.get(
          preview ? `/countries/preview/by-slug/${countrySlug}` : `/public/countries/${countrySlug}`
        );
        if (!cancelled) {
          setCountryData(response);
        }
      } catch {
        if (!cancelled) {
          setCountryData(null);
        }
      }
    }

    if (countrySlug) {
      loadCountry();
    } else {
      setCountryData(null);
    }

    return () => {
      cancelled = true;
    };
  }, [countrySlug]);

  function navigateToPackage(nextPackageSlug) {
    if (!nextPackageSlug || nextPackageSlug === slug) {
      return;
    }

    const params = new URLSearchParams(location.search);
    if (countrySlug) {
      params.set("countrySlug", countrySlug);
    } else {
      params.delete("countrySlug");
    }

    navigate({
      pathname: `${preview ? "/preview" : "/embed"}/packages/${nextPackageSlug}`,
      search: params.toString() ? `?${params.toString()}` : "",
    });
  }

  async function submitLead(payload) {
    await api.post(`/public/packages/${payload.packageSlug}/leads`, {
      fullName: payload.fullName,
      phone: payload.phone,
      email: payload.email,
      message: payload.message,
      website: payload.website,
      departurePublicId: payload.departurePublicId,
    });
  }

  const selector = useMemo(() => {
    const destinations = countryData?.destinations || [];
    if (!countrySlug || destinations.length === 0) {
      return null;
    }

    const selectedValue = destinations.some((item) => item.packageSlug === slug) ? slug : "";

    return (
      <label className="block space-y-2">
        <span className="flex items-center gap-2 text-xs font-bold uppercase tracking-[0.24em] text-teal-700">
          <MapPinned className="h-4 w-4" />
          Destinos en {countryData.countryName || countrySlug}
        </span>
        <select
          value={selectedValue}
          onChange={(event) => navigateToPackage(event.target.value)}
          className="w-full rounded-[1.2rem] border border-[#d7e5e2] bg-[#f8fcfb] px-4 py-3 text-sm font-semibold text-slate-900 outline-none transition focus:border-teal-600 focus:bg-white"
        >
          <option value="" disabled>
            Selecciona un destino
          </option>
          {destinations.map((destination) => (
            <option key={destination.packageSlug} value={destination.packageSlug}>
              {destination.destination}
            </option>
          ))}
        </select>
      </label>
    );
  }, [countryData, countrySlug, slug]);

  return (
    <PackageEmbedExperience
      packageData={packageData}
      loading={loading}
      embedKey={`${preview ? "preview-package" : "package"}:${slug}:${countrySlug || "direct"}`}
      selector={selector}
      onSubmitLead={submitLead}
      leadPreviewMode={preview}
      previewNotice={
        preview
          ? "Esta es una vista previa interna. Cuando el destino este visible en el sitio, las consultas entraran desde aqui."
          : ""
      }
      emptyTitle={preview ? "Vista previa no disponible" : "Paquete no disponible"}
      emptyDescription={
        preview
          ? "Esta propuesta todavia no tiene una salida visible lista para la vista previa o el enlace ya no esta disponible."
          : "Esta propuesta no esta publicada en este momento o el enlace ya no esta disponible."
      }
    />
  );
}
