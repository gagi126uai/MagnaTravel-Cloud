#!/usr/bin/env bash
set -euo pipefail

# ============================================================================
# QUE ES ESTO
# ----------------------------------------------------------------------------
# Ajusta de forma segura y auditada el proxy_read_timeout/proxy_send_timeout
# del nginx del HOST (Ubuntu, fuera de este repo, en /etc/nginx del VPS) en
# TODOS los locations que hacen de reverse-proxy del vhost del backoffice
# (backoffice.magnaviajesyturismo.com). Ver docs/db-operations.md, seccion
# "Requisito OBLIGATORIO antes de usar la restauracion total en produccion
# (nginx del HOST)".
#
# Motivo: el default de nginx (60s) corta la restauracion total del sistema
# (que puede tardar varios minutos) antes de que termine, dejando un
# pg_restore huerfano en el contenedor.
#
# POR QUE TODOS LOS LOCATIONS Y NO UNO
#   El vhost real tiene el layout clasico: `location /` hacia el contenedor de
#   la web y `location /api` hacia el de la API. El que importa para la
#   restauracion es el de la API, pero identificar "cual es el de la API" por
#   el nombre del path seria adivinar. Se tocan todos: un timeout largo en el
#   proxy de la web estatica es inofensivo (solo cambia cuanto espera nginx a
#   un upstream que no contesta, y esos contestan en milisegundos).
#
# QUIEN LO CORRE
#   Este script asume que YA esta corriendo como root. NO se corre desde el
#   worktree del repo: se corre desde una COPIA INSTALADA POR ROOT en
#   /usr/local/sbin/magnatravel-nginx-timeout.sh (ver bloque SUDOERS). El
#   workflow .github/workflows/ops-nginx.yml invoca esa copia instalada.
#
# ACCIONES
#   ver       : SOLO LECTURA. nginx -v, listado de sites-enabled/available, y
#               CADA location detectado (rango de lineas, sus proxy_*timeout
#               actuales y el bloque completo), sin modificar nada.
#   aplicar   : detecta el archivo y TODOS sus locations con proxy_pass activo
#               (fail-closed: si hay ambiguedad, aborta sin tocar nada); hace
#               UN backup del archivo entero con timestamp en
#               /var/backups/nginx; inserta o actualiza
#               proxy_read_timeout/proxy_send_timeout a 2700s en CADA uno de
#               esos locations, en una sola pasada; corre `nginx -t` UNA vez;
#               si falla, RESTAURA el backup y aborta; si pasa, hace
#               `systemctl reload nginx` UNA vez (nunca restart) y verifica.
#               Requiere confirmacion exacta: APLICAR.
#   revertir  : restaura el backup MAS RECIENTE que dejo `aplicar` para el
#               archivo detectado (el archivo ENTERO, o sea todos los
#               locations juntos), corre `nginx -t`, reload, verifica.
#               Requiere confirmacion exacta: REVERTIR.
#
# IDEMPOTENCIA
#   Correr `aplicar` dos veces no duplica directivas: en cada location, si ya
#   existen proxy_read_timeout/proxy_send_timeout se ACTUALIZA su valor en la
#   misma linea en vez de agregar una nueva. Y si TODOS los locations ya
#   tienen las dos en el valor objetivo, no hace nada (ni backup ni reload).
#
# FAIL-CLOSED
#   Si no se puede determinar con certeza el archivo (ningun server_name activo
#   que mencione el dominio, o el archivo no existe), o si CUALQUIERA de los
#   locations con proxy_pass tiene un layout que este script no sabe editar sin
#   riesgo, ABORTA con un mensaje claro y NO modifica nada: el archivo se edita
#   entero o no se edita. Nunca adivina ni edita "a ver si sale".
#
# DONDE VAN LOS BACKUPS (y por que NO al lado del archivo)
#   Los backups van a /var/backups/nginx/ (se crea si falta, root, 0700), con
#   el nombre original + timestamp. NUNCA al lado del archivo original: en el
#   layout estandar de Ubuntu el archivo vivo suele ser un symlink dentro de
#   /etc/nginx/sites-enabled/, y ese directorio se incluye ENTERO en la
#   config (include sites-enabled/*). Un ".bak-..." ahi adentro seria config
#   ACTIVA: server duplicado, y fatal si el server tiene default_server.
#   Ademas los backups se guardan copiando CONTENIDO (cat), no con `cp -a`:
#   copiar un symlink con `cp -a` deja un backup inservible (un symlink que
#   sigue apuntando al archivo vivo, o sea que "restaurar" no restaura nada).
#
# SYMLINKS
#   El archivo que reporta `nginx -T` puede ser el symlink de sites-enabled.
#   Antes de leer/escribir se resuelve con `readlink -f` y se trabaja SIEMPRE
#   sobre el archivo real (normalmente en sites-available). La salida
#   documenta ambas rutas.
#
# QUE NO HACE
#   No toca contenedores, volumenes, ni datos. Solo lee/escribe UN archivo de
#   configuracion de nginx del HOST (con backup previo) y recarga (reload, no
#   restart) el servicio nginx del HOST.
#
# LIMITE CONOCIDO
#   Para ignorar lineas comentadas, el script trabaja sobre una copia del
#   archivo con todo lo que va desde "#" hasta el fin de linea borrado. Eso
#   es correcto para configs de nginx normales, pero si alguna directiva
#   tuviera un "#" LITERAL adentro de un string entrecomillado, esa linea se
#   veria truncada para el analisis (no para la escritura: el archivo real
#   nunca se toca por ese lado). En ese caso, editar a mano.
#
# ----------------------------------------------------------------------------
# SUDOERS — INSTALACION UNA SOLA VEZ, A MANO, POR GASTON, EN EL VPS
# ----------------------------------------------------------------------------
#   POR QUE NO se le da NOPASSWD al script del repo:
#     El worktree del VPS lo REESCRIBE el usuario de deploy en cada `git
#     pull`. Darle NOPASSWD a un archivo que ese mismo usuario puede
#     reescribir es darle root efectivo a cualquiera que pueda pushear a
#     main. Por eso el modelo es "copia instalada por root fuera del
#     worktree": root copia el script a /usr/local/sbin/ (root:root 0755, el
#     usuario de deploy NO lo puede modificar) y el sudoers apunta AHI.
#
#   BLOQUE COPY-PASTE (correr EN EL VPS, con el usuario SSH de deploy, desde
#   el directorio del proyecto). Se re-corre TAL CUAL cada vez que cambie
#   scripts/ops/nginx-timeout.sh en el repo:
#
#     sudo install -o root -g root -m 0755 \
#       scripts/ops/nginx-timeout.sh \
#       /usr/local/sbin/magnatravel-nginx-timeout.sh
#
#     printf '%s ALL=(root) NOPASSWD: /usr/local/sbin/magnatravel-nginx-timeout.sh *\n' "$(whoami)" \
#       | sudo tee /etc/sudoers.d/ops-nginx > /dev/null
#     sudo chmod 0440 /etc/sudoers.d/ops-nginx
#     sudo visudo -c -f /etc/sudoers.d/ops-nginx
#
#   El `*` del final de la regla es NECESARIO: sin comodin, sudoers exige que
#   los argumentos coincidan EXACTAMENTE, y este script siempre recibe dos
#   (accion y confirmacion). Ojo con el argumento vacio: sudoers NO matchea
#   bien "sin argumentos", asi que el workflow manda "-" como marcador cuando
#   la persona no escribio confirmacion (ver mas abajo).
#
#   NO otorgar NOPASSWD sobre awk/grep/sed/cp/tee/nginx/systemctl de forma
#   generica: con argumentos libres eso equivale a root sin restricciones.
# ============================================================================

