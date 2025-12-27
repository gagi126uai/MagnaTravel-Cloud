import Sidebar from "./Sidebar";

export default function Layout({ children, onLogout, isAdmin }) {
  return (
    <div className="flex min-h-screen bg-gradient-to-br from-slate-950 via-slate-900 to-slate-950 text-slate-100">
      <Sidebar onLogout={onLogout} isAdmin={isAdmin} />
      <main className="flex-1 px-8 py-6">
        <div className="mx-auto max-w-6xl">{children}</div>
      </main>
    </div>
  );
}
