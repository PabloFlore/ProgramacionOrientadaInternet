# Music Hub

Red social de música con estética nostálgica de comunidad fan: únete a grupos, completa tareas, colecciona cartas exclusivas y **chatea en tiempo real** con otros dispositivos.

Proyecto integrador de aprendizaje (PIA). Primer avance.

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
| `web/libs/signalr.min.js` | Cliente de SignalR. |
| `server/ChatServer/` | Backend .NET 8: hub, endpoints y acceso a SQLite. |
| `server/ChatServer/Hubs/ChatHub.cs` | Methods de SignalR (unirse a canal, enviar). |
| `server/ChatServer/UsuariosBd.cs` | Tabla y operaciones de usuarios. |
| `server/ChatServer/MensajesBd.cs` | Tabla y operaciones de mensajes. |

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
5. Para cambiar de cuenta: botón de salida de sesión en `Home.html`, o el enlace a `CambiarCuenta.html`.

Cada pestaña guarda su propia sesión (`sessionStorage`), así que puedes usar **dos cuentas distintas en dos pestañas del mismo navegador**.

## Autenticación

- Las contraseñas **no se guardan en texto plano**: se almacena un hash con sal por usuario (`UsuariosBd`).
- El login es **por correo**, que es único. El campo `Usuario` puede repetirse.
- La sesión vive en `sessionStorage` bajo la clave `musicHubUsuario`.
- El acceso a la API de cuenta se hace indicando `CorreoActual` en el cuerpo de la petición: **todavía no hay token (JWT)**, así que es suficiente para un avance académico pero no para producción.
- La contraseña solo es obligatoria en `CambiarCuenta` si se quiere cambiar; para editar nombre, apellido, usuario o correo no hace falta.

## Endpoints

| Método | Ruta | Entrada | Respuesta |
|---|---|---|---|
| `POST` | `/api/registrar` | `Nombre`, `Apellido`, `Usuario`, `Correo`, `Contrasena` | `200` con los datos del usuario · `400` campos vacíos, correo inválido o contraseña débil · `409` correo duplicado |
| `POST` | `/api/login` | `Correo`, `Contrasena` | `200` con `Nombre`, `Apellido`, `Usuario`, `Correo` · `401` correo o contraseña incorrectos |
| `GET` | `/api/mensajes?chatId=…&limite=…` | query `chatId` (obligatorio), `limite` (opcional, 100 por defecto, máx. 500) | `200` con `ChatId`, `Total` y `Mensajes` (del más antiguo al más reciente) |
| `PATCH` | `/api/cuenta` | `CorreoActual`, `Nombre`, `Apellido`, `Usuario`, `Correo` | `200` con el perfil actualizado · `400` datos inválidos · `401` si no existe la cuenta · `409` correo en uso |
| `PATCH` | `/api/cuenta/contrasena` | `Correo`, `ContrasenaActual`, `ContrasenaNueva` | `200` · `400` contraseña débil · `401` cuenta inexistente o contraseña actual incorrecta |

**Reglas de contraseña:** mínimo 8 caracteres, al menos una mayúscula, un número y un símbolo.

## Hub de SignalR (`/chat`)

| Método | Parámetros | Qué hace |
|---|---|---|
| `JoinGroup` | `chatId` | Une la conexión a un canal. |
| `LeaveGroup` | `chatId` | Sale de un canal. |
| `SendToGroup` | `chatId`, `user`, `message` | **Guarda el mensaje en SQLite y lo difunde solo a ese canal.** |
| `SendMessage` | `user`, `message` | Broadcast a todos (método legacy del experimento React; `Home.html` no lo usa). |

El evento que escucha el cliente es `ReceiveMessage` y llega con cuatro argumentos:
`usuario`, `mensaje`, `hora` y **`chatId`**.

Los canales disponibles en la barra lateral son `amigos`, `gaming`, `universidad` y `proyecto`. **El historial es por canal**, no por usuario: cualquiera que entre a un canal ve lo que ahí se escribió.

## Base de datos

SQLite en `chat.db`, junto al binario compilado
(`server/ChatServer/bin/Debug/net8.0/chat.db`). El archivo está en `.gitignore`.