DOMAIN="backoffice.magnaviajesyturismo.com"
TIMEOUT="2700s"
INSTALL_PATH="/usr/local/sbin/magnatravel-nginx-timeout.sh"
BACKUP_DIR="/var/backups/nginx"
BACKUPS_A_CONSERVAR=10

ACCION="${1:-}"
CONFIRMACION="${2:-}"

# El workflow manda "-" cuando la persona no escribio nada en 'confirmacion'.
# Motivo: sudoers no matchea de forma confiable un argumento VACIO, asi que
# se manda un marcador no-vacio que aca se traduce de vuelta a "no escribio
# nada" (y por lo tanto nunca puede valer APLICAR ni REVERTIR).
if [ "${CONFIRMACION}" = "-" ]; then
  CONFIRMACION=""
fi

if [ "$(id -u)" -ne 0 ]; then
  echo "ERROR: este script debe correr como root (via sudo). No se ejecuta nada."
  echo "Se invoca desde .github/workflows/ops-nginx.yml como:"
  echo "  sudo -n ${INSTALL_PATH} <accion> <confirmacion>"
  exit 1
fi

# ----------------------------------------------------------------------------
# Limpieza de archivos temporales: pase lo que pase (error, exit temprano,
# señal), los temporales que crea el script se borran. Nunca borra otra cosa:
# solo las rutas que el propio script fue anotando en TMP_FILES.
# ----------------------------------------------------------------------------
TMP_FILES=()
limpiar_temporales() {
  local f
  for f in "${TMP_FILES[@]:-}"; do
    [ -n "${f}" ] && rm -f -- "${f}"
  done
}
trap limpiar_temporales EXIT

nuevo_temporal() {
  local t
  t="$(mktemp)"
  TMP_FILES+=("${t}")
  printf '%s' "${t}"
}

