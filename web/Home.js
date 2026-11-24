/* ============================================================
   Music Hub — Home (Home.html)
   Chat en tiempo real con SignalR (canales por chat de la barra)
   Requiere: libs/signalr.min.js cargado antes que este script
   ============================================================ */

/* -------------------- URL del servidor -------------------- */

const SERVER_URL = `https://${window.location.hostname || "localhost"}:5000`;

/* -------------------- Sesión -------------------- */

const CLAVE_SESION = "musicHubUsuario";
const CLAVE_BORRADORES = "musicHubBorradores";

/* La sesión vive en sessionStorage, así cada pestaña puede tener
   una cuenta distinta (útil para probar dos usuarios a la vez). */
function leerSesion() {
    const datos = sessionStorage.getItem(CLAVE_SESION);

    if (!datos) {
        return null;
    }

    try {
        return JSON.parse(datos);
    } catch (error) {
        return null;
    }
}

const sesionUsuario = leerSesion();

/* Sin sesión no se entra al chat: se vuelve al login. */
if (!sesionUsuario) {
    window.location.href = "IniciarSesion.html";
}

/* Token opaco emitido en el login. Viaja al hub (access_token) y a la
   API (Authorization: Bearer). */
const token = sesionUsuario ? (sesionUsuario.Token || "") : "";

/* -------------------- Datos de los chats (barra lateral) -------------------- */

/* Sin canales fijos: la barra lateral solo muestra conversaciones privadas y
   grupos que vienen del servidor (chatsPrivados se llena al cargarlas). */

/* -------------------- Elementos del DOM -------------------- */

const videoCallButton = document.getElementById("videoCallButton");
const chatEmailButton = document.getElementById("chatEmailButton");
const incomingCall = document.getElementById("incomingCall");
const incomingCallName = document.getElementById("incomingCallName");
const acceptCallButton = document.getElementById("acceptCallButton");
const rejectCallButton = document.getElementById("rejectCallButton");
const currentChatName = document.getElementById("currentChatName");
const currentChatStatus = document.getElementById("currentChatStatus");
const currentChatImage = document.getElementById("currentChatImage");
const messagesContainer = document.getElementById("messages");
const messageInput = document.getElementById("messageInput");
const sendButton = document.getElementById("sendButton");
const locationButton = document.getElementById("locationButton");
const attachButton = document.getElementById("attachButton");
const fileInput = document.getElementById("fileInput");

const sesionLabel = document.getElementById("sesionUsuario");
const logoutButton = document.getElementById("logoutButton");
const aliasError = document.getElementById("aliasError");

/* Mensajes privados */
const privateList = document.getElementById("privateList");
const contactsList = document.getElementById("contactsList");
const newDmButton = document.getElementById("newDmButton");
const loadMoreButton = document.getElementById("loadMoreMessages");
const cifradoButton = document.getElementById("cifradoButton");
const cifradoAviso = document.getElementById("cifradoAviso");
const deleteChatButton = document.getElementById("deleteChatButton");

const AVATAR_POR_DEFECTO =
    "data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 48 48'%3E%3Crect width='48' height='48' rx='24' fill='%23161b24'/%3E%3Ccircle cx='24' cy='18' r='7' fill='%235b6577'/%3E%3Cpath d='M10 41c0-8 6.3-13 14-13s14 5 14 13z' fill='%235b6577'/%3E%3C/svg%3E";

/* Metadata de los chats privados abiertos (chatId -> {name,status,image}) */
const chatsPrivados = {};

function chatPrivadoId(a, b) {
    const menor = Math.min(a, b);
    const mayor = Math.max(a, b);
    return `dm:${menor}:${mayor}`;
}

function metaDeChat(chatId) {
    return chatsPrivados[chatId] || {
        name: "Conversación",
        status: "",
        image: AVATAR_POR_DEFECTO
    };
}

function marcarActivo(elemento) {
    document
        .querySelectorAll(".private-item")
        .forEach(item => item.classList.remove("active"));

    if (elemento) {
        elemento.classList.add("active");
    }
}

/* -------------------- Estado -------------------- */

/* El alias ahora es el nombre de la cuenta con la que se inició
   sesión: ya no es un texto libre que cualquiera pueda escribir. */
let alias = sesionUsuario
    ? `${sesionUsuario.Nombre} ${sesionUsuario.Apellido}`.trim()
    : "";

/* Sin chat abierto al cargar la página: no hay canal que unir todavía. */
let currentChatId = null;
let connection = null;
let primerIdHistorial = null;
let tieneMasHistorial = false;
let temporizadorBarraLateral = null;

/* Chats de grupo de los que ya no eres integrante (salió o te quitaron).
   Se puede leer su historial, pero no escribir ni recibir mensajes en vivo. */
const gruposInactivos = new Set();
let ultimaFechaSeparador = "";
const presenciaUsuarios = new Map();
let otroUsuarioIdChatActual = null;
const mensajesPendientes = new Map();
let compartiendoUbicacion = false;
let subiendoArchivo = false;
const adjuntosCompletados = new Set();

/* Cifrado extremo a extremo: estado por pestaña y por chat. */
const CLAVE_CIFRADO_CHATS = "musicHubCifradoChats";
let cifradoListo = false;
let cifradoError = "";
const clavesPublicasCache = new Map();

function leerBorradores() {
    try {
        const datos = sessionStorage.getItem(CLAVE_BORRADORES);
        if (!datos) {
            return {};
        }
        const parsed = JSON.parse(datos);
        return parsed && typeof parsed === "object" ? parsed : {};
    } catch (error) {
        return {};
    }
}

function guardarBorradores(obj) {
    try {
        sessionStorage.setItem(CLAVE_BORRADORES, JSON.stringify(obj));
    } catch (error) {
        /* Ignorar errores de almacenamiento */
    }
}

function obtenerBorrador(chatId) {
    if (!chatId) return "";
    const borradores = leerBorradores();
    const valor = borradores[chatId];
    return typeof valor === "string" ? valor : "";
}

function guardarBorradorActual() {
    if (!currentChatId) return;
    const borradores = leerBorradores();
    const valor = messageInput ? messageInput.value : "";
    if (valor === "") {
        if (borradores[currentChatId] !== undefined) {
            delete borradores[currentChatId];
            guardarBorradores(borradores);
        }
        return;
    }
    borradores[currentChatId] = valor;
    guardarBorradores(borradores);
}

function aplicarBorrador(chatId) {
    if (!messageInput) return;
    const valor = obtenerBorrador(chatId);
    messageInput.value = valor;
}

function limpiarBorrador(chatId) {
    if (!chatId) return;
    const borradores = leerBorradores();
    if (borradores[chatId] !== undefined) {
        delete borradores[chatId];
        guardarBorradores(borradores);
    }
}

if (sesionLabel) {
    sesionLabel.textContent = alias;
}

/* El panel lateral ya no lleva un nombre fijo: se llena con la
   cuenta que inició sesión. */
const profileTitle = document.getElementById("profileTitle");
const profileName = document.getElementById("profileName");
const profileUsername = document.getElementById("profileUsername");
const profileEmail = document.getElementById("profileEmail");
const coleccionTitle = document.getElementById("coleccionTitle");
const changeProfileLink = document.getElementById("changeProfileLink");
const volverPerfilButton = document.getElementById("volverPerfilButton");
const enviarCorreoButton = document.getElementById("enviarCorreoButton");
const emailModal = document.getElementById("emailModal");
const emailModalTitle = document.getElementById("emailModalTitle");
const emailModalHint = document.getElementById("emailModalHint");
const closeEmailModal = document.getElementById("closeEmailModal");
const cancelEmailButton = document.getElementById("cancelEmailButton");
const sendEmailButton = document.getElementById("sendEmailButton");
const emailRecipient = document.getElementById("emailRecipient");
const emailSubject = document.getElementById("emailSubject");
const emailBody = document.getElementById("emailBody");
const emailError = document.getElementById("emailError");
const emailSuccess = document.getElementById("emailSuccess");
const currentChatPresence = document.getElementById("currentChatPresence");

let cargandoCorreo = false;
let enviandoCorreo = false;

if (profileName && sesionUsuario) {
    profileName.textContent = alias || "Usuario";
}

if (profileEmail && sesionUsuario) {
    profileEmail.textContent = sesionUsuario.Correo || "";
}

if (profileUsername && sesionUsuario) {
    profileUsername.textContent = `@${sesionUsuario.Usuario || ""}`;
}

const profilePicture = document.getElementById("profilePicture");
const profileBanner = document.getElementById("profileBanner");

let perfilPropio = {
    Nombre: profileName ? profileName.textContent : "",
    Usuario: profileUsername ? profileUsername.textContent : "",
    Correo: profileEmail ? profileEmail.textContent : "",
    Foto: profilePicture ? profilePicture.src : AVATAR_POR_DEFECTO,
    BannerImagen: profileBanner ? profileBanner.style.backgroundImage : "",
    BannerTamano: profileBanner ? profileBanner.style.backgroundSize : "",
    BannerPosicion: profileBanner ? profileBanner.style.backgroundPosition : ""
};

/* Foto y banner no viajan en la sesión (pesan mucho para sessionStorage):
   se piden a la API. Si la cuenta no tiene imagen, quedan las del HTML. */
async function cargarImagenesPerfil() {
    if (!token) return;

    try {
        const respuesta = await fetch(`${SERVER_URL}/api/cuenta`, {
            headers: { "Authorization": `Bearer ${token}` }
        });

        if (!respuesta.ok) return;

        const datos = await respuesta.json();

        if (profilePicture && datos.FotoPerfil) {
            profilePicture.src = datos.FotoPerfil;
        }

        if (profileBanner && datos.Banner) {
            profileBanner.style.backgroundImage = `url("${datos.Banner}")`;
            profileBanner.style.backgroundSize = "cover";
            profileBanner.style.backgroundPosition = "center";
        }

        perfilPropio.Foto = profilePicture ? profilePicture.src : AVATAR_POR_DEFECTO;
        perfilPropio.BannerImagen = profileBanner
            ? profileBanner.style.backgroundImage
            : "";
        perfilPropio.BannerTamano = profileBanner
            ? profileBanner.style.backgroundSize
            : "";
        perfilPropio.BannerPosicion = profileBanner
            ? profileBanner.style.backgroundPosition
            : "";
    } catch (error) {
        console.error("No se pudieron cargar las imágenes del perfil:", error);
    }
}

cargarImagenesPerfil();

if (logoutButton) {
    logoutButton.addEventListener("click", async () => {
        try {
            await fetch(`${SERVER_URL}/api/logout`, {
                method: "POST",
                headers: { "Authorization": `Bearer ${token}` }
            });
        } catch (error) {
            /* Aunque falle, la sesión local se borra igual. */
        }

        sessionStorage.removeItem(CLAVE_SESION);
        window.location.href = "IniciarSesion.html";
    });
}

/* -------------------- Historial desde la base de datos -------------------- */

function formatearEtiquetaFecha(fechaIso) {
    try {
        const fecha = new Date(fechaIso);
        const hoy = new Date();
        const ayer = new Date();
        ayer.setDate(hoy.getDate() - 1);

        const esMismoDia = (a, b) =>
            a.getFullYear() === b.getFullYear() &&
            a.getMonth() === b.getMonth() &&
            a.getDate() === b.getDate();

        if (esMismoDia(fecha, hoy)) {
            return "Hoy";
        }
        if (esMismoDia(fecha, ayer)) {
            return "Ayer";
        }
        return fecha.toLocaleDateString("es-MX");
    } catch (error) {
        return "";
    }
}

function agregarSeparadorFecha(fechaIso) {
    if (!messagesContainer || !fechaIso) {
        return;
    }
    const etiqueta = formatearEtiquetaFecha(fechaIso);
    if (!etiqueta) {
        return;
    }
    if (ultimaFechaSeparador === etiqueta) {
        return;
    }
    ultimaFechaSeparador = etiqueta;

    const separador = document.createElement("div");
    separador.className = "date-separator";
    separador.innerHTML = `<span>${escapeHtml(etiqueta)}</span>`;
    messagesContainer.appendChild(separador);
}

function agregarSeparadorFechaAlInicio(fechaIso) {
    if (!messagesContainer || !fechaIso) {
        return;
    }
    const etiqueta = formatearEtiquetaFecha(fechaIso);
    if (!etiqueta) {
        return;
    }
    const separador = document.createElement("div");
    separador.className = "date-separator";
    separador.innerHTML = `<span>${escapeHtml(etiqueta)}</span>`;
    messagesContainer.insertBefore(separador, messagesContainer.firstChild);
}

async function cargarHistorial(chatId) {
    if (messagesContainer) {
        messagesContainer.innerHTML = "";
    }
    if (loadMoreButton) {
        loadMoreButton.hidden = true;
        loadMoreButton.disabled = false;
        loadMoreButton.textContent = "Cargar más mensajes";
    }
    primerIdHistorial = null;
    tieneMasHistorial = false;
    ultimaFechaSeparador = "";

    try {
        const respuesta = await fetch(
            `${SERVER_URL}/api/mensajes?chatId=${encodeURIComponent(chatId)}&limite=30`,
            {
                headers: { "Authorization": `Bearer ${token}` }
            }
        );

        if (!respuesta.ok) {
            return;
        }

        const datos = await respuesta.json();

        primerIdHistorial = datos.PrimerId || null;
        tieneMasHistorial = !!datos.TieneMas;

        if (loadMoreButton) {
            if (tieneMasHistorial && primerIdHistorial) {
                loadMoreButton.hidden = false;
            } else {
                loadMoreButton.hidden = true;
            }
        }

        if (!messagesContainer) {
            return;
        }

        for (const mensaje of datos.Mensajes) {
            /* Se compara por UsuarioId (único por cuenta). Los mensajes
               anteriores a la migración no tienen UsuarioId (null): solo
               para esos se usa el nombre como respaldo. */
            agregarSeparadorFecha(mensaje.Fecha || mensaje.Hora);

            const esMio = mensaje.UsuarioId !== null
                ? mensaje.UsuarioId === sesionUsuario.Id
                : mensaje.Usuario === alias;

            if (mensaje.Tipo === "ubicacion") {
                const tarjeta = crearMensajeUbicacion({
                    esMio,
                    usuario: mensaje.Usuario,
                    latitud: mensaje.Latitud,
                    longitud: mensaje.Longitud,
                    etiqueta: mensaje.Etiqueta,
                    hora: mensaje.Hora
                });
                if (tarjeta) {
                    messagesContainer.appendChild(tarjeta);
                }
                continue;
            }

            if (mensaje.Tipo === "archivo") {
                messagesContainer.appendChild(crearMensajeArchivo({
                    esMio,
                    usuario: mensaje.Usuario,
                    id: mensaje.Id,
                    url: mensaje.ArchivoUrl,
                    nombre: mensaje.ArchivoNombre,
                    mime: mensaje.ArchivoMime,
                    tamano: mensaje.ArchivoTamano,
                    hora: mensaje.Hora
                }));
                continue;
            }

            let texto = mensaje.Texto;
            let falloCifrado = false;

            if (mensaje.Cifrado) {
                const descifrado = await descifrarTextoMensaje(
                    mensaje,
                    chatId
                );
                texto = descifrado.texto;
                falloCifrado = descifrado.fallo;
            }

            if (esMio) {
                renderSent(
                    texto,
                    mensaje.Hora,
                    null,
                    !!mensaje.Cifrado,
                    falloCifrado
                );
            } else {
                renderIncoming(
                    mensaje.Usuario,
                    texto,
                    mensaje.Hora,
                    !!mensaje.Cifrado,
                    falloCifrado
                );
            }
        }

        if (messagesContainer) {
            messagesContainer.scrollTop = messagesContainer.scrollHeight;
        }

    } catch (error) {
        console.error("No se pudo cargar el historial:", error);
    }
}