**Tabla `Usuarios`**

| Columna | Nota |
|---|---|
| `Id` | primary key autoincremental |
| `Nombre`, `Apellido`, `Usuario` | texto |
| `Correo` | **único** |
| `HashContrasena`, `Sal` | hash y sal por usuario |
| `FechaRegistro` | ISO 8601 |

**Tabla `Mensajes`**

| Columna | Nota |
|---|---|
| `Id` | primary key autoincremental |
| `ChatId` | canal (`amigos`, `gaming`, …) |
| `Usuario` | nombre completo de quien lo envió |
| `Mensaje` | texto |
| `FechaEnvio` | ISO 8601 |

Índice `IX_Mensajes_Chat` sobre `(ChatId, Id)` para que el historial de un canal se lea rápido.

> `Mensajes.Usuario` guarda el **nombre completo**, no el id de usuario. Es una decisión simplicada de este avance: si dos cuentas se llaman igual, el historial las muestra como la misma persona.

## Notas de implementación

- **Caché desactivada en desarrollo.** El backend responde `Cache-Control: no-store` a todo lo que no sea `/chat`, porque si no el navegador y el celular siguen mostrando el HTML/JS viejo aunque el código ya haya cambiado.
- **JSON con los nombres de C#.** Se configuró `PropertyNamingPolicy = null` para que las respuestas salgan como `Nombre`, `Correo`, `Mensajes` y no en camelCase: JavaScript distingue mayúsculas y el frontend está escrito con esos nombres.
- **Estáticos fuera del `.db`.** El backend sirve `web/` con un `FileProvider` propio, no la raíz del proyecto, para que el `chat.db` (correos, hashes y sales) no quede accesible por HTTP.
- **CORS permisivo** para poder probar entre dispositivos en la red local.

## Probar entre dos dispositivos

1. Averigua la IP del equipo con el servidor: `ipconfig` → IPv4 (ej. `192.168.1.50`).
2. Ambos dispositivos deben estar en la **misma red Wi-Fi**. Si uno está en datos móviles, verá rangos de IP parecidos (los dos `192.168.x.x`) pero **no se van a alcanzar**: por eso puede parecer que funciona y luego no.
3. Dispositivo A: abre `http://192.168.1.50:5000/RegistrarUsuario.html`, crea su cuenta e inicia sesión.
4. Dispositivo B: misma URL, con **otra cuenta** (el correo debe ser distinto).
5. Entren al **mismo canal** y escriban: los mensajes se ven al instante.
6. Prueben recargar la página: el historial sigue ahí porque se lee de la base de datos.

En una sola PC se puede con dos pestañas del mismo navegador (cada una con su propia sesión).

### Notas sobre firewall

Windows Firewall puede bloquear el puerto `5000`. Acepta el permiso cuando lo pida, o agrega una regla de entrada TCP al puerto `5000` con perfil **Privado**. El equipo debe tener la red marcada como «red privada» para que la regla aplique.

## Pruebas realizadas

Verificado contra el backend en ejecución:

- ✅ Alta de cuentas y validación de correo duplicado (`409`), contraseña débil (`400`) y correo inválido (`400`).
- ✅ Inicio de sesión correcto, y rechazo de contraseña incorrecta (`401`) y cuenta inexistente (`401`).
- ✅ Persistencia: los mensajes enviados quedan guardados en la tabla `Mensajes` y se recuperan al recargar.
- ✅ Mensajería en tiempo real entre dos cuentas distintas en el mismo canal.
- ✅ Aislamiento entre canales: lo escrito en un canal no aparece en otro.

Pendiente de probar:

- ⬜ Comportamiento con redes distintas (movil con datos).
- ⬜ Validación real de `CambiarCuenta` desde el navegador.

## Limitaciones conocidas

- No hay autenticación por token: la API de cuenta confía en el `CorreoActual` que envía el cliente.
- Cualquiera que abra la página puede unirse a cualquier canal y enviar mensajes con el nombre que sea.
- Sin HTTPS, sin límite de intentos de inicio de sesión y sin protección contra spam.
- El historial se limita a los últimos 100 mensajes por canal.