# ----------------------------------------------------------------------------
# Devuelve el valor crudo de una directiva en una linea dada (por ejemplo
# "2700s", "60", "5m"), o vacio si no se puede parsear. NUNCA falla: si no
# entiende la linea devuelve vacio, y quien llama lo trata como "distinto del
# objetivo" (que es lo seguro: reescribir de mas es inofensivo, morir mudo a
# mitad de camino no).
# ----------------------------------------------------------------------------
valor_directiva() {
  local numero_linea="$1" directiva="$2" linea valor
  linea="$(sed -n "${numero_linea}p" "${REAL_FILE}" 2>/dev/null || true)"
  linea="${linea%%#*}"
  valor="$(printf '%s\n' "${linea}" | sed -nE "s/.*${directiva}[[:space:]]+([^;[:space:]]+).*/\1/p" || true)"
  printf '%s' "${valor}"
}

# ----------------------------------------------------------------------------
# Encuentra CON CERTEZA el archivo de config y TODOS los locations que hacen
# de proxy en ese archivo, a partir de la config ACTIVA de nginx (nginx -T),
# nunca adivinando por convencion de nombres.
#
# POR QUE VARIOS LOCATIONS (y no uno solo)
#   El vhost real del backoffice tiene el layout clasico: un `location /` que
#   proxypasea al contenedor de la web y un `location /api` que proxypasea al
#   contenedor de la API. Antes este script exigia EXACTAMENTE 1 proxy_pass y
#   abortaba en ese vhost. Ahora acepta N: pone el timeout largo en TODOS los
#   locations con proxy_pass del archivo. El que de verdad importa es el de la
#   API (por ahi va la restauracion total); un timeout largo en el proxy de la
#   web estatica es inofensivo (solo cambia cuanto espera nginx a un upstream
#   que no contesta, y esos responden en milisegundos).
#
# Deja el resultado en las variables globales:
#   CONFIG_FILE  : la ruta tal cual la reporta nginx -T (puede ser un symlink)
#   REAL_FILE    : el archivo REAL a editar (symlink ya resuelto)
#   CLEAN_FILE   : copia temporal de REAL_FILE sin comentarios (para analizar)
#   BASE_NAME    : nombre del archivo real (para nombrar los backups)
#   arrays paralelos, uno por location con proxy_pass activo:
#     LOC_START[]  linea del 'location'
#     LOC_END[]    linea del '}' que lo cierra
#     LOC_PROXY[]  linea de su proxy_pass
#     LOC_RT[]     linea de su proxy_read_timeout, o "" si no lo tiene
#     LOC_ST[]     linea de su proxy_send_timeout, o "" si no lo tiene
#     LOC_INDENT[] sangria a usar si hay que insertar lineas nuevas
# Devuelve 1 y explica el motivo si no puede determinarlo con certeza.
# ----------------------------------------------------------------------------
resolver_config() {
  NGINX_T="$(nginx -T 2>&1)" || { echo "ERROR: 'nginx -T' fallo:"; echo "${NGINX_T}"; return 1; }

  # Busca el archivo cuyo server_name menciona el dominio. Dos detalles:
  #  - el dominio se compara con index() (busqueda LITERAL de texto), no como
  #    expresion regular: si fuera regex, los puntos matchearian cualquier
  #    caracter y "backofficeXmagnaviajesyturismoYcom" contaria como match;
  #  - antes de mirar la linea se le borra el comentario, asi un server_name
  #    comentado no cuenta como config activa.
  CONFIG_FILE="$(printf '%s\n' "${NGINX_T}" | awk -v domain="${DOMAIN}" '
    /^# configuration file / {
      line = $0
      sub(/^# configuration file /, "", line)
      sub(/:$/, "", line)
      current = line
      next
    }
    {
      l = $0
      sub(/#.*$/, "", l)
      if (l ~ /^[[:space:]]*server_name[[:space:]]/ && index(l, domain) > 0) { print current; exit }
    }
  ')"

  if [ -z "${CONFIG_FILE:-}" ]; then
    echo "ERROR: no se encontro ningun 'server_name' activo (no comentado) que mencione '${DOMAIN}' en la config activa (nginx -T)."
    return 1
  fi
  if [ ! -e "${CONFIG_FILE}" ]; then
    echo "ERROR: nginx -T referencia '${CONFIG_FILE}' pero el archivo no existe en disco."
    return 1
  fi

  # Ubuntu estandar: /etc/nginx/sites-enabled/<sitio> es un SYMLINK a
  # /etc/nginx/sites-available/<sitio>. Se edita SIEMPRE el archivo real.
  REAL_FILE="$(readlink -f "${CONFIG_FILE}")"
  if [ -z "${REAL_FILE:-}" ] || [ ! -f "${REAL_FILE}" ]; then
    echo "ERROR: no se pudo resolver '${CONFIG_FILE}' a un archivo regular (readlink -f dio: '${REAL_FILE:-}')."
    return 1
  fi

  echo "Archivo de configuracion detectado: ${CONFIG_FILE}"
  if [ "${REAL_FILE}" != "${CONFIG_FILE}" ]; then
    echo "  (es un symlink) archivo REAL que se lee/edita: ${REAL_FILE}"
  else
    echo "  (no es symlink: se lee/edita ese mismo archivo)"
  fi

  # Copia sin comentarios, MISMA cantidad de lineas (solo se vacia lo que va
  # de '#' al fin de linea), para que los numeros de linea sigan sirviendo
  # contra el archivo real. Todo el analisis se hace contra esta copia; el
  # archivo real solo se toca al escribir.
  CLEAN_FILE="$(nuevo_temporal)"
  sed 's/#.*$//' "${REAL_FILE}" > "${CLEAN_FILE}"

  # El patron acepta proxy_pass al principio de la linea O despues de '{' / ';'
  # (o sea, tambien el caso "location / { proxy_pass ...; }" de una sola
  # linea). Si se buscara SOLO al principio de la linea, ese layout daria
  # "0 proxy_pass" y el script abortaria con un mensaje enganoso, en vez de
  # con el mensaje claro de "este layout de location no esta contemplado".
  # El [[:space:]] final evita confundirlo con proxy_pass_header.
  # Todos los proxy_pass ACTIVOS (los comentados ya no estan en CLEAN_FILE).
  # El patron acepta proxy_pass al principio de la linea O despues de '{' / ';'
  # (o sea, tambien el caso "location / { proxy_pass ...; }" de una sola
  # linea). Si se buscara SOLO al principio de la linea, ese layout daria
  # "0 proxy_pass" y el script abortaria con un mensaje enganoso, en vez de
  # con el mensaje claro de "este layout de location no esta contemplado".
  # El [[:space:]] final evita confundirlo con proxy_pass_header.
  PROXY_LINES=()
  mapfile -t PROXY_LINES < <(grep -nE '(^|[{;])[[:space:]]*proxy_pass[[:space:]]' "${CLEAN_FILE}" | cut -d: -f1)

  if [ "${#PROXY_LINES[@]}" -eq 0 ]; then
    echo "ERROR: no se encontro ningun 'proxy_pass' activo (sin contar comentarios) en ${REAL_FILE}."
    echo "Fail-closed: si este vhost no proxypasea a ningun lado, no hay nada que este script deba tocar."
    return 1
  fi

  LOC_START=(); LOC_END=(); LOC_PROXY=(); LOC_RT=(); LOC_ST=(); LOC_INDENT=()
  local pl loc_line loc_text block_end indent ya j problemas=0

  for pl in "${PROXY_LINES[@]}"; do
    loc_line="$(awk -v proxy_line="${pl}" '
      /^[[:space:]]*location[[:space:]]/ { loc = NR }
      NR == proxy_line { print loc; exit }
    ' "${CLEAN_FILE}")"

    if [ -z "${loc_line}" ]; then
      echo "ERROR: el proxy_pass de la linea ${pl} de ${REAL_FILE} no esta dentro de ningun bloque 'location'"
      echo "  (esta suelto a nivel server). Este layout no esta contemplado: editalo a mano segun docs/db-operations.md."
      problemas=$((problemas + 1))
      continue
    fi

    # Dos proxy_pass en el MISMO location: se registra una sola vez.
    ya=""
    for j in "${!LOC_START[@]}"; do
      if [ "${LOC_START[$j]}" = "${loc_line}" ]; then ya="si"; break; fi
    done
    if [ -n "${ya}" ]; then
      continue
    fi

    # --------------------------------------------------------------------
    # Layouts de 'location' que este script NO sabe editar sin romper algo.
    # Se aborta TODO (ver el chequeo de 'problemas' al final): el archivo se
    # edita entero o no se edita, nunca a medias.
    #  (a) location y su bloque en UNA sola linea -> insertar "despues de la
    #      linea del location" dejaria las directivas FUERA del location, con
    #      alcance de server entero. Silencioso: nginx -t pasa igual.
    #  (b) la llave de apertura en la linea SIGUIENTE -> insertar despues de
    #      la linea del location las meteria ENTRE el 'location' y su '{'.
    #      Ahi nginx -t falla, pero con un error que no explica nada.
    # --------------------------------------------------------------------
    loc_text="$(sed -n "${loc_line}p" "${CLEAN_FILE}")"
    if ! printf '%s' "${loc_text}" | grep -q '{'; then
      echo "ERROR: el 'location' de la linea ${loc_line} de ${REAL_FILE} abre la llave '{' en OTRA linea."
      echo "  Este layout de location no esta contemplado por este script: editalo a mano segun docs/db-operations.md."
      problemas=$((problemas + 1))
      continue
    fi
    if printf '%s' "${loc_text}" | grep -q '}'; then
      echo "ERROR: el 'location' de la linea ${loc_line} de ${REAL_FILE} abre y cierra en la MISMA linea (bloque de una sola linea)."
      echo "  Este layout de location no esta contemplado por este script: editalo a mano segun docs/db-operations.md."
      problemas=$((problemas + 1))
      continue
    fi

    # Cierre del bloque contando llaves sobre la copia SIN comentarios (una
    # llave dentro de un comentario no cuenta).
    block_end="$(awk -v start="${loc_line}" '
      BEGIN { depth = 0 }
      NR < start { next }
      {
        depth += gsub(/\{/, "{")
        depth -= gsub(/\}/, "}")
        if (depth == 0) { print NR; exit }
      }
    ' "${CLEAN_FILE}")"

    if [ -z "${block_end}" ]; then
      echo "ERROR: no se pudo determinar el cierre del bloque location de la linea ${loc_line} en ${REAL_FILE}; llaves no balanceadas."
      problemas=$((problemas + 1))
      continue
    fi
    if [ "${block_end}" -le "${loc_line}" ]; then
      echo "ERROR: el bloque location de la linea ${loc_line} de ${REAL_FILE} no abarca ninguna linea propia (cierre calculado: ${block_end})."
      echo "  Este layout de location no esta contemplado por este script: editalo a mano segun docs/db-operations.md."
      problemas=$((problemas + 1))
      continue
    fi

    LOC_START+=("${loc_line}")
    LOC_END+=("${block_end}")
    LOC_PROXY+=("${pl}")
    LOC_RT+=("$(awk -v s="${loc_line}" -v e="${block_end}" 'NR>=s && NR<=e && /proxy_read_timeout/ {print NR; exit}' "${CLEAN_FILE}")")
    LOC_ST+=("$(awk -v s="${loc_line}" -v e="${block_end}" 'NR>=s && NR<=e && /proxy_send_timeout/ {print NR; exit}' "${CLEAN_FILE}")")

    # Sangria para las lineas nuevas: la misma que ya usa el proxy_pass de
    # ESE location (cada location puede tener su propia profundidad).
    indent="$(sed -n "${pl}p" "${REAL_FILE}" | sed -E 's/^([[:space:]]*).*/\1/')"
    [ -n "${indent}" ] || indent="        "
    LOC_INDENT+=("${indent}")
  done

  # Fail-closed GLOBAL: si CUALQUIER location con proxy_pass tiene un layout
  # que no sabemos editar, no se toca NADA. El archivo se edita entero o no se
  # edita: dejar la mitad de los locations con timeout largo y la otra mitad
  # sin tocar seria peor que no hacer nada (y mas dificil de diagnosticar).
  if [ "${problemas}" -ne 0 ]; then
    echo
    echo "ABORTADO (fail-closed): ${problemas} location(es) con proxy_pass tienen un layout no contemplado."
    echo "No se modifica NADA del archivo (se edita entero o no se edita)."
    return 1
  fi
  if [ "${#LOC_START[@]}" -eq 0 ]; then
    echo "ERROR: no quedo ningun location con proxy_pass utilizable en ${REAL_FILE}."
    return 1
  fi

  BASE_NAME="$(basename "${REAL_FILE}")"

  echo "Locations con proxy_pass activo detectados: ${#LOC_START[@]}"
  return 0
}

# ----------------------------------------------------------------------------
# Imprime, para cada location detectado, su rango de lineas y el estado actual
# de sus dos timeouts. Se usa igual en 'ver' y en 'aplicar'.
# ----------------------------------------------------------------------------
describir_locations() {
  local i n
  for i in "${!LOC_START[@]}"; do
    n=$((i + 1))
    echo "  [${n}] location de las lineas ${LOC_START[$i]}-${LOC_END[$i]} (proxy_pass en la linea ${LOC_PROXY[$i]}):"
    echo "      $(sed -n "${LOC_START[$i]}p" "${REAL_FILE}" | sed -e 's/^[[:space:]]*//')"
    if [ -n "${LOC_RT[$i]}" ]; then
      echo "      proxy_read_timeout actual (linea ${LOC_RT[$i]}): $(sed -n "${LOC_RT[$i]}p" "${REAL_FILE}" | sed -e 's/^[[:space:]]*//')"
    else
      echo "      proxy_read_timeout: no configurado aca (rige el default de nginx, 60s)"
    fi
    if [ -n "${LOC_ST[$i]}" ]; then
      echo "      proxy_send_timeout actual (linea ${LOC_ST[$i]}): $(sed -n "${LOC_ST[$i]}p" "${REAL_FILE}" | sed -e 's/^[[:space:]]*//')"
    else
      echo "      proxy_send_timeout: no configurado aca (rige el default de nginx, 60s)"
    fi
  done
}

# ----------------------------------------------------------------------------
# Backups: siempre en BACKUP_DIR, nunca al lado del archivo (ver cabecera).
# ----------------------------------------------------------------------------
asegurar_directorio_backups() {
  if [ ! -d "${BACKUP_DIR}" ]; then
    mkdir -p "${BACKUP_DIR}"
    chmod 0700 "${BACKUP_DIR}"
    # A stderr a proposito: quien llama a crear_backup captura su stdout para
    # quedarse con la ruta del backup, y este aviso la ensuciaria.
    echo "(se creo el directorio de backups ${BACKUP_DIR}, solo root)" >&2
  fi
}

# Guarda el CONTENIDO del archivo (no un symlink) en un backup nuevo.
# Imprime la ruta del backup por stdout, los mensajes van por stderr.
crear_backup() {
  local sufijo="${1:-}" destino
  asegurar_directorio_backups
  destino="${BACKUP_DIR}/${BASE_NAME}.bak-$(date +%Y%m%d-%H%M%S)${sufijo}"
  cat "${REAL_FILE}" > "${destino}"
  chmod 0600 "${destino}"
  printf '%s' "${destino}"
}

# Lista los backups de este archivo, del mas nuevo al mas viejo, ordenados
# POR NOMBRE (el timestamp esta en el nombre).
#
# POR QUE NO por fecha de modificacion (`ls -t`): los backups se escriben
# copiando contenido y, con `cp -a`, la fecha que quedaba era la del ORIGEN,
# no la del backup. Resultado: el "-pre-revert" (copia del archivo vivo,
# recien modificado) ganaba como "mas reciente" y `revertir` terminaba
# RE-APLICANDO el cambio en vez de sacarlo. Ordenar por nombre no depende de
# mtime, y el argumento 'tipo' saca los -pre-revert de los candidatos.
#
#   tipo=normales    -> solo los que dejo 'aplicar' (candidatos a restaurar)
#   tipo=pre-revert  -> solo las fotos de seguridad que deja 'revertir'
listar_backups() {
  local tipo="$1"
  if [ ! -d "${BACKUP_DIR}" ]; then
    return 0
  fi
  case "${tipo}" in
    normales)
      ls -1 "${BACKUP_DIR}/${BASE_NAME}".bak-* 2>/dev/null | grep -v -- '-pre-revert$' | sort -r || true
      ;;
    pre-revert)
      ls -1 "${BACKUP_DIR}/${BASE_NAME}".bak-*-pre-revert 2>/dev/null | sort -r || true
      ;;
  esac
}

