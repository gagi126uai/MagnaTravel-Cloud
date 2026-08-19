// Reglas de presentacion del rediseño de Configuración (Mezcla A+B, spec firmada
// 2026-08-18, docs/ux/2026-08-18-spec-rediseno-configuracion-mezcla-a-b.md).
//
// Todo lo que decide QUE tarjeta/item se ve, en QUE orden, y QUE chip lleva vive acá,
// separado de los componentes visuales — mismo criterio que ya usa la Inteligencia
// artificial (features/ai-settings/lib/aiSettingsPresentation.js): se puede probar con
// tests simples (sin renderizar React) y los componentes no se llenan de "if" repetidos.

import { puedeVerConfiguracionIa } from "../../ai-settings/lib/aiSettingsPresentation.js";

// ─── Grupos de la portada y del menú lateral (mismo orden en las dos pantallas, §3.2) ──

export const SETTINGS_GROUPS = {
  TU_EMPRESA: "TU EMPRESA",
  LO_QUE_VE_EL_CLIENTE: "LO QUE VE EL CLIENTE",
  REGLAS_Y_SISTEMA: "REGLAS Y SISTEMA",
};

// ─── Las 8 secciones, tabla §2.1/§3.1 de la spec — orden EXACTO firmado ────────────────
// `icono` es solo el nombre (string) del ícono de lucide-react: este módulo es lógica
// pura sin JSX, el componente visual es quien lo traduce al ícono real (ver
// components/settingsIconMap.js). `descripcion` se usa dos veces sin reescribirla: como
// texto de la tarjeta en la portada Y como bajada de la cabecera de sección (§3.4, "texto
// idéntico a la tarjeta, no se inventa copy nuevo").
export const SETTINGS_SECTIONS = [
  {
    slug: "agencia",
    grupo: SETTINGS_GROUPS.TU_EMPRESA,
    titulo: "Agencia",
    descripcion: "Nombre, CUIT, legajo, dirección y las cuentas donde te depositan.",
    icono: "Building2",
  },
  {
    slug: "facturacion",
    grupo: SETTINGS_GROUPS.TU_EMPRESA,
    titulo: "Facturación",
    descripcion: "Punto de venta, certificados de ARCA y cómo salen tus comprobantes.",
    icono: "FileText",
  },
  {
    slug: "operativa-caja",
    grupo: SETTINGS_GROUPS.TU_EMPRESA,
    titulo: "Operativa y Caja",
    descripcion: "Frenos de plata, avisos de deuda y las reglas del día a día.",
    icono: "Settings2",
  },
  {
    slug: "presupuestos-pdf",
    grupo: SETTINGS_GROUPS.LO_QUE_VE_EL_CLIENTE,
    titulo: "Presupuestos y PDF",
    descripcion: "Tu logo, los colores y las condiciones que salen en cada presupuesto.",
    icono: "Palette",
  },
  {
    slug: "whatsapp",
    grupo: SETTINGS_GROUPS.LO_QUE_VE_EL_CLIENTE,
    titulo: "WhatsApp Bot",
    descripcion: "El número conectado y los mensajes con los que atiende consultas.",
    icono: "Smartphone",
  },
  {
    slug: "ia",
    grupo: SETTINGS_GROUPS.LO_QUE_VE_EL_CLIENTE,
    titulo: "Inteligencia artificial",
    descripcion: "El ayudante que sugiere textos y evita cargar cosas dos veces.",
    icono: "Sparkles",
    // Misma regla de siempre (§15.1, ya en SettingsPage.jsx original): solo Admin, ni
    // apagada para el resto — puedeVerConfiguracionIa es la función compartida y testeada.
    adminOnly: true,
  },
  {
    slug: "aprobaciones",
    grupo: SETTINGS_GROUPS.REGLAS_Y_SISTEMA,
    titulo: "Workflows de aprobación",
    descripcion: "Qué cosas necesitan tu OK antes de salir.",
    icono: "ShieldCheck",
    requiredPermission: "approvals.policies",
  },
  {
    slug: "logs",
    grupo: SETTINGS_GROUPS.REGLAS_Y_SISTEMA,
    titulo: "Logs y Programación",
    descripcion: "El detrás de escena: registros del sistema y tareas programadas.",
    icono: "TerminalSquare",
    // Ojo: en el código de hoy (SettingsPage.jsx, isTabVisible) "logs" se chequea con
    // isAdmin() DIRECTO, en una rama separada de "adminOnly" (que usa
    // puedeVerConfiguracionIa). Hoy las dos reglas dan el mismo resultado, pero se
    // conserva la distinción para no mezclar dos reglas de negocio que el dueño podría
    // hacer divergir mañana (ej. si IA se abriera a otro rol y Logs siguiera admin-estricto).
    logsAdminEstricto: true,
  },
];

