import Sidebar from "./Sidebar";

export default function Layout({ children, onLogout }) {
  return (
    <div className="flex min-h-screen bg-slate-950 text-slate-100">
      <Sidebar onLogout={onLogout} />
      <main className="flex-1 px-8 py-6">{children}</main>
    </div>
  );
}