# Poda simple: conserva los BACKUPS_A_CONSERVAR mas nuevos de cada familia y
# borra el resto. Solo toca rutas que salieron de listar_backups (o sea,
# archivos creados por este mismo script dentro de BACKUP_DIR).
podar_backups() {
  local tipo="$1" viejos=() f
  mapfile -t viejos < <(listar_backups "${tipo}" | tail -n "+$((BACKUPS_A_CONSERVAR + 1))")
  for f in "${viejos[@]:-}"; do
    [ -n "${f}" ] || continue
    rm -f -- "${f}"
    echo "  (poda de backups viejos, se conservan los ${BACKUPS_A_CONSERVAR} mas nuevos) borrado: ${f}"
  done
}

# Aviso, no bloqueo: backups viejos al lado del archivo son peligrosos si el
# archivo vive en sites-enabled (se incluirian como config activa).
avisar_backups_legacy() {
  local sueltos=()
  mapfile -t sueltos < <(ls -1 "${CONFIG_FILE}".bak-* "${REAL_FILE}".bak-* 2>/dev/null | sort -u || true)
  if [ "${#sueltos[@]}" -gt 0 ]; then
    echo
    echo "ADVERTENCIA: hay archivos .bak-* AL LADO de la config de nginx:"
    printf '  %s\n' "${sueltos[@]}"
    echo "Si alguno esta dentro de /etc/nginx/sites-enabled/, nginx lo esta cargando como config ACTIVA"
    echo "(include sites-enabled/*): eso duplica el server y puede romper el sitio. Moverlos a mano a"
    echo "${BACKUP_DIR}/ o a otro lado FUERA de /etc/nginx. Este script ya no crea backups ahi."
  fi
}

