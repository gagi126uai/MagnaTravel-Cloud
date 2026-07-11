/**
 * Sidebar principal del ERP MagnaTravel.
 *
 * Organiza la navegación en módulos colapsables (VENTAS, COMPRAS, RESERVAS…).
 * Cada módulo se puede abrir o cerrar con un click; el estado persiste en
 * localStorage para que las elecciones del usuario sobrevivan recargas.
 *
 * Reglas clave:
 *  - Un módulo cuyos ítems son TODOS invisibles para el usuario no se renderiza.
 *  - El módulo que contiene la ruta activa siempre se abre (aunque esté guardado cerrado).
 *  - En modo comprimido (collapsed=true, solo íconos) se muestran todos los ítems
 *    sin agrupar, porque los títulos de módulo no tienen espacio.
 *  - Los permisos de cada ítem no cambian respecto del menú anterior: solo es un reagrupamiento visual.
 */

import { useState, useEffect, useCallback } from "react";
import { NavLink, useLocation } from "react-router-dom";
import {
  LayoutDashboard,
  Users,
  FolderOpen,
  CreditCard,
  ArrowLeftRight,
  Building2,
  Settings,
  LogOut,
  DollarSign,
  MessageSquare,
  UserPlus,
  Package,
  Shield,
  ShieldCheck,
  Inbox,
  FileText,
  TrendingUp,
  ChevronRight,
  X,
} from "lucide-react";
import { cn } from "../lib/utils";
import { useAlerts } from "../contexts/AlertsContext";
import { hasPermission, isAdmin } from "../auth";
import { useApprovalsPendingCount } from "../features/approvals/hooks/useApprovals";

// ─── localStorage helpers ──────────────────────────────────────────────────

// Clave del localStorage donde se guarda el estado de apertura de cada módulo.
const MODULES_STORAGE_KEY = "sidebar-modules-state";

/**
 * Lee el estado persistido de los módulos (qué está abierto/cerrado).
 * Devuelve null si nunca se guardó (primera visita o localStorage bloqueado).
 */
export function readStoredModulesState() {
  try {
    const raw = localStorage.getItem(MODULES_STORAGE_KEY);
    return raw ? JSON.parse(raw) : null;
  } catch {
    // localStorage puede estar bloqueado en incógnito o con restricciones.
    return null;
  }
}

/**
 * Persiste el estado de apertura de los módulos en localStorage.
 */
export function saveModulesState(state) {
  try {
    localStorage.setItem(MODULES_STORAGE_KEY, JSON.stringify(state));
  } catch {
    // No es crítico: si falla, simplemente no persiste entre recargas.
  }
}

// ─── Estructura del menú ───────────────────────────────────────────────────

// Ítems sueltos arriba del todo, sin módulo (acceso directo rápido).
export const LOOSE_LINKS = [
  { to: "/dashboard", label: "Inicio",    icon: LayoutDashboard },
  { to: "/messages",  label: "Mensajes",  icon: MessageSquare,  requiredPermission: "messages.view" },
];

