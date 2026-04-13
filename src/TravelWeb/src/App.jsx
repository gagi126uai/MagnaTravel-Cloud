import { useCallback, useEffect } from "react";
import { Navigate, Route, Routes, useNavigate } from "react-router-dom";
import Swal from "sweetalert2";
import { api, hasSessionCookieHint } from "./api";
import { clearAuthState, setAuthLoading, setCurrentUser, useAuthState, hasPermission } from "./auth";
import { usePermissions } from "./hooks/usePermissions";
import Layout from "./components/Layout";
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
import { Toaster } from "sonner";
import PackagesPage from "./features/packages/pages/PackagesPage";
import DestinationEditorPage from "./features/packages/pages/DestinationEditorPage";
import AdminHubPage from "./pages/AdminHubPage";

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
    <>
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
              <AlertsProvider>
                <Layout onLogout={handleLogout} isAdmin={adminUser}>
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
                      <Route index element={<Navigate to="/payments/collections" replace />} />
                      <Route path="collections" element={<PaymentsCollectionsPage />} />
                      <Route path="cash" element={<Navigate to="/cash" replace />} />
                      <Route path="invoicing" element={<PaymentsInvoicingPage />} />
                      <Route path="history" element={<PaymentsHistoryPage />} />
                    </Route>
                    <Route path="/cash" element={<CashPage />} />
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
                      path="/audit"
                      element={hasPermission("auditoria.view") ? <AdminHubPage /> : <Navigate to="/dashboard" replace />}
                    />
                  </Routes>                </Layout>
              </AlertsProvider>
            </PrivateRoute>
          }
        />
      </Routes>
    </>
  );
}


