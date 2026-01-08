import { useState } from "react";
import { useNavigate } from "react-router-dom";
import { apiRequest } from "../api";
import { setAuthToken } from "../auth";
import { showError, showSuccess } from "../alerts";

export default function LoginPage() {
  const navigate = useNavigate();
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [fullName, setFullName] = useState("");
  const [isRegister, setIsRegister] = useState(false);

  const handleSubmit = async (event) => {
    event.preventDefault();
    try {
      const path = isRegister ? "/api/auth/register" : "/api/auth/login";
      const payload = isRegister
        ? { fullName, email, password }
        : { email, password };
      const response = await apiRequest(path, {
        method: "POST",
        body: JSON.stringify(payload),
      });

      setAuthToken(response.token);
      await showSuccess(isRegister ? "Cuenta creada correctamente." : "Bienvenido.");
      navigate("/dashboard");
    } catch (err) {
      await showError(err.message || "No pudimos iniciar sesión. Revisa los datos.");
    }
  };

  return (
    <div className="flex min-h-screen items-center justify-center bg-gradient-to-br from-indigo-50 via-slate-50 to-white px-4 dark:from-slate-950 dark:via-slate-950 dark:to-slate-900">
      <div className="w-full max-w-md rounded-3xl border border-slate-200 bg-white/90 p-8 shadow-2xl shadow-indigo-500/10 backdrop-blur dark:border-slate-800 dark:bg-slate-950/90">
        <div className="mb-6 flex items-center gap-3">
          <div className="flex h-12 w-12 items-center justify-center rounded-2xl bg-indigo-600/10 text-indigo-600 dark:text-indigo-300">
            MT
          </div>
          <div>
            <h1 className="text-2xl font-semibold">MagnaTravel</h1>
            <p className="text-xs uppercase tracking-[0.2em] text-slate-400">Backoffice</p>
          </div>
        </div>
        <p className="text-sm text-slate-500 dark:text-slate-400">
          {isRegister ? "Crea tu cuenta de administrador." : "Accede a tu back office."}
        </p>

        <form className="mt-6 space-y-4" onSubmit={handleSubmit}>
          {isRegister && (
            <div>
              <label className="text-sm text-slate-500 dark:text-slate-300">Nombre completo</label>
              <input
                className="mt-1 w-full rounded-xl border border-slate-200 bg-white px-3 py-2 text-slate-900 shadow-sm focus:border-indigo-400 focus:outline-none focus:ring-2 focus:ring-indigo-200 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100 dark:focus:ring-indigo-500/30"
                value={fullName}
                onChange={(event) => setFullName(event.target.value)}
                required
              />
            </div>
          )}
          <div>
            <label className="text-sm text-slate-500 dark:text-slate-300">Email</label>
            <input
              type="email"
              className="mt-1 w-full rounded-xl border border-slate-200 bg-white px-3 py-2 text-slate-900 shadow-sm focus:border-indigo-400 focus:outline-none focus:ring-2 focus:ring-indigo-200 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100 dark:focus:ring-indigo-500/30"
              value={email}
              onChange={(event) => setEmail(event.target.value)}
              required
            />
          </div>
          <div>
            <label className="text-sm text-slate-500 dark:text-slate-300">Contraseña</label>
            <input
              type="password"
              className="mt-1 w-full rounded-xl border border-slate-200 bg-white px-3 py-2 text-slate-900 shadow-sm focus:border-indigo-400 focus:outline-none focus:ring-2 focus:ring-indigo-200 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100 dark:focus:ring-indigo-500/30"
              value={password}
              onChange={(event) => setPassword(event.target.value)}
              required
            />
          </div>
          <button
            type="submit"
            className="w-full rounded-xl bg-indigo-600 px-3 py-3 text-sm font-semibold text-white shadow-lg shadow-indigo-500/30 transition hover:bg-indigo-500"
          >
            {isRegister ? "Crear cuenta" : "Ingresar"}
          </button>
        </form>

        <button
          type="button"
          className="mt-5 text-sm font-medium text-indigo-600 hover:text-indigo-500 dark:text-indigo-300 dark:hover:text-indigo-200"
          onClick={() => setIsRegister((prev) => !prev)}
        >
          {isRegister ? "Ya tengo cuenta" : "Crear cuenta administrador"}
        </button>
      </div>
    </div>
  );
}
