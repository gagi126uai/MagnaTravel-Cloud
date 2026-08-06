// Hallazgo 2026-08-06 (revision de seguridad): decide si una falla al intentar refrescar la
// sesion (POST /api/auth/refresh) significa "la sesion ya NO es valida" (hay que desloguear al
// usuario) o es solo una falla pasajera (429 por limite de pedidos, 5xx del backend, o un error
// de red donde fetch ni siquiera llego a responder). SOLO un 401 explicito del propio refresh es
// un "no" real del backend (sin cookie de sesion, o el token esta vencido/revocado de verdad);
// cualquier otra cosa es ruido transitorio que NO deberia tirar abajo una sesion que sigue
// siendo valida.
//
// Antes de este fix, api.js trataba CUALQUIER falla del refresh como "sesion muerta": un 429
// causado por una rafaga de reconexion tras el reinicio del contenedor en un deploy (varias
// pestañas reconectando a la vez) deslogueaba a gente que no habia hecho nada mal.
//
// Funcion PURA (no toca fetch/window) a proposito: se puede testear sin mockear la red.
export function isSessionDefinitelyInvalid(refreshError) {
  return refreshError?.status === 401;
}