async function cargarMasMensajes() {
    if (!loadMoreButton || loadMoreButton.hidden || !primerIdHistorial) {
        return;
    }
    if (!messagesContainer) {
        return;
    }

    loadMoreButton.disabled = true;
    loadMoreButton.textContent = "Cargando...";

    const alturaAntes = messagesContainer.scrollHeight;
    const scrollAntes = messagesContainer.scrollTop;

    try {
        const respuesta = await fetch(
            `${SERVER_URL}/api/mensajes?chatId=${encodeURIComponent(currentChatId)}&limite=30&antesDe=${encodeURIComponent(primerIdHistorial)}`,
            {
                headers: { "Authorization": `Bearer ${token}` }
            }
        );

        if (!respuesta.ok) {
            loadMoreButton.disabled = false;
            loadMoreButton.textContent = "Cargar más mensajes";
            return;
        }

        const datos = await respuesta.json();

        const primeros = [...datos.Mensajes];
        const nuevoPrimerId = datos.PrimerId || null;
        tieneMasHistorial = !!datos.TieneMas;

        if (loadMoreButton) {
            if (tieneMasHistorial && nuevoPrimerId) {
                loadMoreButton.hidden = false;
            } else {
                loadMoreButton.hidden = true;
            }
        }
        primerIdHistorial = nuevoPrimerId;

        const fragmento = document.createDocumentFragment();
        const tempContainer = document.createElement("div");

        for (const mensaje of primeros) {
            if (mensaje.Fecha) {
                agregarSeparadorFechaAlInicio(mensaje.Fecha);
            }
            const esMio = mensaje.UsuarioId !== null
                ? mensaje.UsuarioId === sesionUsuario.Id
                : mensaje.Usuario === alias;

            if (mensaje.Tipo === "ubicacion") {
                const tarjeta = crearMensajeUbicacion({
                    esMio,
                    usuario: mensaje.Usuario,
                    latitud: mensaje.Latitud,
                    longitud: mensaje.Longitud,
                    etiqueta: mensaje.Etiqueta,
                    hora: mensaje.Hora
                });
                if (tarjeta) {
                    tempContainer.appendChild(tarjeta);
                }
                continue;
            }

            if (mensaje.Tipo === "archivo") {
                tempContainer.appendChild(crearMensajeArchivo({
                    esMio,
                    usuario: mensaje.Usuario,
                    id: mensaje.Id,
                    url: mensaje.ArchivoUrl,
                    nombre: mensaje.ArchivoNombre,
                    mime: mensaje.ArchivoMime,
                    tamano: mensaje.ArchivoTamano,
                    hora: mensaje.Hora
                }));
                continue;
            }

            let texto = mensaje.Texto;
            let falloCifrado = false;

            if (mensaje.Cifrado) {
                const descifrado = await descifrarTextoMensaje(
                    mensaje,
                    currentChatId
                );
                texto = descifrado.texto;
                falloCifrado = descifrado.fallo;
            }

            if (esMio) {
                const message = document.createElement("div");
                message.className = "message sent";
                message.innerHTML = `
                    <div class="message-content">
                        <p>${escapeHtml(texto)}</p>
                        ${mensaje.Cifrado ? candadoHtml(falloCifrado) : ""}
                        ${mensaje.Hora ? `<span class="msg-time">${escapeHtml(mensaje.Hora)}</span>` : ""}
                    </div>
                `;
                tempContainer.appendChild(message);
            } else {
                const container = document.createElement("div");
                container.className = "message received";
                container.innerHTML = `
                    <img src="${currentChatImage.src || ""}" alt="${escapeHtml(mensaje.Usuario)}">
                    <div class="message-content">
                        <strong>${escapeHtml(mensaje.Usuario)}</strong>
                        <p>${escapeHtml(texto)}</p>
                        ${mensaje.Cifrado ? candadoHtml(falloCifrado) : ""}
                        ${mensaje.Hora ? `<span class="msg-time">${escapeHtml(mensaje.Hora)}</span>` : ""}
                    </div>
                `;
                tempContainer.appendChild(container);
            }
        }

        while (tempContainer.firstChild) {
            fragmento.appendChild(tempContainer.firstChild);
        }

        if (messagesContainer.firstChild) {
            messagesContainer.insertBefore(fragmento, messagesContainer.firstChild);
        } else {
            messagesContainer.appendChild(fragmento);
        }

        const alturaDespues = messagesContainer.scrollHeight;
        messagesContainer.scrollTop = scrollAntes + (alturaDespues - alturaAntes);
    } catch (error) {
        console.error("No se pudieron cargar más mensajes:", error);
    } finally {
        if (loadMoreButton && !loadMoreButton.hidden) {
            loadMoreButton.disabled = false;
            loadMoreButton.textContent = "Cargar más mensajes";
        }
    }
}

if (loadMoreButton) {
    loadMoreButton.addEventListener("click", cargarMasMensajes);
}

/* -------------------- Conexión SignalR -------------------- */

/* Reintentos de reconexión indefinidos. La espera crece (1 s, 2 s, 4 s…)
   y se queda en 30 s. Nunca devuelve null: el chat sigue intentando hasta
   que el servidor vuelva o la sesión deje de ser válida. */
const POLITICA_RECONEXION = {
    nextRetryDelayInMilliseconds(contexto) {
        const escalon = Math.min(contexto.previousRetryCount, 5);
        return Math.min(1000 * 2 ** escalon, 30000);
    }
};

/* Mientras SignalR reintenta, la sesión se revisa cada 10 s. Si el token
   venció mientras el servidor estaba caído, sesionSigueVigente() redirige
   al login en vez de seguir reintentando con un token muerto. */
const INTERVALO_VIGENCIA_MS = 10000;
let vigenciaTimer = null;

function iniciarVigilanciaSesion() {
    if (vigenciaTimer === null) {
        vigenciaTimer = setInterval(sesionSigueVigente, INTERVALO_VIGENCIA_MS);
    }
}

function detenerVigilanciaSesion() {
    if (vigenciaTimer !== null) {
        clearInterval(vigenciaTimer);
        vigenciaTimer = null;
    }
}

/* Ya no hay botón "Conectar": se conecta solo al abrir la página. */
actualizarBotonUbicacion();
conectar();

/* Prepara el par ECDH del dispositivo y publica la clave pública. Es
   independiente de la conexión del chat: si falla, el chat sigue en
   texto plano y el interruptor queda deshabilitado. */
inicializarCifrado();

async function conectar(reintento = 0) {
    if (!sesionUsuario) {
        return;
    }

    aliasError.textContent = reintento === 0 ? "Conectando..." : `Reconectando... (intento ${reintento + 1})`;

    try {
        const hub = new signalR.HubConnectionBuilder()
            .withUrl(`${SERVER_URL}/chat`, {
                accessTokenFactory: () => token
            })
            .withAutomaticReconnect(POLITICA_RECONEXION)
            .build();

        hub.on("SesionRevocada", () => {
            detenerVigilanciaSesion();
            sessionStorage.removeItem(CLAVE_SESION);
            window.location.href = "IniciarSesion.html";
        });

        hub.on("ReceiveMessage", async (user, message, time, chatId, usuarioId, fechaMensaje, mensajeId, tempIdRecibido, cifrado, iv, clavePublica, clavePublicaDestino) => {
            refrescarBarraLateral();

            /* Filtro por canal: si el mensaje es de otro chat se
               descarta. Evita que caiga en el chat equivocado
               durante el cambio de canal. */
            if (chatId && chatId !== currentChatId) {
                return;
            }

            const esCifrado = !!cifrado;
            let texto = message;
            let falloCifrado = false;

            if (esCifrado) {
                const descifrado = await descifrarTextoMensaje({
                    Texto: message,
                    Iv: iv,
                    ClavePublica: clavePublica,
                    ClavePublicaDestino: clavePublicaDestino,
                    UsuarioId: usuarioId
                }, chatId);

                texto = descifrado.texto;
                falloCifrado = descifrado.fallo;
            }

            /* El servidor no envía al emisor. Si el usuario de este mensaje
               es la misma cuenta, viene de otra pestaña suya: se pinta como
               propio (derecha). */
            if (usuarioId === sesionUsuario.Id) {
                agregarSeparadorFecha(fechaMensaje);
                if (tempIdRecibido && messagesContainer) {
                    const existente = messagesContainer.querySelector(`[data-temp-id="${tempIdRecibido}"]`);
                    if (existente) {
                        const contenido = existente.querySelector(".message-content");
                        if (contenido) {
                            const p = contenido.querySelector("p");
                            if (p) p.textContent = texto;
                            if (esCifrado && !contenido.querySelector(".msg-lock")) {
                                contenido.insertAdjacentHTML(
                                    "beforeend",
                                    candadoHtml(falloCifrado)
                                );
                            }
                            let span = contenido.querySelector(".msg-time");
                            if (!span && time) {
                                span = document.createElement("span");
                                span.className = "msg-time";
                                contenido.appendChild(span);
                            }
                            if (span && time) {
                                span.textContent = time;
                            }
                        }
                        if (mensajesPendientes.has(tempIdRecibido)) {
                            mensajesPendientes.delete(tempIdRecibido);
                        }
                        messagesContainer.scrollTop = messagesContainer.scrollHeight;
                        return;
                    }
                }
                if (messagesContainer) {
                    const duplicados = [...messagesContainer.querySelectorAll(".message.sent p")]
                        .filter(p => p.textContent === texto)
                        .slice(-1);
                    if (duplicados.length > 0 && time) {
                        const padre = duplicados[0].closest(".message.sent");
                        if (padre && !padre.dataset.tempId) {
                            messagesContainer.scrollTop = messagesContainer.scrollHeight;
                            return;
                        }
                    }
                }
                renderSent(texto, time, null, esCifrado, falloCifrado);
                if (tempIdRecibido && mensajesPendientes.has(tempIdRecibido)) {
                    mensajesPendientes.delete(tempIdRecibido);
                }
                return;
            }

            agregarSeparadorFecha(fechaMensaje);
            renderIncoming(
                user,
                texto,
                time,
                esCifrado,
                falloCifrado
            );
        });

        hub.on("ReceiveUbicacion", (user, chatId, usuarioId, fechaMensaje, mensajeId, tempIdRecibido, latitud, longitud, etiqueta, time) => {
            refrescarBarraLateral();

            if (chatId && chatId !== currentChatId) {
                return;
            }

            const esMio = usuarioId === sesionUsuario.Id;

            if (esMio && tempIdRecibido && messagesContainer) {
                const existente = messagesContainer.querySelector(
                    `[data-temp-id="${tempIdRecibido}"]`
                );
                if (existente) {
                    messagesContainer.scrollTop = messagesContainer.scrollHeight;
                    return;
                }
            }

            if (!messagesContainer) {
                return;
            }

            const tarjeta = crearMensajeUbicacion({
                esMio,
                usuario: user,
                latitud,
                longitud,
                etiqueta,
                hora: time,
                tempId: esMio ? tempIdRecibido : null
            });

            if (!tarjeta) {
                return;
            }

            agregarSeparadorFecha(fechaMensaje);
            messagesContainer.appendChild(tarjeta);
            messagesContainer.scrollTop = messagesContainer.scrollHeight;
        });

        hub.on("ReceiveArchivo", (user, chatId, usuarioId, fechaMensaje, mensajeId, url, nombre, mime, tamano, time, tempIdRecibido) => {
            refrescarBarraLateral();

            if (chatId && chatId !== currentChatId) {
                return;
            }

            if (!messagesContainer) {
                return;
            }

            const esMio = usuarioId === sesionUsuario.Id;

            if (esMio && tempIdRecibido &&
                adjuntosCompletados.has(tempIdRecibido)) {
                return;
            }

            if (esMio && tempIdRecibido) {
                const existente = messagesContainer.querySelector(
                    `[data-temp-id="${tempIdRecibido}"]`
                );

                if (existente) {
                    const final = crearMensajeArchivo({
                        esMio: true,
                        usuario: user,
                        id: mensajeId,
                        url,
                        nombre,
                        mime,
                        tamano,
                        hora: time,
                        tempId: tempIdRecibido
                    });

                    existente.replaceWith(final);
                    adjuntosCompletados.add(tempIdRecibido);
                    setTimeout(
                        () => adjuntosCompletados.delete(tempIdRecibido),
                        60000
                    );
                    messagesContainer.scrollTop =
                        messagesContainer.scrollHeight;
                    return;
                }
            }

            agregarSeparadorFecha(fechaMensaje);

            messagesContainer.appendChild(crearMensajeArchivo({
                esMio,
                usuario: user,
                id: mensajeId,
                url,
                nombre,
                mime,
                tamano,
                hora: time
            }));
            messagesContainer.scrollTop = messagesContainer.scrollHeight;
        });

        hub.on("UsuarioConectado", (usuarioId) => {
            const idNum = Number(usuarioId);
            if (!isNaN(idNum)) {
                presenciaUsuarios.set(idNum, true);
                actualizarIndicadoresPresencia();
            }
        });

        hub.on("UsuarioDesconectado", (usuarioId) => {
            const idNum = Number(usuarioId);
            if (!isNaN(idNum)) {
                presenciaUsuarios.set(idNum, false);
                actualizarIndicadoresPresencia();
            }
        });

        /* Avisos del servidor sobre grupos. GrupoActualizado: alguien entró o
           salió; se refresca la lista y el panel si está abierto.
           QuitadoDeGrupo: ya no eres integrante; el chat abierto pasa a solo
           lectura y se muestra el aviso. */
        hub.on("GrupoActualizado", (chatId) => {
            cargarGrupos();
            if (chatId === currentChatId && !groupPanel.hidden) {
                renderPanelGrupo();
            }
        });

        hub.on("QuitadoDeGrupo", async (chatId) => {
            await cargarGrupos();
            salirDelGrupoActual(chatId);
        });

        hub.on("TareaActualizada", (chatId) => {
            if (chatId === currentChatId) {
                cargarTareas(idDeGrupoDeChat(chatId));
            }
        });

        hub.on("RecompensaOtorgada", async (recompensa) => {
            try {
                await cargarColeccion();
                aliasError.textContent =
                    `¡Obtuviste ${recompensa.Nombre}! Ya está en tu colección.`;
            } catch (error) {
                console.error("Error al actualizar la colección:", error);
            }
        });

        /* Videollamadas: avisos del servidor sobre la llamada entrante. */
        hub.on("LlamadaEntrante", (llamadaId, chatId, usuarioId, nombre) => {
            mostrarLlamadaEntrante(llamadaId, nombre);
        });

        hub.on("LlamadaAceptada", (llamadaId) => {
            if (llamadaId === llamadaEntranteId) {
                ocultarLlamadaEntrante();
            }
        });

        hub.on("LlamadaTerminada", (llamadaId, motivo) => {
            if (llamadaId === llamadaEntranteId) {
                ocultarLlamadaEntrante();
                aliasError.textContent = `La videollamada terminó (${motivo}).`;
            }
        });

        hub.onreconnecting(() => {
            messageInput.disabled = true;
            sendButton.disabled = true;
            actualizarBotonUbicacion();
            aliasError.textContent = "Conexión perdida. Reconectando...";
            iniciarVigilanciaSesion();
        });

        /* SignalR no conserva los grupos al reconectar: hay que volver a
           unirse al canal actual y recargar el historial para recuperar lo
           que llegó mientras la conexión estuvo caída. */
        hub.onreconnected(async () => {
            detenerVigilanciaSesion();
            try {
            if (currentChatId && !esSoloLectura(currentChatId)) {
                await hub.invoke("JoinGroup", currentChatId);
            }
            if (currentChatId) {
                await cargarHistorial(currentChatId);
            }
            refrescarBarraLateral();
            actualizarIndicadoresPresencia();
            messageInput.disabled = !currentChatId;
                sendButton.disabled = !currentChatId;
                aplicarModoLectura(currentChatId);
                aliasError.textContent = "";
            } catch (error) {
                aliasError.textContent =
                    "No se pudo volver a entrar al chat. Recarga la página.";
            }
        });

        hub.onclose(async () => {
            /* Con la política indefinida, esto solo ocurre si la conexión
               se cierra de forma definitiva. Si la sesión venció, la función
               redirige al login; si no, se muestra el aviso. */
            detenerVigilanciaSesion();

            if (!(await sesionSigueVigente())) {
                return;
            }

            messageInput.disabled = true;
            sendButton.disabled = true;
            actualizarBotonUbicacion();
            aliasError.textContent =
                "Se cerró la conexión con el servidor. " +
                "Recarga la página para volver a conectar.";
        });

        await hub.start();

        connection = hub;

        /* Sin chat abierto al inicio: solo se cargan las listas. */
        if (currentChatId) {
            await connection.invoke("JoinGroup", currentChatId);
            await cargarHistorial(currentChatId);
        }
    await cargarConversaciones();
    await cargarGrupos();
    actualizarIndicadoresPresencia();

        messageInput.disabled = !currentChatId;
        sendButton.disabled = !currentChatId;
        aplicarModoLectura(currentChatId);
        if (currentChatId) {
            messageInput.focus();
        }

        aliasError.textContent = "";

    } catch (error) {
        if (!(await sesionSigueVigente())) {
            return;
        }

        if (reintento < 5) {
            const retraso = Math.pow(2, reintento) * 1000;
            aliasError.textContent =
                `No se pudo conectar a ${SERVER_URL}. Reintentando en ${retraso / 1000}s...`;
            setTimeout(() => conectar(reintento + 1), retraso);
            return;
        }

        aliasError.textContent =
            `No se pudo conectar a ${SERVER_URL}. ` +
            "¿El servidor está corriendo? Recarga la página.";
    }
}

