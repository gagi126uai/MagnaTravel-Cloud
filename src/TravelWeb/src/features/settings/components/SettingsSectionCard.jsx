import { Link } from "react-router-dom";
import { ChevronRight } from "lucide-react";
import { SETTINGS_ICON_MAP } from "./settingsIconMap";
import { StatusChip } from "../../../components/ui/badge";

// El StatusChip general (components/ui/badge.jsx) ya trae tonos "verde"/"ambar" del
// estándar visual del rollout, pero sus matices por defecto no calzan pixel a pixel con
// el HEX que Gastón firmó puntualmente para estos dos chips (ver "Fuente visual exacta"
// en la spec). En vez de duplicar todo el componente, se pisa el color exacto por
// className: cn()/twMerge se queda con la clase que aparece MÁS TARDE dentro del mismo
// grupo (borde/fondo/texto), así que esto reemplaza el tono sin romper la forma del chip.
const CHIP_TONE_OVERRIDE = {
  verde: "border-emerald-200 bg-emerald-50 text-emerald-700",
  ambar: "border-amber-200 bg-amber-100 text-amber-700",
};

/**
 * Tarjeta de la Portada de Configuración (/settings). Cada tarjeta es un link a una
 * sección (/settings/{slug}) — toda la superficie es clickeable (un <Link> en bloque,
 * no un <div onClick>) para que funcione bien con teclado y lectores de pantalla.
 *
 * @param {object} seccion - un elemento de features/settings/lib/settingsSections.js
 * @param {{texto: string, tono: "verde"|"ambar"}|null} chip - chip de estado ya resuelto
 *   (null = todavía sin dato o esta tarjeta no lleva chip: se ve el chevron a secas).
 */
export default function SettingsSectionCard({ seccion, chip }) {
  const Icono = SETTINGS_ICON_MAP[seccion.icono];

  return (
    <Link
      to={`/settings/${seccion.slug}`}
      className="block rounded-[14px] border border-slate-200 bg-white p-5 shadow-sm transition-all hover:border-slate-300 hover:shadow-md focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/40 focus-visible:ring-offset-2 dark:border-slate-800 dark:bg-slate-900"
    >
      <div className="flex h-10 w-10 items-center justify-center rounded-[10px] bg-blue-50 text-primary dark:bg-blue-950/40">
        {Icono ? <Icono className="h-5 w-5" aria-hidden="true" /> : null}
      </div>

      <div className="mt-3 flex items-center justify-between gap-2">
        <span className="text-[15px] font-bold text-slate-900 dark:text-white">{seccion.titulo}</span>
        {/* Chip o chevron, nunca los dos juntos (§2.5 de la spec): si hay chip real, ese
            chip reemplaza al chevron en esta misma fila. */}
        {chip ? (
          <StatusChip tone={chip.tono} className={CHIP_TONE_OVERRIDE[chip.tono]}>
            {chip.texto}
          </StatusChip>
        ) : (
          <ChevronRight className="h-4 w-4 shrink-0 text-slate-500" aria-hidden="true" />
        )}
      </div>

      <p className="mt-1 text-[13px] leading-relaxed text-slate-500 dark:text-slate-400">
        {seccion.descripcion}
      </p>
    </Link>
  );
}
