import { useEffect, useMemo, useState } from "react";
import { MapPinned } from "lucide-react";
import { useParams } from "react-router-dom";
import { api } from "../api";
import { showError } from "../alerts";
import { PackageEmbedExperience } from "../features/packages/components/PackageEmbedExperience";

function buildSelectedPackage(countryData, selectedPackageSlug) {
  if (!countryData?.packages?.length) {
    return null;
  }

  return (
    countryData.packages.find((item) => item.slug === selectedPackageSlug) ||
    countryData.packages.find((item) => item.slug === countryData.selectedPackageSlug) ||
    countryData.packages[0]
  );
}

export default function PublicCountryEmbedPage() {
  const { countrySlug = "" } = useParams();
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
          setSelectedPackageSlug(response?.selectedPackageSlug || "");
        }
      } catch (error) {
        if (!cancelled) {
          setCountryData(null);
          setSelectedPackageSlug("");
          showError(error.message || "No se pudieron cargar los destinos.");
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

  const selectedPackage = useMemo(
    () => buildSelectedPackage(countryData, selectedPackageSlug),
    [countryData, selectedPackageSlug]
  );

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

  const selector = countryData?.destinations?.length ? (
    <label className="block space-y-2">
      <span className="flex items-center gap-2 text-xs font-bold uppercase tracking-[0.24em] text-teal-700">
        <MapPinned className="h-4 w-4" />
        Destinos en {countryData.countryName || countrySlug}
      </span>
      <select
        value={selectedPackage?.slug || selectedPackageSlug}
        onChange={(event) => setSelectedPackageSlug(event.target.value)}
        className="w-full rounded-[1.2rem] border border-[#d7e5e2] bg-[#f8fcfb] px-4 py-3 text-sm font-semibold text-slate-900 outline-none transition focus:border-teal-600 focus:bg-white"
      >
        {countryData.destinations.map((destination) => (
          <option key={destination.packageSlug} value={destination.packageSlug}>
            {destination.destination}
          </option>
        ))}
      </select>
    </label>
  ) : null;

  return (
    <PackageEmbedExperience
      packageData={selectedPackage}
      loading={loading}
      embedKey={`country:${countrySlug}:${selectedPackage?.slug || selectedPackageSlug}`}
      selector={selector}
      onSubmitLead={submitLead}
      loadingLabel="Cargando destinos..."
      emptyTitle="Pais no disponible"
      emptyDescription="Todavia no hay destinos publicados para este pais o las salidas activas no estan listas para el embed."
    />
  );
}
