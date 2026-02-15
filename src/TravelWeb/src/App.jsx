import { useEffect } from "react";
import { Navigate, Route, Routes, useNavigate } from "react-router-dom";
import { clearAuthToken, isAdmin, isAuthenticated } from "./auth";
import Swal from "sweetalert2";
import Layout from "./components/Layout";
import DashboardPage from "./pages/DashboardPage";
import LoginPage from "./pages/LoginPage";
import CustomersPage from "./pages/CustomersPage";
import CustomerAccountPage from "./pages/CustomerAccountPage";
import FilesPage from "./pages/FilesPage";
import FileDetailPage from "./pages/FileDetailPage";
import PaymentsPage from "./pages/PaymentsPage";
import SettingsPage from "./pages/SettingsPage";
import SuppliersPage from "./pages/SuppliersPage";
import SupplierAccountPage from "./pages/SupplierAccountPage";
import ReportsPage from "./pages/ReportsPage";
import RatesPage from "./pages/RatesPage";
import RatesPage from "./pages/RatesPage";
import PaymentsTrashPage from "./pages/PaymentsTrashPage";
import AlertsPage from "./pages/AlertsPage";
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
                  <Route path="/files" element={<FilesPage />} />
                  <Route path="/files/:id" element={<FileDetailPage />} />

                  <Route path="/customers" element={<CustomersPage />} />
                  <Route path="/customers/:id/account" element={<CustomerAccountPage />} />

                  <Route path="/suppliers" element={<SuppliersPage />} />
                  <Route path="/suppliers/:id/account" element={<SupplierAccountPage />} />

                  {/* Treasury */}
                  <Route path="/payments" element={<PaymentsPage />} />
                  <Route path="/rates" element={<RatesPage />} />

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
                    path="/payments/trash"
                    element={adminUser ? <PaymentsTrashPage /> : <Navigate to="/dashboard" replace />}
                  />
                  <Route
                    path="/alerts"
                    element={adminUser ? <AlertsPage /> : <Navigate to="/dashboard" replace />}
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