exigir_confirmacion() {
  local esperado="$1"
  if [ "${CONFIRMACION}" != "${esperado}" ]; then
    echo "ERROR: esta accion escribe configuracion de nginx del HOST. Hay que pasar confirmacion=${esperado} (texto exacto). Valor recibido: '${CONFIRMACION}'"
    exit 1
  fi
}

# Verificacion final tras un reload. Si la directiva NO aparece puede ser
# perfectamente lo esperado (justo despues de 'revertir'), asi que el mensaje
# lo aclara y el script NO termina en rojo por eso.
verificar_post_reload() {
  local contexto="$1"
  nginx -T 2>&1 | grep -E -A3 'proxy_(read|send)_timeout' \
    || echo "(sin proxy_*_timeout en la config activa — es lo esperado tras revertir; ${contexto})"
}

accion_ver() {
  echo "== nginx -v =="
  nginx -v 2>&1
  echo

  echo "== /etc/nginx/sites-enabled =="
  ls -la /etc/nginx/sites-enabled/ 2>&1 || echo "(no existe sites-enabled)"
  echo

  echo "== /etc/nginx/sites-available =="
  ls -la /etc/nginx/sites-available/ 2>&1 || echo "(no existe sites-available)"
  echo

  echo "== Deteccion del archivo y los locations del backoffice (${DOMAIN}) =="
  if resolver_config; then
    echo
    echo "== Resumen de los locations que 'aplicar' tocaria =="
    describir_locations

    local i n
    for i in "${!LOC_START[@]}"; do
      n=$((i + 1))
      echo
      echo "== [${n}] Bloque completo: lineas ${LOC_START[$i]}-${LOC_END[$i]} de ${REAL_FILE} =="
      sed -n "${LOC_START[$i]},${LOC_END[$i]}p" "${REAL_FILE}"
    done

    echo
    echo "== Backups disponibles para revertir (${BACKUP_DIR}, del mas nuevo al mas viejo) =="
    if [ -n "$(listar_backups normales)" ]; then
      listar_backups normales | sed -e 's/^/  /'
    else
      echo "  (todavia no hay backups: 'aplicar' crea el primero)"
    fi
    avisar_backups_legacy
  else
    echo
    echo "(No se pudo identificar el archivo/los locations con certeza; ver el error de arriba. 'aplicar' abortaria en el mismo punto.)"
  fi
}

