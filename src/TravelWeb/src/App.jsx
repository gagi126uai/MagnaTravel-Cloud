import { useCallback, useEffect } from "react";
import { Navigate, Route, Routes, useNavigate } from "react-router-dom";
import Swal from "sweetalert2";
import { api, hasSessionCookieHint } from "./api";
import { clearAuthState, setAuthLoading, setCurrentUser, useAuthState, hasPermission, isAdmin } from "./auth";
import { usePermissions } from "./hooks/usePermissions";
import Layout from "./components/Layout";
import { ErrorBoundary } from "./components/ErrorBoundary";
import DashboardPage from "./pages/DashboardPage";
import LoginPage from "./pages/LoginPage";
import CustomersPage from "./features/customers/pages/CustomersPage";
import CustomerAccountPage from "./features/customers/pages/CustomerAccountPage";
import ReservasPage from "./features/reservas/pages/ReservasPage";
import ReservaDetailPage from "./features/reservas/pages/ReservaDetailPage";
import PaymentsPage from "./features/payments/pages/PaymentsPage";
import PaymentsCollectionsPage from "./features/payments/pages/PaymentsCollectionsPage";
import PaymentsInvoicingPage from "./features/payments/pages/PaymentsInvoicingPage";
import PaymentsHistoryPage from "./features/payments/pages/PaymentsHistoryPage";
import PaymentsByReservaPage from "./features/payments/pages/PaymentsByReservaPage";
import PaymentsMovementsPage from "./features/payments/pages/PaymentsMovementsPage";
import PaymentsPendingPage from "./features/payments/pages/PaymentsPendingPage";
import MessagesPage from "./features/messages/pages/MessagesPage";
import CashPage from "./features/payments/pages/CashPage";
import SettingsPage from "./pages/SettingsPage";
import SuppliersPage from "./features/suppliers/pages/SuppliersPage";
import SupplierAccountPage from "./features/suppliers/pages/SupplierAccountPage";
import ReportsPage from "./pages/ReportsPage";
import RatesPage from "./pages/RatesPage";
import AnalyticsPage from "./pages/AnalyticsPage";
import QuotesPage from "./pages/QuotesPage";
import CRMPage from "./pages/CRMPage";
import PaymentsTrashPage from "./features/payments/pages/PaymentsTrashPage";
import NotificationsPage from "./pages/NotificationsPage";
import PublicCountryEmbedPage from "./pages/PublicCountryEmbedPage";
import PublicPackageEmbedPage from "./pages/PublicPackageEmbedPage";
import PreviewCountryPage from "./pages/PreviewCountryPage";
import PreviewPackagePage from "./pages/PreviewPackagePage";
import { AlertsProvider } from "./contexts/AlertsContext";
import { OperationalFlagsProvider } from "./contexts/OperationalFlagsContext";
import { Toaster } from "sonner";
import PackagesPage from "./features/packages/pages/PackagesPage";
import DestinationEditorPage from "./features/packages/pages/DestinationEditorPage";
import AdminHubPage from "./pages/AdminHubPage";
import ApprovalsInboxPage from "./features/approvals/pages/ApprovalsInboxPage";
import MyApprovalRequestsPage from "./features/approvals/pages/MyApprovalRequestsPage";
import MovementsPreviewPage from "./features/movements/pages/MovementsPreviewPage";
// ADR-044 T4 (2026-07-10, spec sección 3): "Pendientes con AFIP" se DESARMÓ. El monitor
// pasivo "Comprobantes por resolver" y "Recibos por regularizar" pasaron a vivir DENTRO
// de /facturacion (FacturacionPage.jsx) — PendientesAfipPage.jsx quedó sin ruta que la
// use (se conserva el archivo por si hace falta revertir, pero no se importa más acá).
// Comisiones de vendedor: solo visible para el dueño/admin.
import CommissionsPage from "./features/commissions/pages/CommissionsPage";
// Pantalla global de Facturación (spec 2026-06-28 §4/P14): todos los comprobantes de la agencia.
// Requiere cobranzas.view_all (quien no lo tiene solo ve los suyos desde la cuenta del cliente).
import FacturacionPage from "./features/invoices/pages/FacturacionPage";