/* Pregunta a la API si el token sigue vigente. Si responde 401 (venció,
   se cerró en otro lugar o no es válido) se borra la sesión y se vuelve al
   login. Sin red no es un problema de sesión, así que devuelve true. */
async function sesionSigueVigente() {
    try {
        const respuesta = await fetch(`${SERVER_URL}/api/usuarios`, {
            headers: { "Authorization": `Bearer ${token}` }
        });

        if (respuesta.status === 401) {
            sessionStorage.removeItem(CLAVE_SESION);
            window.location.href = "IniciarSesion.html";
            return false;
        }

        return true;
    } catch (error) {
        return true;
    }
}

/* -------------------- Cifrado extremo a extremo -------------------- */

/* El interruptor es por pestaña y por chat: sessionStorage evita que dos
   cuentas abiertas en el mismo navegador compartan la decisión. */
function leerChatsCifrados() {
    try {
        const datos = sessionStorage.getItem(CLAVE_CIFRADO_CHATS);
        const parsed = datos ? JSON.parse(datos) : {};
        return parsed && typeof parsed === "object" ? parsed : {};
    } catch (error) {
        return {};
    }
}

function guardarCifradoChat(chatId, activo) {
    const chats = leerChatsCifrados();

    if (activo) {
        chats[chatId] = true;
    } else {
        delete chats[chatId];
    }

    try {
        sessionStorage.setItem(
            CLAVE_CIFRADO_CHATS,
            JSON.stringify(chats)
        );
    } catch (error) {
        /* Sin sessionStorage el interruptor dura lo que la página. */
    }
}

function cifradoActivo(chatId) {
    return !!leerChatsCifrados()[chatId];
}

function otroUsuarioDeChat(chatId) {
    if (!chatId || !chatId.startsWith("dm:") || !sesionUsuario) {
        return null;
    }

    const partes = chatId.split(":");
    const a = Number(partes[1]);
    const b = Number(partes[2]);

    if (!Number.isFinite(a) || !Number.isFinite(b)) {
        return null;
    }

    return a === sesionUsuario.Id ? b : a;
}

async function obtenerClavePublicaUsuario(usuarioId, forzar = false) {
    if (!usuarioId) {
        return null;
    }

    if (!forzar && clavesPublicasCache.has(usuarioId)) {
        return clavesPublicasCache.get(usuarioId);
    }

    const respuesta = await fetch(
        `${SERVER_URL}/api/usuarios/${encodeURIComponent(usuarioId)}/clave`,
        {
            headers: { "Authorization": `Bearer ${token}` }
        }
    );

    if (!respuesta.ok) {
        throw new Error("No se pudo obtener la clave del otro usuario.");
    }

    const datos = await respuesta.json();
    const clave = datos.ClavePublica || null;

    if (clave) {
        clavesPublicasCache.set(usuarioId, clave);
    } else if (forzar) {
        clavesPublicasCache.delete(usuarioId);
    }

    return clave;
}

/* Descifra un mensaje con la clave del emisor. Si el mensaje es mío, la
   clave pública guardada como emisor es la mía: hace falta la del
   destinatario usada al cifrar. Los mensajes antiguos no la guardaban,
   así que se usa como respaldo la clave actual del otro participante. */
async function descifrarTextoMensaje(mensaje, chatId) {
    try {
        let publica = mensaje.ClavePublica;
        const esMio = sesionUsuario &&
            Number(mensaje.UsuarioId) === Number(sesionUsuario.Id);

        if (esMio) {
            publica = mensaje.ClavePublicaDestino ||
                await obtenerClavePublicaUsuario(
                    otroUsuarioDeChat(chatId)
                );
        }

        if (!publica) {
            throw new Error("Falta la clave pública para descifrar.");
        }

        const clave = await Cifrado.derivarClave(publica);
        const texto = await Cifrado.descifrar(
            mensaje.Texto,
            mensaje.Iv,
            clave
        );

        return { texto, fallo: false };
    } catch (error) {
        return {
            texto: "Mensaje cifrado (no se pudo descifrar en este dispositivo).",
            fallo: true
        };
    }
}

function candadoHtml(fallo) {
    const titulo = fallo
        ? "No se pudo descifrar en este dispositivo"
        : "Cifrado extremo a extremo";

    return `
        <span class="msg-lock${fallo ? " msg-lock-error" : ""}" title="${titulo}">
            <svg viewBox="0 0 24 24" aria-hidden="true">
                <path d="M7 10V8a5 5 0 0 1 10 0v2"
                      fill="none" stroke="currentColor" stroke-width="2"
                      stroke-linecap="round"/>
                <rect x="4" y="10" width="16" height="11" rx="2"
                      fill="none" stroke="currentColor" stroke-width="2"/>
            </svg>
        </span>
    `;
}

function textoPreviewUltimoMensaje(datos) {
    if (datos.UltimoCifrado) {
        return "Mensaje cifrado";
    }
    if (datos.UltimoTipo === "ubicacion") {
        return "Ubicación compartida";
    }
    if (datos.UltimoTipo === "archivo") {
        return "Archivo adjunto";
    }
    return datos.UltimoMensaje || "";
}

function refrescarBarraLateral() {
    clearTimeout(temporizadorBarraLateral);
    temporizadorBarraLateral = setTimeout(() => {
        cargarConversaciones();
        cargarGrupos();
    }, 100);
}

function actualizarInterruptorCifrado() {
    if (!cifradoButton) {
        return;
    }

    const esPrivado = !!(currentChatId &&
        currentChatId.startsWith("dm:"));
    const activo = esPrivado && cifradoActivo(currentChatId);

    cifradoButton.hidden = !esPrivado;

    if (!esPrivado) {
        if (cifradoAviso) {
            cifradoAviso.hidden = true;
        }
        actualizarBotonUbicacion();
        return;
    }

    cifradoButton.setAttribute(
        "aria-checked",
        activo ? "true" : "false"
    );
    cifradoButton.classList.toggle("activo", activo);
    cifradoButton.disabled = !cifradoListo;

    if (!cifradoListo) {
        cifradoButton.title = cifradoError ||
            "Preparando el cifrado de este dispositivo...";
    } else if (activo) {
        cifradoButton.title =
            "Cifrado activo. Los mensajes nuevos salen cifrados.";
    } else {
        cifradoButton.title =
            "Activar cifrado extremo a extremo en este chat.";
    }

    const etiqueta = cifradoButton.querySelector(".cifrado-label");
    if (etiqueta) {
        etiqueta.textContent = activo ? "Cifrado activo" : "Cifrado";
    }

    if (cifradoAviso) {
        cifradoAviso.hidden = !(activo && cifradoListo);
        if (activo && cifradoListo) {
            cifradoAviso.textContent =
                "Cifrado extremo a extremo activo: los mensajes nuevos de este chat solo se pueden leer en un dispositivo con la clave.";
        }
    }

    actualizarBotonUbicacion();
}

async function inicializarCifrado() {
    if (!sesionUsuario) {
        return;
    }

    if (!(window.Cifrado && Cifrado.soportado())) {
        cifradoError =
            "El cifrado no está disponible en este navegador.";
        actualizarInterruptorCifrado();
        return;
    }

    try {
        const publica = await Cifrado.publicaBase64();

        const respuesta = await fetch(
            `${SERVER_URL}/api/cuenta/clave`,
            {
                method: "PUT",
                headers: {
                    "Authorization": `Bearer ${token}`,
                    "Content-Type": "application/json"
                },
                body: JSON.stringify({ ClavePublica: publica })
            }
        );

        if (!respuesta.ok) {
            throw new Error(
                "No se pudo publicar la clave de cifrado.");
        }

        cifradoListo = true;
        cifradoError = "";
    } catch (error) {
        cifradoError = error.message ||
            "No se pudo preparar el cifrado.";
    }

    actualizarInterruptorCifrado();
}

async function alternarCifrado() {
    if (!currentChatId || !currentChatId.startsWith("dm:")) {
        return;
    }

    if (!cifradoListo) {
        aliasError.textContent = cifradoError ||
            "El cifrado todavía no está listo.";
        return;
    }

    const chatId = currentChatId;

    if (cifradoActivo(chatId)) {
        guardarCifradoChat(chatId, false);
        actualizarInterruptorCifrado();
        aliasError.textContent = "";
        return;
    }

    const otro = otroUsuarioDeChat(chatId);

    try {
        const publica = await obtenerClavePublicaUsuario(otro, true);

        if (!publica) {
            aliasError.textContent =
                "El otro usuario debe abrir el chat una vez para publicar su clave de cifrado.";
            return;
        }

        guardarCifradoChat(chatId, true);
        aliasError.textContent = "";
        actualizarInterruptorCifrado();
    } catch (error) {
        aliasError.textContent = error.message ||
            "No se pudo activar el cifrado.";
    }
}

/* -------------------- Cambio de chat (canal o privado) -------------------- */

function actualizarBotonBorrarChat(chatId) {
    if (!deleteChatButton) return;

    const borrable = !!chatId &&
        (chatId.startsWith("dm:") || chatId.startsWith("grupo:"));
    deleteChatButton.hidden = !borrable;
}

async function borrarChatActual() {
    const chatId = currentChatId;

    if (!chatId ||
        (!chatId.startsWith("dm:") && !chatId.startsWith("grupo:"))) {
        return;
    }

    const esGrupo = chatId.startsWith("grupo:");
    const aviso = esGrupo
        ? "¿Borrar todos los mensajes de este grupo?\n\n" +
          "Solo se borrarán para ti. El grupo seguirá en tu lista y los " +
          "demás integrantes conservarán el historial."
        : "¿Borrar todos los mensajes de esta conversación?\n\n" +
          "Solo se borrarán para ti. La conversación desaparecerá de tu " +
          "lista hasta que llegue un mensaje nuevo.";

    if (!confirm(aviso)) {
        return;
    }

    deleteChatButton.disabled = true;

    try {
        const respuesta = await fetch(
            `${SERVER_URL}/api/mensajes?chatId=${encodeURIComponent(chatId)}`,
            {
                method: "DELETE",
                headers: { "Authorization": `Bearer ${token}` }
            }
        );

        const datos = await respuesta.json().catch(() => ({}));

        if (!respuesta.ok) {
            alert(datos.Error || datos.title || "No se pudo borrar el chat.");
            return;
        }

        limpiarBorrador(chatId);

        if (currentChatId === chatId) {
            if (messagesContainer) {
                messagesContainer.innerHTML = "";
            }
            primerIdHistorial = null;
            tieneMasHistorial = false;
            ultimaFechaSeparador = "";
        }

        await cargarConversaciones();
        await cargarGrupos();
    } catch (error) {
        console.error("No se pudo borrar el chat:", error);
        alert("Error de red al borrar el chat.");
    } finally {
        if (deleteChatButton) {
            deleteChatButton.disabled = false;
        }
    }
}

if (deleteChatButton) {
    deleteChatButton.addEventListener("click", borrarChatActual);
}

async function abrirChat(chatId, meta) {
    if (meta) {
        chatsPrivados[chatId] = meta;
    }

    if (emailModal && !emailModal.hidden && !enviandoCorreo) {
        cerrarModalCorreo();
    }

    if (currentChatId && currentChatId !== chatId) {
        guardarBorradorActual();
    }

    const info = metaDeChat(chatId);

    currentChatName.textContent = info.name;
    currentChatStatus.textContent = info.status;
    currentChatImage.src = info.image;
    currentChatImage.hidden = false;

    if (chatId && chatId.startsWith("dm:")) {
        const partes = chatId.split(":");
        const a = Number(partes[1]);
        const b = Number(partes[2]);
        if (sesionUsuario) {
            otroUsuarioIdChatActual = a === sesionUsuario.Id ? b : a;
        } else {
            otroUsuarioIdChatActual = null;
        }
        if (otroUsuarioIdChatActual) {
            obtenerPresencia([otroUsuarioIdChatActual]);
        }
    } else {
        otroUsuarioIdChatActual = null;
    }
    actualizarIndicadoresPresencia();

    if (connection && chatId !== currentChatId) {
        try {
            if (currentChatId) {
                await connection.invoke("LeaveGroup", currentChatId);
            }
            if (!esSoloLectura(chatId)) {
                await connection.invoke("JoinGroup", chatId);
            }
        } catch (error) {
            console.error("Error al cambiar de chat:", error);
        }
    }

    currentChatId = chatId;
    actualizarBarraGrupo(chatId);
    actualizarBotonVideollamada(chatId);
    actualizarBotonCorreoChat(chatId);
    actualizarPanelTareas(chatId);
    actualizarInterruptorCifrado();
    actualizarBotonUbicacion(chatId);
    actualizarBotonBorrarChat(chatId);

    if (!esSoloLectura(chatId) && connection && connection.state === "Connected") {
        messageInput.disabled = false;
        sendButton.disabled = false;
    }
    aplicarModoLectura(chatId);

    aplicarBorrador(chatId);

    await cargarHistorial(chatId);
    actualizarIndicadoresPresencia();
}



/* -------------------- Mensajes privados -------------------- */

async function obtenerPresencia(ids) {
    if (!ids || ids.length === 0) return;
    const idsUnicos = [...new Set(ids.filter(id => id))];
    if (idsUnicos.length === 0) return;
    try {
        const respuesta = await fetch(
            `${SERVER_URL}/api/presencia?ids=${encodeURIComponent(idsUnicos.join(","))}`,
            {
                headers: { "Authorization": `Bearer ${token}` }
            }
        );
        if (!respuesta.ok) return;
        const datos = await respuesta.json();
        for (const key of Object.keys(datos)) {
            const idNum = Number(key);
            if (!isNaN(idNum)) {
                presenciaUsuarios.set(idNum, !!datos[key]);
            }
        }
        actualizarIndicadoresPresencia();
    } catch (error) {
        console.error("No se pudo obtener presencia:", error);
    }
}

function actualizarIndicadoresPresencia() {
    if (privateList) {
        privateList.querySelectorAll(".private-item").forEach(boton => {
            const chatId = boton.dataset.chat || "";
            const dot = boton.querySelector(".status-dot");
            if (!dot) return;
            if (!chatId.startsWith("dm:")) {
                dot.hidden = true;
                dot.classList.remove("online", "offline");
                return;
            }
            const partes = chatId.split(":");
            const a = Number(partes[1]);
            const b = Number(partes[2]);
            if (!sesionUsuario) {
                dot.hidden = true;
                return;
            }
            const otro = a === sesionUsuario.Id ? b : a;
            const conectado = presenciaUsuarios.get(otro);
            dot.hidden = conectado === undefined;
            dot.classList.toggle("online", conectado === true);
            dot.classList.toggle("offline", conectado === false);
        });
    }

    if (currentChatPresence) {
        if (currentChatId && currentChatId.startsWith("dm:") && otroUsuarioIdChatActual) {
            const conectado = presenciaUsuarios.get(otroUsuarioIdChatActual);
            currentChatPresence.hidden = conectado === undefined;
            currentChatPresence.classList.toggle("online", conectado === true);
            currentChatPresence.classList.toggle("offline", conectado === false);
        } else {
            currentChatPresence.hidden = true;
            currentChatPresence.classList.remove("online", "offline");
        }
    }
}

async function cargarConversaciones() {
    if (!token || !privateList) return;

    try {
        const respuesta = await fetch(
            `${SERVER_URL}/api/conversaciones`,
            {
                headers: { "Authorization": `Bearer ${token}` }
            }
        );

        if (!respuesta.ok) return;

        const datos = await respuesta.json();

        const listaOrdenada = [...(datos.Conversaciones || [])].sort((a, b) => {
            const fa = a.UltimaFecha ? new Date(a.UltimaFecha).getTime() : 0;
            const fb = b.UltimaFecha ? new Date(b.UltimaFecha).getTime() : 0;
            if (fb !== fa) return fb - fa;
            return 0;
        });

        const desplazamiento = privateList.scrollTop;
        privateList.innerHTML = "";

        const idsPresencia = [];
        for (const conversacion of listaOrdenada) {
            chatsPrivados[conversacion.ChatId] = {
                name: conversacion.OtroNombre || "Usuario",
                status: "Conversación privada",
                image: AVATAR_POR_DEFECTO,
                otroUsuarioId: conversacion.OtroUsuarioId
            };
            if (conversacion.OtroUsuarioId) {
                idsPresencia.push(conversacion.OtroUsuarioId);
            }

            const preview = textoPreviewUltimoMensaje(conversacion);

            const boton = document.createElement("button");
            boton.type = "button";
            boton.className = "private-item";
            boton.dataset.chat = conversacion.ChatId;
            boton.innerHTML = `
                <span class="status-dot offline" hidden></span>
                <div class="private-info">
                    <span class="private-name">${escapeHtml(conversacion.OtroNombre || "Usuario")}</span>
                    <span class="private-preview">${conversacion.UltimoCifrado ? `${candadoHtml(false)} ` : ""}${escapeHtml(preview)}</span>
                </div>
            `;

            if (conversacion.ChatId === currentChatId) {
                boton.classList.add("active");
            }

            boton.addEventListener("click", () => {
                marcarActivo(boton);
                abrirChat(
                    conversacion.ChatId,
                    chatsPrivados[conversacion.ChatId]
                );
            });

            privateList.appendChild(boton);
        }

        privateList.scrollTop = desplazamiento;
        obtenerPresencia(idsPresencia);
        actualizarIndicadoresPresencia();
    } catch (error) {
        console.error(
            "No se pudieron cargar las conversaciones:",
            error
        );
    }
}

