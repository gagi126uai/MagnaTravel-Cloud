import {
  Building2,
  FileText,
  Settings2,
  Palette,
  Smartphone,
  Sparkles,
  ShieldCheck,
  TerminalSquare,
} from "lucide-react";

// Traduce el `icono` (string) de cada sección en settingsSections.js al componente de
// lucide-react real. Vive separado del módulo de lógica pura a propósito: ese módulo no
// tiene JSX ni depende de una librería de íconos, así se puede testear con node --test
// sin levantar nada de React (ver settingsSections.test.mjs).
export const SETTINGS_ICON_MAP = {
  Building2,
  FileText,
  Settings2,
  Palette,
  Smartphone,
  Sparkles,
  ShieldCheck,
  TerminalSquare,
};