accion_aplicar() {
  exigir_confirmacion "APLICAR"

  echo "== Paso 1/5: deteccion (fail-closed) =="
  if ! resolver_config; then
    echo "ABORTADO: no se toco ningun archivo."
    exit 1
  fi

  echo
  describir_locations

  # ------------------------------------------------------------------------
  # Idempotencia: si TODOS los locations ya tienen las DOS directivas en el
  # valor objetivo, no se toca nada (ni backup, ni reload).
  #
  # Si algun valor no se puede parsear (por ejemplo "60" sin sufijo, o "5m"),
  # valor_directiva devuelve vacio: cuenta como "distinto del objetivo" y se
  # reescribe. Nunca se muere en silencio por no entender un valor.
  # ------------------------------------------------------------------------
  local i falta=0
  UPD_RT=""; UPD_ST=""; INS_SPECS=""
  for i in "${!LOC_START[@]}"; do
    local flags=""
    if [ -n "${LOC_RT[$i]}" ]; then
      if [ "$(valor_directiva "${LOC_RT[$i]}" 'proxy_read_timeout')" != "${TIMEOUT}" ]; then
        UPD_RT="${UPD_RT}${LOC_RT[$i]},"
        falta=$((falta + 1))
      fi
    else
      flags="R"
      falta=$((falta + 1))
    fi
    if [ -n "${LOC_ST[$i]}" ]; then
      if [ "$(valor_directiva "${LOC_ST[$i]}" 'proxy_send_timeout')" != "${TIMEOUT}" ]; then
        UPD_ST="${UPD_ST}${LOC_ST[$i]},"
        falta=$((falta + 1))
      fi
    else
      if [ "${flags}" = "R" ]; then flags="B"; else flags="S"; fi
      falta=$((falta + 1))
    fi
    # Spec de insercion: "linea_del_location:flags:sangria", registros
    # separados por ';'. Se parsea con split(...,":") en awk, asi la sangria
    # (que es solo espacios/tabs) viaja intacta en el tercer campo.
    if [ -n "${flags}" ]; then
      INS_SPECS="${INS_SPECS}${LOC_START[$i]}:${flags}:${LOC_INDENT[$i]};"
    fi
  done

  if [ "${falta}" -eq 0 ]; then
    echo
    echo "Los ${#LOC_START[@]} location(es) ya tienen proxy_read_timeout y proxy_send_timeout en ${TIMEOUT}."
    echo "Nada que cambiar (idempotente): no se crea backup nuevo ni se recarga nginx."
    exit 0
  fi

  echo
  echo "== Paso 2/5: backup con timestamp =="
  BACKUP_PATH="$(crear_backup)"
  echo "Backup creado: ${BACKUP_PATH}"
  echo "(es una copia del CONTENIDO de ${REAL_FILE}, fuera de /etc/nginx, para que nginx no la cargue)"
  echo "(UN backup del archivo ENTERO, aunque se toquen varios locations: revertir vuelve todo junto)"
  podar_backups normales

  echo
  echo "== Paso 3/5: insertar/actualizar proxy_read_timeout y proxy_send_timeout (${TIMEOUT}) en ${#LOC_START[@]} location(es) =="
  NEW_RT="proxy_read_timeout ${TIMEOUT};"
  NEW_ST="proxy_send_timeout ${TIMEOUT};"
  TMP_FILE="$(nuevo_temporal)"

  # UNA sola pasada por el archivo, atendiendo a todos los locations:
  #   upd_rt / upd_st : lineas donde hay que REEMPLAZAR el valor existente
  #   ins_specs       : locations donde hay que INSERTAR la directiva que falta
  awk -v upd_rt="${UPD_RT}" -v upd_st="${UPD_ST}" -v ins_specs="${INS_SPECS}" \
      -v rt="${NEW_RT}" -v st="${NEW_ST}" '
    BEGIN {
      n = split(upd_rt, a, ",")
      for (i = 1; i <= n; i++) if (a[i] != "") RT_UPD[a[i] + 0] = 1
      n = split(upd_st, b, ",")
      for (i = 1; i <= n; i++) if (b[i] != "") ST_UPD[b[i] + 0] = 1
      n = split(ins_specs, recs, ";")
      for (i = 1; i <= n; i++) {
        if (recs[i] == "") continue
        split(recs[i], f, ":")
        INS_FLAG[f[1] + 0] = f[2]
        INS_IND[f[1] + 0] = f[3]
      }
    }
    {
      line = $0
      if (NR in RT_UPD) { sub(/proxy_read_timeout[^;]*;/, rt, line) }
      if (NR in ST_UPD) { sub(/proxy_send_timeout[^;]*;/, st, line) }
      print line
      if (NR in INS_FLAG) {
        if (INS_FLAG[NR] == "B" || INS_FLAG[NR] == "R") { print INS_IND[NR] rt }
        if (INS_FLAG[NR] == "B" || INS_FLAG[NR] == "S") { print INS_IND[NR] st }
      }
    }
  ' "${REAL_FILE}" > "${TMP_FILE}"

  # Se escribe el CONTENIDO dentro del archivo que ya existe (no se reemplaza
  # el archivo): asi conserva dueño y permisos originales. Con `cp -a` del
  # temporal heredaria el 0600 de mktemp y nginx podria quedarse sin poder
  # leer su propia config.
  cat "${TMP_FILE}" > "${REAL_FILE}"

  echo
  echo "== Paso 4/5: 'nginx -t' (si falla, se restaura el backup y se aborta) =="
  if ! nginx -t 2>&1; then
    echo "ERROR: 'nginx -t' fallo con la config nueva. Restaurando backup y abortando."
    cat "${BACKUP_PATH}" > "${REAL_FILE}"
    echo "Backup restaurado desde: ${BACKUP_PATH}. La config de nginx quedo IGUAL que antes de correr esta accion."
    exit 1
  fi

  systemctl reload nginx

  echo
  echo "== Paso 5/5: verificacion post-reload =="
  verificar_post_reload "recien se aplicaron los timeouts, asi que si esto sale vacio hay algo raro: revisar"
}

