import { useEffect, useState } from "react";
import { api } from "../../../api";
import { isAdmin, hasPermission } from "../../../auth";
import SettingsSectionCard from "../components/SettingsSectionCard";
import { agruparSeccionesVisibles, chipWhatsApp, chipFacturacion } from "../lib/settingsSections";

/**
 * Portada de Configuración (`/settings`) — Pantalla 1 del rediseño Mezcla A+B (spec
 * firmada 2026-08-18). Reemplaza a las pestañas horizontales de siempre: entrar a
 * Configuración ahora muestra esta grilla de tarjetas agrupadas, no un formulario
 * directo. Cada tarjeta lleva a su propia sección (`/settings/{slug}`).
 */
export default function SettingsHomePage() {
  const grupos = agruparSeccionesVisibles({ esAdmin: isAdmin(), tienePermiso: hasPermission });

  // Los dos chips de la portada (WhatsApp/Facturación) piden UN dato liviano cada uno.
  // Arrancan en null a propósito: mientras la API no contestó, la tarjeta se ve IGUAL que
  // las que no tienen chip (regla dura §2.4 de la spec — nunca "Cargando...", nunca un
  // chip inventado).
  const [botStatus, setBotStatus] = useState(null);
  const [facturacionEsProduccion, setFacturacionEsProduccion] = useState(null);

  // Efecto con dependencias vacías: corre una sola vez al entrar a la portada. Son dos
  // "fotos" livianas solo para pintar el chip — a diferencia de WhatsAppBotTab (que hace
  // polling cada 5s porque ahí el estado del bot ES el contenido de la pantalla), acá no
  // hace falta refrescar mientras el usuario mira la portada.
  useEffect(() => {
    let cancelado = false;

    api.get("/webhooks/status")
      .then((data) => { if (!cancelado) setBotStatus(data?.status ?? null); })
      .catch(() => { /* Silencio a propósito: sin dato confirmado = sin chip, nunca un error acá. */ });

    api.get("/afip/settings")
      .then((data) => { if (!cancelado) setFacturacionEsProduccion(data?.isProduction ?? null); })
      .catch(() => { /* idem */ });

    return () => { cancelado = true; };
  }, []);

  return (
    <div className="max-w-7xl mx-auto space-y-8 pb-20 md:pb-0">
      <header>
        <h1 className="text-2xl font-bold tracking-tight text-slate-900 dark:text-white">Configuración</h1>
        <p className="mt-1 text-sm text-slate-500 dark:text-slate-400">
          Todo lo que define cómo trabaja tu agencia, junto y de un vistazo. Tocá una tarjeta para entrar.
        </p>
      </header>

      {grupos.map((grupo) => (
        <section key={grupo.grupo} className="space-y-3">
          <p className="text-[11px] font-semibold uppercase tracking-wide text-slate-400">{grupo.grupo}</p>
          <div className="grid grid-cols-1 gap-6 md:grid-cols-3">
            {grupo.items.map((seccion) => (
              <SettingsSectionCard
                key={seccion.slug}
                seccion={seccion}
                chip={
                  seccion.slug === "whatsapp" ? chipWhatsApp(botStatus)
                    : seccion.slug === "facturacion" ? chipFacturacion(facturacionEsProduccion)
                      : null
                }
              />
            ))}
          </div>
        </section>
      ))}
    </div>
  );
}
