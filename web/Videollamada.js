const CLAVE_SESION = "musicHubUsuario";

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

if (!sesionUsuario) {
    window.location.href = "IniciarSesion.html";
}

const selfName = document.getElementById("selfName");

if (selfName && sesionUsuario) {
    selfName.textContent =
        sesionUsuario.Nombre || sesionUsuario.Usuario || "Tú";
}

const toggleMic = document.getElementById("toggleMic");
const toggleCamera = document.getElementById("toggleCamera");
const toggleScreen = document.getElementById("toggleScreen");
const endCall = document.getElementById("endCall");

const remoteName = document.getElementById("remoteName");
const remoteState = document.getElementById("remoteState");

toggleMic.addEventListener("click", () => {
    toggleMic.classList.toggle("off");
});

toggleCamera.addEventListener("click", () => {
    toggleCamera.classList.toggle("off");
});

toggleScreen.addEventListener("click", () => {
    toggleScreen.classList.toggle("active");
});

endCall.addEventListener("click", () => {
    window.location.href = "Home.html";
});

const participantes = document.querySelectorAll(".participant");

participantes.forEach((boton) => {
    boton.addEventListener("click", () => {
        participantes.forEach((otro) => {
            otro.classList.remove("active");
        });

        boton.classList.add("active");
        remoteName.textContent = boton.dataset.name;
        remoteState.textContent = "Conectado";
    });
});

const tabs = document.querySelectorAll(".side-tab");
const panels = {
    participantes: document.getElementById("panelParticipantes"),
    chat: document.getElementById("panelChat")
};

tabs.forEach((tab) => {
    tab.addEventListener("click", () => {
        tabs.forEach((otra) => {
            otra.classList.remove("active");
        });

        tab.classList.add("active");

        Object.values(panels).forEach((panel) => {
            panel.classList.remove("active");
        });

        panels[tab.dataset.tab].classList.add("active");
    });
});

const callChatInput = document.getElementById("callChatInput");
const callChatSend = document.getElementById("callChatSend");
const callChat = document.querySelector(".call-chat");

function enviarMensajeLlamada() {
    const texto = callChatInput.value.trim();

    if (!texto) {
        return;
    }

    const mensaje = document.createElement("div");
    mensaje.className = "call-message own";

    const autor = document.createElement("strong");
    autor.textContent = "Tú";

    const cuerpo = document.createElement("p");
    cuerpo.textContent = texto;

    mensaje.appendChild(autor);
    mensaje.appendChild(cuerpo);
    callChat.appendChild(mensaje);
    callChat.scrollTop = callChat.scrollHeight;
    callChatInput.value = "";
}

callChatSend.addEventListener("click", enviarMensajeLlamada);

callChatInput.addEventListener("keydown", (event) => {
    if (event.key === "Enter") {
        enviarMensajeLlamada();
    }
});
