# Spec UX (BORRADOR) — Constancia "le avisé los requisitos al pasajero" (2026-08-05)

> **⛔ PENDIENTE DE FIRMA — NO CONSTRUIR.** Nada de esto se implementa hasta que Gastón responda las
> preguntas del final. Lo de arriba es la propuesta única (no un menú de diez ideas): si la firma tal
> cual, queda como especificación; si cambia algo, se corrige y recién ahí se construye.
>
> **Origen:** decisión 3 de "Documentación internacional" firmada el 2026-08-03 — *"Constancia de aviso
> al pasajero: investigar diseño (botón 'avisé' + rastro en auditoría) y presentar para firma. NO
> arrancado."* Nace como defensa de la agencia ante el reclamo *"a mí nadie me dijo que necesitaba
> pasaporte / visa / autorización del menor"*.
>
> **Fuentes usadas:** `docs/ux/guia-ux-gaston.md` (única autoridad de UX) ·
> `docs/ux/specs/2026-08-03-semaforo-dni-cabotaje.md` (patrón de la solapa Pasajeros) ·
> constitución del producto (P-5, P-6, P-7, P-9, P-10, P-11, P-14, P-15, P-16, P-17, P-21, F-6, F-14,
> F-16, PR-12, T-14) · objetivo firmado **A-17** (requisitos especiales por destino/pasajero).
>
> **Alcance:** solo pantallas. No decide reglas de negocio (qué requisitos pide cada destino ya está
> firmado como NO reglamentable: 2026-08-03, punto 4 "nada de tabla de reglas migratorias por país").

---

## 0) De qué se trata, en criollo

Hoy la solapa **Pasajeros** ya marca en rojo lo que puede hacer que el pasajero no viaje: pasaporte
vencido, DNI vencido para un vuelo dentro del país, y está por sumarse el recordatorio de menores. Eso
le avisa **al vendedor**. Lo que falta es lo otro: que quede escrito que **el vendedor se lo avisó al
cliente**, con fecha, nombre y qué le avisó — para que el día que el cliente reclame "nadie me dijo",
la agencia abra la reserva y muestre el renglón.

En una frase: **un botón que deja un renglón que dice "el 05/08/2026 Maite le avisó los requisitos de
documentación al cliente, y esto fue lo que le avisó".**

---

## 1) Dónde vive: solapa Pasajeros de la reserva

El aviso de documentación ya vive ahí (los chips). El rastro de haberlo comunicado vive **en el mismo
lugar**, no en una pantalla nueva (P-16: un dato, una superficie).

Va en la **barra de arriba de la solapa**, a la derecha, al lado de "Agregar Pasajero", con la palabra
al lado del ícono (P-10). Es una acción normal, no de excepción: no se esconde detrás del "⋯".

```
┌─ RESERVA F-2026-1087 · Cancún ────────────────────────────────────────────────────┐
│  Servicios │ PASAJEROS │ Estado de cuenta │ Documentos │ Historial                 │
├───────────────────────────────────────────────────────────────────────────────────┤
│  ADULTOS [2]   MENORES [1]   INFANTES [0]                                         │
│                                                                                   │
│  2 de 3 nombres cargados        [ + Agregar Pasajero ]  [ 📋 Registrar que le      │
│                                                          avisé los requisitos ]   │
│                                                                                   │
│  (A) ADULTO 1  JUAN PEREZ  [PASAPORTE VENCIDO PARA EL VIAJE]        ✏ Editar 🗑 …  │
│      DNI 20123456                                                                 │
│  (A) ADULTO 2  ANA PEREZ                                            ✏ Editar 🗑 …  │
│      DNI 25987654                                                                 │
│  (M) MENOR 1 — sin cargar                                           [ Cargar ]    │
└───────────────────────────────────────────────────────────────────────────────────┘
```

- **Texto del botón:** "Registrar que le avisé los requisitos". No "Constancia", no "Notificar", no
  "Disclaimer" (P-1: nada que un no-programador no diga en el mostrador).