function FullScreenLoader() {
  return (
    <div className="flex min-h-screen items-center justify-center bg-slate-50 dark:bg-slate-950">
      <div className="rounded-2xl border border-slate-200 bg-white px-6 py-5 text-sm font-medium text-slate-600 shadow-sm dark:border-slate-800 dark:bg-slate-900 dark:text-slate-300">
        Validando sesion...
      </div>
    </div>
  );
}

function PrivateRoute({ children }) {
  const { user, loading } = useAuthState();

  if (loading) {
    return <FullScreenLoader />;
  }

  if (!user) {
    return <Navigate to="/login" replace />;
  }

  return children;
}

export default function App() {
  const navigate = useNavigate();
  const { user, loading } = useAuthState();
  const adminUser = Boolean(user?.isAdmin);
  usePermissions();

  const handleLogout = useCallback(
    async ({ callServer = true } = {}) => {
      try {
        if (callServer) {
          await api.post("/auth/logout", undefined, { skipAuthRedirect: true });
        }
      } catch {
        // Clear client state even if the API cannot be reached.
      } finally {
        clearAuthState();
        navigate("/login", { replace: true });
      }
    },
    [navigate]
  );

  useEffect(() => {
    let cancelled = false;

    const bootstrapSession = async () => {
      setAuthLoading(true);

      if (!hasSessionCookieHint()) {
        if (!cancelled) {
          clearAuthState();
        }
        return;
      }

      try {
        const currentUser = await api.get("/auth/me", { skipAuthRedirect: true });
        if (!cancelled) {
          setCurrentUser(currentUser);
        }
      } catch {
        if (!cancelled) {
          clearAuthState();
        }
      }
    };

    bootstrapSession();

    return () => {
      cancelled = true;
    };
  }, []);

  useEffect(() => {
    const handleUnauthorized = () => {
      if (!user) {
        clearAuthState();
        navigate("/login", { replace: true });
        return;
      }

      if (document.querySelector(".swal2-container")) {
        return;
      }

      Swal.fire({
        title: "Sesion expirada",
        text: "Tu sesion ya no es valida. Ingresa nuevamente para continuar.",
        icon: "warning",
        confirmButtonText: "Entendido",
        confirmButtonColor: "#4f46e5",
        allowOutsideClick: false,
        allowEscapeKey: false,
      }).then(() => {
        handleLogout({ callServer: false });
      });
    };

    const handleForcedLogout = (event) => {
      handleLogout({ callServer: event?.detail?.callServer ?? false });
    };

    window.addEventListener("auth:unauthorized", handleUnauthorized);
    window.addEventListener("auth:logout", handleForcedLogout);

    return () => {
      window.removeEventListener("auth:unauthorized", handleUnauthorized);
      window.removeEventListener("auth:logout", handleForcedLogout);
    };
  }, [handleLogout, navigate, user]);

  return (
    /*
      ErrorBoundary EXTERNO (variant="fullscreen"): red de ultimo recurso.
      Envuelve todo el arbol (router + Toaster). Si crashea algo fuera del Layout
      (ej: el propio router, providers de contexto, pantallas publicas/embed),
      el usuario ve la pantalla de error completa en lugar de pantalla negra.

      El Toaster va aqui dentro para que los toasts no queden activos cuando
      ya se muestra el fallback de error.

      ErrorBoundary INTERNO (variant="inline", mas abajo en <Layout>): si una
      pantalla privada crashea, el cartel de error se muestra dentro del layout,
      conservando sidebar y topbar. El boundary externo nunca llega a activarse
      en ese caso porque el interno ya atrapó el error.
    */
    <ErrorBoundary variant="fullscreen">
      <Toaster richColors position="top-right" />
      <Routes>
        <Route
          path="/login"
          element={loading ? <FullScreenLoader /> : user ? <Navigate to="/dashboard" replace /> : <LoginPage />}
        />
        <Route path="/embed/countries/:countrySlug" element={<PublicCountryEmbedPage />} />
        <Route path="/embed/packages/:slug" element={<PublicPackageEmbedPage />} />
        <Route
          path="/preview/countries/:countrySlug"
          element={
            <PrivateRoute>
              <PreviewCountryPage />
            </PrivateRoute>
          }
        />
        <Route
          path="/preview/packages/:slug"
          element={
            <PrivateRoute>
              <PreviewPackagePage />
            </PrivateRoute>
          }
        />
        <Route
          path="/*"
          element={
            <PrivateRoute>
              {/*
                OperationalFlagsProvider va dentro de PrivateRoute porque
                GET /afip/settings requiere autenticacion. Al estar aca arriba
                (antes que cualquier pagina autenticada se monte), el fetch
                ocurre una sola vez para toda la sesion. Cuando el usuario
                navega entre /reservas y /reservas/:id, el valor ya esta cacheado
                en el contexto y no hay re-fetch ni parpadeo.
              */}
              <OperationalFlagsProvider>
              <AlertsProvider>
                <Layout onLogout={handleLogout} isAdmin={adminUser}>
                  {/*
                    ErrorBoundary INTERNO (variant="inline"): si cualquier pantalla
                    privada lanza durante el render, el cartel de error aparece en
                    el area de contenido, manteniendo sidebar y topbar visibles.
                    Esto es mucho mejor UX que perder todo el layout por un crash
                    en una sola pagina.
                  */}
                  <ErrorBoundary>
                  <Routes>
                    <Route path="/" element={<Navigate to="/dashboard" replace />} />
                    <Route path="/dashboard" element={<DashboardPage />} />
                    <Route path="/reservas" element={<ReservasPage />} />
                    <Route path="/reservas/:publicId" element={<ReservaDetailPage />} />
                    <Route path="/customers" element={<CustomersPage />} />
                    <Route path="/customers/:publicId/account" element={<CustomerAccountPage />} />
                    <Route path="/suppliers" element={<SuppliersPage />} />
                    <Route path="/suppliers/:publicId/account" element={<SupplierAccountPage />} />
                    <Route path="/payments" element={<PaymentsPage />}>
                      <Route index element={<Navigate to="/payments/reservas" replace />} />
                      {/* B1.15 Fase D'.B: 3 tabs nuevos */}
                      <Route path="reservas" element={<PaymentsByReservaPage />} />
                      <Route path="movements" element={<PaymentsMovementsPage />} />
                      <Route path="pending" element={<PaymentsPendingPage />} />
                      {/* Rutas viejas: mantengo accesibles para bookmarks y links externos.
                          Recomendaria deprecarlas en Fase D'.D si no se usan. */}
                      <Route path="collections" element={<PaymentsCollectionsPage />} />
                      <Route path="cash" element={<Navigate to="/cash" replace />} />
                      <Route path="invoicing" element={<PaymentsInvoicingPage />} />
                      <Route path="history" element={<PaymentsHistoryPage />} />
                    </Route>
                    <Route path="/cash" element={<CashPage />} />
                    <Route
                      path="/messages"
                      element={hasPermission("messages.view") ? <MessagesPage /> : <Navigate to="/dashboard" replace />}
                    />
                    <Route path="/rates" element={<RatesPage />} />
                    <Route path="/quotes" element={<QuotesPage />} />
                    <Route
                      path="/packages"
                      element={hasPermission("paquetes.view") ? <PackagesPage /> : <Navigate to="/dashboard" replace />}
                    />
                    <Route
                      path="/packages/destinations/:publicId"
                      element={hasPermission("paquetes.view") ? <DestinationEditorPage /> : <Navigate to="/dashboard" replace />}
                    />
                    <Route path="/crm" element={hasPermission("crm.view") ? <CRMPage /> : <Navigate to="/dashboard" replace />} />
                    <Route
                      path="/reports"
                      element={hasPermission("reportes.view") ? <ReportsPage /> : <Navigate to="/dashboard" replace />}
                    />
                    <Route
                      path="/settings"
                      element={hasPermission("configuracion.view") ? <SettingsPage /> : <Navigate to="/dashboard" replace />}
                    />
                    <Route
                      path="/analytics"
                      element={hasPermission("reportes.view") ? <AnalyticsPage /> : <Navigate to="/dashboard" replace />}
                    />
                    <Route
                      path="/payments/trash"
                      element={hasPermission("cobranzas.edit") ? <PaymentsTrashPage /> : <Navigate to="/dashboard" replace />}
                    />
                    <Route path="/notifications" element={<NotificationsPage />} />
                    <Route
                      path="/approvals/inbox"
                      element={hasPermission("approvals.review") ? <ApprovalsInboxPage /> : <Navigate to="/dashboard" replace />}
                    />
                    {/* ADR-044 T4 (2026-07-10, spec sección 3): "Pendientes con AFIP" se desarmó — el
                        monitor pasivo ("Comprobantes por resolver") y "Recibos por regularizar" viven
                        ahora dentro de /facturacion (como solapas). Esta ruta vieja queda como redirect
                        para que ningún link/bookmark existente se rompa; el guard de permisos vive en
                        /facturacion (requiere cobranzas.view_all) y en cada solapa adentro. */}
                    <Route path="/pendientes-afip" element={<Navigate to="/facturacion?tab=comprobantes" replace />} />
                    {/* Rutas viejas de las 3 bandejas originales: mismo criterio, apuntan a la nueva
                        ubicación dentro de Facturación. "Recibos por regularizar" conserva su solapa
                        propia; multas y NC por revisar se fusionaron en "Comprobantes por resolver". */}
                    <Route path="/credit-note-reconciliation/inbox" element={<Navigate to="/facturacion?tab=recibos" replace />} />
                    <Route path="/cancellations/debit-notes/inbox" element={<Navigate to="/facturacion?tab=comprobantes" replace />} />
                    <Route path="/cancellations/credit-notes/inbox" element={<Navigate to="/facturacion?tab=comprobantes" replace />} />
                    <Route
                      path="/approvals/my-requests"
                      element={hasPermission("approvals.request") ? <MyApprovalRequestsPage /> : <Navigate to="/dashboard" replace />}
                    />
                    <Route
                      path="/movements"
                      element={hasPermission("cobranzas.view") ? <MovementsPreviewPage /> : <Navigate to="/dashboard" replace />}
                    />
                                      {/* Comisiones de vendedor: solo el dueño/admin puede ver esta pantalla.
                        El backend también valida el permiso. Mismo patrón que /admin. */}
                    <Route
                      path="/commissions"
                      element={isAdmin() ? <CommissionsPage /> : <Navigate to="/dashboard" replace />}
                    />
                    {/* La bandeja global /operator-refunds se eliminó (decisión 5, spec 2026-07-03 P1=C):
                        los reembolsos del operador se ven en la solapa "Reembolsos" de cada ficha. */}
                    {/* Pantalla global de Facturación: todos los comprobantes de la agencia
                        ("Todos los comprobantes", solo con cobranzas.view_all) MÁS, desde
                        ADR-044 T4 (2026-07-10), el monitor "Comprobantes por resolver" y
                        "Recibos por regularizar" que antes vivían en /pendientes-afip.
                        FIX F1 (gate 2026-07-10): la ruta unificada hereda el guard "AL MENOS
                        UNO de los 3 permisos" que ya tenía /pendientes-afip — un Vendedor con
                        SOLO cobranzas.invoice_annul, o un revisor con SOLO approvals.review,
                        seguían entrando a esa bandeja antes de la fusión; exigir acá únicamente
                        cobranzas.view_all les habría cortado el acceso. Dentro de la página,
                        cada solapa se muestra/oculta por SU propio permiso (igual que antes). */}
                    <Route
                      path="/facturacion"
                      element={
                        hasPermission("cobranzas.view_all") ||
                        hasPermission("cobranzas.invoice_annul") ||
                        hasPermission("approvals.review")
                          ? <FacturacionPage />
                          : <Navigate to="/dashboard" replace />
                      }
                    />
                    <Route
                      path="/admin"
                      element={isAdmin() ? <AdminHubPage /> : <Navigate to="/dashboard" replace />}
                    />
                  </Routes>
                  </ErrorBoundary>
                </Layout>
              </AlertsProvider>
              </OperationalFlagsProvider>
            </PrivateRoute>
          }
        />
      </Routes>
    </ErrorBoundary>
  );
}





