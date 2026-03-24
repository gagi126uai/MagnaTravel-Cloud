import { useEffect } from "react";
import { Navigate, Route, Routes, useNavigate } from "react-router-dom";
import { clearAuthToken, isAdmin, isAuthenticated } from "./auth";
import Swal from "sweetalert2";
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
import { AlertsProvider } from "./contexts/AlertsContext";
import { Toaster } from "sonner";

// LEGACY REMOVED: Cupos, Quotes, Tariffs, Agencies

function PrivateRoute({ children }) {
  if (!isAuthenticated()) {
    return <Navigate to="/login" replace />;
  }
  return children;
}

export default function App() {
  const navigate = useNavigate();
  const adminUser = isAdmin();

  const handleLogout = () => {
    clearAuthToken();
    navigate("/login");
  };

  useEffect(() => {
    const handleUnauthorized = () => {
      // Prevent multiple alerts stacking
      if (document.querySelector('.swal2-container')) return;

      Swal.fire({
        title: 'Sesión Expirada',
        text: 'Tu sesión ha caducado por seguridad. Por favor ingresa nuevamente.',
        icon: 'warning',
        confirmButtonText: 'Entendido',
        confirmButtonColor: '#4f46e5', // Indigo-600
        allowOutsideClick: false,
        allowEscapeKey: false
      }).then(() => {
        handleLogout();
      });
    };

    window.addEventListener('auth:unauthorized', handleUnauthorized);
    return () => window.removeEventListener('auth:unauthorized', handleUnauthorized);
  }, [navigate]);

  return (
    <Routes>
      <Route path="/login" element={<LoginPage />} />
      <Route
        path="/*"
        element={
          <PrivateRoute>
            <Toaster richColors position="top-right" />
            <AlertsProvider>
              <Layout onLogout={handleLogout} isAdmin={adminUser}>
                <Routes>
                  <Route path="/" element={<Navigate to="/dashboard" replace />} />
                  <Route path="/dashboard" element={<DashboardPage />} />

                  {/* Core ERP Modules */}
                  <Route path="/reservas" element={<ReservasPage />} />
                  <Route path="/reservas/:publicId" element={<ReservaDetailPage />} />

                  <Route path="/customers" element={<CustomersPage />} />
                  <Route path="/customers/:publicId/account" element={<CustomerAccountPage />} />

                  <Route path="/suppliers" element={<SuppliersPage />} />
                  <Route path="/suppliers/:publicId/account" element={<SupplierAccountPage />} />

                  {/* Treasury */}
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
                  <Route path="/crm" element={adminUser ? <CRMPage /> : <Navigate to="/dashboard" replace />} />

                  {/* Admin */}
                  <Route
                    path="/reports"
                    element={adminUser ? <ReportsPage /> : <Navigate to="/dashboard" replace />}
                  />
                  <Route
                    path="/settings"
                    element={adminUser ? <SettingsPage /> : <Navigate to="/dashboard" replace />}
                  />
                  <Route
                    path="/analytics"
                    element={adminUser ? <AnalyticsPage /> : <Navigate to="/dashboard" replace />}
                  />
                  <Route
                    path="/payments/trash"
                    element={adminUser ? <PaymentsTrashPage /> : <Navigate to="/dashboard" replace />}
                  />
                  <Route
                    path="/notifications"
                    element={<NotificationsPage />}
                  />
                </Routes>
              </Layout>
            </AlertsProvider>
          </PrivateRoute>
        }
      />
    </Routes>
  );
}