- **Cuándo aparece:** desde que la reserva tiene al menos un pasajero declarado (o sea, desde
  Presupuesto en adelante, igual que la solapa — 2026-08-03 P8=A). En estados congelados (En viaje,
  Finalizada, Anulada, Perdida) **no aparece**: ahí ya no se avisa nada (P-9, "acción que
  estructuralmente ya no aplica no se muestra"). Lo ya registrado **se sigue viendo** (P-18).
- **No depende de que haya chips rojos.** Se puede dejar la constancia aunque esté todo en orden: la
  defensa vale igual ("le avisé que para Brasil el menor necesita autorización", aunque el sistema no
  tenga nada marcado).

---

## 2) La ficha en línea (nunca una ventana encima)

Al tocar el botón se abre una **ficha en línea debajo de la barra** (P-5: "el modal me parece
horrible"), empujando la lista hacia abajo. Mismo traje que "Registrar cobro" / la ficha de servicio.

```
┌─ AVISO DE REQUISITOS AL PASAJERO ─────────────────────────────────────── ✕ ──────┐
│                                                                                   │
│  Le avisé a        [ Fam. Pérez (cliente de la reserva)          ▾ ]              │
│  Cómo le avisé     [ WhatsApp ▾ ]        Cuándo  [ 05/08/2026 ]                   │
│                                                                                   │
│  QUÉ LE ESTÁS AVISANDO (queda escrito tal cual)                                   │
│  ┌─────────────────────────────────────────────────────────────────────────────┐  │
│  │ • Documentación vigente para el viaje (DNI y/o pasaporte).                  │  │
│  │ • Visas, vacunas y requisitos de ingreso del destino.                       │  │
│  │ • Autorización de salida del país para menores de 18.                       │  │
│  │ ────────────────────────────────────────────────────────────────────────    │  │
│  │ Avisos abiertos en esta reserva hoy:                                        │  │
│  │ • JUAN PEREZ — pasaporte vencido para el viaje.                             │  │
│  └─────────────────────────────────────────────────────────────────────────────┘  │
│                                                                                   │
│  Aclaración                                                                       │
│  [ ______________________________________________________________________ ]      │
│                                                                                   │
│                          [ 📄 Copiar el texto ]   [ Cancelar ]  [ Registrar ]     │
└───────────────────────────────────────────────────────────────────────────────────┘
```

Campos, en este orden:

1. **"Le avisé a"** — desplegable. Primera opción y **default: el cliente de la reserva**. Debajo, cada
   pasajero con nombre cargado. Una sola elección.
2. **"Cómo le avisé"** — desplegable de 4: **WhatsApp** (default) · Teléfono · Email · En persona.
3. **"Cuándo"** — casillero de fecha, **precargado con hoy** (hora argentina, T-14), editable hacia
   atrás (el vendedor a veces carga el lunes lo que avisó el viernes). Nunca fecha futura.
4. **Recuadro gris de solo lectura "Qué le estás avisando"** — no se edita: es lo que va a quedar
   congelado. Dos partes: el texto fijo de requisitos generales + la lista de avisos abiertos hoy en
   esta reserva (los mismos chips que se ven arriba, escritos en criollo). Si no hay ningún aviso
   abierto, esa segunda parte no aparece (no se muestra un "ninguno" al pedo).
5. **"Aclaración"** — casillero de texto libre, opcional, para "le mandé el link de la embajada" o
   "quedó en traerme la autorización el jueves". Sin leyenda explicativa (P-15).

Botones:

- **"Copiar el texto"** — copia al portapapeles el texto del recuadro, listo para pegarlo en el
  WhatsApp del cliente. No manda nada: el sistema **sugiere, no decide** (P-21).
- **"Cancelar"** — cierra sin registrar (mismo wording que el resto de las fichas en línea).
- **"Registrar"** — deja el rastro. **Sin ventanita de "¿seguro?"**: no es una acción destructiva ni
  fiscal (P-14 y Ronda 6 reservan el "¿seguro?" para lo que borra/anula/factura). El paso deliberado
  ya es abrir la ficha y apretar el botón.

---

## 3) Qué queda registrado (la foto congelada)

Cada constancia guarda, para siempre (PR-12: quién / cuándo / por qué):

| Qué | De dónde sale |
|---|---|
| Quién lo registró | el usuario logueado (nombre, no el id) |
| Cuándo lo registró | fecha y hora reales del registro (hora argentina) |
| Fecha del aviso | lo que puso el vendedor en "Cuándo" (puede ser anterior) |
| A quién le avisó | cliente de la reserva o el pasajero elegido |
| Por qué medio | WhatsApp / Teléfono / Email / En persona |
| **Texto completo del aviso** | el recuadro gris **congelado tal cual estaba ese día** |
| **Avisos abiertos en ese momento** | la lista de chips activos al momento de registrar |
| Aclaración | lo que escribió el vendedor |

**Congelado quiere decir congelado:** si mañana cambia el texto general de requisitos, o el pasajero
renueva el pasaporte y el chip desaparece, la constancia vieja **sigue diciendo lo mismo que decía ese
día**. Es una foto, no un espejo. (Mismo criterio que el snapshot fiscal de los comprobantes.)

---

## 4) Cómo se ve después

**a) En la solapa Pasajeros — un renglón de una sola línea, gris, debajo del contador** (2026-08-03
P11=A: lo que solo informa va gris y en una línea):

```
  2 de 3 nombres cargados        [ + Agregar Pasajero ]  [ 📋 Registrar que le avisé… ]
  ── Requisitos avisados el 05/08/2026 por Maite · por WhatsApp · Ver ────────────────
```

- Muestra **siempre la última** constancia. "Ver" despliega **en línea, debajo** (nunca ventana) la
  foto completa: a quién, medio, texto congelado, avisos de ese momento, aclaración — y, si hubo más de
  una, la lista de todas, la más nueva arriba.
- Si nunca se registró ninguna, **este renglón no existe** (no se muestra un "todavía no avisaste":
  eso sería un reto, no información).

**b) Si aparece un aviso NUEVO después de la última constancia**, el renglón cambia de gris a **ámbar**
y suma la frase (regla de color 2026-08-03 P11=A: lo que pide hacer algo va con color):