async function cargarContactos() {
    if (!token || !contactsList) return;

    try {
        const respuesta = await fetch(
            `${SERVER_URL}/api/usuarios`,
            {
                headers: { "Authorization": `Bearer ${token}` }
            }
        );

        if (!respuesta.ok) return;

        const datos = await respuesta.json();

        contactsList.innerHTML = "";

        if (!datos.Usuarios || datos.Usuarios.length === 0) {
            contactsList.innerHTML =
                `<div class="contact-empty">No hay otros usuarios.</div>`;
            return;
        }

        for (const contacto of datos.Usuarios) {
            const boton = document.createElement("button");
            boton.type = "button";
            boton.className = "contact-item";
            boton.innerHTML = `
                <span class="contact-name">${escapeHtml(contacto.Nombre || "Usuario")}</span>
                <span class="contact-user">@${escapeHtml(contacto.Usuario || "")}</span>
            `;

            boton.addEventListener("click", () => {
                const chatId = chatPrivadoId(
                    sesionUsuario.Id,
                    contacto.Id
                );

                const meta = {
                    name: contacto.Nombre || "Usuario",
                    status: "Conversación privada",
                    image: AVATAR_POR_DEFECTO
                };

                chatsPrivados[chatId] = meta;
                contactsList.setAttribute("hidden", "");
                marcarActivo(null);
                abrirChat(chatId, meta);
            });

            contactsList.appendChild(boton);
        }
    } catch (error) {
        console.error("No se pudieron cargar los contactos:", error);
    }
}

if (cifradoButton) {
    cifradoButton.addEventListener("click", alternarCifrado);
}

if (newDmButton && contactsList) {
    newDmButton.addEventListener("click", async () => {
        const oculto = contactsList.hasAttribute("hidden");

        if (oculto) {
            await cargarContactos();
            contactsList.removeAttribute("hidden");
        } else {
            contactsList.setAttribute("hidden", "");
        }
    });
}

function mostrarMensajeCorreo(elemento, texto) {
    if (!elemento) {
        return;
    }

    elemento.textContent = texto;
    elemento.hidden = texto === "";
}

function limpiarMensajesCorreo() {
    mostrarMensajeCorreo(emailError, "");
    mostrarMensajeCorreo(emailSuccess, "");
}

function actualizarBotonEnvioCorreo() {
    if (!sendEmailButton || !emailRecipient) {
        return;
    }

    const listo = !cargandoCorreo &&
        !enviandoCorreo &&
        emailRecipient.value !== "";

    sendEmailButton.disabled = !listo;
}

function cerrarModalCorreo() {
    if (!emailModal || enviandoCorreo) {
        return;
    }

    emailModal.hidden = true;
    limpiarMensajesCorreo();
    emailRecipient.disabled = false;
}

async function abrirModalCorreo(destinoFijo = null) {
    if (!emailModal || !emailRecipient || !emailSubject || !emailBody) {
        return;
    }

    const fijo = destinoFijo && Number(destinoFijo.Id) > 0
        ? destinoFijo
        : null;

    limpiarMensajesCorreo();
    emailSubject.value = "";
    emailBody.value = "";
    emailRecipient.innerHTML = `<option value="">Cargando...</option>`;
    emailRecipient.disabled = true;
    emailModal.hidden = false;
    cargandoCorreo = true;
    actualizarBotonEnvioCorreo();

    if (emailModalTitle) {
        emailModalTitle.textContent = fijo
            ? `Enviar correo a ${fijo.Nombre || "usuario"}`
            : "Enviar correo";
    }

    if (emailModalHint) {
        emailModalHint.hidden = Boolean(fijo);
    }

    if (fijo) {
        emailRecipient.innerHTML = "";
        const opcion = document.createElement("option");
        opcion.value = String(fijo.Id);
        opcion.textContent = fijo.Nombre || "Usuario";
        opcion.selected = true;
        emailRecipient.appendChild(opcion);
        cargandoCorreo = false;
        actualizarBotonEnvioCorreo();
        emailSubject.focus();
        return;
    }

    try {
        const respuesta = await fetch(`${SERVER_URL}/api/usuarios`, {
            headers: { "Authorization": `Bearer ${token}` }
        });

        if (!respuesta.ok) {
            throw new Error("No se pudieron cargar los usuarios.");
        }

        const datos = await respuesta.json();
        const usuarios = Array.isArray(datos.Usuarios)
            ? datos.Usuarios.filter(
                usuario => Number(usuario.Id) !== Number(sesionUsuario.Id)
            )
            : [];

        emailRecipient.innerHTML =
            `<option value="">Selecciona un compañero</option>`;

        if (usuarios.length === 0) {
            mostrarMensajeCorreo(
                emailError,
                "No hay otros usuarios registrados para enviarles un correo."
            );
            return;
        }

        for (const usuario of usuarios) {
            const opcion = document.createElement("option");
            opcion.value = String(usuario.Id);
            opcion.textContent =
                `${usuario.Nombre || "Usuario"} (@${usuario.Usuario || ""})`;
            emailRecipient.appendChild(opcion);
        }

        emailRecipient.disabled = false;
        emailRecipient.focus();
    } catch (error) {
        emailRecipient.innerHTML =
            `<option value="">Sin usuarios disponibles</option>`;
        mostrarMensajeCorreo(
            emailError,
            "No se pudieron cargar los usuarios registrados."
        );
    } finally {
        cargandoCorreo = false;
        actualizarBotonEnvioCorreo();
    }
}

async function enviarCorreoSeleccionado() {
    if (!emailRecipient || !emailSubject || !emailBody || !sendEmailButton) {
        return;
    }

    limpiarMensajesCorreo();

    const destinatarioId = Number(emailRecipient.value);
    const asunto = emailSubject.value.trim();
    const mensaje = emailBody.value.trim();

    if (!Number.isInteger(destinatarioId) || destinatarioId <= 0) {
        mostrarMensajeCorreo(emailError, "Selecciona un compañero.");
        return;
    }

    if (asunto === "") {
        mostrarMensajeCorreo(emailError, "Escribe un asunto.");
        emailSubject.focus();
        return;
    }

    if (asunto.length > 200) {
        mostrarMensajeCorreo(
            emailError,
            "El asunto no puede tener más de 200 caracteres."
        );
        emailSubject.focus();
        return;
    }

    if (/[\r\n]/.test(asunto)) {
        mostrarMensajeCorreo(
            emailError,
            "El asunto no puede contener saltos de línea."
        );
        emailSubject.focus();
        return;
    }

    if (mensaje === "") {
        mostrarMensajeCorreo(emailError, "Escribe un mensaje.");
        emailBody.focus();
        return;
    }

    if (mensaje.length > 5000) {
        mostrarMensajeCorreo(
            emailError,
            "El mensaje no puede tener más de 5000 caracteres."
        );
        emailBody.focus();
        return;
    }

    enviandoCorreo = true;
    sendEmailButton.textContent = "Enviando...";
    actualizarBotonEnvioCorreo();

    if (cancelEmailButton) {
        cancelEmailButton.disabled = true;
    }

    if (closeEmailModal) {
        closeEmailModal.disabled = true;
    }

    try {
        const respuesta = await fetch(`${SERVER_URL}/api/correo/enviar`, {
            method: "POST",
            headers: {
                "Content-Type": "application/json",
                "Authorization": `Bearer ${token}`
            },
            body: JSON.stringify({
                DestinatarioId: destinatarioId,
                Asunto: asunto,
                Mensaje: mensaje
            })
        });

        const datos = await respuesta.json().catch(() => ({}));

        if (!respuesta.ok) {
            mostrarMensajeCorreo(
                emailError,
                datos.Error || datos.title ||
                    "No se pudo enviar el correo."
            );
            return;
        }

        const nombreDestino = datos.DestinatarioNombre ||
            emailRecipient.options[emailRecipient.selectedIndex]?.textContent ||
            "el destinatario";

        mostrarMensajeCorreo(
            emailSuccess,
            `Correo enviado a ${nombreDestino}.`
        );
        emailSubject.value = "";
        emailBody.value = "";
    } catch (error) {
        mostrarMensajeCorreo(
            emailError,
            "Error de red al enviar el correo."
        );
    } finally {
        enviandoCorreo = false;
        sendEmailButton.textContent = "Enviar correo";

        if (cancelEmailButton) {
            cancelEmailButton.disabled = false;
        }

        if (closeEmailModal) {
            closeEmailModal.disabled = false;
        }

        actualizarBotonEnvioCorreo();
    }
}

if (enviarCorreoButton && emailModal) {
    enviarCorreoButton.addEventListener("click", () => abrirModalCorreo());
}

if (closeEmailModal) {
    closeEmailModal.addEventListener("click", cerrarModalCorreo);
}

if (cancelEmailButton) {
    cancelEmailButton.addEventListener("click", cerrarModalCorreo);
}

if (sendEmailButton) {
    sendEmailButton.addEventListener("click", enviarCorreoSeleccionado);
}

if (emailRecipient) {
    emailRecipient.addEventListener("change", actualizarBotonEnvioCorreo);
}

if (emailModal) {
    emailModal.addEventListener("click", event => {
        if (event.target === emailModal) {
            cerrarModalCorreo();
        }
    });
}

document.addEventListener("keydown", event => {
    if (event.key === "Escape" &&
        emailModal &&
        !emailModal.hidden) {
        cerrarModalCorreo();
    }
});

/* -------------------- Grupos -------------------- */

/* Mínimo de integrantes contando al creador. El servidor lo vuelve a
   validar; aquí solo se avisa antes de enviar. */
const MINIMO_INTEGRANTES_GRUPO = 3;

const newGroupButton = document.getElementById("newGroupButton");
const groupCreate = document.getElementById("groupCreate");
const groupName = document.getElementById("groupName");
const groupLink = document.getElementById("groupLink");
const groupContacts = document.getElementById("groupContacts");
const groupCount = document.getElementById("groupCount");
const groupError = document.getElementById("groupError");
const groupCreateButton = document.getElementById("groupCreateButton");
const groupList = document.getElementById("groupList");

function integrantesSeleccionados() {
    return [...groupContacts.querySelectorAll("input[type='checkbox']:checked")]
        .map(input => Number(input.value));
}

function actualizarContadorGrupo() {
    const total = integrantesSeleccionados().length + 1;
    groupCount.textContent = `Integrantes: ${total}`;
}

/* Lista de grupos del usuario en la barra lateral. */
async function cargarGrupos() {
    if (!token || !groupList) return;

    try {
        const respuesta = await fetch(`${SERVER_URL}/api/grupos`, {
            headers: { "Authorization": `Bearer ${token}` }
        });

        if (!respuesta.ok) return;

        const datos = await respuesta.json();

        const listaOrdenada = [...(datos.Grupos || [])].sort((a, b) => {
            if (a.Activo !== b.Activo) {
                return a.Activo ? -1 : 1;
            }
            const fa = a.UltimaFecha ? new Date(a.UltimaFecha).getTime() : 0;
            const fb = b.UltimaFecha ? new Date(b.UltimaFecha).getTime() : 0;
            if (fb !== fa) {
                return fb - fa;
            }
            return (a.Nombre || "").localeCompare(b.Nombre || "", "es", {
                sensitivity: "base"
            });
        });

        const eraSoloLectura = esSoloLectura(currentChatId);

        gruposInactivos.clear();
        const desplazamiento = groupList.scrollTop;
        groupList.innerHTML = "";

        for (const grupo of listaOrdenada) {
            if (!grupo.Activo) {
                gruposInactivos.add(grupo.ChatId);
            }

            const meta = {
                name: grupo.Nombre,
                status: grupo.Activo
                    ? `${grupo.Integrantes} integrantes`
                    : "Ya no eres integrante",
                image: AVATAR_POR_DEFECTO
            };

            const textoPreview = textoPreviewUltimoMensaje(grupo);
            const previewGrupo = grupo.Activo
                ? (textoPreview
                    ? `${grupo.UltimoUsuario || "Alguien"}: ${textoPreview}`
                    : "Sin mensajes")
                : "Solo lectura";

            const boton = document.createElement("button");
            boton.type = "button";
            boton.className = grupo.Activo
                ? "private-item"
                : "private-item inactive";
            boton.dataset.chat = grupo.ChatId;
            boton.innerHTML = `
                <span class="private-name">${escapeHtml(grupo.Nombre)}</span>
                <span class="private-preview">${escapeHtml(previewGrupo)}</span>
            `;

            if (grupo.ChatId === currentChatId) {
                boton.classList.add("active");
                currentChatStatus.textContent = meta.status;
            }

            boton.addEventListener("click", () => {
                marcarActivo(boton);
                abrirChat(grupo.ChatId, meta);
            });

            groupList.appendChild(boton);
        }

        groupList.scrollTop = desplazamiento;
        await reconciliarChatActual(eraSoloLectura);
    } catch (error) {
        console.error("No se pudieron cargar los grupos:", error);
    }
}

/* -------------------- Solo lectura -------------------- */

/* Un grupo del que ya no eres integrante: se puede leer, no escribir. */
function esSoloLectura(chatId) {
    return gruposInactivos.has(chatId);
}

function aplicarModoLectura(chatId) {
    if (!esSoloLectura(chatId)) {
        actualizarBotonUbicacion(chatId);
        return;
    }

    messageInput.disabled = true;
    sendButton.disabled = true;
    actualizarBotonUbicacion(chatId);
    groupMembersButton.hidden = true;
    groupPanel.hidden = true;
}

/* Si cambió el estado del chat abierto (te quitaron o te volvieron a
   agregar), ajusta la entrada. Al volver a ser integrante hay que unirse de
   nuevo al grupo en vivo: el servidor no lo hace por ti. */
async function reconciliarChatActual(eraSoloLectura) {
    if (esSoloLectura(currentChatId)) {
        aplicarModoLectura(currentChatId);
        return;
    }

    if (eraSoloLectura && connection && connection.state === "Connected") {
        try {
            await connection.invoke("JoinGroup", currentChatId);
        } catch (error) {
            console.error("No se pudo volver a unir al grupo:", error);
            return;
        }

        messageInput.disabled = false;
        sendButton.disabled = false;
        actualizarBotonUbicacion(currentChatId);
        groupMembersButton.hidden = idDeGrupo(currentChatId) === null;
    }
}

/* Usuarios que se pueden elegir como integrantes (sin el propio). */
async function cargarContactosGrupo() {
    if (!token || !groupContacts) return;

    groupContacts.innerHTML = "";
    groupError.textContent = "";

    try {
        const respuesta = await fetch(`${SERVER_URL}/api/usuarios`, {
            headers: { "Authorization": `Bearer ${token}` }
        });

        if (!respuesta.ok) {
            groupError.textContent = "No se pudieron cargar los usuarios.";
            return;
        }

        const datos = await respuesta.json();

        if (!datos.Usuarios || datos.Usuarios.length === 0) {
            groupContacts.innerHTML =
                `<div class="contact-empty">No hay otros usuarios.</div>`;
            return;
        }

        for (const contacto of datos.Usuarios) {
            const etiqueta = document.createElement("label");
            etiqueta.className = "group-contact";
            etiqueta.innerHTML = `
                <input type="checkbox" value="${Number(contacto.Id)}">
                <span>${escapeHtml(contacto.Nombre || "Usuario")}
                      <small>@${escapeHtml(contacto.Usuario || "")}</small></span>
            `;

            etiqueta.querySelector("input")
                .addEventListener("change", actualizarContadorGrupo);

            groupContacts.appendChild(etiqueta);
        }

        actualizarContadorGrupo();
    } catch (error) {
        groupError.textContent = "No hay conexión con el servidor.";
    }
}

