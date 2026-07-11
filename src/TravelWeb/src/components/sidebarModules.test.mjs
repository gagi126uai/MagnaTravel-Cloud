/**
 * Tests de lógica pura para el reagrupamiento del Sidebar en módulos.
 *
 * Qué cubre:
 *   - Estructura de MODULE_DEFS: cada ítem está en el módulo correcto.
 *   - "Operadores" (ex "Proveedores") apunta a /suppliers con el permiso correcto.
 *   - isLinkVisible: gate de adminOnly y requiredPermission.
 *   - findActiveModuleId: detecta qué módulo contiene la ruta activa.
 *   - computeInitialModulesOpen: primera visita (todo abierto), visita con estado guardado,
 *     módulo activo forzado abierto aunque esté guardado cerrado.
 *   - Un módulo con todos sus ítems ocultos por permisos tiene hasVisibleItems=false.
 *
 * Por qué lógica pura:
 *   Estas funciones no tienen efectos secundarios ni dependencias del DOM.
 *   Se replican inline (no se importan de Sidebar.jsx) para que los tests
 *   sean estables ante refactors internos del componente.
 *   Patrón idéntico a serviceFormModalLock.test.mjs y notificationBell.test.mjs.
 *
 * Correr: node --test src/components/sidebarModules.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

// ─── Réplica de MODULE_DEFS (Sidebar.jsx) ────────────────────────────────────
// Si se agrega/mueve un ítem en Sidebar.jsx, actualizar acá también.

const MODULE_DEFS = [
  {
    id: "ventas",
    title: "VENTAS",
    links: [
      { to: "/customers", label: "Clientes",               requiredPermission: "clientes.view" },
      { to: "/crm",       label: "Posibles clientes",      requiredPermission: "crm.view" },
      { to: "/payments",  label: "Cobranza y Facturación", requiredPermission: "cobranzas.view" },
      // "NC por revisar" y "Reconciliación NC" se sacaron de acá — ahora viven unificadas en
      // /pendientes-afip, dentro del módulo GESTIÓN (spec "fin de las bandejas", 2026-07-08).
    ],
  },
  {
    id: "compras",
    title: "COMPRAS",
    links: [
      { to: "/suppliers", label: "Operadores", requiredPermission: "proveedores.view" },
      // "Reembolsos operador" sacado del menú (decisión 5, spec 2026-07-03 P1=C): los reembolsos
      // pendientes se ven operador por operador, en la solapa "Reembolsos" de cada ficha. Sin
      // vista global de reemplazo (elección consciente de Gastón). Ver test más abajo.
    ],
  },
  {
    id: "cajaYBancos",
    title: "CAJA Y BANCOS",
    links: [
      { to: "/cash", label: "Caja", requiredPermission: "caja.view" },
    ],
  },
  {
    id: "reservas",
    title: "RESERVAS",
    links: [
      { to: "/reservas", label: "Reservas", requiredPermission: "reservas.view" },
    ],
  },
  {
    id: "catalogo",
    title: "CATÁLOGO",
    links: [
      { to: "/rates",    label: "Tarifario",         requiredPermission: "tarifario.view" },
      { to: "/packages", label: "Países y destinos", requiredPermission: "paquetes.view" },
    ],
  },
  {
    id: "gestion",
    title: "GESTIÓN",
    links: [
      { to: "/approvals/inbox",       label: "Aprobaciones",   requiredPermission: "approvals.review" },
      { to: "/approvals/my-requests", label: "Mis solicitudes", requiredPermission: "approvals.request" },
      // "Pendientes con AFIP" se DESARMA (ADR-044 T4, 2026-07-10): pasa a vivir dentro de
      // /facturacion (solapas "Comprobantes por resolver" y "Recibos por regularizar"),
      // ya no es una entrada propia del menú.
      { to: "/commissions",           label: "Comisiones",     adminOnly: true },
      { to: "/admin",    label: "Administración", requiredPermission: "auditoria.view" },
      { to: "/settings", label: "Configuración",  requiredPermission: "configuracion.view" },
    ],
  },
];

const LOOSE_LINKS = [
  { to: "/dashboard", label: "Inicio" },
  { to: "/messages",  label: "Mensajes", requiredPermission: "messages.view" },
];

// ─── Réplica de las funciones puras de Sidebar.jsx ───────────────────────────

function isLinkVisible(link, isAdminUser, permissionFn) {
  if (link.adminOnly) return isAdminUser;
  if (Array.isArray(link.anyPermission)) return link.anyPermission.some(permissionFn);
  if (link.requiredPermission) return permissionFn(link.requiredPermission);
  return true;
}

function findActiveModuleId(pathname) {
  for (const module of MODULE_DEFS) {
    for (const link of module.links) {
      if (pathname === link.to || pathname.startsWith(link.to + "/")) {
        return module.id;
      }
    }
  }
  return null;
}

// Versión testeable de computeInitialModulesOpen: recibe savedState como param.
function computeInitialModulesOpen(pathname, savedState) {
  const activeModuleId = findActiveModuleId(pathname);

  if (savedState) {
    if (activeModuleId && !savedState[activeModuleId]) {
      return { ...savedState, [activeModuleId]: true };
    }
    return savedState;
  }

  const allOpen = {};
  MODULE_DEFS.forEach((m) => { allOpen[m.id] = true; });
  return allOpen;
}

// Helper: cuenta cuántos módulos tienen todos sus ítems ocultos para un usuario dado.
function getModulesWithVisibleItems(isAdminUser, permissionFn) {
  return MODULE_DEFS.filter((module) =>
    module.links.some((link) => isLinkVisible(link, isAdminUser, permissionFn))
  );
}

// ─── Tests: estructura de MODULE_DEFS ────────────────────────────────────────

test("estructura: todos los módulos tienen id, title y links", () => {
  for (const module of MODULE_DEFS) {
    assert.ok(module.id,    `módulo sin id: ${JSON.stringify(module)}`);
    assert.ok(module.title, `módulo sin title: ${module.id}`);
    assert.ok(Array.isArray(module.links) && module.links.length > 0,
      `módulo '${module.id}' no tiene links`);
  }
});

test("estructura: todos los links tienen to y label", () => {
  for (const module of MODULE_DEFS) {
    for (const link of module.links) {
      assert.ok(link.to,    `link sin 'to' en módulo '${module.id}'`);
      assert.ok(link.label, `link sin 'label' en módulo '${module.id}': ${link.to}`);
    }
  }
});

test("estructura: no hay rutas duplicadas entre módulos y ítems sueltos", () => {
  const allRoutes = [
    ...LOOSE_LINKS.map((l) => l.to),
    ...MODULE_DEFS.flatMap((m) => m.links.map((l) => l.to)),
  ];
  const unique = new Set(allRoutes);
  assert.equal(unique.size, allRoutes.length, "Hay rutas duplicadas en la definición del menú");
});

// ─── Tests: renombre Proveedores → Operadores ─────────────────────────────────

test("Operadores: el label es 'Operadores' (no 'Proveedores')", () => {
  const comprasModule = MODULE_DEFS.find((m) => m.id === "compras");
  assert.ok(comprasModule, "El módulo COMPRAS debe existir");

  const operadoresLink = comprasModule.links.find((l) => l.to === "/suppliers");
  assert.ok(operadoresLink, "El link /suppliers debe estar en el módulo COMPRAS");
  assert.equal(operadoresLink.label, "Operadores",
    "El label de /suppliers debe ser 'Operadores', no 'Proveedores'");
});

test("Operadores: la palabra 'Proveedores' no aparece como label en ningún ítem", () => {
  const allLinks = MODULE_DEFS.flatMap((m) => m.links);
  const proveedoresLink = allLinks.find((l) => l.label === "Proveedores");
  assert.equal(proveedoresLink, undefined,
    "El label 'Proveedores' no debe aparecer en ningún ítem del menú");
});

test("Operadores: el link /suppliers mantiene el permiso 'proveedores.view'", () => {
  const comprasModule = MODULE_DEFS.find((m) => m.id === "compras");
  const operadoresLink = comprasModule.links.find((l) => l.to === "/suppliers");
  assert.equal(operadoresLink.requiredPermission, "proveedores.view",
    "El renombre no debe cambiar el permiso requerido");
});

// ─── Tests: agrupamiento de ítems por módulo ─────────────────────────────────

test("agrupamiento: /customers, /crm y /payments están en VENTAS", () => {
  const ventas = MODULE_DEFS.find((m) => m.id === "ventas");
  const routes = ventas.links.map((l) => l.to);
  assert.ok(routes.includes("/customers"), "/customers debe estar en VENTAS");
  assert.ok(routes.includes("/crm"),       "/crm debe estar en VENTAS");
  assert.ok(routes.includes("/payments"),  "/payments debe estar en VENTAS");
});

test("agrupamiento (spec 'fin de las bandejas', 2026-07-08): NC por revisar y Reconciliación NC ya NO están sueltas en VENTAS", () => {
  const ventas = MODULE_DEFS.find((m) => m.id === "ventas");
  const routes = ventas.links.map((l) => l.to);
  assert.ok(!routes.includes("/cancellations/credit-notes/inbox"), "NC por revisar no debe estar en VENTAS");
  assert.ok(!routes.includes("/credit-note-reconciliation/inbox"), "Reconciliación NC no debe estar en VENTAS");
});

test("agrupamiento (ADR-044 T4, 2026-07-10): /pendientes-afip YA NO está en ningún módulo del menú", () => {
  // "Pendientes con AFIP" se desarma: el monitor pasivo pasa a vivir dentro de
  // /facturacion (solapas "Comprobantes por resolver" y "Recibos por regularizar").
  const allRoutes = MODULE_DEFS.flatMap((m) => m.links.map((l) => l.to));
  assert.ok(!allRoutes.includes("/pendientes-afip"), "/pendientes-afip no debe estar en ningún módulo");
});

test("agrupamiento (decisión 5, spec 2026-07-03): /operator-refunds NO está en ningún módulo del menú", () => {
  // La pantalla global de reembolsos sale del menú principal (P1=C: sin vista de reemplazo).
  // Los reembolsos se ven operador por operador, en la solapa "Reembolsos" de cada ficha.
  const allRoutes = MODULE_DEFS.flatMap((m) => m.links.map((l) => l.to));
  assert.ok(!allRoutes.includes("/operator-refunds"), "/operator-refunds no debe estar en el menú");
});

test("agrupamiento (decisión 5): COMPRAS solo tiene 'Operadores', sin 'Reembolsos operador'", () => {
  const compras = MODULE_DEFS.find((m) => m.id === "compras");
  const labels = compras.links.map((l) => l.label);
  assert.deepEqual(labels, ["Operadores"]);
});

test("agrupamiento: /cash está en CAJA Y BANCOS", () => {
  const caja = MODULE_DEFS.find((m) => m.id === "cajaYBancos");
  assert.ok(caja, "El módulo cajaYBancos debe existir");
  const routes = caja.links.map((l) => l.to);
  assert.ok(routes.includes("/cash"), "/cash debe estar en CAJA Y BANCOS");
});

test("agrupamiento: /reservas está en su propio módulo RESERVAS", () => {
  const reservas = MODULE_DEFS.find((m) => m.id === "reservas");
  assert.ok(reservas, "El módulo RESERVAS debe existir");
  const routes = reservas.links.map((l) => l.to);
  assert.ok(routes.includes("/reservas"), "/reservas debe estar en RESERVAS");
});

test("agrupamiento: /rates y /packages están en CATÁLOGO", () => {
  const catalogo = MODULE_DEFS.find((m) => m.id === "catalogo");
  assert.ok(catalogo, "El módulo CATÁLOGO debe existir");
  const routes = catalogo.links.map((l) => l.to);
  assert.ok(routes.includes("/rates"),    "/rates debe estar en CATÁLOGO");
  assert.ok(routes.includes("/packages"), "/packages debe estar en CATÁLOGO");
});

test("agrupamiento: /approvals, /commissions, /admin y /settings están en GESTIÓN", () => {
  const gestion = MODULE_DEFS.find((m) => m.id === "gestion");
  assert.ok(gestion, "El módulo GESTIÓN debe existir");
  const routes = gestion.links.map((l) => l.to);
  assert.ok(routes.includes("/approvals/inbox"),       "/approvals/inbox debe estar en GESTIÓN");
  assert.ok(routes.includes("/approvals/my-requests"), "/approvals/my-requests debe estar en GESTIÓN");
  assert.ok(routes.includes("/commissions"),            "/commissions debe estar en GESTIÓN");
  assert.ok(routes.includes("/admin"),                  "/admin debe estar en GESTIÓN");
  assert.ok(routes.includes("/settings"),               "/settings debe estar en GESTIÓN");
});

test("agrupamiento: /dashboard y /messages son ítems sueltos (no están en ningún módulo)", () => {
  const allModuleRoutes = MODULE_DEFS.flatMap((m) => m.links.map((l) => l.to));
  assert.ok(!allModuleRoutes.includes("/dashboard"), "/dashboard no debe estar en ningún módulo");
  assert.ok(!allModuleRoutes.includes("/messages"),  "/messages no debe estar en ningún módulo");
});

// ─── Tests: isLinkVisible ─────────────────────────────────────────────────────

test("isLinkVisible: adminOnly=true + isAdmin=true → visible", () => {
  const link = { to: "/commissions", label: "Comisiones", adminOnly: true };
  assert.equal(isLinkVisible(link, true, () => false), true);
});

test("isLinkVisible: adminOnly=true + isAdmin=false → oculto", () => {
  const link = { to: "/commissions", label: "Comisiones", adminOnly: true };
  assert.equal(isLinkVisible(link, false, () => true), false);
});

test("isLinkVisible: requiredPermission + permissionFn=true → visible", () => {
  const link = { to: "/customers", label: "Clientes", requiredPermission: "clientes.view" };
  // El usuario tiene el permiso clientes.view
  assert.equal(isLinkVisible(link, false, () => true), true);
});

test("isLinkVisible: requiredPermission + permissionFn=false → oculto", () => {
  const link = { to: "/customers", label: "Clientes", requiredPermission: "clientes.view" };
  // El usuario no tiene el permiso
  assert.equal(isLinkVisible(link, false, () => false), false);
});

test("isLinkVisible: sin permiso ni adminOnly → visible para cualquier usuario", () => {
  const link = { to: "/dashboard", label: "Inicio" };
  assert.equal(isLinkVisible(link, false, () => false), true);
  assert.equal(isLinkVisible(link, true,  () => false), true);
});

test("isLinkVisible: anyPermission con UNO de los permisos en true → visible", () => {
  const link = { to: "/algun-link", label: "Algún ítem paraguas", anyPermission: ["a.x", "b.y", "c.z"] };
  // El usuario solo tiene "b.y" — alcanza para verlo (es un OR).
  assert.equal(isLinkVisible(link, false, (p) => p === "b.y"), true);
});

test("isLinkVisible: anyPermission sin NINGUNO de los permisos → oculto", () => {
  const link = { to: "/algun-link", label: "Algún ítem paraguas", anyPermission: ["a.x", "b.y", "c.z"] };
  assert.equal(isLinkVisible(link, false, () => false), false);
});

test("isLinkVisible: adminOnly tiene prioridad sobre anyPermission (no se evalúa permissionFn)", () => {
  const link = { to: "/admin-only", label: "Solo admin", adminOnly: true, anyPermission: ["a.x"] };
  let permissionFnCalled = false;
  const permFn = () => { permissionFnCalled = true; return true; };
  const result = isLinkVisible(link, false, permFn);
  assert.equal(result, false, "adminOnly=false + isAdmin=false debe ocultar el link");
  assert.equal(permissionFnCalled, false, "permissionFn no debe llamarse cuando adminOnly aplica");
});

test("isLinkVisible: adminOnly tiene prioridad sobre requiredPermission (no se evalúa permissionFn)", () => {
  // Si adminOnly=true y isAdmin=false, el link es oculto sin importar los permisos.
  const link = { to: "/admin-only", label: "Solo admin", adminOnly: true, requiredPermission: "algo.view" };
  let permisionFnCalled = false;
  const permFn = () => { permisionFnCalled = true; return true; };
  const result = isLinkVisible(link, false, permFn);
  assert.equal(result, false, "adminOnly=false + isAdmin=false debe ocultar el link");
  assert.equal(permisionFnCalled, false, "permissionFn no debe llamarse cuando adminOnly aplica");
});

// ─── Tests: findActiveModuleId ────────────────────────────────────────────────

test("findActiveModuleId: ruta exacta /suppliers → módulo 'compras'", () => {
  assert.equal(findActiveModuleId("/suppliers"), "compras");
});

test("findActiveModuleId: sub-ruta /suppliers/123 → módulo 'compras' (match por prefijo)", () => {
  assert.equal(findActiveModuleId("/suppliers/123"), "compras");
});

test("findActiveModuleId: /dashboard → null (ítem suelto, no en módulo)", () => {
  assert.equal(findActiveModuleId("/dashboard"), null);
});

test("findActiveModuleId: /messages → null (ítem suelto)", () => {
  assert.equal(findActiveModuleId("/messages"), null);
});

test("findActiveModuleId: /cash → módulo 'cajaYBancos'", () => {
  assert.equal(findActiveModuleId("/cash"), "cajaYBancos");
});

test("findActiveModuleId: /reservas → módulo 'reservas'", () => {
  assert.equal(findActiveModuleId("/reservas"), "reservas");
});

test("findActiveModuleId: /reservas/abc → módulo 'reservas' (sub-ruta)", () => {
  assert.equal(findActiveModuleId("/reservas/abc-123"), "reservas");
});

test("findActiveModuleId: /approvals/inbox → módulo 'gestion'", () => {
  assert.equal(findActiveModuleId("/approvals/inbox"), "gestion");
});

test("findActiveModuleId: /approvals/my-requests → módulo 'gestion'", () => {
  assert.equal(findActiveModuleId("/approvals/my-requests"), "gestion");
});

test("findActiveModuleId: /settings → módulo 'gestion'", () => {
  assert.equal(findActiveModuleId("/settings"), "gestion");
});

test("findActiveModuleId: /payments → módulo 'ventas'", () => {
  assert.equal(findActiveModuleId("/payments"), "ventas");
});

test("findActiveModuleId: ruta desconocida → null", () => {
  assert.equal(findActiveModuleId("/ruta-inexistente"), null);
  assert.equal(findActiveModuleId("/"),                 null);
  assert.equal(findActiveModuleId(""),                  null);
});

test("findActiveModuleId: /cash-extra no matchea /cash (el prefijo requiere '/' o exacto)", () => {
  // /cash-extra comienza con /cash pero no es sub-ruta válida (/cash/).
  // La función verifica pathname.startsWith(link.to + "/") o igualdad exacta.
  assert.equal(findActiveModuleId("/cash-extra"), null);
});

// ─── Tests: computeInitialModulesOpen ────────────────────────────────────────

test("primera visita (savedState=null): todos los módulos están abiertos", () => {
  const state = computeInitialModulesOpen("/dashboard", null);
  for (const module of MODULE_DEFS) {
    assert.equal(state[module.id], true,
      `El módulo '${module.id}' debe estar abierto en la primera visita`);
  }
});

test("primera visita: la cantidad de módulos abiertos coincide con MODULE_DEFS", () => {
  const state = computeInitialModulesOpen("/payments", null);
  assert.equal(Object.keys(state).length, MODULE_DEFS.length);
});

test("visita con estado guardado: se respeta el estado del usuario", () => {
  // El usuario ya dejó compras y catalogo cerrados, el resto abierto.
  const saved = { ventas: true, compras: false, cajaYBancos: true, reservas: true, catalogo: false, gestion: true };
  const state = computeInitialModulesOpen("/dashboard", saved); // ruta suelta, sin módulo activo
  assert.equal(state.compras,  false, "compras debe seguir cerrado");
  assert.equal(state.catalogo, false, "catálogo debe seguir cerrado");
  assert.equal(state.ventas,   true,  "ventas debe seguir abierto");
});

test("visita con estado guardado: módulo activo CERRADO se fuerza abierto", () => {
  // El usuario guardó 'compras' como cerrado, pero la ruta activa es /suppliers.
  const saved = { ventas: true, compras: false, cajaYBancos: true, reservas: true, catalogo: true, gestion: true };
  const state = computeInitialModulesOpen("/suppliers", saved);
  assert.equal(state.compras, true,
    "El módulo 'compras' debe forzarse abierto porque /suppliers está activo");
});

test("visita con estado guardado: módulo activo YA abierto → sin cambio", () => {
  const saved = { ventas: true, compras: true, cajaYBancos: true, reservas: true, catalogo: true, gestion: true };
  const state = computeInitialModulesOpen("/suppliers", saved);
  // Mismo objeto de referencia (nada que cambiar)
  assert.equal(state, saved, "Si el módulo activo ya está abierto, debe devolver la misma referencia");
});

test("visita con estado guardado y ruta suelta: se devuelve el estado tal cual", () => {
  // /dashboard es ítem suelto → findActiveModuleId devuelve null → no se toca nada.
  const saved = { ventas: true, compras: false, cajaYBancos: false, reservas: true, catalogo: false, gestion: false };
  const state = computeInitialModulesOpen("/dashboard", saved);
  assert.equal(state, saved, "Con ruta suelta, el estado guardado se devuelve sin modificar");
});

// ─── Tests: módulo con todos los ítems ocultos no se renderiza ───────────────

test("hasVisibleItems: módulo con al menos un permiso → tiene ítems visibles", () => {
  // Simulamos un usuario que solo tiene el permiso de ver /cash.
  const permFn = (p) => p === "caja.view";
  const cajaModule = MODULE_DEFS.find((m) => m.id === "cajaYBancos");
  const hasVisible = cajaModule.links.some((l) => isLinkVisible(l, false, permFn));
  assert.equal(hasVisible, true, "CAJA Y BANCOS debe tener ítems visibles para el usuario con caja.view");
});

test("hasVisibleItems: módulo COMPRAS sin permisos de proveedor/reembolsos → sin ítems visibles", () => {
  // Usuario sin ninguno de los permisos de COMPRAS.
  const permFn = () => false;
  const comprasModule = MODULE_DEFS.find((m) => m.id === "compras");
  const hasVisible = comprasModule.links.some((l) => isLinkVisible(l, false, permFn));
  assert.equal(hasVisible, false, "COMPRAS no debe mostrar ítems si el usuario no tiene ningún permiso de compras");
});

test("hasVisibleItems: módulo GESTIÓN sin permisos → solo aparece si el user es admin (Comisiones)", () => {
  // Usuario sin permisos pero tampoco admin → todos los links de GESTIÓN ocultos.
  const permFn = () => false;
  const gestion = MODULE_DEFS.find((m) => m.id === "gestion");
  const hasVisible = gestion.links.some((l) => isLinkVisible(l, false, permFn));
  assert.equal(hasVisible, false,
    "GESTIÓN no debe tener ítems visibles si el user no es admin y no tiene permisos");
});

test("hasVisibleItems: módulo GESTIÓN sin permisos pero ES admin → Comisiones es visible", () => {
  // Admin sin otros permisos → ve Comisiones (adminOnly).
  const permFn = () => false; // hasPermission devuelve false para todo
  const gestion = MODULE_DEFS.find((m) => m.id === "gestion");
  const hasVisible = gestion.links.some((l) => isLinkVisible(l, true, permFn));
  assert.equal(hasVisible, true,
    "Un admin debe ver al menos Comisiones en GESTIÓN aunque no tenga permisos específicos");
});

test("módulos visibles para usuario con TODOS los permisos: todos los 6 módulos", () => {
  // Admin con todos los permisos → los 6 módulos deben tener ítems visibles.
  const permFn = () => true;
  const visibleModules = getModulesWithVisibleItems(true, permFn);
  assert.equal(visibleModules.length, MODULE_DEFS.length,
    `Todos los ${MODULE_DEFS.length} módulos deben tener ítems visibles para un admin`);
});

test("módulos visibles para usuario sin NINGÚN permiso y sin ser admin: 0 módulos de módulo", () => {
  // Usuario sin ningún permiso y no admin → ningún módulo tiene ítems visibles.
  const permFn = () => false;
  const visibleModules = getModulesWithVisibleItems(false, permFn);
  assert.equal(visibleModules.length, 0,
    "Sin permisos y sin ser admin, ningún módulo debe tener ítems visibles");
});