```
  ⚠ Requisitos avisados el 05/08/2026 por Maite · Desde ese día apareció un aviso
    nuevo: ANA PEREZ — DNI vencido para el viaje.   [ Registrar que le avisé ]
```

Es un aviso suave: **jamás frena nada** (P-20), no apaga ningún botón, no manda nada a la campanita.

**c) En la solapa Historial** — un renglón más, contado en criollo del negocio (2026-08-03):

```
  Hoy
  14:32 ● Maite le avisó los requisitos de documentación a Fam. Pérez — por WhatsApp
```

Sin nombres internos, sin "por Sistema", agrupado por día y con la hora al costado, igual que todo lo
demás del historial (P-17, T-5, T-14).

**d) En ningún lado más.** No va a la campanita, no va a la solapa Documentos, no arma un cartel arriba
de la ficha, no aparece en el listado de reservas. Un dato no se dice dos veces (P-16).

---

## 5) Deshacer: no se borra nunca

- **Nada se borra** (F-6 + regla del dueño 2026-08-03 "nada importante se borra").
- El camino normal cuando algo cambió es **registrar una constancia nueva**: quedan las dos, ordenadas
  por fecha, y arriba manda la última.
- **Cargada por error** (se equivocó de reserva, eligió mal el medio): se puede **anular la constancia**
  desde la ficha desplegada, con **motivo obligatorio**. Queda **tachada, a la vista**, con quién la
  anuló y cuándo — nunca desaparece (F-6, F-16). Si la anulada era la última, el renglón gris vuelve a
  mostrar la anterior, o desaparece si no había otra.

```
  ✗ 05/08/2026 · Maite · por WhatsApp    (tachado)
    Anulada por Gastón el 06/08/2026 — Motivo: era de otra reserva
```

---

## 6) Estados de la pantalla

