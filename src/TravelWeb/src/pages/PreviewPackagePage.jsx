import { useEffect, useMemo, useState } from "react";
import { MapPinned } from "lucide-react";
import { useLocation, useNavigate, useParams } from "react-router-dom";
import { api } from "../api";
import { PackageEmbedExperience } from "../features/packages/components/PackageEmbedExperience";
import { PackagePreviewShell } from "../features/packages/components/PackagePreviewShell";

export default function PreviewPackagePage() {
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
        const response = await api.get(`/destinations/preview/by-slug/${slug}`);
        if (!cancelled) {
          setPackageData(response);
        }
      } catch {
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
        const response = await api.get(`/countries/preview/by-slug/${countrySlug}`);
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
      pathname: `/preview/packages/${nextPackageSlug}`,
      search: params.toString() ? `?${params.toString()}` : "",
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
              {destination.isPublished ? "" : " - En preparacion"}
            </option>
          ))}
        </select>
      </label>
    );
  }, [countryData, countrySlug, slug]);

  const helperText = useMemo(() => {
    if (!packageData) {
      return "Esta vista interna te permite revisar el diseno final antes de publicarlo en el sitio.";
    }

    if (!packageData.primaryDeparture && (packageData.departures || []).length > 0) {
      return "Estas viendo una salida de borrador para revisar el diseno. Todavia no esta visible en el sitio.";
    }

    if ((packageData.departures || []).length === 0) {
      return "Esta propuesta todavia no tiene salidas cargadas. Puedes revisar el diseno general y completar la disponibilidad despues.";
    }

    return "Esta vista interna simula la experiencia final del sitio. Los formularios no generan consultas reales desde aqui.";
  }, [packageData]);

  return (
    <PackagePreviewShell
      title={packageData?.title || "Vista previa del destino"}
      subtitle={
        packageData?.countryName
          ? `${packageData.destination || "Destino"} | ${packageData.countryName}`
          : "Revisa como se vera este destino en el sitio."
      }
      isPublished={Boolean(packageData?.isPublished)}
      helperText={helperText}
      issues={packageData?.publishIssues || []}
    >
      <PackageEmbedExperience
        packageData={packageData}
        loading={loading}
        embedKey={`preview-package:${slug}:${countrySlug || "direct"}`}
        selector={selector}
        onSubmitLead={async () => {}}
        leadPreviewMode
        previewNotice="Esta es una simulacion interna. Los formularios no generan consultas reales desde la vista previa."
        emptyTitle="Vista previa no disponible"
        emptyDescription="No pudimos preparar esta vista previa todavia. Revisa que el destino exista y vuelve a intentarlo."
      />
    </PackagePreviewShell>
  );
}