async function crearGrupo() {
    groupError.textContent = "";

    const nombre = groupName.value.trim();
    const enlace = groupLink ? groupLink.value.trim() : "";
    const miembros = integrantesSeleccionados();

    if (nombre === "") {
        groupError.textContent = "Escribe un nombre para el grupo.";
        return;
    }

    if (enlace !== "" && !/^https?:\/\//i.test(enlace)) {
        groupError.textContent =
            "El enlace debe empezar por http:// o https://.";
        return;
    }

    if (miembros.length + 1 < MINIMO_INTEGRANTES_GRUPO) {
        groupError.textContent =
            `Un grupo necesita al menos ${MINIMO_INTEGRANTES_GRUPO} ` +
            "integrantes, contándote a ti.";
        return;
    }

    groupCreateButton.disabled = true;

    try {
        const respuesta = await fetch(`${SERVER_URL}/api/grupos`, {
            method: "POST",
            headers: {
                "Content-Type": "application/json",
                "Authorization": `Bearer ${token}`
            },
            body: JSON.stringify({
                Nombre: nombre,
                Miembros: miembros,
                Enlace: enlace
            })
        });

        const datos = await respuesta.json().catch(() => ({}));

        if (!respuesta.ok) {
            groupError.textContent =
                datos.Error || "No se pudo crear el grupo.";
            return;
        }

        groupName.value = "";
        if (groupLink) groupLink.value = "";
        groupCreate.setAttribute("hidden", "");

        await abrirChat(datos.ChatId, {
            name: nombre,
            status: `${datos.Integrantes} integrantes`,
            image: AVATAR_POR_DEFECTO
        });

        await cargarGrupos();
    } catch (error) {
        groupError.textContent = "No hay conexión con el servidor.";
    } finally {
        groupCreateButton.disabled = false;
    }
}

if (newGroupButton && groupCreate) {
    newGroupButton.addEventListener("click", async () => {
        if (groupCreate.hasAttribute("hidden")) {
            groupCreate.removeAttribute("hidden");
            await cargarContactosGrupo();
        } else {
            groupCreate.setAttribute("hidden", "");
        }
    });
}

if (groupCreateButton) {
    groupCreateButton.addEventListener("click", crearGrupo);
}

/* -------------------- Integrantes del grupo (agregar / quitar) -------------------- */

const groupMembersButton = document.getElementById("groupMembersButton");
const groupPanel = document.getElementById("groupPanel");
const groupPanelList = document.getElementById("groupPanelList");
const groupPanelCount = document.getElementById("groupPanelCount");
const integrantesPanelGrupo = new Map();
const groupAddContacts = document.getElementById("groupAddContacts");
const groupAddButton = document.getElementById("groupAddButton");
const groupPanelError = document.getElementById("groupPanelError");

const groupRewards = document.getElementById("groupRewards");
const rewardProgress = document.getElementById("rewardProgress");
const rewardSlots = document.getElementById("rewardSlots");
const rewardError = document.getElementById("rewardError");
const attachCardsButton = document.getElementById("attachCardsButton");
const rewardPicker = document.getElementById("rewardPicker");
const rewardLink = document.getElementById("rewardLink");
const saveRewardConfig = document.getElementById("saveRewardConfig");
const visitRewardPage = document.getElementById("visitRewardPage");
let configuracionRecompensasGrupo = null;
let recompensasSeleccionadas = [];

/* "grupo:12" -> 12. Null si el chat no es de grupo. */
function idDeGrupo(chatId) {
    return chatId && chatId.startsWith("grupo:")
        ? Number(chatId.slice(6))
        : null;
}

/* El botón y el panel solo existen en chats de grupo. Al cambiar de chat
   el panel se cierra. */
function actualizarBarraGrupo(chatId) {
    groupMembersButton.hidden =
        idDeGrupo(chatId) === null || esSoloLectura(chatId);
    groupPanel.hidden = true;
    groupPanelError.textContent = "";
}

async function obtenerDetalleGrupo(grupoId) {
    const respuesta = await fetch(`${SERVER_URL}/api/grupos/${grupoId}`, {
        headers: { "Authorization": `Bearer ${token}` }
    });

    const datos = await respuesta.json().catch(() => ({}));

    if (!respuesta.ok) {
        throw new Error(datos.Error || "No se pudo cargar el grupo.");
    }

    return datos;
}

/* Dibuja la lista de integrantes (con Quitar o Salir) y los usuarios que
   aún se pueden agregar. */
async function renderPanelGrupo() {
    const grupoId = idDeGrupo(currentChatId);
    if (grupoId === null) return;

    groupPanelError.textContent = "";

    try {
        const grupo = await obtenerDetalleGrupo(grupoId);
        const soyCreador = grupo.CreadorId === sesionUsuario.Id;
        const integrantes = grupo.Integrantes;
        const idsIntegrantes = new Set(integrantes.map(m => m.Id));

        groupPanelCount.textContent = `${integrantes.length} en total`;
        groupPanelList.innerHTML = "";
        integrantesPanelGrupo.clear();

        for (const miembro of integrantes) {
            integrantesPanelGrupo.set(Number(miembro.Id), miembro);
            const yo = miembro.Id === sesionUsuario.Id;
            let accion = "";

            if (yo) {
                accion = `<button type="button" class="group-panel-action" data-accion="salir">Salir</button>`;
            } else {
                accion = `<button type="button" class="group-panel-action" data-accion="coleccion" data-id="${Number(miembro.Id)}">Colección</button>`;
                if (soyCreador) {
                    accion += `<button type="button" class="group-panel-action" data-accion="quitar" data-id="${Number(miembro.Id)}">Quitar</button>`;
                }
            }

            const fila = document.createElement("div");
            fila.className = "group-panel-row";
            fila.innerHTML = `
                <span>${escapeHtml(miembro.Nombre || "Usuario")}${yo ? " (tú)" : ""}
                      <small>@${escapeHtml(miembro.Usuario || "")}</small></span>
                ${accion}
            `;
            groupPanelList.appendChild(fila);
        }

        const respuestaUsuarios = await fetch(`${SERVER_URL}/api/usuarios`, {
            headers: { "Authorization": `Bearer ${token}` }
        });

        groupAddContacts.innerHTML = "";

        const datosUsuarios = respuestaUsuarios.ok
            ? await respuestaUsuarios.json()
            : { Usuarios: [] };

        const disponibles = (datosUsuarios.Usuarios || [])
            .filter(c => !idsIntegrantes.has(c.Id));

        if (disponibles.length === 0) {
            groupAddContacts.innerHTML =
                `<div class="contact-empty">Todos los usuarios ya son integrantes.</div>`;
        }

        for (const candidato of disponibles) {
            const etiqueta = document.createElement("label");
            etiqueta.className = "group-contact";
            etiqueta.innerHTML = `
                <input type="checkbox" value="${Number(candidato.Id)}">
                <span>${escapeHtml(candidato.Nombre || "Usuario")}
                      <small>@${escapeHtml(candidato.Usuario || "")}</small></span>
            `;
            groupAddContacts.appendChild(etiqueta);
        }

        await cargarConfigRecompensas(grupoId, soyCreador);
    } catch (error) {
        groupPanelError.textContent = error.message;
    }
}

function cartaPorId(id) {
    return CARTAS.find(carta => carta.Id === Number(id)) || null;
}

function renderSlotsRecompensas(ids) {
    if (!rewardSlots) return;

    rewardSlots.innerHTML = "";

    const etiquetas = [
        "Retos 1 y 2",
        "Retos 1 y 2",
        "Con el enlace"
    ];

    for (let i = 0; i < 3; i++) {
        const carta = cartaPorId(ids[i]);
        const contenedor = document.createElement("div");
        contenedor.className = "reward-slot";

        if (carta) {
            const imagen = document.createElement("img");
            imagen.src = carta.Imagen;
            imagen.alt = carta.Nombre;
            contenedor.appendChild(imagen);
        }

        const etiqueta = document.createElement("span");
        etiqueta.textContent = etiquetas[i];
        contenedor.appendChild(etiqueta);
        rewardSlots.appendChild(contenedor);
    }
}

function renderSeleccionRecompensas() {
    if (rewardPicker) {
        [...rewardPicker.children].forEach(boton => {
            boton.classList.toggle(
                "selected",
                recompensasSeleccionadas.includes(Number(boton.dataset.id)));
        });
    }

    renderSlotsRecompensas(recompensasSeleccionadas);
}

function renderConfigRecompensas(soyCreador) {
    if (!groupRewards || !configuracionRecompensasGrupo) return;

    const datos = configuracionRecompensasGrupo;
    const tieneEnlace =
        datos.RetosDisponibles !== false &&
        Boolean((datos.Enlace || "").trim());

    groupRewards.hidden = !soyCreador && !tieneEnlace;
    if (groupRewards.hidden) return;

    const mias = tareasGrupoActual.filter(
        tarea => Number(tarea.AsignadoId) === Number(sesionUsuario.Id));
    const hechas = mias.filter(tarea => tarea.Finalizada).length;
    const total = mias.length || 3;

    rewardProgress.textContent = tieneEnlace
        ? `${hechas}/${total} retos`
        : "Sin enlace · retos desactivados";

    recompensasSeleccionadas = (datos.Plan || []).map(carta => carta.Id);
    renderSeleccionRecompensas();

    if (rewardLink) {
        rewardLink.value = datos.Enlace || "";
    }

    if (attachCardsButton) attachCardsButton.hidden = !soyCreador;
    if (rewardLink) rewardLink.hidden = !soyCreador;
    if (saveRewardConfig) saveRewardConfig.hidden = !soyCreador;
    if (visitRewardPage) {
        visitRewardPage.hidden =
            !(tieneEnlace && (datos.Completadas || hechas === 3));
    }

    if (rewardError) {
        rewardError.textContent = "";
        rewardError.hidden = true;
    }
}

async function cargarConfigRecompensas(grupoId, soyCreador) {
    if (!groupRewards) return;

    try {
        const respuesta = await fetch(
            `${SERVER_URL}/api/grupos/${grupoId}/recompensas`,
            { headers: { "Authorization": `Bearer ${token}` } });
        const datos = await respuesta.json().catch(() => ({}));

        if (!respuesta.ok) {
            throw new Error(datos.Error || "No se pudo cargar la configuración.");
        }

        configuracionRecompensasGrupo = datos;
        renderConfigRecompensas(soyCreador);
    } catch (error) {
        if (rewardError) {
            rewardError.textContent = error.message;
            rewardError.hidden = false;
        }
    }
}

async function guardarConfigRecompensas() {
    const grupoId = idDeGrupo(currentChatId);

    if (grupoId === null || !rewardError) return;

    rewardError.hidden = true;
    rewardError.textContent = "";

    if (recompensasSeleccionadas.length !== 3) {
        rewardError.textContent = "Adjunta exactamente 3 cartas.";
        rewardError.hidden = false;
        return;
    }

    const enlace = (rewardLink ? rewardLink.value.trim() : "");

    if (enlace !== "" && !/^https?:\/\//i.test(enlace)) {
        rewardError.textContent = "El enlace debe empezar por http:// o https://.";
        rewardError.hidden = false;
        return;
    }

    saveRewardConfig.disabled = true;

    try {
        const respuesta = await fetch(
            `${SERVER_URL}/api/grupos/${grupoId}/recompensas`,
            {
                method: "PUT",
                headers: {
                    "Content-Type": "application/json",
                    "Authorization": `Bearer ${token}`
                },
                body: JSON.stringify({
                    Recompensas: recompensasSeleccionadas,
                    Enlace: enlace
                })
            });
        const datos = await respuesta.json().catch(() => ({}));

        if (!respuesta.ok) {
            throw new Error(datos.Error || "No se pudo guardar la configuración.");
        }

        configuracionRecompensasGrupo = datos;
        if (rewardPicker) rewardPicker.hidden = true;
        renderConfigRecompensas(true);
        aliasError.textContent = "Cartas y enlace del grupo guardados.";
        await cargarTareas(grupoId);
    } catch (error) {
        rewardError.textContent = error.message;
        rewardError.hidden = false;
    } finally {
        saveRewardConfig.disabled = false;
    }
}

async function abrirPaginaRecompensa() {
    const grupoId = idDeGrupo(currentChatId);

    if (grupoId === null) return;

    try {
        if (!configuracionRecompensasGrupo ||
            Number(configuracionRecompensasGrupo.GrupoId) !== grupoId) {
            await cargarConfigRecompensas(grupoId, false);
        }

        if (configuracionRecompensasGrupo &&
            configuracionRecompensasGrupo.RetosDisponibles === false) {
            throw new Error("Este grupo aún no tiene retos habilitados.");
        }

        const miEnlace = configuracionRecompensasGrupo
            ? configuracionRecompensasGrupo.MiEnlace
            : null;

        if (!miEnlace) {
            throw new Error("No se pudo crear tu enlace de recompensa.");
        }

        window.open(`${SERVER_URL}${miEnlace}`, "_blank", "noopener");
    } catch (error) {
        if (rewardError) {
            rewardError.textContent = error.message;
            rewardError.hidden = false;
        }
    }
}

/* Si el chat abierto es el grupo del que saliste o te quitaron, se queda
   abierto en solo lectura: el historial sigue disponible. Es idempotente: el
   aviso del hub y la respuesta de la API pueden llegar en cualquier orden. */
async function salirDelGrupoActual(chatId) {
    if (chatId !== currentChatId) return;

    gruposInactivos.add(chatId);
    currentChatStatus.textContent = "Ya no eres integrante · solo lectura";
    aplicarModoLectura(chatId);
    aliasError.textContent =
        "Ya no eres integrante de este grupo. Puedes leer su historial.";
}

async function quitarIntegrante(grupoId, usuarioId) {
    groupPanelError.textContent = "";

    try {
        const respuesta = await fetch(
            `${SERVER_URL}/api/grupos/${grupoId}/miembros/${usuarioId}`,
            {
                method: "DELETE",
                headers: { "Authorization": `Bearer ${token}` }
            }
        );

        const datos = await respuesta.json().catch(() => ({}));

        if (!respuesta.ok) {
            groupPanelError.textContent =
                datos.Error || "No se pudo quitar al integrante.";
            return;
        }

        if (usuarioId === sesionUsuario.Id) {
            await salirDelGrupoActual(`grupo:${grupoId}`);
        } else {
            await renderPanelGrupo();
        }

        await cargarGrupos();
    } catch (error) {
        groupPanelError.textContent = "No hay conexión con el servidor.";
    }
}

async function agregarIntegrantes(grupoId, nuevos) {
    groupPanelError.textContent = "";
    groupAddButton.disabled = true;

    try {
        const respuesta = await fetch(
            `${SERVER_URL}/api/grupos/${grupoId}/miembros`,
            {
                method: "POST",
                headers: {
                    "Content-Type": "application/json",
                    "Authorization": `Bearer ${token}`
                },
                body: JSON.stringify({ Miembros: nuevos })
            }
        );

        const datos = await respuesta.json().catch(() => ({}));

        if (!respuesta.ok) {
            groupPanelError.textContent =
                datos.Error || "No se pudo agregar a los integrantes.";
            return;
        }

        await renderPanelGrupo();
        await cargarGrupos();
    } catch (error) {
        groupPanelError.textContent = "No hay conexión con el servidor.";
    } finally {
        groupAddButton.disabled = false;
    }
}

groupMembersButton.addEventListener("click", async () => {
    if (groupPanel.hidden) {
        groupPanel.hidden = false;
        await renderPanelGrupo();
    } else {
        groupPanel.hidden = true;
    }
});

groupPanelList.addEventListener("click", async (evento) => {
    const boton = evento.target.closest("button[data-accion]");
    const grupoId = idDeGrupo(currentChatId);

    if (!boton || grupoId === null) return;

    if (boton.dataset.accion === "salir") {
        await quitarIntegrante(grupoId, sesionUsuario.Id);
    } else if (boton.dataset.accion === "coleccion") {
        await verColeccionDe(
            Number(boton.dataset.id),
            integrantesPanelGrupo.get(Number(boton.dataset.id)));
    } else if (boton.dataset.accion === "quitar") {
        await quitarIntegrante(grupoId, Number(boton.dataset.id));
    }
});

groupAddButton.addEventListener("click", async () => {
    const grupoId = idDeGrupo(currentChatId);
    const nuevos = [...groupAddContacts.querySelectorAll("input:checked")]
        .map(input => Number(input.value));

    if (grupoId === null) return;

    if (nuevos.length === 0) {
        groupPanelError.textContent = "Elige al menos un usuario.";
        return;
    }

    await agregarIntegrantes(grupoId, nuevos);
});