// Cada módulo tiene: id único, título visible en MAYÚSCULAS y sus ítems.
// El orden de MODULE_DEFS define el orden de aparición en el menú.
export const MODULE_DEFS = [
  {
    id: "ventas",
    title: "VENTAS",
    links: [
      { to: "/customers", label: "Clientes",               icon: Users,       requiredPermission: "clientes.view" },
      { to: "/crm",       label: "Posibles clientes",      icon: UserPlus,    requiredPermission: "crm.view" },
      { to: "/payments",     label: "Cobranza y Facturación", icon: CreditCard, requiredPermission: "cobranzas.view" },
      // Pantalla global de Facturación: todos los comprobantes de la agencia (spec 2026-06-28 §4/P14).
      // Requiere cobranzas.view_all (un vendedor sin ese permiso no la ve aquí; accede a sus propios
      // comprobantes desde la solapa de facturación de cada cliente).
      { to: "/facturacion", label: "Facturación",             icon: FileText,   requiredPermission: "cobranzas.view_all" },
      // "NC por revisar" y "Reconciliación NC" se sacaron de acá (spec "fin de las bandejas",
      // 2026-07-08): las 3 bandejas back-office se unificaron en /pendientes-afip, con acceso
      // desde el módulo GESTIÓN (ver más abajo).
    ],
  },
  {
    id: "compras",
    title: "COMPRAS",
    links: [
      // "Proveedores" renombrado a "Operadores" — spec 2026-06-28 §5. Ruta y permisos sin cambio.
      { to: "/suppliers", label: "Operadores", icon: Building2, requiredPermission: "proveedores.view" },
      // "Reembolsos operador" sacado del menú (decisión 5, spec 2026-07-03 P1=C): los reembolsos
      // pendientes se ven operador por operador, en la solapa "Reembolsos" de cada ficha
      // (OperatorRefundsPendingSection dentro de SupplierAccountPage). NO se reemplaza por ninguna
      // vista global — Gastón lo eligió así a sabiendas (el trade-off queda anotado en la spec).
      // La ruta /operator-refunds y su página se eliminaron junto con esta entrada (misma spec).
    ],
  },
  {
    id: "cajaYBancos",
    title: "CAJA Y BANCOS",
    links: [
      { to: "/cash", label: "Caja", icon: ArrowLeftRight, requiredPermission: "caja.view" },
      // TODO: cuando se construya la pantalla de Cuentas bancarias de la agencia
      //       (spec 2026-06-28 §P16 árbol), agregar aquí el link a su ruta.
    ],
  },
  {
    id: "reservas",
    title: "RESERVAS",
    links: [
      { to: "/reservas", label: "Reservas", icon: FolderOpen, requiredPermission: "reservas.view" },
    ],
  },
  {
    id: "catalogo",
    title: "CATÁLOGO",
    links: [
      { to: "/rates",    label: "Tarifario",         icon: DollarSign, requiredPermission: "tarifario.view" },
      { to: "/packages", label: "Países y destinos", icon: Package,    requiredPermission: "paquetes.view" },
    ],
  },
  {
    id: "gestion",
    title: "GESTIÓN",
    links: [
      { to: "/approvals/inbox",       label: "Aprobaciones",   icon: ShieldCheck, requiredPermission: "approvals.review" },
      { to: "/approvals/my-requests", label: "Mis solicitudes", icon: Inbox,      requiredPermission: "approvals.request" },
      // "Pendientes con AFIP" se DESARMA (ADR-044 T4, spec 2026-07-10, decisión final #2 de
      // Gastón): la resolución vive en la ficha (ya hecho 2026-07-08) y el monitor pasivo
      // "Comprobantes por resolver" + "Recibos por regularizar" pasan a vivir DENTRO de la
      // pantalla de Facturación (/facturacion), no como entrada propia del menú. Las rutas
      // viejas (/pendientes-afip y las 3 bandejas sueltas) siguen respondiendo con un
      // redirect en App.jsx para no romper bookmarks/links existentes.
      // Comisiones: solo el dueño/admin la ve (decisión del dueño, guia-ux 2026-06-13).
      { to: "/commissions",           label: "Comisiones",     icon: TrendingUp,  adminOnly: true },
      // TODO: cuando se construya la pantalla global de Reportes (spec 2026-06-28),
      //       agregar aquí el link a su ruta.
      { to: "/admin",    label: "Administración", icon: Shield,   requiredPermission: "auditoria.view" },
      { to: "/settings", label: "Configuración",  icon: Settings, requiredPermission: "configuracion.view" },
    ],
  },
];

// ─── Lógica pura (exportada para tests) ───────────────────────────────────

/**
 * Determina si un ítem del menú es visible para el usuario actual.
 *
 * - adminOnly=true: solo admin/dueño. No se revisa requiredPermission ni anyPermission.
 * - anyPermission=[...]: visible si tiene AL MENOS UNO de esos permisos (OR). Se usa en
 *   ítems "paraguas" que agrupan varias bandejas con permisos distintos (ej: /pendientes-afip).
 * - requiredPermission: verifica con hasPermission() (que devuelve true para admin).
 * - sin ninguno de los tres: visible para cualquier usuario autenticado.
 */