accion_revertir() {
  exigir_confirmacion "REVERTIR"

  echo "== Paso 1/4: deteccion (fail-closed) =="
  if ! resolver_config; then
    echo "ABORTADO: no se toco ningun archivo."
    exit 1
  fi

  echo
  echo "== Paso 2/4: ubicar el backup mas reciente de ${REAL_FILE} =="
  CANDIDATOS=()
  mapfile -t CANDIDATOS < <(listar_backups normales)
  LATEST_BACKUP="${CANDIDATOS[0]:-}"
  if [ -z "${LATEST_BACKUP}" ]; then
    echo "ERROR: no hay ningun backup '${BACKUP_DIR}/${BASE_NAME}.bak-*' para restaurar. No se toco nada."
    avisar_backups_legacy
    exit 1
  fi
  echo "Backup a restaurar: ${LATEST_BACKUP}"
  echo "(elegido por NOMBRE — el timestamp esta en el nombre — y excluyendo los '-pre-revert', que son fotos"
  echo " del estado YA modificado y no sirven como punto al que volver)"

  echo
  echo "== Paso 3/4: restaurar y validar =="
  PRE_REVERT_BACKUP="$(crear_backup '-pre-revert')"
  echo "(por las dudas, tambien se guardo el estado actual antes de revertir en: ${PRE_REVERT_BACKUP})"
  podar_backups pre-revert

  cat "${LATEST_BACKUP}" > "${REAL_FILE}"

  if ! nginx -t 2>&1; then
    echo "ERROR: 'nginx -t' fallo tras restaurar '${LATEST_BACKUP}'. Restaurando el estado previo a este revert y abortando."
    cat "${PRE_REVERT_BACKUP}" > "${REAL_FILE}"
    exit 1
  fi

  systemctl reload nginx

  # El backup NO se borra: correr 'revertir' dos veces seguidas vuelve a
  # dejar exactamente el mismo estado original (idempotente e inofensivo).
  # Antes no era asi: elegia el '-pre-revert' como "mas reciente" y el
  # segundo revertir RE-APLICABA el cambio.

  echo
  echo "== Paso 4/4: verificacion post-reload =="
  verificar_post_reload "se acaba de revertir"
}

case "${ACCION}" in
  ver)
    accion_ver
    ;;
  aplicar)
    accion_aplicar
    ;;
  revertir)
    accion_revertir
    ;;
  *)
    echo "ERROR: accion desconocida: '${ACCION}'. Usar: ver | aplicar | revertir."
    exit 1
    ;;
esac
