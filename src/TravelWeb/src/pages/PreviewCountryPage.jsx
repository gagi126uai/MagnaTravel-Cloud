import { useEffect, useMemo, useState } from "react";
import { MapPinned } from "lucide-react";
import { useLocation, useNavigate, useParams } from "react-router-dom";
import { api } from "../api";
import { PackageEmbedExperience } from "../features/packages/components/PackageEmbedExperience";
import { PackagePreviewShell } from "../features/packages/components/PackagePreviewShell";

export default function PreviewCountryPage() {
  const { countrySlug = "" } = useParams();
  const location = useLocation();
  const navigate = useNavigate();
  const [loading, setLoading] = useState(true);
  const [countryData, setCountryData] = useState(null);
  const [selectedPackageSlug, setSelectedPackageSlug] = useState("");

  useEffect(() => {
    let cancelled = false;

    async function loadCountry() {
      setLoading(true);

      try {
        const response = await api.get(`/countries/preview/by-slug/${countrySlug}`);
        if (!cancelled) {
          setCountryData(response);
          setSelectedPackageSlug("");
        }
      } catch {
        if (!cancelled) {
          setCountryData(null);
          setSelectedPackageSlug("");
        }
      } finally {
        if (!cancelled) {
          setLoading(false);
        }
      }
    }

    if (countrySlug) {
      loadCountry();
    } else {
      setCountryData(null);
      setSelectedPackageSlug("");
      setLoading(false);
    }

    return () => {
      cancelled = true;
    };
  }, [countrySlug]);

  function navigateToPackage(nextPackageSlug) {
    if (!nextPackageSlug) {
      return;
    }

    const params = new URLSearchParams(location.search);
    params.set("countrySlug", countrySlug);

    navigate({
      pathname: `/preview/packages/${nextPackageSlug}`,
      search: params.toString() ? `?${params.toString()}` : "",
    });
  }

  const selector = useMemo(() => {
    const destinations = countryData?.destinations || [];
    if (destinations.length === 0) {
      return null;
    }

    return (
      <label className="block space-y-2">
        <span className="flex items-center gap-2 text-xs font-bold uppercase tracking-[0.24em] text-teal-700">
          <MapPinned className="h-4 w-4" />
          Destinos en {countryData.countryName || countrySlug}
        </span>
        <select
          value={selectedPackageSlug}
          onChange={(event) => {
            const nextValue = event.target.value;
            setSelectedPackageSlug(nextValue);
            navigateToPackage(nextValue);
          }}
          className="w-full rounded-[1.2rem] border border-[#d7e5e2] bg-[#f8fcfb] px-4 py-3 text-sm font-semibold text-slate-900 outline-none transition focus:border-teal-600 focus:bg-white"
        >
          <option value="">Selecciona un destino</option>
          {destinations.map((destination) => (
            <option key={destination.packageSlug} value={destination.packageSlug}>
              {destination.destination}
              {destination.isPublished ? "" : " - En preparacion"}
            </option>
          ))}
        </select>
      </label>
    );
  }, [countryData, countrySlug, selectedPackageSlug]);

  const destinations = countryData?.destinations || [];
  const publishedCount = destinations.filter((item) => item.isPublished).length;

  return (
    <PackagePreviewShell
      title={countryData?.countryName || "Vista previa del pais"}
      subtitle={
        countryData
          ? `${destinations.length} destinos cargados | ${publishedCount} visibles en el sitio`
          : "Revisa como se vera la seleccion de destinos antes de publicarla."
      }
      isPublished={publishedCount > 0}
      helperText="Selecciona un destino para abrir la ficha interna. Esta vista te permite revisar el orden y la navegacion del pais en el sitio."
      issues={[]}
    >
      <PackageEmbedExperience
        packageData={null}
        loading={loading}
        embedKey={`preview-country:${countrySlug}`}
        selector={selector}
        onSubmitLead={async () => {}}
        leadPreviewMode
        loadingLabel="Cargando destinos..."
        emptyTitle={countryData ? "Selecciona un destino" : "Vista previa no disponible"}
        emptyDescription={
          countryData
            ? "Este pais todavia no tiene destinos listos para revisar. Crea o completa un destino y vuelve a intentarlo."
            : "No pudimos preparar la vista previa del pais. Verifica el enlace y vuelve a intentarlo."
        }
      />
    </PackagePreviewShell>
  );
}
