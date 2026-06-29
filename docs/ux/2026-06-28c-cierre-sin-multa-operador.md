# Cierre "el operador no cobró multa" + deshacer de Admin — diseño UX

> Fecha: 2026-06-28. Autor: `ux-ui-disenador`.
> Pantalla: detalle de reserva (`ReservaDetailPage`), estado **Anulada, esperando reembolso del operador**.
> Contexto: cuando se anula una reserva y queda un paso pendiente de "multa del operador",
> hoy aparece **"Confirmar multa del operador"** (panel en línea `ConfirmarMultaOperadorInline`, que
> emite una Nota de Débito). El backend ahora permite, en cambio, **cerrar el paso como "el operador
> no cobró nada / devolvió todo"** (un cierre SIN nota de débito), y que un **administrador lo deshaga**.

---

## Lo que la guía YA define y se aplica tal cual

Estas reglas NO se preguntan, ya están decididas y este diseño las respeta:

1. **Todo EN LÍNEA, nunca ventana flotante** (regla dura, multimoneda P3 / carga de servicios C / nombres P4b / cancelar en línea 2026-06-25). Tanto el cierre "sin multa" como el "deshacer" se abren **debajo**, dentro de la página.
2. **Español de agencia, nada técnico al usuario** (regla general). El usuario nunca lee "waive", "revert", "pass-through", "conceptKind", ni "Nota de Débito" salvo donde ya está aprobado decir "nota de crédito/débito".
3. **Motivo obligatorio en acciones sensibles** (ADR-035 C: mín. caracteres; "Sacar de viaje" 2026-06-22; "Reabrir" pide motivo). El backend exige 5..500 en ambas acciones → ambos paneles piden motivo.
4. **Acción de excepción de Admin = discreta, separada de los botones normales, visible SOLO para Admin, con motivo obligatorio + cartel que explica la consecuencia** (patrón "Sacar de viaje", 2026-06-22, y destrabar candado). El "deshacer" del cierre sin multa copia ESE patrón.
5. **Lo ya cumplido se esconde; lo que no se puede por estado/permiso va gris o se oculta** (2026-06-26). Tras cerrar sin multa, los dos botones de elección desaparecen (el paso ya se resolvió).
6. **Un solo cartel de estado arriba** para estados terminales (ADR-035 A, 2026-06-19). El texto del cartel cambia según el paso esté pendiente o ya cerrado sin multa.
7. **Iconos de acción con la palabra al lado, siempre visible** (2026-06-08).
8. **Para algo irreversible se admite un "¿seguro?" en línea** (patrón costo $0 / Nota de Débito, 2026-06-24 P1). Aplica a discutir para el cierre sin multa (ver P-7).

Lo que la guía **NO** cubre son las etiquetas exactas, dónde se para la 2da opción, el texto del motivo, el texto del estado "cerrado sin multa", y si el "deshacer" es link discreto o botón. Eso va a **PREGUNTAS PARA GASTÓN** (abajo). Los mockups muestran mi **recomendación**, pero no se construye hasta que Gastón confirme.

---

## Mockup 1 — El momento de elegir (hay multa pendiente)

Hoy el cartel rojo de "Anulada, esperando reembolso" trae UN botón "Confirmar multa del operador".
Propongo reemplazarlo por una **pregunta clara + dos opciones**, para que el vendedor entienda
que son dos caminos distintos y opuestos:

```
┌──────────────────────────────────────────────────────────────────────────┐
│  Anulada, esperando el reembolso del operador — solo lectura.              │
│                                                                            │
│  ¿El operador te cobró una multa por anular?                               │
│                                                                            │
│     [ Sí, el operador cobró una multa ]   [ No cobró nada / devolvió todo ]│
│        (abre el panel naranja de cargar           (abre el panel de        │
│         monto + emitir nota de débito)             cierre sin multa)       │
└──────────────────────────────────────────────────────────────────────────┘
```

- Botón izquierdo = el de hoy (abre `ConfirmarMultaOperadorInline`, naranja, pide monto/moneda/fecha y emite Nota de Débito).
- Botón derecho = NUEVO (abre el panel de cierre sin multa, mockup 2).
- Los dos colores y textos los separa la pregunta de arriba, para que sea **imposible confundir**
  "cobró una multa" con "no cobró nada".

## Mockup 2 — Panel "El operador no cobró multa" (cierre, en línea)

Va **debajo** del cartel, deliberadamente en un color **distinto del naranja de la multa**
(propongo verde/neutro) para que no se parezca al panel que emite nota de débito:

```
┌──────────────────────────────────────────────────────────────────────────┐
│  ✓ El operador no cobró multa                          Reserva #1042   [x] │
│                                                                            │
│  Estás registrando que el operador NO te cobró ninguna penalidad por la    │
│  anulación y devolvió todo. No se emite ninguna nota de débito al cliente. │
│  El paso de la multa queda cerrado.                                        │
│                                                                            │
│  ¿Por qué? *                                                               │
│  ┌──────────────────────────────────────────────────────────────────────┐ │
│  │ El operador confirmó por mail que no aplica penalidad...              │ │
│  └──────────────────────────────────────────────────────────────────────┘ │
│                                                                            │
│                                          [ Volver ]   [ Confirmar: sin multa ]│
└──────────────────────────────────────────────────────────────────────────┘
```