| Situación | Qué se ve |
|---|---|
| Nunca se registró nada | Solo el botón. Sin renglón, sin reto. |
| Guardando | El botón "Registrar" queda apagado con "Registrando…" (no se puede apretar dos veces). |
| Falló guardar | Cartel rojo **dentro de la ficha, arriba de los botones**, con todo lo cargado intacto; se reintenta en el mismo botón (P-6, P-7). |
| Éxito | La ficha se cierra, aparece el renglón gris y sale el globito verde "Listo, quedó registrado" (P-6: el toast es solo para el éxito). |
| No se pudo traer la lista de constancias | El renglón dice, en gris: "No pudimos traer los avisos registrados" + **"Probar de nuevo"** (mismo criterio que el resto del rediseño). Nunca un vacío disfrazado de error. |
| Reserva en estado congelado | Sin botón; el renglón (y su "Ver") siguen visibles. |

---

## 7) Permisos y candado

- **Lo puede registrar cualquiera que pueda ver la reserva y trabajarla** (mismo permiso que cargar un
  pasajero). No es plata ni fiscal: no lleva permiso especial.
- **Anular una constancia** sí es una corrección sobre algo firme: **permiso elevado + motivo
  obligatorio + rastro** (F-16), igual que "Sacar de viaje" o "Deshacer".
- **Con la reserva trabada (candado), el botón sigue encendido.** Dejar constancia de que avisaste no
  cambia plata ni datos firmes, y bloquearlo sería un callejón sin salida justo cuando más falta hace
  (misma exención que "Cargar" un pasajero vacío, spec candado 2026-07-22 §1.6). → **Confirmar en P10.**

---

## 8) Qué NO hay que hacer

- No frenar **nada** por falta de constancia (ni emitir voucher, ni pasar a En viaje, ni facturar).
- No mandar nada solo: el sistema no le escribe al cliente por su cuenta (P-21).
- No armar tabla de requisitos por país ni checklist automático por destino (descartado el 2026-08-03).
- No usar la palabra "constancia", "disclaimer", "notificación fehaciente" ni nada legal en la pantalla.
- No poner cartelitos aclarativos en la ficha (P-15).
- No repetir el dato en la campanita, en el header ni en la solapa Documentos (P-16).
- No borrar ni editar una constancia ya registrada (F-6).
- No mostrar ids ni nombres internos en el historial (T-5).

---

## 9) Dependencias de motor (para el brief de backend, no son decisiones de UX)

1. Guardar la constancia con su **texto congelado** y la **lista de avisos activos al momento**
   (no una referencia viva a los chips).
2. Devolver, en el DTO de la reserva o en un endpoint propio, la lista de constancias (última primero)
   con: quién, cuándo, fecha del aviso, a quién, medio, texto, avisos de ese momento, aclaración,
   anulada sí/no + quién/cuándo/por qué.
3. Decir si **apareció un aviso nuevo después de la última constancia** (el front NO compara listas ni
   calcula fechas: mismo criterio que los chips de pasaporte y DNI).
4. Escribir el evento en el **historial de la reserva** (`/reservas/{id}/timeline`) con la frase en
   criollo ya armada, sin nombres internos.

---

## 10) Reglas que aplica esta spec

P-1 (sin jerga) · P-2 (dd/MM/aaaa) · P-5 (ficha en línea) · P-6 y P-7 (error en cartel, no se pierde
nada) · P-9 (botón que no aplica no se muestra) · P-10 (palabra al lado del ícono) · P-11 (sin callejón)
· P-14 (el "¿seguro?" se reserva para lo destructivo) · P-15 (sin cartelitos) · P-16 (un dato, una
superficie) · P-17 (voz de los avisos) · P-18 (lo registrado se sigue viendo) · P-20 (aviso suave, no
freno) · P-21 (sugiere, no decide) · F-6 (nada se borra) · F-16 (anular = permiso + motivo + rastro) ·
PR-12 (rastro de quién/cuándo/por qué) · T-5 (sin nombres internos) · T-14 (hora argentina) ·
A-17 (objetivo firmado: requisitos especiales marcados antes del viaje).

---

# PREGUNTAS PARA GASTON

> Diez decisiones, de lo grande a lo chico. Cada una tiene una **recomendación**; si estás de acuerdo
> con todas, alcanza con responder "todas las recomendadas".

### Tema: qué hace el botón

Contexto: la idea es que quede escrito que le avisaste al pasajero los requisitos del viaje (pasaporte,
visas, autorización de menores), para tener con qué defenderte si después dice "nadie me dijo".

