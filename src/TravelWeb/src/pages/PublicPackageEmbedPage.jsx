import { useEffect, useState } from "react";
import { useParams } from "react-router-dom";
import { api } from "../api";
import { showError } from "../alerts";
import { PackageEmbedExperience } from "../features/packages/components/PackageEmbedExperience";

export default function PublicPackageEmbedPage() {
  const { slug = "" } = useParams();
  const [loading, setLoading] = useState(true);
  const [packageData, setPackageData] = useState(null);

  useEffect(() => {
    let cancelled = false;

    async function loadPackage() {
      setLoading(true);

      try {
        const response = await api.get(`/public/packages/${slug}`);
        if (!cancelled) {
          setPackageData(response);
        }
      } catch (error) {
        if (!cancelled) {
          setPackageData(null);
          showError(error.message || "No se pudo cargar el paquete.");
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

  return (
    <PackageEmbedExperience
      packageData={packageData}
      loading={loading}
      embedKey={`package:${slug}`}
      onSubmitLead={submitLead}
    />
  );
}