- **Sin** campos de monto/moneda/fecha: acá no hay plata que cargar.
- Motivo obligatorio (backend: 5..500). Si está corto/vacío, el botón "Confirmar" queda gris.
- Si falla Guardar: el panel queda abierto con el motivo intacto + cartel rojo arriba de los
  botones, reintenta en el mismo botón (regla Ronda 2 de la guía).

## Estado después de cerrar sin multa

Los dos botones de elección **desaparecen** (el paso ya se resolvió, regla 2026-06-26). El cartel
de arriba pasa a leer el cierre. Propongo:

```
┌──────────────────────────────────────────────────────────────────────────┐
│  Anulada — cerrada sin multa del operador. Solo lectura.                   │
└──────────────────────────────────────────────────────────────────────────┘
```

Mensaje de éxito al confirmar (toast verde): **"Listo. Se cerró sin multa del operador."**

## Mockup 3 — Deshacer (SOLO Admin), sobre un cierre ya hecho

Copia el patrón "Sacar de viaje": **discreto, separado de los botones normales, visible solo para
Admin**. El vendedor común NO lo ve. Propongo un enlace discreto debajo del cartel:

```
┌──────────────────────────────────────────────────────────────────────────┐
│  Anulada — cerrada sin multa del operador. Solo lectura.                   │
│                                                                            │
│  · Deshacer: el operador sí cobró una multa   ← (link, solo Admin)         │
└──────────────────────────────────────────────────────────────────────────┘
```

Al tocarlo, panel en línea (también separado de los botones normales):

```
┌──────────────────────────────────────────────────────────────────────────┐
│  Deshacer el cierre sin multa                          Reserva #1042   [x] │
│                                                                            │
│  Esto reabre el paso de la multa del operador. Vas a poder volver a elegir │
│  entre cargar la multa (con nota de débito) o cerrar sin multa otra vez.   │
│                                                                            │
│  ¿Por qué lo deshacés? *                                                   │
│  ┌──────────────────────────────────────────────────────────────────────┐ │
│  │ El operador finalmente informó una penalidad de US$ 80...             │ │
│  └──────────────────────────────────────────────────────────────────────┘ │
│                                                                            │
│                                              [ Volver ]   [ Deshacer ]     │
└──────────────────────────────────────────────────────────────────────────┘
```

Tras deshacer: vuelve el estado del mockup 1 (la pregunta + los dos botones). Toast:
**"Listo. Se reabrió el paso de la multa."**

## Estados (todas las situaciones)

- **Cargando** (al traer la cancelación tras el clic): spinner discreto en el botón, igual que hoy
  el "Confirmar multa" (`buscandoMulta`).
- **Sin paso pendiente** (no hay multa): no aparece ni la pregunta ni los botones (igual que hoy
  cuando `canConfirmOperatorPenalty.allowed === false`).
- **Sin permiso** para cerrar/confirmar: no se ven los dos botones (misma capacidad que "Confirmar
  multa": `reservas.cancel` + clasificar penalidad / Admin).
- **Deshacer sin ser Admin**: el link "Deshacer" **no existe** para no-Admin.
- **Error al confirmar/cerrar/deshacer**: cartel rojo en línea dentro del panel, datos intactos,
  reintento en el mismo botón. Nada de perder lo escrito.
- **Éxito**: panel se cierra, se refresca la reserva, toast verde, cartel de arriba actualizado.

## Qué NO hacer

- NO usar ventana flotante para nada de esto.
- NO pintar el panel "sin multa" de naranja (es el color del que emite nota de débito → confunde).
- NO mostrarle el "Deshacer" a un vendedor común.
- NO escribir "Nota de Débito" en el panel sin multa (ahí no se emite nada).
- NO decidir las etiquetas finales: son las de las preguntas de abajo hasta que Gastón confirme.

---

## PREGUNTAS PARA GASTON

### Tema: anular una reserva — el último paso de "la multa del operador"
Contexto: cuando se anula una reserva y el operador todavía tiene que decir si te cobra una multa por
anular, la pantalla muestra un paso pendiente. Hasta hoy solo se podía cargar la multa (que le genera
una nota de débito al cliente). Ahora también se puede cerrar diciendo "el operador no cobró nada,
devolvió todo". Hay que decidir cómo se ven esas dos opciones para que nadie las confunda.

**P1. ¿Cómo presentamos las DOS opciones para que se entiendan bien?**
  A) (recomendada) Una pregunta arriba y dos botones lado a lado:
```
   ¿El operador te cobró una multa por anular?
   [ Sí, el operador cobró una multa ]   [ No cobró nada / devolvió todo ]
```
  B) Un solo botón "Cerrar la multa del operador" que al tocarlo abre un panel donde adentro elegís
     "Sí cobró / No cobró":
