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
    <div className="flex min-h-screen items-center justify-center bg-slate-950 px-4">
      <div className="w-full max-w-md rounded-2xl border border-slate-800 bg-slate-900 p-8 shadow-xl">
        <h1 className="text-2xl font-semibold text-white">MagnaTravel</h1>
        <p className="mt-2 text-sm text-slate-400">
          {isRegister ? "Crea tu cuenta de administrador." : "Accede a tu back office."}
        </p>

        <form className="mt-6 space-y-4" onSubmit={handleSubmit}>
          {isRegister && (
            <div>
              <label className="text-sm text-slate-300">Nombre completo</label>
              <input
                className="mt-1 w-full rounded-lg border border-slate-700 bg-slate-800 px-3 py-2 text-slate-100"
                value={fullName}
                onChange={(event) => setFullName(event.target.value)}
                required
              />
            </div>
          )}
          <div>
            <label className="text-sm text-slate-300">Email</label>
            <input
              type="email"
              className="mt-1 w-full rounded-lg border border-slate-700 bg-slate-800 px-3 py-2 text-slate-100"
              value={email}
              onChange={(event) => setEmail(event.target.value)}
              required
            />
          </div>
          <div>
            <label className="text-sm text-slate-300">Contraseña</label>
            <input
              type="password"
              className="mt-1 w-full rounded-lg border border-slate-700 bg-slate-800 px-3 py-2 text-slate-100"
              value={password}
              onChange={(event) => setPassword(event.target.value)}
              required
            />
          </div>
          <button
            type="submit"
            className="w-full rounded-lg bg-indigo-500 px-3 py-2 text-sm font-semibold text-white hover:bg-indigo-600"
          >
            {isRegister ? "Crear cuenta" : "Ingresar"}
          </button>
        </form>

        <button
          type="button"
          className="mt-4 text-sm text-indigo-300 hover:text-indigo-200"
          onClick={() => setIsRegister((prev) => !prev)}
        >
          {isRegister ? "Ya tengo cuenta" : "Crear cuenta administrador"}
        </button>
      </div>
    </div>
  );
}