**P1. Cuando apretás el botón, ¿qué querés que pase? (podés elegir más de una)**

  A) **El sistema solo lo anota**, y te da el texto listo para copiar y pegarlo vos en el WhatsApp del
     cliente. (RECOMENDADO: el aviso lo das vos por donde ya hablás con el cliente; el sistema no
     inventa mensajes ni manda nada solo)
```
     [ 📄 Copiar el texto ]   [ Cancelar ]   [ Registrar ]
             ↓
     "Listo, quedó registrado"   →  Requisitos avisados el 05/08/2026 por Maite
```
  B) **El sistema se lo manda por WhatsApp** desde acá (como ya hace con los vouchers) y anota que se
     mandó.
```
     [ Cancelar ]   [ Mandar por WhatsApp y registrar ]
             ↓
     "Se lo mandamos al 11 5555-5555 y quedó registrado"
```
  C) **Además, poder imprimir un papel** con el texto para que el cliente lo firme y quede en la carpeta.
```
     [ 🖨 Imprimir para firmar ]   [ Cancelar ]   [ Registrar ]
```

---

### Tema: alcance del aviso

**P2. El aviso, ¿es de toda la reserva o pasajero por pasajero?**

  A) **Uno para toda la reserva**, que adentro nombra a los pasajeros con problema (RECOMENDADO: al
     cliente le hablás una vez, no una vez por pasajero)
```
     2 de 3 nombres cargados    [ + Agregar Pasajero ]  [ 📋 Registrar que le avisé… ]
     ── Requisitos avisados el 05/08/2026 por Maite · por WhatsApp · Ver ──────────────
```
  B) **Uno por pasajero**, con su botón en cada fila
```
     (A) ADULTO 1  JUAN PEREZ  [PASAPORTE VENCIDO]      ✏ Editar  🗑  📋 Le avisé
     (A) ADULTO 2  ANA PEREZ                            ✏ Editar  🗑  📋 Le avisé
```

---

### Tema: ¿frena algo?

**P3. ¿Puede pasar que, por no haber dejado el aviso registrado, el sistema no te deje seguir?**

  A) **Nunca frena nada.** Es solo un rastro (RECOMENDADO: igual que los cartelitos rojos de documento
     vencido, que avisan pero jamás bloquean)
  B) **Frena la emisión del voucher** hasta que quede registrado el aviso
```
     [ Enviar voucher ]  ← gris:  "Primero registrá que le avisaste los requisitos"
```

---

### Tema: dónde vive el botón

**P4. ¿Dónde ponemos el botón?**

  A) **Arriba de la solapa Pasajeros**, al lado de "Agregar Pasajero" (RECOMENDADO: es donde ya están
     los avisos de documentación; todo el tema vive en un solo lugar)
```
     Servicios │ PASAJEROS │ Estado de cuenta │ Documentos │ Historial
     2 de 3 nombres cargados   [ + Agregar Pasajero ]  [ 📋 Registrar que le avisé… ]
```
  B) **Arriba de la ficha**, pegado a los avisos de la reserva
```
     ⚠ Hay 2 pasajeros con documentación vencida      [ 📋 Registrar que le avisé ]
```
  C) **En la solapa Documentos**, junto a los vouchers y archivos de la reserva

---

### Tema: qué queda escrito

Contexto: lo que se guarda es una foto congelada — si mañana el pasajero renueva el pasaporte, la
constancia vieja sigue diciendo lo que decía ese día.

**P5. ¿Qué texto queda guardado?**

  A) **Los requisitos generales (fijos) + los problemas puntuales de esta reserva** (RECOMENDADO:
     cubre lo de siempre y además lo específico)
```
     • Documentación vigente para el viaje (DNI y/o pasaporte).
     • Visas, vacunas y requisitos de ingreso del destino.
     • Autorización de salida del país para menores de 18.
     ───────────────────────────────────────────────────────
     Avisos abiertos hoy:  • JUAN PEREZ — pasaporte vencido para el viaje.
```
  B) **Solo los problemas puntuales** de esta reserva
