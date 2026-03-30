import { useEffect, useMemo, useState } from "react";
import { MapPinned } from "lucide-react";
import { useLocation, useNavigate, useParams } from "react-router-dom";
import { api } from "../api";
import { PackageEmbedExperience } from "../features/packages/components/PackageEmbedExperience";

export default function PublicCountryEmbedPage() {
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
        const response = await api.get(`/public/countries/${countrySlug}`);
        if (!cancelled) {
          setCountryData(response);
          setSelectedPackageSlug("");
        }
      } catch (error) {
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
      pathname: `/embed/packages/${nextPackageSlug}`,
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
            </option>
          ))}
        </select>
      </label>
    );
  }, [countryData, countrySlug, location.search, navigate, selectedPackageSlug]);

  return (
    <PackageEmbedExperience
      packageData={null}
      loading={loading}
      embedKey={`country:${countrySlug}`}
      selector={selector}
      onSubmitLead={async () => {}}
      loadingLabel="Cargando destinos..."
      emptyTitle={countryData ? "Elegi un destino" : "Destinos no disponibles"}
      emptyDescription={
        countryData
          ? "Selecciona un destino del listado para abrir la ficha real del paquete en este mismo iframe."
          : "Todavia no hay destinos publicados para este pais o las salidas activas no estan listas."
      }
    />
  );
}
