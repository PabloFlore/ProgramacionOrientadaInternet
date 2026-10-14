/* ============================================================
   Music Hub — Home (Home.html)
   Chat en tiempo real con SignalR (canales por chat de la barra)
   Requiere: libs/signalr.min.js cargado antes que este script
   ============================================================ */

/* -------------------- URL del servidor -------------------- */

const SERVER_URL = `http://${window.location.hostname || "localhost"}:5000`;

/* -------------------- Sesión -------------------- */

const CLAVE_SESION = "musicHubUsuario";

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

const chats = {
    amigos: {
        name: "Arinator",
        status: "2 integrantes",
        image: "https://i.pinimg.com/736x/e8/11/c3/e811c31c34cd0789d5f35939a065802c.jpg"
    },
    gaming: {
        name: "Twenty One Pilots se robo mi radio",
        status: "6 integrantes",
        image: "https://i.pinimg.com/1200x/0b/de/25/0bde25514b531ada811674223955fc04.jpg"
    },
    universidad: {
        name: "Nedsito",
        status: "2 integrantes",
        image: "https://i.pinimg.com/736x/01/39/d9/0139d91c48aeaf302e28121a46093b34.jpg"
    },
    proyecto: {
        name: "Aquí amamos a Josh Dun",
        status: "5 integrantes",
        image: "https://i.pinimg.com/1200x/6e/44/81/6e44814a20c65645359c3731e49bbf43.jpg"
    }
};

/* -------------------- Elementos del DOM -------------------- */

const chatButtons = document.querySelectorAll(".chat-icon");
const currentChatName = document.getElementById("currentChatName");
const currentChatStatus = document.getElementById("currentChatStatus");
const currentChatImage = document.getElementById("currentChatImage");
const messagesContainer = document.getElementById("messages");
const messageInput = document.getElementById("messageInput");
const sendButton = document.getElementById("sendButton");

const sesionLabel = document.getElementById("sesionUsuario");
const logoutButton = document.getElementById("logoutButton");
const aliasError = document.getElementById("aliasError");

/* Mensajes privados */
const privateList = document.getElementById("privateList");
const contactsList = document.getElementById("contactsList");
const newDmButton = document.getElementById("newDmButton");

const AVATAR_POR_DEFECTO =
    "https://i.pinimg.com/736x/e8/11/c3/e811c31c34cd0789d5f35939a065802c.jpg";

/* Metadata de los chats privados abiertos (chatId -> {name,status,image}) */
const chatsPrivados = {};

function chatPrivadoId(a, b) {
    const menor = Math.min(a, b);
    const mayor = Math.max(a, b);
    return `dm:${menor}:${mayor}`;
}

function metaDeChat(chatId) {
    return chats[chatId] || chatsPrivados[chatId] || {
        name: "Conversación",
        status: "",
        image: AVATAR_POR_DEFECTO
    };
}