/**
 * ¿Esta sección la puede ver el usuario logueado? Reutiliza EXACTAMENTE la misma regla
 * que ya vivía en SettingsPage.jsx (isTabVisible) — esta obra no cambia ni una coma de
 * los permisos, solo cómo se navega entre pantallas.
 *
 * @param {object} seccion - un elemento de SETTINGS_SECTIONS
 * @param {{ esAdmin: boolean, tienePermiso: (permiso: string) => boolean }} contexto
 * @returns {boolean}
 */
export function esSeccionVisible(seccion, contexto) {
  const { esAdmin, tienePermiso } = contexto;

  if (seccion.logsAdminEstricto) {
    return esAdmin === true;
  }
  if (seccion.adminOnly) {
    return puedeVerConfiguracionIa(esAdmin);
  }
  if (seccion.requiredPermission) {
    return tienePermiso ? tienePermiso(seccion.requiredPermission) : false;
  }
  return true;
}

/**
 * Arma los grupos (portada Y menú lateral usan esta misma función, §3.2) filtrando las
 * secciones que el usuario no puede ver. Regla dura de la spec: un grupo entero
 * desaparece (encabezado incluido) si no le queda ningún ítem visible — nunca se muestra
 * un grupo vacío ni un ítem apagado/con candado (P-9).
 *
 * @param {{ esAdmin: boolean, tienePermiso: (permiso: string) => boolean }} contexto
 * @returns {Array<{ grupo: string, items: object[] }>}
 */
export function agruparSeccionesVisibles(contexto) {
  const grupos = [];
  const indicePorGrupo = new Map();

  for (const seccion of SETTINGS_SECTIONS) {
    if (!esSeccionVisible(seccion, contexto)) continue;

    if (!indicePorGrupo.has(seccion.grupo)) {
      indicePorGrupo.set(seccion.grupo, grupos.length);
      grupos.push({ grupo: seccion.grupo, items: [] });
    }
    grupos[indicePorGrupo.get(seccion.grupo)].items.push(seccion);
  }

  return grupos;
}

/**
 * Resuelve un slug de la URL (`/settings/{slug}`) a su sección, SOLO si existe y el
 * usuario la puede ver. Un slug inventado o una sección sin permiso devuelven `null` —
 * quien llama decide qué hacer (la página redirige a la Portada, §6 de la spec: "no a
 * Agencia", la Portada es el punto de entrada por defecto).
 *
 * @param {string} slug
 * @param {{ esAdmin: boolean, tienePermiso: (permiso: string) => boolean }} contexto
 * @returns {object|null}
 */
export function encontrarSeccionVisiblePorSlug(slug, contexto) {
  const seccion = SETTINGS_SECTIONS.find((s) => s.slug === slug);
  if (!seccion) return null;
  if (!esSeccionVisible(seccion, contexto)) return null;
  return seccion;
}

// ─── Chips de estado de la portada — regla exacta §2.4, sin excepciones ────────────────
// Regla dura: NUNCA un chip inventado, NUNCA un chip "Cargando...". Mientras el dato
// real todavía no llegó (undefined/null), estas funciones devuelven `null` — la tarjeta
// se ve exactamente igual que las que no tienen chip (chevron a secas).

/**
 * Chip de la tarjeta "WhatsApp Bot", a partir de `botStatus` (mismo campo que ya usa
 * WhatsAppBotTab.jsx, viene de GET /webhooks/status → data.status).
 *
 * @param {string|null|undefined} botStatus
 * @returns {{ texto: string, tono: "verde"|"neutro" }|null}
 */
export function chipWhatsApp(botStatus) {
  if (botStatus === undefined || botStatus === null) return null;
  if (botStatus === "READY") {
    return { texto: "CONECTADO", tono: "verde" };
  }
  // Cualquier otro estado ya confirmado (OFFLINE, STARTING, SCAN_QR...) es "Desconectado"
  // en gris neutro, NUNCA ámbar — el ámbar de esta app está reservado para "te pide algo
  // vos" (una alerta accionable), y un bot sin conectar todavía no es una alerta.
  return { texto: "DESCONECTADO", tono: "neutro" };
}

/**
 * Chip de la tarjeta "Facturación", a partir de `isProduction` (mismo campo que ya usa
 * AfipSettingsTab.jsx, viene de GET /afip/settings → data.isProduction).
 *
 * @param {boolean|null|undefined} isProduction
 * @returns {{ texto: string, tono: "verde"|"ambar" }|null}
 */
export function chipFacturacion(isProduction) {
  if (isProduction === undefined || isProduction === null) return null;
  if (isProduction === true) {
    return { texto: "PRODUCCIÓN", tono: "verde" };
  }
  return { texto: "HOMOLOGACIÓN", tono: "ambar" };
}