if (attachCardsButton && rewardPicker) {
    attachCardsButton.addEventListener("click", () => {
        rewardPicker.hidden = !rewardPicker.hidden;
        if (rewardPicker.hidden) return;

        if (rewardPicker.childElementCount === 0) {
            for (const carta of CARTAS) {
                const boton = document.createElement("button");
                boton.type = "button";
                boton.className = "reward-option";
                boton.title = carta.Nombre;
                boton.dataset.id = String(carta.Id);
                boton.innerHTML = `<img src="${carta.Imagen}" alt="${carta.Nombre}">`;
                boton.addEventListener("click", () => {
                    const yaElegida = recompensasSeleccionadas.includes(carta.Id);

                    if (yaElegida) {
                        recompensasSeleccionadas =
                            recompensasSeleccionadas.filter(id => id !== carta.Id);
                    } else if (recompensasSeleccionadas.length < 3) {
                        recompensasSeleccionadas.push(carta.Id);
                    }

                    renderSeleccionRecompensas();
                });
                rewardPicker.appendChild(boton);
            }
        }

        renderSeleccionRecompensas();
    });
}

if (saveRewardConfig) {
    saveRewardConfig.addEventListener("click", async () => {
        await guardarConfigRecompensas();
    });
}

if (visitRewardPage) {
    visitRewardPage.addEventListener("click", () => abrirPaginaRecompensa());
}

/* -------------------- Compartir ubicación -------------------- */

function actualizarBotonUbicacion(chatId = currentChatId) {
    if (!locationButton) {
        return;
    }

    const hayChat = !!chatId;
    const soportado = !!navigator.geolocation;
    const conectado = !!(connection && connection.state === "Connected");
    const soloLectura = hayChat && esSoloLectura(chatId);
    const cifrado = !!(chatId &&
        chatId.startsWith("dm:") &&
        cifradoActivo(chatId));

    let motivo = "";

    if (!hayChat) {
        motivo = "Abre una conversación para compartir tu ubicación.";
    } else if (!soportado) {
        motivo = "Este navegador no permite compartir la ubicación.";
    } else if (soloLectura) {
        motivo = "No puedes escribir en este chat.";
    } else if (!conectado) {
        motivo = "Sin conexión con el servidor.";
    } else if (cifrado) {
        motivo = "Desactiva el cifrado para compartir ubicación.";
    } else if (compartiendoUbicacion) {
        motivo = "Obteniendo tu ubicación...";
    }

    locationButton.disabled = motivo !== "";
    locationButton.title = motivo || "Compartir mi ubicación";
    locationButton.setAttribute(
        "aria-label",
        motivo || "Compartir mi ubicación"
    );

    actualizarBotonArchivo(chatId);
}

function obtenerUbicacion() {
    return new Promise((resolve, reject) => {
        if (!navigator.geolocation) {
            reject({ code: 2 });
            return;
        }

        let terminado = false;

        const finalizar = (accion, valor) => {
            if (terminado) {
                return;
            }
            terminado = true;
            clearTimeout(temporizador);
            accion(valor);
        };

        const temporizador = setTimeout(
            () => finalizar(reject, { code: 3 }),
            15000
        );

        navigator.geolocation.getCurrentPosition(
            posicion => finalizar(resolve, posicion),
            error => finalizar(reject, error),
            {
                enableHighAccuracy: true,
                timeout: 10000,
                maximumAge: 0
            }
        );
    });
}

function mensajeErrorUbicacion(error) {
    const codigo = error && typeof error.code === "number"
        ? error.code
        : 0;

    if (codigo === 1) {
        return "Permiso de ubicación denegado. Habilítalo en el navegador para compartir tu ubicación.";
    }
    if (codigo === 2) {
        return "Tu ubicación no está disponible en este momento.";
    }
    if (codigo === 3) {
        return "Se agotó el tiempo para obtener tu ubicación. Inténtalo de nuevo.";
    }
    return "No se pudo obtener tu ubicación.";
}

function enlacesUbicacion(latitud, longitud) {
    const lat = Number(latitud).toFixed(6);
    const lon = Number(longitud).toFixed(6);
    const delta = 0.004;
    const bbox = [
        (Number(longitud) - delta).toFixed(6),
        (Number(latitud) - delta).toFixed(6),
        (Number(longitud) + delta).toFixed(6),
        (Number(latitud) + delta).toFixed(6)
    ].join(",");

    return {
        embed: `https://www.openstreetmap.org/export/embed.html?bbox=${encodeURIComponent(bbox)}&layer=mapnik&marker=${encodeURIComponent(`${lat},${lon}`)}`,
        mapa: `https://www.openstreetmap.org/?mlat=${lat}&mlon=${lon}#map=16/${lat}/${lon}`
    };
}

function crearTarjetaUbicacion(latitud, longitud, etiqueta) {
    const urls = enlacesUbicacion(latitud, longitud);

    const tarjeta = document.createElement("div");
    tarjeta.className = "location-card";

    const label = document.createElement("span");
    label.className = "location-label";
    label.textContent = (etiqueta || "").trim() || "Mi ubicación";

    const mapa = document.createElement("iframe");
    mapa.className = "location-map";
    mapa.src = urls.embed;
    mapa.title = "Mapa de la ubicación compartida";
    mapa.loading = "lazy";
    mapa.setAttribute("referrerpolicy", "no-referrer");

    const enlace = document.createElement("a");
    enlace.className = "location-link";
    enlace.href = urls.mapa;
    enlace.target = "_blank";
    enlace.rel = "noopener noreferrer";
    enlace.textContent = "Abrir en OpenStreetMap";

    /* Atribución exigida por OpenStreetMap. */
    const atribucion = document.createElement("a");
    atribucion.className = "location-attribution";
    atribucion.href = "https://www.openstreetmap.org/copyright";
    atribucion.target = "_blank";
    atribucion.rel = "noopener noreferrer";
    atribucion.textContent = "© OpenStreetMap";

    tarjeta.appendChild(label);
    tarjeta.appendChild(mapa);
    tarjeta.appendChild(enlace);
    tarjeta.appendChild(atribucion);

    return tarjeta;
}

/* Crea la burbuja completa. Devuelve null si las coordenadas no son
   válidas; en ese caso no se pinta nada (nunca se muestran como texto). */
function crearMensajeUbicacion({
    esMio,
    usuario,
    latitud,
    longitud,
    etiqueta,
    hora,
    tempId = null
}) {
    const lat = Number(latitud);
    const lon = Number(longitud);

    if (!Number.isFinite(lat) || lat < -90 || lat > 90 ||
        !Number.isFinite(lon) || lon < -180 || lon > 180) {
        return null;
    }

    const mensaje = document.createElement("div");
    mensaje.className = esMio ? "message sent" : "message received";

    if (tempId) {
        mensaje.dataset.tempId = tempId;
    }

    if (!esMio) {
        const imagen = document.createElement("img");
        imagen.src = (currentChatImage && currentChatImage.src)
            ? currentChatImage.src
            : AVATAR_POR_DEFECTO;
        imagen.alt = usuario || "Usuario";
        mensaje.appendChild(imagen);
    }

    const contenido = document.createElement("div");
    contenido.className = "message-content";

    if (!esMio) {
        const nombre = document.createElement("strong");
        nombre.textContent = usuario || "Usuario";
        contenido.appendChild(nombre);
    }

    contenido.appendChild(crearTarjetaUbicacion(lat, lon, etiqueta));

    if (hora) {
        const tiempo = document.createElement("span");
        tiempo.className = "msg-time";
        tiempo.textContent = hora;
        contenido.appendChild(tiempo);
    }

    mensaje.appendChild(contenido);
    return mensaje;
}

async function compartirUbicacion() {
    const chatId = currentChatId;

    if (!chatId || compartiendoUbicacion) {
        return;
    }

    if (!connection || connection.state !== "Connected") {
        aliasError.textContent = "Sin conexión con el servidor.";
        actualizarBotonUbicacion();
        return;
    }

    if (esSoloLectura(chatId)) {
        aliasError.textContent = "No puedes escribir en este chat.";
        actualizarBotonUbicacion();
        return;
    }

    if (chatId.startsWith("dm:") && cifradoActivo(chatId)) {
        aliasError.textContent =
            "Desactiva el cifrado para compartir ubicación.";
        actualizarBotonUbicacion();
        return;
    }

    if (!navigator.geolocation) {
        aliasError.textContent =
            "Este navegador no permite compartir la ubicación.";
        actualizarBotonUbicacion();
        return;
    }

    compartiendoUbicacion = true;
    actualizarBotonUbicacion();
    aliasError.textContent = "Obteniendo tu ubicación...";

    try {
        const posicion = await obtenerUbicacion();

        /* Si el usuario cambió de chat mientras el navegador pedía la
           ubicación, no se envía a la conversación equivocada. */
        if (currentChatId !== chatId) {
            aliasError.textContent = "";
            return;
        }

        const latitud = posicion.coords.latitude;
        const longitud = posicion.coords.longitude;

        if (!Number.isFinite(latitud) || latitud < -90 || latitud > 90 ||
            !Number.isFinite(longitud) || longitud < -180 || longitud > 180) {
            throw new Error("La ubicación recibida no es válida.");
        }

        const etiqueta = "Mi ubicación";
        const hora = new Date().toLocaleTimeString("es-MX", {
            hour: "2-digit",
            minute: "2-digit"
        });
        const tempId = typeof crypto !== "undefined" && crypto.randomUUID
            ? crypto.randomUUID()
            : `temp-${Date.now()}-${Math.random()}`;

        agregarSeparadorFecha(new Date().toISOString());

        const tarjeta = crearMensajeUbicacion({
            esMio: true,
            latitud,
            longitud,
            etiqueta,
            hora,
            tempId
        });

        if (tarjeta && messagesContainer) {
            messagesContainer.appendChild(tarjeta);
            messagesContainer.scrollTop = messagesContainer.scrollHeight;
        }

        try {
            await connection.invoke(
                "SendToGroupUbicacion",
                chatId,
                latitud,
                longitud,
                etiqueta,
                tempId
            );
        } catch (error) {
            if (tarjeta) {
                tarjeta.remove();
            }

            const detalle = error && error.message ? error.message : "";
            const marca = detalle.lastIndexOf("HubException: ");
            aliasError.textContent = marca >= 0
                ? detalle.slice(marca + "HubException: ".length)
                : "No se pudo compartir la ubicación.";
            return;
        }

        aliasError.textContent = "";

        refrescarBarraLateral();
    } catch (error) {
        if (error && typeof error.code === "number") {
            aliasError.textContent = mensajeErrorUbicacion(error);
        } else {
            aliasError.textContent = error && error.message
                ? error.message
                : mensajeErrorUbicacion(error);
        }
    } finally {
        compartiendoUbicacion = false;
        actualizarBotonUbicacion();
    }
}

/* -------------------- Archivos adjuntos -------------------- */

function actualizarBotonArchivo(chatId = currentChatId) {
    if (!attachButton) {
        return;
    }

    const hayChat = !!chatId;
    const conectado = !!(connection && connection.state === "Connected");
    const soloLectura = hayChat && esSoloLectura(chatId);
    const cifrado = !!(chatId &&
        chatId.startsWith("dm:") &&
        cifradoActivo(chatId));

    let motivo = "";

    if (!hayChat) {
        motivo = "Abre una conversación para enviar archivos.";
    } else if (soloLectura) {
        motivo = "No puedes escribir en este chat.";
    } else if (!conectado) {
        motivo = "Sin conexión con el servidor.";
    } else if (cifrado) {
        motivo = "Desactiva el cifrado para enviar archivos.";
    } else if (subiendoArchivo) {
        motivo = "Subiendo archivo...";
    }

    attachButton.disabled = motivo !== "";
    attachButton.title = motivo ||
        "Adjuntar archivo (texto o imagen, máx. 2 MB)";
    attachButton.setAttribute(
        "aria-label",
        attachButton.title
    );
}

function formatearTamano(bytes) {
    const numero = Number(bytes);

    if (!Number.isFinite(numero) || numero <= 0) {
        return "";
    }

    if (numero < 1024) {
        return `${numero} B`;
    }

    if (numero < 1024 * 1024) {
        return `${(numero / 1024).toFixed(1)} KB`;
    }

    return `${(numero / (1024 * 1024)).toFixed(1)} MB`;
}

function validarArchivoCliente(archivo) {
    const maximo = 2 * 1024 * 1024;
    const nombre = (archivo.name || "").toLowerCase();
    const punto = nombre.lastIndexOf(".");
    const extension = punto >= 0 ? nombre.slice(punto + 1) : "";
    const texto = ["txt", "md", "csv", "json", "log"];
    const imagenes = ["jpg", "jpeg", "png", "webp", "gif"];

    if (archivo.size === 0) {
        return "El archivo está vacío.";
    }

    if (archivo.size > maximo) {
        return "El archivo supera el máximo de 2 MB.";
    }

    if (!texto.includes(extension) && !imagenes.includes(extension)) {
        return "Solo se permiten archivos de texto (.txt, .md, .csv, " +
            ".json, .log) o imágenes (.jpg, .png, .webp, .gif).";
    }

    return "";
}

function crearPieAdjunto(nombre, tamano) {
    const pie = document.createElement("div");
    pie.className = "attachment-caption";

    const nombreSpan = document.createElement("span");
    nombreSpan.className = "attachment-name";
    nombreSpan.textContent = nombre || "Archivo";

    const tamanoSpan = document.createElement("span");
    tamanoSpan.className = "attachment-meta";
    tamanoSpan.textContent = formatearTamano(tamano);

    pie.appendChild(nombreSpan);

    if (tamanoSpan.textContent) {
        pie.appendChild(tamanoSpan);
    }

    return pie;
}

function crearTarjetaTextoAdjunto({ id, nombre, tamano }) {
    const tarjeta = document.createElement("div");
    tarjeta.className = "attachment-card";

    const fila = document.createElement("div");
    fila.className = "attachment-file";

    const icono = document.createElement("span");
    icono.className = "attachment-icon";
    icono.innerHTML = `
        <svg viewBox="0 0 24 24" aria-hidden="true">
            <path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"
                  fill="none" stroke="currentColor" stroke-width="2"
                  stroke-linejoin="round"/>
            <path d="M14 2v6h6M8 13h8M8 17h8"
                  fill="none" stroke="currentColor" stroke-width="2"
                  stroke-linecap="round"/>
        </svg>
    `;

    const info = document.createElement("div");
    info.className = "attachment-info";

    const nombreSpan = document.createElement("span");
    nombreSpan.className = "attachment-name";
    nombreSpan.textContent = nombre || "Archivo";

    const tamanoSpan = document.createElement("span");
    tamanoSpan.className = "attachment-meta";
    tamanoSpan.textContent = formatearTamano(tamano);

    info.appendChild(nombreSpan);

    if (tamanoSpan.textContent) {
        info.appendChild(tamanoSpan);
    }

    fila.appendChild(icono);
    fila.appendChild(info);
    tarjeta.appendChild(fila);

    const vista = document.createElement("pre");
    vista.className = "attachment-text";
    vista.hidden = true;

    if (id) {
        const acciones = document.createElement("div");
        acciones.className = "attachment-actions";

        const ver = document.createElement("button");
        ver.type = "button";
        ver.textContent = "Ver";

        const descargar = document.createElement("button");
        descargar.type = "button";
        descargar.textContent = "Descargar";

        ver.addEventListener("click", () => {
            verTextoAdjunto(id, vista, ver);
        });
        descargar.addEventListener("click", () => {
            descargarAdjunto(id, nombre);
        });

        acciones.appendChild(ver);
        acciones.appendChild(descargar);
        tarjeta.appendChild(acciones);
    }

    tarjeta.appendChild(vista);
    return tarjeta;
}

function crearMensajeArchivo({
    esMio,
    usuario = "",
    id = null,
    url = "",
    nombre = "archivo",
    mime = "",
    tamano = 0,
    hora = "",
    tempId = null,
    subiendo = false
}) {
    const mensaje = document.createElement("div");
    mensaje.className = esMio ? "message sent" : "message received";

    if (tempId) {
        mensaje.dataset.tempId = tempId;
    }

    if (!esMio) {
        const avatar = document.createElement("img");
        avatar.src = (currentChatImage && currentChatImage.src)
            ? currentChatImage.src
            : AVATAR_POR_DEFECTO;
        avatar.alt = usuario || "Usuario";
        mensaje.appendChild(avatar);
    }

    const contenido = document.createElement("div");
    contenido.className = "message-content";

    if (!esMio) {
        const nombreUsuario = document.createElement("strong");
        nombreUsuario.textContent = usuario || "Usuario";
        contenido.appendChild(nombreUsuario);
    }

    const esImagen = (mime || "").toLowerCase().startsWith("image/");

    if (subiendo) {
        const tarjeta = document.createElement("div");
        tarjeta.className = "attachment-card attachment-loading";

        const nombreSpan = document.createElement("span");
        nombreSpan.className = "attachment-name";
        nombreSpan.textContent = nombre || "Archivo";

        const meta = document.createElement("span");
        meta.className = "attachment-meta";
        meta.textContent = "Subiendo...";

        tarjeta.appendChild(nombreSpan);
        tarjeta.appendChild(meta);
        contenido.appendChild(tarjeta);
    } else if (esImagen && url) {
        const enlace = document.createElement("a");
        enlace.className = "attachment-image-link";
        enlace.href = url;
        enlace.target = "_blank";
        enlace.rel = "noopener noreferrer";

        const imagen = document.createElement("img");
        imagen.className = "attachment-image";
        imagen.src = url;
        imagen.alt = nombre || "Imagen adjunta";
        imagen.loading = "lazy";
        imagen.referrerPolicy = "no-referrer";

        enlace.appendChild(imagen);
        contenido.appendChild(enlace);
        contenido.appendChild(crearPieAdjunto(nombre, tamano));
    } else if (url) {
        contenido.appendChild(crearTarjetaTextoAdjunto({
            id,
            nombre,
            tamano
        }));
    } else {
        const aviso = document.createElement("p");
        aviso.textContent = "Archivo no disponible.";
        contenido.appendChild(aviso);
    }

    if (hora) {
        const tiempo = document.createElement("span");
        tiempo.className = "msg-time";
        tiempo.textContent = hora;
        contenido.appendChild(tiempo);
    }

    mensaje.appendChild(contenido);
    return mensaje;
}