function marcarActivo(elemento) {
    document
        .querySelectorAll(".chat-icon, .private-item")
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

let currentChatId = "amigos";
let connection = null;

if (sesionLabel) {
    sesionLabel.textContent = alias;
}

/* El panel lateral ya no lleva un nombre fijo: se llena con la
   cuenta que inició sesión. */
const profileName = document.getElementById("profileName");
const profileUsername = document.getElementById("profileUsername");

if (profileName && sesionUsuario) {
    profileName.textContent = alias || "Usuario";
}

if (profileUsername && sesionUsuario) {
    profileUsername.textContent = `@${sesionUsuario.Usuario || ""}`;
}

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

async function cargarHistorial(chatId) {
    messagesContainer.innerHTML = "";

    try {
        const respuesta = await fetch(
            `${SERVER_URL}/api/mensajes?chatId=${encodeURIComponent(chatId)}&limite=100`,
            {
                headers: { "Authorization": `Bearer ${token}` }
            }
        );

        if (!respuesta.ok) {
            return;
        }

        const datos = await respuesta.json();

        for (const mensaje of datos.Mensajes) {
            if (mensaje.Usuario === alias) {
                renderSent(mensaje.Texto, mensaje.Hora);
            } else {
                renderIncoming(
                    mensaje.Usuario,
                    mensaje.Texto,
                    mensaje.Hora
                );
            }
        }

        messagesContainer.scrollTop =
            messagesContainer.scrollHeight;

    } catch (error) {
        console.error("No se pudo cargar el historial:", error);
    }
}

/* -------------------- Conexión SignalR -------------------- */

/* Ya no hay botón "Conectar": se conecta solo al abrir la página. */
conectar();

async function conectar() {
    if (!sesionUsuario) {
        return;
    }

    aliasError.textContent = "Conectando...";

    try {
        const hub = new signalR.HubConnectionBuilder()
            .withUrl(`${SERVER_URL}/chat`, {
                accessTokenFactory: () => token
            })
            .build();

        hub.on("ReceiveMessage", (user, message, time, chatId) => {
            /* Filtro por canal: si el mensaje es de otro chat se
               descarta. Evita que caiga en el chat equivocado
               durante el cambio de canal. */
            if (chatId && chatId !== currentChatId) {
                if (chatId.startsWith("dm:")) {
                    cargarConversaciones();
                }
                return;
            }

            renderIncoming(user, message, time);
        });

        hub.onclose(() => {
            aliasError.textContent =
                "Se perdió la conexión con el servidor.";
        });

        await hub.start();

        connection = hub;

        /* Unirse al canal del primer chat y traer su historial */
        await connection.invoke("JoinGroup", currentChatId);
        await cargarHistorial(currentChatId);
        await cargarConversaciones();

        messageInput.disabled = false;
        sendButton.disabled = false;
        messageInput.focus();

        aliasError.textContent = "";

    } catch (error) {
        aliasError.textContent =
            `No se pudo conectar a ${SERVER_URL}. ` +
            "¿El servidor está corriendo?";
    }
}

/* -------------------- Cambio de chat (canal o privado) -------------------- */

async function abrirChat(chatId, meta) {
    if (meta) {
        chatsPrivados[chatId] = meta;
    }

    const info = metaDeChat(chatId);

    currentChatName.textContent = info.name;
    currentChatStatus.textContent = info.status;
    currentChatImage.src = info.image;

    if (connection && chatId !== currentChatId) {
        try {
            await connection.invoke("LeaveGroup", currentChatId);
            await connection.invoke("JoinGroup", chatId);
        } catch (error) {
            console.error("Error al cambiar de chat:", error);
        }
    }

    currentChatId = chatId;

    await cargarHistorial(chatId);
}

chatButtons.forEach(button => {
    button.addEventListener("click", () => {
        const chatId = button.dataset.chat;
        if (!chats[chatId]) return;

        marcarActivo(button);
        abrirChat(chatId, chats[chatId]);
    });
});

/* -------------------- Mensajes privados -------------------- */

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

        privateList.innerHTML = "";

        for (const conversacion of datos.Conversaciones) {
            chatsPrivados[conversacion.ChatId] = {
                name: conversacion.OtroNombre || "Usuario",
                status: "Conversación privada",
                image: AVATAR_POR_DEFECTO
            };

            const boton = document.createElement("button");
            boton.type = "button";
            boton.className = "private-item";
            boton.dataset.chat = conversacion.ChatId;
            boton.innerHTML = `
                <span class="private-name">${escapeHtml(conversacion.OtroNombre || "Usuario")}</span>
                <span class="private-preview">${escapeHtml(conversacion.UltimoMensaje || "")}</span>
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

/* -------------------- Enviar mensaje (al canal actual) -------------------- */

sendButton.addEventListener("click", sendMessage);

messageInput.addEventListener("keydown", event => {
    if (event.key === "Enter") {
        sendMessage();
    }
});

async function sendMessage() {
    const text = messageInput.value.trim();
    if (!text || !connection) return;

    try {
        /* El hub guarda en SQLite (ligado al perfil autenticado)
           y solo después retransmite. */
        await connection.invoke(
            "SendToGroup",
            currentChatId,
            text
        );

        renderSent(text);
        messageInput.value = "";

        if (currentChatId.startsWith("dm:")) {
            cargarConversaciones();
        }

    } catch (error) {
        console.error("No se pudo enviar:", error);
        aliasError.textContent =
            "No se pudo enviar el mensaje. Revisa tu conexión.";
    }
}

function renderSent(text, hora) {
    const message = document.createElement("div");
    message.className = "message sent";
    message.innerHTML = `
        <div class="message-content">
            <p>${escapeHtml(text)}</p>
            ${hora ? `<span class="msg-time">${escapeHtml(hora)}</span>` : ""}
        </div>
    `;
    messagesContainer.appendChild(message);
    messagesContainer.scrollTop = messagesContainer.scrollHeight;
}

function renderIncoming(user, message, time) {
    if (!currentChatId) return;
    if (user === alias) return;

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

/* -------------------- TAREAS (localStorage) -------------------- */

const taskLinks = document.querySelectorAll(".task-link");

taskLinks.forEach(link => {
    link.addEventListener("click", () => {
        const taskId = link.dataset.task;
        const task = document.getElementById(taskId);
        if (!task) return;
        const status = task.querySelector(".task-status");
        localStorage.setItem(taskId, "visitado");
        task.classList.add("completed");
        if (status) {
            status.textContent = "✓ Completada";
        }
    });
});

/* Revisar si el usuario ya entró al link */
taskLinks.forEach(link => {
    const taskId = link.dataset.task;
    const task = document.getElementById(taskId);
    if (!task) return;
    const status = task.querySelector(".task-status");
    if (localStorage.getItem(taskId) === "visitado") {
        task.classList.add("completed");
        if (status) {
            status.textContent = "✓ Completada";
        }
    }
});

/* -------------------- CARRUSEL DE CARTAS -------------------- */

const pokemonCards = [
    "https://i.pinimg.com/736x/48/62/47/486247923ba212d2f45d47ff1b81f69d.jpg",
    "https://i.pinimg.com/736x/78/41/23/784123912a27a92f01d8597d1d32b069.jpg",
    "https://i.pinimg.com/736x/a4/6e/fc/a46efc5422e919ce3c8176ab3a543785.jpg"
];

let currentCard = 0;

const cardModal = document.getElementById("cardModal");
const largeCard = document.getElementById("largeCard");
const closeCard = document.getElementById("closeCard");
const previousCard = document.getElementById("previousCard");
const nextCard = document.getElementById("nextCard");
const indicators = document.querySelectorAll(".card-indicator");

function showCard(index) {
    if (index < 0) index = pokemonCards.length - 1;
    if (index >= pokemonCards.length) index = 0;

    currentCard = index;
    largeCard.src = pokemonCards[currentCard];

    indicators.forEach(indicator => {
        indicator.classList.remove("active");
    });
    indicators[currentCard].classList.add("active");
}

pokemonCards.forEach((card, index) => {
    const button = document.querySelector(`.pokemon-card[data-card="${index}"]`);
    if (!button) return;

    button.addEventListener("click", () => {
        showCard(index);
        cardModal.classList.add("show");
        document.body.style.overflow = "hidden";
    });
});

function closeCardModal() {
    cardModal.classList.remove("show");
    document.body.style.overflow = "";
}

closeCard.addEventListener("click", closeCardModal);

cardModal.addEventListener("click", event => {
    if (event.target === cardModal) {
        closeCardModal();
    }
});

previousCard.addEventListener("click", () => showCard(currentCard - 1));
nextCard.addEventListener("click", () => showCard(currentCard + 1));

indicators.forEach(indicator => {
    indicator.addEventListener("click", () => {
        showCard(Number(indicator.dataset.indicator));
    });
});

document.addEventListener("keydown", event => {
    if (event.key === "Escape" && cardModal.classList.contains("show")) {
        closeCardModal();
        return;
    }
    if (event.key === "ArrowLeft" && cardModal.classList.contains("show")) {
        showCard(currentCard - 1);
    }
    if (event.key === "ArrowRight" && cardModal.classList.contains("show")) {
        showCard(currentCard + 1);
    }
});