export function isLinkVisible(link, isAdminUser, permissionFn = hasPermission) {
  if (link.adminOnly) return isAdminUser;
  if (Array.isArray(link.anyPermission)) return link.anyPermission.some(permissionFn);
  if (link.requiredPermission) return permissionFn(link.requiredPermission);
  return true;
}

/**
 * Devuelve el id del módulo que contiene la ruta actualmente activa.
 * Acepta coincidencia exacta (/suppliers) o sub-ruta (/suppliers/123).
 * Devuelve null si la ruta pertenece a un ítem suelto (Inicio, Mensajes)
 * o si no hay coincidencia.
 */
export function findActiveModuleId(pathname) {
  for (const module of MODULE_DEFS) {
    for (const link of module.links) {
      if (pathname === link.to || pathname.startsWith(link.to + "/")) {
        return module.id;
      }
    }
  }
  return null;
}

/**
 * Calcula el estado inicial de apertura de los módulos.
 *
 * - savedState=null (primera visita): abre TODOS los módulos para que el
 *   usuario vea el árbol completo desde el principio.
 * - savedState existe: respeta las elecciones del usuario, pero siempre
 *   fuerza abierto el módulo que contiene la ruta activa.
 *
 * El parámetro savedState se recibe como argumento (no se lee directamente del
 * localStorage) para mantener la función testeable sin mock.
 */
export function computeInitialModulesOpen(pathname, savedState) {
  const activeModuleId = findActiveModuleId(pathname);

  if (savedState) {
    // Si el módulo activo está guardado como cerrado, lo abrimos igual.
    if (activeModuleId && !savedState[activeModuleId]) {
      return { ...savedState, [activeModuleId]: true };
    }
    return savedState;
  }

  // Primera visita: todos abiertos (el usuario ve el árbol entero desde el arranque).
  const allOpen = {};
  MODULE_DEFS.forEach((m) => { allOpen[m.id] = true; });
  return allOpen;
}

// ─── Subcomponentes ────────────────────────────────────────────────────────

/**
 * Encabezado colapsable de un módulo del sidebar (VENTAS, COMPRAS, etc.).
 *
 * Oculta el módulo entero si ningún ítem es visible para el usuario,
 * evitando encabezados vacíos sin ítems debajo.
 *
 * Props:
 *   - title: string — etiqueta en MAYÚSCULAS (ej: "VENTAS")
 *   - isOpen: boolean — si el módulo está expandido
 *   - onToggle: () => void — callback al hacer click
 *   - hasVisibleItems: boolean — false si todos los ítems están ocultos por permisos
 *   - children: ReactNode — los NavLinks del módulo
 */
function SidebarModule({ title, isOpen, onToggle, hasVisibleItems, children }) {
  // No renderizar si todos los ítems del módulo son invisibles para el usuario.
  if (!hasVisibleItems) return null;

  return (
    <div className="mb-1">
      {/* El encabezado del módulo es un <button> real (accesible con teclado, aria-expanded) */}
      <button
        type="button"
        onClick={onToggle}
        aria-expanded={isOpen}
        className={cn(
          "flex w-full items-center justify-between",
          "mt-3 px-3 py-1",
          "text-[10px] font-bold tracking-widest uppercase",
          "text-slate-400 dark:text-slate-500",
          "hover:text-slate-600 dark:hover:text-slate-300",
          "rounded-md transition-colors",
          "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-indigo-500"
        )}
      >
        <span>{title}</span>
        {/* El chevron rota 90° cuando el módulo está abierto */}
        <ChevronRight
          className={cn(
            "h-3 w-3 flex-shrink-0 transition-transform duration-200",
            isOpen && "rotate-90"
          )}
          aria-hidden="true"
        />
      </button>

      {isOpen && (
        <div className="mt-0.5 space-y-0.5">
          {children}
        </div>
      )}
    </div>
  );
}

// ─── Componente principal ──────────────────────────────────────────────────