async function verTextoAdjunto(id, vista, boton) {
    if (!id) {
        return;
    }

    if (!vista.hidden) {
        vista.hidden = true;
        boton.textContent = "Ver";
        return;
    }

    boton.disabled = true;

    try {
        const respuesta = await fetch(
            `${SERVER_URL}/api/archivos/${encodeURIComponent(id)}/texto`,
            {
                headers: { "Authorization": `Bearer ${token}` }
            }
        );
        const datos = await respuesta.json().catch(() => ({}));

        if (!respuesta.ok) {
            throw new Error(datos.Error || "No se pudo leer el archivo.");
        }

        vista.textContent = datos.Texto || "";

        if (datos.Truncado) {
            vista.textContent += "\n\n[Vista previa truncada]";
        }

        vista.hidden = false;
        boton.textContent = "Ocultar";
    } catch (error) {
        aliasError.textContent = error && error.message
            ? error.message
            : "No se pudo leer el archivo.";
    } finally {
        boton.disabled = false;
    }
}

async function descargarAdjunto(id, nombre) {
    if (!id) {
        return;
    }

    try {
        const respuesta = await fetch(
            `${SERVER_URL}/api/archivos/${encodeURIComponent(id)}?descargar=1`,
            {
                headers: { "Authorization": `Bearer ${token}` }
            }
        );

        if (!respuesta.ok) {
            const datos = await respuesta.json().catch(() => ({}));
            throw new Error(
                datos.Error || "No se pudo descargar el archivo."
            );
        }

        const blob = await respuesta.blob();
        const url = URL.createObjectURL(blob);
        const enlace = document.createElement("a");
        enlace.href = url;
        enlace.download = nombre || "archivo";
        document.body.appendChild(enlace);
        enlace.click();
        enlace.remove();

        setTimeout(() => URL.revokeObjectURL(url), 10000);
    } catch (error) {
        aliasError.textContent = error && error.message
            ? error.message
            : "No se pudo descargar el archivo.";
    }
}

async function subirArchivo(archivo) {
    if (!archivo || !currentChatId) {
        return;
    }

    const chatId = currentChatId;

    if (!connection || connection.state !== "Connected") {
        aliasError.textContent = "Sin conexión con el servidor.";
        actualizarBotonArchivo();
        return;
    }

    if (esSoloLectura(chatId)) {
        aliasError.textContent = "No puedes escribir en este chat.";
        actualizarBotonArchivo();
        return;
    }

    if (chatId.startsWith("dm:") && cifradoActivo(chatId)) {
        aliasError.textContent =
            "Desactiva el cifrado para enviar archivos.";
        actualizarBotonArchivo();
        return;
    }

    const problema = validarArchivoCliente(archivo);

    if (problema) {
        aliasError.textContent = problema;
        return;
    }

    const tempId = typeof crypto !== "undefined" && crypto.randomUUID
        ? crypto.randomUUID()
        : `temp-${Date.now()}-${Math.random()}`;
    const hora = new Date().toLocaleTimeString("es-MX", {
        hour: "2-digit",
        minute: "2-digit"
    });

    agregarSeparadorFecha(new Date().toISOString());

    const pendiente = crearMensajeArchivo({
        esMio: true,
        nombre: archivo.name,
        mime: archivo.type,
        tamano: archivo.size,
        hora,
        tempId,
        subiendo: true
    });

    if (messagesContainer) {
        messagesContainer.appendChild(pendiente);
        messagesContainer.scrollTop = messagesContainer.scrollHeight;
    }

    subiendoArchivo = true;
    actualizarBotonArchivo();
    aliasError.textContent = "";

    const datos = new FormData();
    datos.append("chatId", chatId);
    datos.append("tempId", tempId);
    datos.append("archivo", archivo);

    try {
        const respuesta = await fetch(`${SERVER_URL}/api/archivos`, {
            method: "POST",
            headers: { "Authorization": `Bearer ${token}` },
            body: datos
        });
        const cuerpo = await respuesta.json().catch(() => ({}));

        if (!respuesta.ok) {
            throw new Error(
                cuerpo.Error || "No se pudo subir el archivo."
            );
        }

        if (adjuntosCompletados.has(tempId)) {
            return;
        }

        const final = crearMensajeArchivo({
            esMio: true,
            usuario: cuerpo.Usuario,
            id: cuerpo.Id,
            url: cuerpo.ArchivoUrl,
            nombre: cuerpo.ArchivoNombre,
            mime: cuerpo.ArchivoMime,
            tamano: cuerpo.ArchivoTamano,
            hora: cuerpo.Hora,
            tempId
        });

        const existente = messagesContainer
            ? messagesContainer.querySelector(
                `[data-temp-id="${tempId}"]`
            )
            : null;

        if (existente) {
            existente.replaceWith(final);
        } else if (messagesContainer && currentChatId === chatId) {
            messagesContainer.appendChild(final);
        }

        adjuntosCompletados.add(tempId);
        setTimeout(() => adjuntosCompletados.delete(tempId), 60000);

        if (messagesContainer && currentChatId === chatId) {
            messagesContainer.scrollTop = messagesContainer.scrollHeight;
        }

        refrescarBarraLateral();
    } catch (error) {
        const pendienteActual = messagesContainer
            ? messagesContainer.querySelector(
                `[data-temp-id="${tempId}"]`
            )
            : null;

        if (pendienteActual) {
            pendienteActual.remove();
        }

        aliasError.textContent = error && error.message
            ? error.message
            : "No se pudo subir el archivo.";
    } finally {
        subiendoArchivo = false;
        actualizarBotonArchivo();
    }
}

if (attachButton && fileInput) {
    attachButton.addEventListener("click", () => {
        if (attachButton.disabled) {
            return;
        }

        fileInput.value = "";
        fileInput.click();
    });

    fileInput.addEventListener("change", () => {
        const archivo = fileInput.files && fileInput.files[0];
        fileInput.value = "";

        if (archivo) {
            subirArchivo(archivo);
        }
    });
}

/* -------------------- Enviar mensaje (al canal actual) -------------------- */

if (locationButton) {
    locationButton.addEventListener("click", compartirUbicacion);
}

sendButton.addEventListener("click", sendMessage);

messageInput.addEventListener("keydown", event => {
    if (event.key === "Enter") {
        sendMessage();
    }
});

messageInput.addEventListener("input", () => {
    if (!currentChatId) return;
    const borradores = leerBorradores();
    const valor = messageInput.value;
    if (valor === "") {
        if (borradores[currentChatId] !== undefined) {
            delete borradores[currentChatId];
            guardarBorradores(borradores);
        }
        return;
    }
    borradores[currentChatId] = valor;
    guardarBorradores(borradores);
});

async function sendMessage() {
    const text = messageInput.value.trim();
    if (!text || !connection || !currentChatId || esSoloLectura(currentChatId)) return;

    const chatId = currentChatId;
    const tempId = typeof crypto !== "undefined" && crypto.randomUUID
        ? crypto.randomUUID()
        : `temp-${Date.now()}-${Math.random()}`;

    const cifrar = chatId.startsWith("dm:") &&
        cifradoListo &&
        cifradoActivo(chatId);

    let enviar = text;
    let iv = null;
    let clavePublica = null;
    let clavePublicaDestino = null;
    let fase = cifrar ? "cifrado" : "envio";

    try {
        if (cifrar) {
            const publica = await obtenerClavePublicaUsuario(
                otroUsuarioDeChat(chatId)
            );

            if (!publica) {
                throw new Error(
                    "El otro usuario todavía no publicó su clave de cifrado.");
            }

            const clave = await Cifrado.derivarClave(publica);
            const salida = await Cifrado.cifrar(text, clave);

            enviar = salida.texto;
            iv = salida.iv;
            clavePublica = await Cifrado.publicaBase64();
            clavePublicaDestino = publica;
        }

        fase = "envio";

        agregarSeparadorFecha(new Date().toISOString());
        renderSent(
            text,
            new Date().toLocaleTimeString("es-MX", {
                hour: "2-digit",
                minute: "2-digit"
            }),
            tempId,
            cifrar
        );
        mensajesPendientes.set(tempId, { chatId, texto: text });
        messageInput.value = "";
        limpiarBorrador(chatId);

        /* El hub guarda en SQLite (ligado al perfil autenticado)
           y solo después retransmite. Si el chat está cifrado, el
           servidor solo recibe el texto cifrado, el IV y las claves
           públicas del emisor y del destinatario. */
        if (cifrar) {
            await connection.invoke(
                "SendToGroupCifrado",
                chatId,
                enviar,
                tempId,
                iv,
                clavePublica,
                clavePublicaDestino
            );
        } else {
            await connection.invoke(
                "SendToGroup",
                chatId,
                enviar,
                tempId
            );
        }

        refrescarBarraLateral();

    } catch (error) {
        console.error("No se pudo enviar:", error);
        const pend = messagesContainer.querySelector(`[data-temp-id="${tempId}"]`);
        if (pend) {
            pend.remove();
        }
        mensajesPendientes.delete(tempId);

        if (fase === "cifrado" && error.message) {
            aliasError.textContent = error.message;
        } else {
            aliasError.textContent =
                "No se pudo enviar el mensaje. Revisa tu conexión.";
        }
    }
}

function renderSent(text, hora, tempId = null, cifrado = false, fallo = false) {
    if (!messagesContainer) return;
    const message = document.createElement("div");
    message.className = "message sent";
    if (tempId) {
        message.dataset.tempId = tempId;
    }
    message.innerHTML = `
        <div class="message-content">
            <p>${escapeHtml(text)}</p>
            ${cifrado ? candadoHtml(fallo) : ""}
            ${hora ? `<span class="msg-time">${escapeHtml(hora)}</span>` : ""}
        </div>
    `;
    messagesContainer.appendChild(message);
    messagesContainer.scrollTop = messagesContainer.scrollHeight;
    return message;
}

function renderIncoming(user, message, time, cifrado = false, fallo = false) {
    if (!currentChatId) return;
    if (!messagesContainer) return;

    /* Si el usuario está leyendo mensajes de arriba, no lo forzamos
       a bajar; solo auto-scrolleamos si ya estaba cerca del fondo. */
    const seguirFondo = estaCercaDelFondo();

    const container = document.createElement("div");
    container.className = "message received";
    container.innerHTML = `
        <img src="${currentChatImage.src || ""}" alt="${escapeHtml(user)}">
        <div class="message-content">
            <strong>${escapeHtml(user)}</strong>
            <p>${escapeHtml(message)}</p>
            ${cifrado ? candadoHtml(fallo) : ""}
            ${time ? `<span class="msg-time">${escapeHtml(time)}</span>` : ""}
        </div>
    `;
    messagesContainer.appendChild(container);

    if (seguirFondo) {
        messagesContainer.scrollTop = messagesContainer.scrollHeight;
    }
}

/* ¿La lista está (casi) abajo del todo? Se usa para decidir si un
   mensaje nuevo debe arrastrar el scroll o respetar la posición del
   usuario que está leyendo hacia arriba. */
function estaCercaDelFondo(margen = 60) {
    return messagesContainer.scrollHeight
        - messagesContainer.scrollTop
        - messagesContainer.clientHeight <= margen;
}

function escapeHtml(str) {
    const div = document.createElement("div");
    div.textContent = str;
    return div.innerHTML;
}

/* -------------------- TAREAS DEL GRUPO -------------------- */

const tasksPanel = document.getElementById("tasksPanel");
const tasksCount = document.getElementById("tasksCount");
const taskError = document.getElementById("taskError");
const tasksList = document.getElementById("tasksList");
const visitPageButton = document.getElementById("visitPageButton");

let tareasGrupoActual = [];
let grupoActualId = null;
let grupoActualCreadorId = null;
let grupoActualIntegrantes = [];
let grupoActualEnlace = "";

function mostrarErrorTarea(texto) {
    if (!taskError) return;
    taskError.textContent = texto || "";
    taskError.hidden = !texto;
}

function idDeGrupoDeChat(chatId) {
    if (!chatId || !chatId.startsWith("grupo:")) return null;
    const id = Number(chatId.split(":")[1]);
    return Number.isInteger(id) && id > 0 ? id : null;
}

async function cargarDetalleGrupoTareas(grupoId) {
    const respuesta = await fetch(`${SERVER_URL}/api/grupos/${grupoId}`, {
        headers: { "Authorization": `Bearer ${token}` }
    });
    const datos = await respuesta.json().catch(() => ({}));

    if (!respuesta.ok) {
        throw new Error(datos.Error || "No se pudo cargar el grupo.");
    }

    grupoActualId = datos.Id;
    grupoActualCreadorId = datos.CreadorId;
    grupoActualEnlace = datos.EnlaceRecompensa || "";
    grupoActualIntegrantes = Array.isArray(datos.Integrantes)
        ? datos.Integrantes
        : [];
}

function renderTareas() {
    if (!tasksList || !tasksCount) return;

    tasksList.innerHTML = "";

    if (!grupoActualEnlace) {
        tasksCount.textContent = "Retos desactivados";
        if (visitPageButton) visitPageButton.hidden = true;
        const vacio = document.createElement("p");
        vacio.className = "card-help";
        vacio.textContent =
            "Este grupo es solo chat. Agrega un enlace de recompensas para activar los retos.";
        tasksList.appendChild(vacio);
        return;
    }

    const mias = tareasGrupoActual.filter(
        tarea => Number(tarea.AsignadoId) === Number(sesionUsuario.Id));
    const pendientes = mias.filter(tarea => !tarea.Finalizada).length;
    tasksCount.textContent =
        `${pendientes} pendiente${pendientes === 1 ? "" : "s"} de ${mias.length || 3}`;

    if (visitPageButton) {
        visitPageButton.hidden = pendientes > 0;
    }

    if (tareasGrupoActual.length === 0) {
        const vacio = document.createElement("p");
        vacio.className = "card-help";
        vacio.textContent = "Este grupo aún no tiene retos.";
        tasksList.appendChild(vacio);
        return;
    }

    for (const integrante of grupoActualIntegrantes) {
        const propias = tareasGrupoActual.filter(
            tarea => Number(tarea.AsignadoId) === Number(integrante.Id));
        const yo = Number(integrante.Id) === Number(sesionUsuario.Id);

        const cabecera = document.createElement("div");
        cabecera.className = "task-group-head";
        cabecera.innerHTML =
            `<strong>${escapeHtml(integrante.Nombre || "Usuario")}${yo ? " (tú)" : ""}</strong>` +
            `<span>${propias.filter(t => t.Finalizada).length}/3</span>`;
        tasksList.appendChild(cabecera);

        propias.forEach((tarea, indice) => {
            const fila = document.createElement("div");
            fila.className = "task" + (tarea.Finalizada ? " completed" : "");

            const info = document.createElement("div");
            info.className = "task-info";
            const numero = document.createElement("span");
            numero.className = "task-number";
            numero.textContent = String(indice + 1);
            const texto = document.createElement("div");
            const titulo = document.createElement("strong");
            titulo.textContent = tarea.Titulo || "";
            const detalle = document.createElement("p");
            detalle.textContent = tarea.Descripcion || "Sin descripción";
            texto.appendChild(titulo);
            texto.appendChild(detalle);
            info.appendChild(numero);
            info.appendChild(texto);
            fila.appendChild(info);

            const estado = document.createElement("span");
            estado.className = "task-status";
            estado.textContent = tarea.Finalizada ? "✓ Listo" : "Pendiente";
            fila.appendChild(estado);

            if (!tarea.Finalizada && yo) {
                const boton = document.createElement("button");
                boton.type = "button";
                boton.className = "task-verify";
                boton.textContent = "Verificar";
                boton.addEventListener("click", () => verificarTarea(tarea));
                fila.appendChild(boton);
            }

            tasksList.appendChild(fila);
        });
    }
}

