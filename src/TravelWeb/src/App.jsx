import { Navigate, Route, Routes, useNavigate } from "react-router-dom";
import { clearAuthToken, isAdmin, isAuthenticated } from "./auth";
import Layout from "./components/Layout";
import DashboardPage from "./pages/DashboardPage";
import LoginPage from "./pages/LoginPage";
import CustomersPage from "./pages/CustomersPage";
import ReservationsPage from "./pages/ReservationsPage";
import PaymentsPage from "./pages/PaymentsPage";
import QuotesPage from "./pages/QuotesPage";
import ReportsPage from "./pages/ReportsPage";
import SettingsPage from "./pages/SettingsPage";
import SuppliersPage from "./pages/SuppliersPage";
import TariffsPage from "./pages/TariffsPage";
import CuposPage from "./pages/CuposPage";
import TreasuryPage from "./pages/TreasuryPage";
import AgenciesPage from "./pages/AgenciesPage";

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

  return (
    <Routes>
      <Route path="/login" element={<LoginPage />} />
      <Route
        path="/*"
        element={
          <PrivateRoute>
            <Layout onLogout={handleLogout} isAdmin={adminUser}>
              <Routes>
                <Route path="/" element={<Navigate to="/dashboard" replace />} />
                <Route path="/dashboard" element={<DashboardPage />} />
                <Route path="/customers" element={<CustomersPage />} />
                <Route path="/quotes" element={<QuotesPage />} />
                <Route path="/reservations" element={<ReservationsPage />} />
                <Route path="/payments" element={<PaymentsPage />} />
                <Route path="/treasury" element={<TreasuryPage />} />
                <Route path="/tariffs" element={<TariffsPage />} />
                <Route path="/cupos" element={<CuposPage />} />
                <Route path="/suppliers" element={<SuppliersPage />} />
                <Route
                  path="/agencies"
                  element={<AgenciesPage />}
                />
                <Route
                  path="/reports"
                  element={<ReportsPage />}
                />
                <Route
                  path="/settings"
                  element={<SettingsPage />}
                />
              </Routes>
            </Layout>
          </PrivateRoute>
        }
      />
    </Routes>
  );
}
