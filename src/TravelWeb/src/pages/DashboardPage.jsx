import { useAuthState } from "../auth";
import AdminDashboard from "./AdminDashboard";
import AgentDashboard from "./AgentDashboard";

export default function DashboardPage() {
    const { user } = useAuthState();
    const isAdmin = Boolean(user?.isAdmin);

    if (isAdmin) {
        return <AdminDashboard />;
    }

    return <AgentDashboard />;
}