async function cargarTareas(grupoId) {
    if (!tasksPanel) return;

    if (!grupoId) {
        tasksPanel.hidden = true;
        tareasGrupoActual = [];
        grupoActualEnlace = "";
        return;
    }

    try {
        await cargarDetalleGrupoTareas(grupoId);
        const soyCreador =
            Number(grupoActualCreadorId) === Number(sesionUsuario.Id);

        if (!grupoActualEnlace) {
            tareasGrupoActual = [];
            mostrarErrorTarea("");
            renderTareas();
            tasksPanel.hidden = true;
            await cargarConfigRecompensas(grupoId, soyCreador);
            return;
        }

        const respuesta = await fetch(
            `${SERVER_URL}/api/grupos/${grupoId}/tareas`,
            { headers: { "Authorization": `Bearer ${token}` } });
        const datos = await respuesta.json().catch(() => ({}));

        if (!respuesta.ok) {
            throw new Error(datos.Error || "No se pudieron cargar las tareas.");
        }

        tareasGrupoActual = Array.isArray(datos.Tareas) ? datos.Tareas : [];
        mostrarErrorTarea("");
        renderTareas();
        tasksPanel.hidden = false;

        await cargarConfigRecompensas(grupoId, soyCreador);
    } catch (error) {
        tareasGrupoActual = [];
        grupoActualEnlace = "";
        renderTareas();
        tasksPanel.hidden = false;
        mostrarErrorTarea(error.message);
    }
}

function actualizarPanelTareas(chatId) {
    cargarTareas(idDeGrupoDeChat(chatId));
}

async function buscarEvidencia(tarea) {
    const respuesta = await fetch(
        `${SERVER_URL}/api/mensajes?chatId=${encodeURIComponent(`grupo:${grupoActualId}`)}&limite=50`,
        { headers: { "Authorization": `Bearer ${token}` } });
    const datos = await respuesta.json().catch(() => ({}));

    if (!respuesta.ok) {
        throw new Error(datos.Error || "No se pudo revisar el historial.");
    }

    const mensaje = (datos.Mensajes || []).find(m =>
        Number(m.UsuarioId) === Number(sesionUsuario.Id) &&
        m.Tipo === tarea.TipoVerificacion);

    return mensaje ? mensaje.Id : null;
}

async function verificarTarea(tarea) {
    if (!grupoActualId || !tarea) return;

    mostrarErrorTarea("");

    try {
        let evidencia = await buscarEvidencia(tarea);

        if (!evidencia) {
            const texto = window.prompt(
                `Pega el id del mensaje de tipo "${tarea.TipoVerificacion}" ` +
                "que enviaste a este grupo:");
            evidencia = texto ? Number(texto.trim()) : null;
        }

        if (!evidencia || !Number.isFinite(Number(evidencia))) {
            mostrarErrorTarea(
                "Envía el mensaje pedido en este grupo y vuelve a verificar.");
            return;
        }

        await finalizarTarea(tarea.Id, Number(evidencia));
    } catch (error) {
        mostrarErrorTarea(error.message);
    }
}

async function finalizarTarea(tareaId, evidenciaMensajeId) {
    if (!grupoActualId || !tareaId) return;

    try {
        const respuesta = await fetch(
            `${SERVER_URL}/api/grupos/${grupoActualId}/tareas/${tareaId}/finalizar`,
            {
                method: "POST",
                headers: {
                    "Content-Type": "application/json",
                    "Authorization": `Bearer ${token}`
                },
                body: JSON.stringify({
                    EvidenciaMensajeId: evidenciaMensajeId
                })
            });
        const datos = await respuesta.json().catch(() => ({}));

        if (!respuesta.ok) {
            throw new Error(datos.Error || "No se pudo verificar la tarea.");
        }

        await cargarTareas(grupoActualId);
        await cargarColeccion();

        const nuevas = datos.Recompensas || [];
        if (nuevas.length > 0) {
            aliasError.textContent =
                `¡Obtuviste ${nuevas.map(c => c.Nombre).join(" y ")}! ` +
                "Ya están en tu colección.";
        } else {
            aliasError.textContent = "Reto verificado.";
        }
    } catch (error) {
        mostrarErrorTarea(error.message);
    }
}

if (visitPageButton) {
    visitPageButton.addEventListener("click", () => abrirPaginaRecompensa());
}

/* -------------------- RECOMPENSAS -------------------- */

const pokemonDisplay = document.getElementById("pokemonDisplay");
const cartasProgreso = document.getElementById("cartasProgreso");
const cardModal = document.getElementById("cardModal");
const cardModalTitle = document.getElementById("cardModalTitle");
const cardCounter = document.getElementById("cardCounter");
const cardMeta = document.getElementById("cardMeta");
const cardIndicators = document.getElementById("cardIndicators");
const largeCard = document.getElementById("largeCard");
const closeCard = document.getElementById("closeCard");
const previousCard = document.getElementById("previousCard");
const nextCard = document.getElementById("nextCard");

let cartasColeccion = [];
let currentCard = 0;

function renderIndicadores(cantidad) {
    if (!cardIndicators) return;

    cardIndicators.innerHTML = "";

    for (let i = 0; i < cantidad; i++) {
        const indicador = document.createElement("span");
        indicador.className = "card-indicator";
        indicador.addEventListener("click", () => showCard(i));
        cardIndicators.appendChild(indicador);
    }
}

function renderGaleria() {
    if (!pokemonDisplay) return;

    pokemonDisplay.innerHTML = "";

    if (cartasColeccion.length === 0) {
        const vacio = document.createElement("p");
        vacio.className = "card-help";
        vacio.textContent = cardModalTitle && cardModalTitle.textContent.startsWith("Colección de")
            ? "Este usuario aún no tiene recompensas."
            : "Aún no tienes recompensas. Finaliza tareas de tu grupo para obtenerlas.";
        pokemonDisplay.appendChild(vacio);
        renderIndicadores(0);
        return;
    }

    cartasColeccion.forEach((carta, indice) => {
        const boton = document.createElement("button");
        boton.type = "button";
        boton.className = "pokemon-card";
        const imagen = document.createElement("img");
        imagen.src = carta.Imagen;
        imagen.alt = carta.Nombre || "Recompensa";
        boton.appendChild(imagen);
        const badge = document.createElement("span");
        badge.className = "pokemon-badge";
        badge.textContent = String(indice + 1).padStart(2, "0");
        boton.appendChild(badge);
        boton.addEventListener("click", () => {
            showCard(indice);
            abrirModalColeccion();
        });
        pokemonDisplay.appendChild(boton);
    });

    renderIndicadores(cartasColeccion.length);
    showCard(0);
}

function showCard(index) {
    if (cartasColeccion.length === 0) return;

    if (index < 0) index = cartasColeccion.length - 1;
    if (index >= cartasColeccion.length) index = 0;

    currentCard = index;
    const carta = cartasColeccion[currentCard];

    largeCard.src = carta.Imagen;
    largeCard.alt = carta.Nombre || "Recompensa";

    if (cardCounter) {
        cardCounter.textContent =
            `${currentCard + 1} / ${cartasColeccion.length}`;
    }

    if (cardMeta) {
        const origen = carta.Origen === "enlace"
            ? "Desbloqueada con el enlace"
            : (carta.Origen === "tareas" || carta.Origen === "tarea")
                ? "Obtenida con los retos"
                : "Recompensa";
        cardMeta.textContent =
            `${carta.Nombre} · ${origen}${carta.GrupoId ? ` · Grupo ${carta.GrupoId}` : ""}`;
    }

    [...cardIndicators.children].forEach((indicador, i) => {
        indicador.classList.toggle("active", i === currentCard);
    });
}

function abrirModalColeccion() {
    if (!cardModal) return;
    cardModal.classList.add("show");
    document.body.style.overflow = "hidden";
}

function closeCardModal() {
    if (!cardModal) return;
    cardModal.classList.remove("show");
    document.body.style.overflow = "";
}

function mostrarPerfilAjeno(usuario) {
    const nombre = (usuario && (usuario.Nombre || usuario.nombre)) || "Usuario";
    const handle = String(
        (usuario && (usuario.Usuario || usuario.usuario)) || "usuario"
    ).replace(/^@/, "");

    if (profileTitle) profileTitle.textContent = `Perfil de @${handle}`;
    if (profileName) profileName.textContent = nombre;
    if (profileUsername) profileUsername.textContent = `@${handle}`;
    if (profileEmail) {
        profileEmail.textContent = "";
        profileEmail.hidden = true;
    }
    if (enviarCorreoButton) enviarCorreoButton.hidden = true;
    if (changeProfileLink) changeProfileLink.hidden = true;
    if (coleccionTitle) coleccionTitle.textContent = `Colección de @${handle}`;
    if (volverPerfilButton) volverPerfilButton.hidden = false;
    if (profilePicture) profilePicture.src = AVATAR_POR_DEFECTO;
    if (profileBanner) {
        profileBanner.style.backgroundImage = "";
        profileBanner.style.backgroundSize = "";
        profileBanner.style.backgroundPosition = "";
    }
}

function restaurarPerfilPropio() {
    if (profileTitle) profileTitle.textContent = "Mi perfil";
    if (profileName) profileName.textContent = perfilPropio.Nombre || "Usuario";
    if (profileUsername) profileUsername.textContent = perfilPropio.Usuario || "";
    if (profileEmail) {
        profileEmail.textContent = perfilPropio.Correo || "";
        profileEmail.hidden = false;
    }
    if (enviarCorreoButton) enviarCorreoButton.hidden = false;
    if (changeProfileLink) changeProfileLink.hidden = false;
    if (coleccionTitle) coleccionTitle.textContent = "Mi colección";
    if (volverPerfilButton) volverPerfilButton.hidden = true;
    if (profilePicture) profilePicture.src = perfilPropio.Foto || AVATAR_POR_DEFECTO;
    if (profileBanner) {
        profileBanner.style.backgroundImage = perfilPropio.BannerImagen;
        profileBanner.style.backgroundSize = perfilPropio.BannerTamano;
        profileBanner.style.backgroundPosition = perfilPropio.BannerPosicion;
    }
}

async function cargarColeccion(
    usuarioId = sesionUsuario.Id,
    esOtro = false,
    dueno = null) {
    const respuesta = await fetch(
        `${SERVER_URL}/api/usuarios/${usuarioId}/recompensas`,
        { headers: { "Authorization": `Bearer ${token}` } });
    const datos = await respuesta.json().catch(() => ({}));

    if (!respuesta.ok) {
        throw new Error(datos.Error || "No se pudo cargar la colección.");
    }

    const handle = String(
        (dueno && (dueno.Usuario || dueno.usuario)) ||
        datos.Usuario ||
        "usuario"
    ).replace(/^@/, "");

    if (esOtro) {
        mostrarPerfilAjeno({
            Nombre: (dueno && (dueno.Nombre || dueno.nombre)) || datos.Nombre,
            Usuario: handle
        });
    } else {
        restaurarPerfilPropio();
    }

    cartasColeccion = Array.isArray(datos.Recompensas) ? datos.Recompensas : [];
    cardModalTitle.textContent = esOtro
        ? `Colección de @${handle}`
        : "Mi colección";
    const unicas = new Set(cartasColeccion.map(carta => carta.Id)).size;
    cartasProgreso.textContent =
        `${cartasColeccion.length} cartas · ${unicas} de ${CARTAS.length} únicas`;
    renderGaleria();
}

async function verColeccionDe(usuarioId, dueno) {
    if (!usuarioId) return;

    try {
        await cargarColeccion(
            usuarioId,
            Number(usuarioId) !== Number(sesionUsuario.Id),
            dueno);
        abrirModalColeccion();
    } catch (error) {
        groupPanelError.textContent = "No se pudo cargar la colección.";
    }
}

if (volverPerfilButton) {
    volverPerfilButton.addEventListener("click", async () => {
        restaurarPerfilPropio();
        closeCardModal();

        try {
            await cargarColeccion();
        } catch (error) {
            cartasColeccion = [];
            cartasProgreso.textContent =
                `0 cartas · 0 de ${CARTAS.length} únicas`;
            renderGaleria();
        }
    });
}

if (closeCard) {
    closeCard.addEventListener("click", closeCardModal);
}

if (cardModal) {
    cardModal.addEventListener("click", event => {
        if (event.target === cardModal) {
            closeCardModal();
        }
    });
}

if (previousCard) {
    previousCard.addEventListener("click", () => showCard(currentCard - 1));
}

if (nextCard) {
    nextCard.addEventListener("click", () => showCard(currentCard + 1));
}

document.addEventListener("keydown", event => {
    if (!cardModal || !cardModal.classList.contains("show")) return;

    if (event.key === "Escape") {
        closeCardModal();
    } else if (event.key === "ArrowLeft") {
        showCard(currentCard - 1);
    } else if (event.key === "ArrowRight") {
        showCard(currentCard + 1);
    }
});

cargarColeccion().catch(() => {
    cartasColeccion = [];
    cartasProgreso.textContent =
        `0 cartas · 0 de ${CARTAS.length} únicas`;
    renderGaleria();
});

/* -------------------- VIDEOLLAMADA (1 a 1 en chats privados) -------------------- */

let llamadaEntranteId = null;
let avisoLlamadaTimer = null;

/* SignalR antepone un texto técnico ("An unexpected error occurred invoking
   ... HubException: "). Al usuario solo le interesa la frase final. */
function mensajeDeLlamada(error) {
    const texto = (error && error.message) ? error.message : "";
    const marca = texto.lastIndexOf("HubException: ");

    if (marca >= 0) {
        return texto.slice(marca + "HubException: ".length);
    }

    return texto || "No se pudo completar la videollamada.";
}

/* El botón solo tiene sentido en una conversación privada ("dm:..."). */
function actualizarBotonVideollamada(chatId) {
    videoCallButton.hidden = !(chatId && chatId.startsWith("dm:"));
}

videoCallButton.addEventListener("click", async () => {
    if (!currentChatId || !currentChatId.startsWith("dm:") || !connection) {
        return;
    }

    videoCallButton.disabled = true;

    try {
        const llamadaId = await connection.invoke("IniciarLlamada", currentChatId);
        window.location.href = `Videollamada.html?llamada=${encodeURIComponent(llamadaId)}`;
    } catch (error) {
        aliasError.textContent = mensajeDeLlamada(error);
        videoCallButton.disabled = false;
    }
});

function actualizarBotonCorreoChat(chatId) {
    if (!chatEmailButton) {
        return;
    }

    chatEmailButton.hidden = !(chatId && chatId.startsWith("dm:"));
}

if (chatEmailButton) {
    chatEmailButton.addEventListener("click", () => {
        if (!currentChatId || !currentChatId.startsWith("dm:")) {
            return;
        }

        const otroId = otroUsuarioDeChat(currentChatId);
        if (!otroId) {
            return;
        }

        abrirModalCorreo({
            Id: otroId,
            Nombre: currentChatName.textContent
        });
    });
}

function mostrarLlamadaEntrante(llamadaId, nombre) {
    llamadaEntranteId = llamadaId;
    incomingCallName.textContent = nombre || "Usuario";
    incomingCall.hidden = false;

    clearTimeout(avisoLlamadaTimer);
    /* El servidor cierra la llamada a los 30 s sin respuesta. */
    avisoLlamadaTimer = setTimeout(ocultarLlamadaEntrante, 32000);
}

function ocultarLlamadaEntrante() {
    clearTimeout(avisoLlamadaTimer);
    avisoLlamadaTimer = null;
    llamadaEntranteId = null;
    incomingCall.hidden = true;
}

acceptCallButton.addEventListener("click", async () => {
    const llamadaId = llamadaEntranteId;
    if (!llamadaId || !connection) return;

    acceptCallButton.disabled = true;

    try {
        await connection.invoke("ContestarLlamada", llamadaId);
        ocultarLlamadaEntrante();
        window.location.href = `Videollamada.html?llamada=${encodeURIComponent(llamadaId)}`;
    } catch (error) {
        aliasError.textContent = mensajeDeLlamada(error);
        ocultarLlamadaEntrante();
    } finally {
        acceptCallButton.disabled = false;
    }
});

rejectCallButton.addEventListener("click", async () => {
    const llamadaId = llamadaEntranteId;
    ocultarLlamadaEntrante();

    if (!llamadaId || !connection) return;

    try {
        await connection.invoke("RechazarLlamada", llamadaId);
    } catch (error) {
        console.error("No se pudo rechazar la llamada:", error);
    }
});