```
     • JUAN PEREZ — pasaporte vencido para el viaje.
```
  C) **Solo lo que escribas vos a mano** cada vez
```
     Aclaración: [ le avisé que el nene necesita la autorización del padre ]
```

> Si elegís A: el texto fijo de los tres puntos, ¿te sirve así o lo querés escrito de otra manera?
> (Se puede dejar editable desde Configuración más adelante, pero eso hoy no está pedido.)

---

### Tema: por qué medio avisaste

**P6. Cuando registrás el aviso, ¿te preguntamos cómo se lo diste?**

  A) **Sí, una lista corta con WhatsApp puesto de fábrica** (RECOMENDADO: es un clic solo cuando fue
     por otro lado, y para defenderte importa poder decir "se lo mandé por WhatsApp el 5")
```
     Cómo le avisé  [ WhatsApp ▾ ]      ← WhatsApp · Teléfono · Email · En persona
```
  B) **No preguntamos nada**: solo queda la fecha y quién lo registró
```
     Requisitos avisados el 05/08/2026 por Maite
```

---

### Tema: cómo se ve después

**P7. Una vez registrado, ¿cómo se ve en la solapa Pasajeros?**

  A) **Un renglón gris de una línea** debajo del contador, con "Ver" para abrir el detalle
     (RECOMENDADO: informa sin gritar, y el detalle está a un clic)
```
     ── Requisitos avisados el 05/08/2026 por Maite · por WhatsApp · Ver ──────────────
```
  B) **Un cartelito verde en la fila de cada pasajero**
```
     (A) ADULTO 1  JUAN PEREZ  [PASAPORTE VENCIDO]  [✓ AVISADO 05/08]
```
  C) **Nada en la solapa**: queda solo en el Historial de la reserva

---

### Tema: cuando aparece un problema nuevo después

Contexto: avisaste el 5 de agosto. El 20 cargás un pasajero nuevo con el DNI vencido. La constancia
vieja no lo cubre.

**P8. ¿Qué hacemos cuando aparece un aviso nuevo después del último registro?**

  A) **El renglón se pone ámbar y te lo dice**, con el botón para volver a avisar (RECOMENDADO: es
     justo el caso en que la defensa queda coja)
```
     ⚠ Requisitos avisados el 05/08/2026 por Maite · Desde ese día apareció un aviso
       nuevo: ANA PEREZ — DNI vencido para el viaje.     [ Registrar que le avisé ]
```
  B) **Nada.** El renglón queda gris como estaba y vos te das cuenta solo
```
     ── Requisitos avisados el 05/08/2026 por Maite · por WhatsApp · Ver ──────────────
```

---

### Tema: registrado por error

**P9. Si alguien registra un aviso en la reserva equivocada, ¿qué se puede hacer?**

  A) **Anularlo con motivo obligatorio; queda tachado y a la vista** (RECOMENDADO: es la misma regla de
     siempre — nada se borra, todo deja rastro)
```
     ✗ 05/08/2026 · Maite · por WhatsApp     (tachado)
       Anulada por Gastón el 06/08/2026 — Motivo: era de otra reserva
```
  B) **No se toca nunca.** Si está mal, se registra uno nuevo arriba y listo
```
     06/08/2026 · Gastón · por Email      ← el que vale
     05/08/2026 · Maite · por WhatsApp
```

> Si elegís A: ¿anular esto lo puede hacer cualquier vendedor, o solo Admin? (Recomendado: solo Admin,
> como "Sacar de viaje" y los demás arreglos de excepción.)

---

### Tema: reserva trabada (candado)

**P10. Con la reserva ya confirmada y trabada, ¿se puede registrar el aviso igual?**

  A) **Sí, siempre.** No cambia plata ni datos de la reserva (RECOMENDADO: es justo cuando el viaje ya
     está firme que más falta hace avisar los requisitos)
```
     [ 📋 Registrar que le avisé los requisitos ]      ← encendido
```
  B) **No:** queda gris con candadito, como Editar y Borrar pasajero
```
     [ 🔒 Registrar que le avisé los requisitos ]      ← gris
     (el motivo lo dice la franja del candado de arriba)
```