/**
 * Sidebar principal.
 *
 * Props:
 *   - onLogout: () => void — llama al logout del store de auth.
 *   - className: string — clases extra (ancho fijo, posicionamiento, etc.).
 *   - collapsed: boolean — true = modo solo-íconos (sidebar comprimido).
 *   - onCloseMobile: (() => void) | undefined — botón de cierre en mobile.
 *
 * Nota sobre la prop isAdmin que llega de App.jsx: la ignoramos intencionalmente.
 * El gate de adminOnly usa isAdmin() del módulo auth (fuente de verdad del store),
 * que no puede quedar stale como una prop.
 */
export default function Sidebar({ onLogout, className, collapsed, onCloseMobile }) {
  const location = useLocation();
  const { alerts } = useAlerts();

  // Badge dinámico de aprobaciones pendientes (solo carga si el user puede revisar).
  const canReview = hasPermission("approvals.review");
  const { count: pendingApprovals } = useApprovalsPendingCount(canReview);

  // Gate de admin: fuente de verdad del store de auth, no la prop.
  const isAdminUser = isAdmin();

  // Estado de apertura de cada módulo: { [moduleId]: boolean }.
  // Se inicializa UNA vez con el estado del localStorage + módulo activo forzado abierto.
  const [modulesOpen, setModulesOpen] = useState(() =>
    computeInitialModulesOpen(location.pathname, readStoredModulesState())
  );

  // Cuando el usuario navega, asegurarse de que el módulo que contiene la nueva
  // ruta activa esté expandido (para que el ítem activo siempre sea visible).
  useEffect(() => {
    const activeModuleId = findActiveModuleId(location.pathname);
    if (!activeModuleId) return; // Ruta suelta (Inicio, Mensajes) — nada que hacer.

    setModulesOpen((prev) => {
      if (prev[activeModuleId]) return prev; // Ya está abierto, sin cambio.
      const next = { ...prev, [activeModuleId]: true };
      saveModulesState(next);
      return next;
    });
  }, [location.pathname]); // Se re-ejecuta cada vez que cambia la ruta.

  // Alterna abierto/cerrado de un módulo y persiste la decisión en localStorage.
  const toggleModule = useCallback((moduleId) => {
    setModulesOpen((prev) => {
      const next = { ...prev, [moduleId]: !prev[moduleId] };
      saveModulesState(next);
      return next;
    });
  }, []); // Sin dependencias: usa el updater funcional de setModulesOpen.

  // Devuelve el badge numérico para un link (si aplica).
  // Solo /approvals/inbox tiene badge por ahora.
  function getLinkBadge(link) {
    if (link.to === "/approvals/inbox") return pendingApprovals;
    return undefined;
  }

  /**
   * Renderiza un NavLink individual con estilos activos, badge e íconos.
   * isCollapsed=true omite el texto y muestra el badge como punto pequeño.
   */
  function renderNavLink(link, isCollapsed) {
    const badge = getLinkBadge(link);
    return (
      <NavLink
        key={link.to}
        to={link.to}
        onClick={onCloseMobile}
        // En modo colapsado el label se muestra como tooltip (title nativo).
        title={isCollapsed ? link.label : undefined}
        data-testid={`sidebar-link-${link.to.replace(/\//g, "-").replace(/^-/, "")}`}
        className={({ isActive }) =>
          cn(
            "group flex items-center rounded-lg text-sm font-medium transition-all",
            isCollapsed ? "justify-center p-3" : "gap-3 px-3 py-2.5",
            "hover:bg-slate-100 dark:hover:bg-slate-800",
            isActive
              ? "bg-indigo-50 text-indigo-700 shadow-sm dark:bg-indigo-900/20 dark:text-indigo-300"
              : "text-slate-600 dark:text-slate-400"
          )
        }
      >
        <link.icon className={cn("flex-shrink-0", isCollapsed ? "h-5 w-5" : "h-4 w-4")} />

        {!isCollapsed && <span className="truncate flex-1">{link.label}</span>}

        {/* Badge numérico en modo expandido */}
        {!isCollapsed && badge > 0 && (
          <span className="bg-red-500 text-white text-[10px] font-bold px-1.5 py-0.5 rounded-full">
            {badge > 99 ? "99+" : badge}
          </span>
        )}

        {/* Badge como punto rojo en modo colapsado (solo íconos) */}
        {isCollapsed && badge > 0 && (
          <span className="absolute top-2 right-2 h-2 w-2 rounded-full bg-red-500 border border-white dark:border-slate-900" />
        )}
      </NavLink>
    );
  }

  return (
    <aside className={cn("flex flex-col border-r bg-card text-card-foreground", className)}>

      {/* Encabezado con logo MagnaTravel */}
      <div
        className={cn(
          "flex items-center border-b border-slate-200 dark:border-slate-800",
          collapsed ? "justify-center px-2 py-4" : "justify-between px-4 py-4"
        )}
      >
        <div className={cn("flex items-center gap-3", collapsed && "justify-center")}>
          <div className="flex h-10 w-10 items-center justify-center rounded-xl bg-indigo-600 text-white shadow-md shadow-indigo-500/20 flex-shrink-0">
            <span className="text-xl font-bold">MT</span>
          </div>
          {!collapsed && (
            <div className="min-w-0">
              <p className="text-sm font-bold truncate">MagnaTravel</p>
              <p className="text-xs text-muted-foreground">Retail ERP</p>
            </div>
          )}
        </div>

        {/* Botón de cierre en mobile: solo aparece si se pasa el callback */}
        {onCloseMobile && !collapsed && (
          <button
            onClick={onCloseMobile}
            className="md:hidden p-2 rounded-lg hover:bg-slate-100 dark:hover:bg-slate-800 transition-colors"
            type="button"
            aria-label="Cerrar menú"
          >
            <X className="h-5 w-5" />
          </button>
        )}
      </div>

      {/* Área de navegación: scrolleable si los módulos no entran en pantalla */}
      <nav
        className={cn("flex-1 overflow-y-auto py-4", collapsed ? "px-2" : "px-3")}
        aria-label="Navegación principal"
      >
        {collapsed ? (
          /*
           * Modo comprimido (solo íconos): todos los ítems visibles en lista plana.
           * No hay espacio para encabezados de módulo sin texto.
           */
          <div className="space-y-1">
            {[...LOOSE_LINKS, ...MODULE_DEFS.flatMap((m) => m.links)]
              .filter((link) => isLinkVisible(link, isAdminUser))
              .map((link) => renderNavLink(link, true))}
          </div>
        ) : (
          /*
           * Modo expandido: ítems sueltos arriba y luego módulos colapsables.
           */
          <div>
            {/* Inicio y Mensajes — sin encabezado de módulo */}
            <div className="space-y-0.5">
              {LOOSE_LINKS
                .filter((link) => isLinkVisible(link, isAdminUser))
                .map((link) => renderNavLink(link, false))}
            </div>

            {/* Módulos colapsables */}
            {MODULE_DEFS.map((module) => {
              const visibleLinks = module.links.filter((l) =>
                isLinkVisible(l, isAdminUser)
              );

              return (
                <SidebarModule
                  key={module.id}
                  title={module.title}
                  isOpen={!!modulesOpen[module.id]}
                  onToggle={() => toggleModule(module.id)}
                  hasVisibleItems={visibleLinks.length > 0}
                >
                  {visibleLinks.map((link) => renderNavLink(link, false))}
                </SidebarModule>
              );
            })}
          </div>
        )}
      </nav>

      {/* Cerrar sesión siempre al pie */}
      <div className={cn("border-t border-slate-200 dark:border-slate-800", collapsed ? "p-2" : "p-3")}>
        <button
          onClick={onLogout}
          title={collapsed ? "Cerrar sesión" : undefined}
          className={cn(
            "flex w-full items-center rounded-lg text-sm font-medium text-slate-500 transition-all",
            "hover:bg-rose-50 hover:text-rose-600 dark:hover:bg-rose-900/20",
            collapsed ? "justify-center p-3" : "gap-3 px-3 py-2.5"
          )}
          type="button"
        >
          <LogOut className={cn("flex-shrink-0", collapsed ? "h-5 w-5" : "h-4 w-4")} />
          {!collapsed && <span>Cerrar sesión</span>}
        </button>
      </div>
    </aside>
  );
}
