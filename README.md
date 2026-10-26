# Music Hub

Red social de música con estética nostálgica de comunidad fan: únete a grupos, completa tareas, colecciona cartas exclusivas y **chatea en tiempo real** con otros dispositivos.

Proyecto integrador de aprendizaje (PIA). Primer avance.

> **Nota:** el detalle de lo último que hicimos y el progreso están en la sección [Últimos cambios](#últimos-cambios-lo-último-que-hicimos).

## Tech Stack

- **Backend:** C# / .NET 8 Web API (minimal API) con **SignalR**.
- **Base de datos:** SQLite (Microsoft.Data.Sqlite) con hash + sal para contraseñas.
- **Frontend:** HTML, CSS y JavaScript vanilla servido por el propio backend.
- **Tiempo real:** WebSockets / SignalR para comunicación bidireccional entre dispositivos.

> La carpeta `client/` contiene un experimento anterior en React + Vite que **ya no se usa ni se versiona** (está en `.gitignore`). El frontend canónico es `web/`.

## Estructura del proyecto

| Ruta | Descripción |
|---|---|
| `web/LandingPage.html` + `LandingPage.css` | Portada con navbar y hero. |
| `web/IniciarSesion.html` | Inicio de sesión (por correo). |
| `web/RegistrarUsuario.html` | Alta de cuenta con requisitos de contraseña. |
| `web/Formularios.css` + `ValidacionRegistro.js` | Estilos compartidos y validaciones de los formularios. |
| `web/Home.html` + `Home.css` + `Home.js` | App principal: lista de chats, mensajes, tareas pendientes y colección de cartas. |
| `web/CambiarCuenta.html` + `.css` + `.js` | Edición de perfil y cambio de contraseña. |
| `web/Videollamada.html` + `.css` + `.js` | Pantalla de videollamada de la sala (maqueta visual, ver nota). |
| `web/libs/signalr.min.js` | Cliente de SignalR. |
| `server/ChatServer/` | Backend .NET 8: hub, endpoints y acceso a SQLite. |
| `server/ChatServer/Hubs/ChatHub.cs` | Methods de SignalR (unirse a canal, enviar). Resuelve la identidad por token. |
| `server/ChatServer/UsuariosBd.cs` | Tabla y operaciones de usuarios. |
| `server/ChatServer/MensajesBd.cs` | Tabla y operaciones de mensajes, conversaciones privadas. |
| `server/ChatServer/SesionesBd.cs` + `sesion.cs` | Tabla de sesiones con token opaco. |
| `server/ChatServer/conversacion.cs` | Modelo de conversación privada (último mensaje y el otro usuario). |

## Cómo correr

```bash
cd server/ChatServer
dotnet run
```

El backend sirve **la API y el frontend** en `http://0.0.0.0:5000`.

- Desde la misma PC: <http://localhost:5000>
- Desde el celular u otra PC: `http://<IP-del-servidor>:5000`

Endpoint del Hub de SignalR: `http://<IP>:5000/chat`.

> El `.csproj` usa `<RollForward>Major</RollForward>` porque en la máquina está instalado el SDK de .NET 10 (el proyecto apunta a `net8.0`).

## Flujo de uso

1. Abrir `http://<IP>:5000/RegistrarUsuario.html` y crear una cuenta.
2. Al registrarse se redirige al login con el correo ya escrito.
3. Iniciar sesión con **correo + contraseña**. `Home.html` exige sesión: sin ella redirige al login.
4. El nombre que aparece en el chat **no se escribe a mano**: sale de la cuenta con la que iniciaste sesión.
5. Para escribir en un canal, entra a `amigos`, `gaming`, `universidad` o `proyecto`.
6. Para un **chat privado**, pulsa «Nuevo mensaje privado», elige un contacto y se abre la conversación con esa persona.
7. Para cambiar de cuenta: botón de salida de sesión en `Home.html`, o el enlace a `CambiarCuenta.html`.

Cada pestaña guarda su propia sesión (`sessionStorage`), así que puedes usar **dos cuentas distintas en dos pestañas del mismo navegador**.

## Autenticación

- Las contraseñas **no se guardan en texto plano**: se almacena un hash con sal por usuario (`UsuariosBd`).
- El login es **por correo**, que es único. El campo `Usuario` puede repetirse.
- Al iniciar sesión el backend emite un **token opaco** (32 bytes aleatorios en hex) que se guarda en la tabla `Sesiones`. La respuesta del login incluye `Id` y `Token`.
- El cliente envía el token:
  - a la **API** como `Authorization: Bearer <token>`;
  - al **hub SignalR** como `?access_token=<token>` (el navegador no puede poner cabeceras en el WebSocket; clientes no-navegador pueden usar `Authorization`).
- `POST /api/logout` elimina el token, así que queda inválido de inmediato.
- **Caducidad de 7 días.** Cada token vence 7 días después de emitirse (columna `ExpiraEn`). Vencido, la API y el hub responden `401` y hay que iniciar sesión otra vez. Al arrancar el servidor se borran las sesiones vencidas y las creadas antes de existir la caducidad, así que tras actualizar cada usuario inicia sesión una vez.
- **Cambiar la contraseña cierra las demás sesiones** de la cuenta. La sesión desde la que se hizo el cambio sigue activa.
- **Límite de intentos de login:** tras **5 fallos en 15 minutos** para un mismo correo, el login responde `429` durante ese lapso. El contador vive en memoria: se reinicia al reiniciar el servidor (aceptable para este proyecto).
- **Contraseñas de máximo 128 caracteres** en registro, login y cambio.
- La API de cuenta **ya no confía en el cliente**: el usuario se resuelve siempre desde el token, no desde un `CorreoActual` en el cuerpo.
- La contraseña solo es obligatoria en `CambiarCuenta` si se quiere cambiar; para editar nombre, apellido, usuario o correo no hace falta.
- La sesión vive en `sessionStorage` bajo la clave `musicHubUsuario` (incluye `Id`, `Nombre`, `Apellido`, `Usuario`, `Correo` y `Token`). Las imágenes no se guardan ahí: se piden con `GET /api/cuenta`.

## Endpoints

Todos los endpoints marcados con 🔒 requieren `Authorization: Bearer <token>`; sin token válido responden `401`.

| Método | Ruta | Entrada | Respuesta |
|---|---|---|---|
| `POST` | `/api/registrar` | `Nombre`, `Apellido`, `Usuario`, `Correo`, `Contrasena` | `201` con los datos del usuario · `400` campos vacíos, correo inválido o contraseña débil (o de más de 128 caracteres) · `409` correo duplicado |
| `POST` | `/api/login` | `Correo`, `Contrasena` | `200` con `Id`, `Nombre`, `Apellido`, `Usuario`, `Correo` y `Token` · `401` correo o contraseña incorrectos · `429` demasiados intentos fallidos (5 en 15 min) |
| `POST` | `/api/logout` 🔒 | — | `200`; invalida el token de la sesión |
| `GET` | `/api/usuarios` 🔒 | — | `200` con los demás usuarios (`Id`, `Nombre`, `Usuario`) para iniciar un chat privado |
| `GET` | `/api/conversaciones` 🔒 | — | `200` con las conversaciones privadas del usuario y su último mensaje |
| `GET` | `/api/mensajes?chatId=…&limite=…` 🔒 | query `chatId` (obligatorio), `limite` (opcional, 100 por defecto, máx. 500) | `200` con `ChatId`, `Total` y `Mensajes` (incluye `UsuarioId`) · `403` si no perteneces a la conversación privada |
| `GET` | `/api/cuenta` 🔒 | — | `200` con `Nombre`, `Apellido`, `Usuario`, `Correo`, `FotoPerfil` y `Banner` (`null` si no hay imagen). Es la fuente de las imágenes para `Home.html` y `CambiarCuenta.html` |
| `PATCH` | `/api/cuenta` 🔒 | Obligatorios: `Nombre`, `Apellido`, `Usuario`, `Correo`. Opcionales: `ContrasenaActual` + `ContrasenaNueva` (cambia la contraseña), `FotoPerfil` y `Banner` (data URL; `""` las quita, si no se envían no cambian) | `200` con el perfil y `ContrasenaCambiada` · `400` datos inválidos, contraseña débil o imagen no válida/demasiado grande · `401` contraseña actual incorrecta · `409` correo en uso |

Todo se valida antes de escribir y los cambios se guardan en una sola transacción: si algo falla, no cambia nada.

**Reglas de contraseña:** mínimo 8 caracteres, máximo 128, al menos una mayúscula, un número y un símbolo.

**Imágenes:** foto y banner se guardan como data URL en la base. Formatos aceptados: **JPEG, PNG y WebP** (SVG se rechaza), con el contenido en base64 válido. El navegador las reduce con `canvas` (foto a 512 px, banner a 1024 px) y el servidor rechaza cualquiera de más de 700 000 caracteres.

## Hub de SignalR (`/chat`)

La conexión **exige token válido**: en `OnConnectedAsync` se lee el token (`access_token` o `Authorization`) y se resuelve el usuario; si no es válido, el servidor cierra la conexión. La identidad del emisor la pone el **servidor** a partir de la sesión, nunca el cliente.

| Método | Parámetros | Qué hace |
|---|---|---|
| `JoinGroup` | `chatId` | Une la conexión a un canal. En conversaciones privadas verifica que seas participante. |
| `LeaveGroup` | `chatId` | Sale de un canal. |
| `SendToGroup` | `chatId`, `message` | **Guarda el mensaje en SQLite ligado al perfil autenticado y lo difunde solo a ese canal.** El nombre no se recibe: lo pone el servidor. |

El evento que escucha el cliente es `ReceiveMessage` y llega con cinco argumentos:
`usuario`, `mensaje`, `hora`, **`chatId`** y **`usuarioId`** (id del emisor; el cliente lo compara con su propio `Id` para no confundir dos cuentas con el mismo nombre).

Los canales públicos de la barra lateral son `amigos`, `gaming`, `universidad` y `proyecto`; cualquiera que entre ve lo que ahí se escribió.

**Reconexión:** el cliente usa `withAutomaticReconnect`. Al volver la conexión se une de nuevo al canal actual y recarga el historial, porque SignalR no conserva los grupos al reconectar. Si el token venció o la sesión se cerró en otro lugar, la página borra la sesión y regresa al login.

### Chats privados

- Un chat privado se identifica con el id `dm:{idMenor}:{idMayor}` (los dos ids de usuario ordenados).
- `GET /api/usuarios` lista los contactos para iniciar una conversación.
- `GET /api/conversaciones` devuelve tus conversaciones privadas con el último mensaje y los datos del otro usuario.
- Solo los **dos participantes** pueden unirse al grupo y leer el historial: `JoinGroup`, `SendToGroup` y `GET /api/mensajes` comprueban la pertenencia (`403`/`HubException` si no).

## Base de datos

SQLite en `chat.db`, junto al binario compilado
(`server/ChatServer/bin/Debug/net8.0/chat.db`). El archivo está en `.gitignore`.

**Tabla `Usuarios`**

| Columna | Nota |
|---|---|
| `Id` | primary key autoincremental |
| `Nombre`, `Apellido`, `Usuario` | texto |
| `Correo` | **único** (sin distinguir mayúsculas; se guarda en minúsculas) |
| `HashContrasena`, `Sal` | hash y sal por usuario |
| `FechaRegistro` | ISO 8601 |
| `FotoPerfil`, `Banner` | data URL de la imagen, o `NULL` si no hay (columnas añadidas; migración automática) |

**Tabla `Mensajes`**

| Columna | Nota |
|---|---|
| `Id` | primary key autoincremental |
| `ChatId` | canal (`amigos`, `gaming`, …) o chat privado (`dm:{a}:{b}`) |
| `UsuarioId` | id del usuario que lo envió (columna añadida; las filas antiguas quedan en `NULL`) |
| `Usuario` | nombre completo de quien lo envió (copiado al escribir) |
| `Mensaje` | texto |
| `FechaEnvio` | ISO 8601 en UTC (`…Z`). La API y el tiempo real la muestran en hora local del servidor |

Índice `IX_Mensajes_Chat` sobre `(ChatId, Id)` para que el historial de un canal se lea rápido.

**Tabla `Sesiones`**

| Columna | Nota |
|---|---|
| `Token` | primary key: token opaco (hex de 32 bytes aleatorios) |
| `UsuarioId` | usuario dueño de la sesión |
| `CreadaEn` | ISO 8601 |
| `ExpiraEn` | ISO 8601; `CreadaEn` + 7 días. Las filas vencidas se ignoran y se borran al arrancar |

Índice `IX_Sesiones_Usuario` sobre `(UsuarioId)`. Al hacer logout, o al cambiar la contraseña (para las demás sesiones), se borra la fila y el token deja de ser válido.

> `Mensajes.Usuario` guarda el **nombre completo** al momento de escribir. `Mensajes.UsuarioId` es la referencia estable al perfil autenticado: con él se filtran las conversaciones privadas y se comprueba la pertenencia.

## Notas de implementación

- **Caché desactivada en desarrollo.** El backend responde `Cache-Control: no-store` a todo lo que no sea `/chat`, porque si no el navegador y el celular siguen mostrando el HTML/JS viejo aunque el código ya haya cambiado.
- **JSON con los nombres de C#.** Se configuró `PropertyNamingPolicy = null` para que las respuestas salgan como `Nombre`, `Correo`, `Mensajes` y no en camelCase: JavaScript distingue mayúsculas y el frontend está escrito con esos nombres.
- **Estáticos fuera del `.db`.** El backend sirve `web/` con un `FileProvider` propio, no la raíz del proyecto, para que el `chat.db` (correos, hashes y sales) no quede accesible por HTTP.
- **CORS permisivo** para poder probar entre dispositivos en la red local.
- **Hora en UTC.** Los mensajes se guardan con `DateTime.UtcNow`. Al mostrarlos (historial y tiempo real) se convierte a la hora local del servidor, así que el mensaje en vivo y el historial coinciden.
- **Imágenes fuera de la sesión.** Foto y banner no se guardan en `sessionStorage` (pesan demasiado); se piden con `GET /api/cuenta`. Las consultas generales (`/api/usuarios`, `/api/conversaciones`, la autenticación de cada petición) no las cargan.
- **Sesión con caducidad.** El servidor limpia las sesiones vencidas al arrancar. Si el chat (`Home.html`) pierde la conexión o no puede conectarse, pregunta a la API si la sesión sigue vigente; si responde `401`, borra la sesión y regresa al login.

## Pantalla de videollamada (maqueta)

`web/Videollamada.html` es una **maqueta visual sin función real**: no abre la cámara, no usa WebRTC ni se conecta al backend. Sirve para mostrar el diseño de una sala de videollamada con la misma estética del resto del sitio:

- Video remoto grande, self-view en miniatura y tira de participantes de ejemplo.
- Barra de controles (micrófono, cámara, pantalla y colgar) que solo cambian su apariencia.
- Panel lateral con pestañas de **Participantes** y **Chat de la llamada** (mensajes de ejemplo).

Se entra desde el botón **Videollamada** en la cabecera del chat de `Home.html`. Como el backend sirve todo `web/`, la pantalla se publica sola en `http://<IP>:5000/Videollamada.html`.

## Probar entre dos dispositivos

1. Averigua la IP del equipo con el servidor: `ipconfig` → IPv4 (ej. `192.168.1.50`).
2. Ambos dispositivos deben estar en la **misma red Wi-Fi**. Si uno está en datos móviles, verá rangos de IP parecidos (los dos `192.168.x.x`) pero **no se van a alcanzar**: por eso puede parecer que funciona y luego no.
3. Dispositivo A: abre `http://192.168.1.50:5000/RegistrarUsuario.html`, crea su cuenta e inicia sesión.
4. Dispositivo B: misma URL, con **otra cuenta** (el correo debe ser distinto).
5. Entren al **mismo canal** y escriban: los mensajes se ven al instante.
6. Para un **chat privado**: en un dispositivo pulsa «Nuevo mensaje privado», elige al otro usuario; ambos verán la conversación en su lista de mensajes privados, aunque la otra cuenta abra la sesión en otro dispositivo.
7. Prueben recargar la página: el historial sigue ahí porque se lee de la base de datos.

En una sola PC se puede con dos pestañas del mismo navegador (cada una con su propia sesión).

### Notas sobre firewall

Windows Firewall puede bloquear el puerto `5000`. Acepta el permiso cuando lo pida, o agrega una regla de entrada TCP al puerto `5000` con perfil **Privado**. El equipo debe tener la red marcada como «red privada» para que la regla aplique.

## Pruebas realizadas

Verificado contra el backend en ejecución. Las pruebas de cuenta están en `server/ChatServer/pruebas/prueba_cuenta.ps1` (27 casos, se ejecutan contra una copia del servidor con base temporal y no tocan `chat.db`). Las pruebas de mensajería y chat privado de la primera etapa se hicieron con un cliente Node de SignalR que **no está en el repositorio**:

- ✅ Alta de cuentas y validación de correo duplicado (`409`), contraseña débil (`400`) y correo inválido (`400`).
- ✅ Inicio de sesión correcto y rechazo de contraseña incorrecta (`401`); el login devuelve `Id` y `Token`.
- ✅ La API de mensajes responde `401` sin token y `200` con token.
- ✅ `GET /api/usuarios` no incluye al propio usuario.
- ✅ Conexiones SignalR con token válido permanecen activas; una conexión **sin token** se rechaza.
- ✅ Mensajería en tiempo real entre dos cuentas en el mismo canal, con el nombre puesto por el servidor.
- ✅ Cada mensaje queda persistido con el `UsuarioId` del perfil autenticado.
- ✅ Chat privado: el destinatario recibe el DM en tiempo real; un tercero recibe `403` al leer el historial y no puede unirse al grupo.
- ✅ `GET /api/conversaciones` devuelve la conversación con su último mensaje y el otro usuario.
- ✅ `POST /api/logout` invalida el token (deja de servir para la API).
- ✅ Cambio de perfil con contraseña actual incorrecta responde `401` y no modifica nada.
- ✅ Perfil, contraseña, foto y banner se guardan en una sola petición `PATCH /api/cuenta`; la foto persiste y aparece en el login.
- ✅ Cambiar la contraseña cierra las demás sesiones de la cuenta; la sesión actual sigue activa.
- ✅ Contraseña anterior rechazada tras el cambio; nueva aceptada.
- ✅ Imagen de más de 700 000 caracteres o que no es imagen: `400`.
- ✅ Correo de otra cuenta en mayúsculas: `409`.
- ✅ Tras 5 fallos de login, `429`; el bloqueo de un correo no afecta a otro.
- ✅ Contraseña de más de 128 caracteres rechazada en registro.
- ✅ La migración de una `chat.db` existente arranca bien y conserva los datos (probado con una copia).
- ✅ `GET /api/cuenta` devuelve foto y banner; el login y `GET /api/usuarios` ya no los incluyen.
- ✅ Imagen SVG y base64 inválido: `400`.
- ✅ Hora: el mensaje en tiempo real y el historial muestran la misma hora local, y la `Fecha` del historial es el instante real del envío. Se probó en una máquina UTC-6 (el servidor guarda UTC y la conversión se comprueba contra la hora real). Esta última prueba fue un script temporal con el cliente `@microsoft/signalr`, no está en el repositorio. Incluye dos cuentas con el mismo nombre completo: el evento identifica al emisor por `UsuarioId`.

Pendiente de probar:

- ⬜ Comportamiento con redes distintas (móvil con datos).
- ⬜ Flujo de `CambiarCuenta` desde el navegador (incluida la reducción de imagen con `canvas`).
- ⬜ Caducidad real de 7 días (la lógica está, pero no se esperó el plazo).
- ⬜ Reconexión del hub en el navegador al reiniciar el servidor.

## Limitaciones conocidas

- El token caduca a los 7 días, pero **no se renueva**: al vencer hay que iniciar sesión otra vez.
- Sin HTTPS, el límite de intentos vive en memoria (se reinicia con el servidor) y no hay protección contra spam.
- Foto y banner se guardan dentro de la base de datos: sirven para un proyecto escolar, pero engordan `chat.db`.
- El bloqueo de intentos es **por correo**, no por IP: alguien que conozca un correo puede bloquear esa cuenta durante 15 minutos con 5 intentos fallidos.
- El historial se limita a los últimos 100 mensajes por chat.
- Un canal público es visible para cualquiera que haya iniciado sesión; solo las conversaciones privadas restringen el acceso a sus dos participantes.
- `Mensajes.Usuario` guarda el nombre **tal como estaba** al escribir: si el usuario cambia su nombre, los mensajes ya enviados conservan el anterior.

## Últimos cambios (lo último que hicimos)

Resumen de la sesión de trabajo más reciente, para retomar fácilmente.

1. **Autenticación por token (reemplaza al `CorreoActual`).**
   - Tabla `Sesiones` + `SesionesBd`: el login emite un **token opaco** (hex de 32 bytes) y devuelve `Id` + `Token`.
   - La API usa `Authorization: Bearer <token>`; el hub SignalR recibe el token por `?access_token=` (navegador) o por `Authorization` (clientes no-navegador como Node).
   - `POST /api/logout` borra la fila y el token deja de servir.
   - La API de cuenta ya no confía en datos del cliente: el usuario sale siempre del token.

2. **Chat privado 1 a 1.**
   - Id de conversación: `dm:{idMenor}:{idMayor}`.
   - Barra lateral con «Mensajes privados»: botón **+** para elegir contacto y lista de conversaciones con el **último mensaje**.
   - Endpoints nuevos `GET /api/usuarios` y `GET /api/conversaciones`.
   - Solo los **dos participantes** pueden unirse/leer (`403` / `HubException` si no).

3. **Mensajes ligados al perfil.**
   - Columna nueva `Mensajes.UsuarioId` (se migra sola al arrancar; filas viejas quedan `NULL`).
   - El **servidor** pone el nombre desde la sesión; el cliente ya no lo envía.

4. **Arreglo del área de mensajes (UI).**
   - El scroll de `.messages` ahora funciona de verdad (se añadió `min-height: 0`): al enviar ya **no se empuja hacia abajo** todo.
   - La barra de escribir (input + botón «Enviar») queda **fija** abajo; solo scrollean los mensajes.
   - Barra de scroll **visible** (antes era casi invisible sobre fondo claro).
   - Auto-scroll inteligente: si estás leyendo hacia arriba **no te salta**; al enviar tú, baja al final.
   - En `Home.html` se subió la versión de assets a `?v=6` para evitar el caché viejo.

5. **Git.**
   - Commit `bd5bb11` «Comunicacion entre dos dispositivos y Guardado de BD» y merge `fd3480c`.
   - Se subió a `origin/main` (`89bd074..fd3480c`) por **HTTPS con `gh`** (la URL `origin` sigue en SSH; el push por SSH falla por falta de llave).

### Qué llevamos (progreso)

- [x] Registro y login con hash + sal.
- [x] El backend sirve el frontend + API en `:5000`.
- [x] Chat en tiempo real por canales (SignalR).
- [x] Persistencia del historial en SQLite.
- [x] Autenticación por token en la API y en el hub.
- [x] Chat privado 1 a 1 con lista de conversaciones y último mensaje.
- [x] Mensajes asociados al perfil (`UsuarioId`).
- [x] UI: scroll de mensajes y barra de escribir fija.
- [x] Pruebas de cuenta versionadas en `server/ChatServer/pruebas/` (27 casos).
- [ ] Probar en redes distintas (móvil con datos).
- [ ] Flujo de `CambiarCuenta` desde el navegador.
- [x] Caducidad/limpieza de tokens en la tabla `Sesiones`.

## Pendientes

Estado de los hallazgos de la revisión (prioridad media y baja).

**Prioridad media**

- [x] **Guardado de perfil no atómico.** Perfil, contraseña e imágenes van en un solo `PATCH /api/cuenta`. El servidor valida todo antes de escribir y la escritura es una transacción.
- [x] **Foto y banner no se guardan.** Se guardan como data URL reducida en el navegador (`canvas`) y se muestran desde la sesión.
- [x] **Seguridad de sesiones y login.** Caducidad de 7 días, cierre de las demás sesiones al cambiar la contraseña, límite de 5 intentos fallidos por correo en 15 minutos (`429`) y máximo de 128 caracteres en la contraseña. El límite de 128 es una buena práctica; no se midió un impacto concreto en CPU.
- [x] **Hub sin reconexión automática.** Usa `withAutomaticReconnect`, vuelve a unirse al canal y recarga el historial al reconectar.

**Prioridad baja**

- [x] El README indicaba `200` en `POST /api/registrar`; corregido a `201` (lo que devuelve el código).
- [x] Hora mezclada: los mensajes se guardan en UTC y se muestran en hora local (historial y tiempo real).
- [x] Código muerto: eliminado `sesion.cs` y el `using` sin uso en `Program.cs`.
- [ ] Archivos sin versionar: `web/Videollamada.html`, `.js` y `.css` no están en git, y hay cambios sin commitear en `README.md`, `Home.html`, `Home.css` y otros archivos. Pendiente de commit (por decisión, no se ha commiteado).
- [x] La sección «Pruebas realizadas» ya no menciona 19/19 sin respaldo: las pruebas de cuenta están en `server/ChatServer/pruebas/`.