```
   [ Cerrar la multa del operador ]
        ↓ (abre panel)
     ( ) Sí, cobró una multa    ( ) No cobró nada
```
  C) Dos botones sin la pregunta arriba (el texto de cada botón se explica solo):
```
   [ El operador cobró una multa ]   [ El operador no cobró multa ]
```

**P2. ¿Qué texto exacto lleva el botón / la opción de "no cobró"?**
  A) (recomendada) **"No cobró nada / devolvió todo"**
  B) **"El operador no cobró multa"**
  C) **"Sin multa (devolvió todo)"**

**P3. ¿Qué texto lleva el botón de "sí cobró" (hoy dice "Confirmar multa del operador")?**
  A) (recomendada) **"Sí, el operador cobró una multa"** (queda parejo con la otra opción)
  B) Dejarlo como hoy: **"Confirmar multa del operador"**
  C) **"Cargar la multa del operador"**

### Tema: el panel donde confirmás que no hubo multa
Contexto: al elegir "no cobró", se abre un recuadro dentro de la página (no una ventana aparte) para
dejar registrado el motivo. No se carga plata. No se emite ningún comprobante.

**P4. ¿Qué dice el recuadro arriba de todo (la explicación)?**
  A) (recomendada) **"Estás registrando que el operador NO te cobró ninguna penalidad por la
     anulación y devolvió todo. No se emite ninguna nota de débito al cliente. El paso de la multa
     queda cerrado."**
  B) Más corto: **"El operador no cobró multa. No se emite nada. El paso queda cerrado."**
  C) Otra cosa (contame cómo lo dirías vos).

**P5. ¿Qué le pedimos en el campo de motivo y qué ejemplo le sugerimos?**
  A) (recomendada) Título **"¿Por qué?"** + ejemplo en gris **"El operador confirmó por mail que no
     aplica penalidad..."**
  B) Título **"Motivo"** + ejemplo **"Sin penalidad por anulación..."**
  C) Otra cosa (contame).

**P6. ¿Qué dice el botón que confirma el cierre sin multa?**
  A) (recomendada) **"Confirmar: sin multa"**
  B) **"Cerrar sin multa"**
  C) **"Confirmar"** a secas.

**P7. Cerrar sin multa, ¿pide un "¿seguro?" antes, o se confirma directo?**
Contexto: no emite ningún comprobante (a diferencia de cargar la multa, que sí emite nota de débito),
pero cierra un paso. Un Admin puede deshacerlo después.
  A) (recomendada) **Directo**, sin "¿seguro?" extra: ya escribiste el motivo y apretás el botón
     (es deshacible por un Admin, no es irreversible).
  B) Con un **"¿seguro?"** en línea antes de cerrar (como cuando confirmás un costo en $0).

### Tema: cómo queda la reserva después de cerrar sin multa
Contexto: una vez cerrado, los dos botones desaparecen y el cartel de arriba tiene que contar que
quedó cerrado sin multa.

**P8. ¿Qué dice el cartel de arriba cuando ya se cerró sin multa?**
  A) (recomendada) **"Anulada — cerrada sin multa del operador. Solo lectura."**
  B) **"Anulada. El operador no cobró multa. Solo lectura."**
  C) **"Reserva anulada, sin multa del operador — solo lectura."**

**P9. ¿Qué dice el cartelito verde de "salió bien" al cerrar?**
  A) (recomendada) **"Listo. Se cerró sin multa del operador."**
  B) **"Cierre sin multa registrado."**
  C) Otra cosa (contame).

### Tema: deshacer el cierre (solo el administrador)
Contexto: si después de cerrar sin multa el operador termina cobrando algo, un administrador (y solo
él) tiene que poder volver atrás. El vendedor común no ve esta opción. Sigue el mismo molde que
"Sacar de viaje": discreto, apartado, con motivo obligatorio.

**P10. ¿El "deshacer" es un enlace discreto o un botón común?**
  A) (recomendada) **Enlace discreto** debajo del cartel, separado de los botones normales y visible
     solo para el admin (igual que "Sacar de viaje"):  `· Deshacer: el operador sí cobró una multa`
  B) **Botón común** al lado del cartel.
  C) Un iconito 🔓 con la palabra "Deshacer" al lado.

**P11. ¿Qué texto lleva ese "deshacer"?**
  A) (recomendada) **"Deshacer: el operador sí cobró una multa"**
  B) **"Deshacer el cierre sin multa"**
  C) **"Reabrir la multa"**

**P12. ¿Qué le explica el panel de deshacer antes de pedir el motivo?**
  A) (recomendada) **"Esto reabre el paso de la multa del operador. Vas a poder volver a elegir entre
     cargar la multa (con nota de débito) o cerrar sin multa otra vez."**
  B) Más corto: **"Reabre el paso de la multa del operador."**
  C) Otra cosa (contame).

**P13. ¿Qué dice el botón final de deshacer?**
  A) (recomendada) **"Deshacer"**
  B) **"Reabrir la multa"**
  C) **"Confirmar"**.
