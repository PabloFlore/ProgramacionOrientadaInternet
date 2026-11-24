/* ============================================================
   Music Hub — Videollamada 1 a 1
   WebRTC (cámara + micrófono) con señalización por SignalR.
   La llamada se crea desde Home (chat privado) y aquí solo se
   une: ?llamada=<id>. No usa servidores STUN/TURN (LAN).
   ============================================================ */

const CLAVE_SESION = "musicHubUsuario";
const SERVER_URL = `https://${window.location.hostname || "localhost"}:5000`;

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

const token = sesionUsuario ? (sesionUsuario.Token || "") : "";

/* -------------------- Estado -------------------- */

const parametros = new URLSearchParams(window.location.search);
const llamadaId = parametros.get("llamada");

const callStatus = document.getElementById("callStatus");
const liveDot = document.getElementById("liveDot");
const remoteName = document.getElementById("remoteName");
const remoteState = document.getElementById("remoteState");
const remoteVideo = document.getElementById("remoteVideo");
const remotePlaceholder = document.getElementById("remotePlaceholder");
const selfVideo = document.getElementById("selfVideo");
const toggleMic = document.getElementById("toggleMic");
const toggleCamera = document.getElementById("toggleCamera");
const endCall = document.getElementById("endCall");
const callEnded = document.getElementById("callEnded");
const callEndedText = document.getElementById("callEndedText");

let connection = null;
let pc = null;
let streamLocal = null;
let rol = null;
let ofertaCreada = false;
let ofertaRecibida = false;
let candidatosPendientes = [];
let terminada = false;

/* -------------------- Interfaz -------------------- */

function mostrarEstado(texto) {
    callStatus.textContent = texto;
}

/* SignalR antepone un texto técnico ("... HubException: "); se quita para
   que el aviso se entienda. */
function mensajeDeError(error) {
    const texto = (error && error.message) ? error.message : "";
    const marca = texto.lastIndexOf("HubException: ");

    if (marca >= 0) {
        return texto.slice(marca + "HubException: ".length);
    }

    return texto;
}

function mensajeDeMotivo(motivo) {
    switch (motivo) {
        case "rechazada":
            return "La otra persona rechazó la videollamada.";
        case "sin respuesta":
            return "La otra persona no contestó.";
        case "colgada":
            return "La otra persona colgó.";
        case "desconectado":
            return "Se perdió la conexión con la otra persona.";
        default:
            return "La videollamada terminó.";
    }
}

function detenerTodo() {
    if (pc) {
        pc.onicecandidate = null;
        pc.ontrack = null;
        pc.onconnectionstatechange = null;
        pc.close();
        pc = null;
    }

    if (streamLocal) {
        streamLocal.getTracks().forEach(track => track.stop());
        streamLocal = null;
    }

    remoteVideo.srcObject = null;
    selfVideo.srcObject = null;

    if (connection) {
        connection.stop();
        connection = null;
    }
}

function terminar(mensaje) {
    if (terminada) return;

    terminada = true;
    callEndedText.textContent = mensaje || "La llamada terminó.";
    callEnded.hidden = false;
    mostrarEstado("Llamada finalizada");
    detenerTodo();
}

/* -------------------- Señalización -------------------- */

function enviarSenal(tipo, datos) {
    if (!connection || connection.state !== "Connected") {
        return;
    }

    connection
        .invoke("EnviarSenalLlamada", llamadaId, tipo, JSON.stringify(datos))
        .catch(error => console.error("No se pudo enviar la señal:", error));
}

function aplicarCandidatosPendientes() {
    const pendientes = candidatosPendientes;
    candidatosPendientes = [];

    return Promise.all(
        pendientes.map(candidato =>
            pc.addIceCandidate(candidato).catch(error =>
                console.error("Candidato ICE rechazado:", error)
            )
        )
    );
}

async function crearOferta() {
    if (ofertaCreada || !pc) return;

    ofertaCreada = true;
    const oferta = await pc.createOffer();
    await pc.setLocalDescription(oferta);
    enviarSenal("oferta", pc.localDescription.toJSON());
}

async function manejarSenal(id, tipo, datos) {
    if (id !== llamadaId || terminada || !pc) return;

    let valor;
    try {
        valor = JSON.parse(datos || "{}");
    } catch (error) {
        return;
    }

    try {
        if (tipo === "listo") {
            if (rol === "llamante") {
                await crearOferta();
            } else if (!ofertaRecibida) {
                /* Si el llamante envió su "listo" antes de que esta página
                   entrara al grupo, su aviso se perdió: se repite el propio
                   para que vuelva a ofertar. */
                enviarSenal("listo", {});
            }
            return;
        }

        if (tipo === "oferta") {
            ofertaRecibida = true;
            await pc.setRemoteDescription(valor);
            await aplicarCandidatosPendientes();

            const respuesta = await pc.createAnswer();
            await pc.setLocalDescription(respuesta);
            enviarSenal("respuesta", pc.localDescription.toJSON());
            return;
        }

        if (tipo === "respuesta") {
            await pc.setRemoteDescription(valor);
            await aplicarCandidatosPendientes();
            mostrarEstado("En llamada");
            remoteState.textContent = "Conectado";
            return;
        }

        if (tipo === "candidato") {
            if (pc.remoteDescription) {
                await pc.addIceCandidate(valor);
            } else {
                candidatosPendientes.push(valor);
            }
        }
    } catch (error) {
        console.error(`Error con la señal "${tipo}":`, error);
    }
}

/* -------------------- WebRTC -------------------- */

function crearConexion() {
    /* En la misma red no hace falta STUN/TURN: se usan los candidatos
       locales del host. */
    pc = new RTCPeerConnection({ iceServers: [] });

    streamLocal.getTracks().forEach(track => {
        pc.addTrack(track, streamLocal);
    });

    pc.onicecandidate = event => {
        if (event.candidate) {
            enviarSenal("candidato", event.candidate.toJSON());
        }
    };

    pc.ontrack = event => {
        if (remoteVideo.srcObject !== event.streams[0]) {
            remoteVideo.srcObject = event.streams[0];
            remotePlaceholder.hidden = true;
        }
    };

    pc.onconnectionstatechange = () => {
        if (!pc || terminada) return;

        if (pc.connectionState === "connected") {
            mostrarEstado("En llamada");
            remoteState.textContent = "Conectado";
            liveDot.classList.add("live");
        } else if (pc.connectionState === "failed") {
            terminar("No se pudo establecer la conexión de video.");
        } else if (pc.connectionState === "disconnected") {
            mostrarEstado("Reconectando...");
            remoteState.textContent = "Sin conexión";
            liveDot.classList.remove("live");
        }
    };
}

/* -------------------- Arranque -------------------- */

async function conectar() {
    connection = new signalR.HubConnectionBuilder()
        .withUrl(`${SERVER_URL}/chat`, {
            accessTokenFactory: () => token
        })
        .withAutomaticReconnect([0, 2000, 5000, 10000])
        .build();

    connection.on("SenalLlamada", manejarSenal);

    connection.on("SesionRevocada", () => {
        sessionStorage.removeItem(CLAVE_SESION);
        window.location.href = "IniciarSesion.html";
    });

    connection.on("LlamadaTerminada", (id, motivo) => {
        if (id !== llamadaId || terminada) return;
        terminar(mensajeDeMotivo(motivo));
    });

    /* SignalR no conserva los grupos al reconectar: sin esto la página
       sigue abierta pero deja de recibir la señalización WebRTC. */
    connection.onreconnected(async () => {
        if (terminada) return;

        try {
            const info = await connection.invoke("UnirseLlamada", llamadaId);
            rol = info.Rol;
            remoteName.textContent = info.OtroNombre || "Usuario";

            if (pc) {
                pc.close();
            }
            ofertaCreada = false;
            ofertaRecibida = false;
            candidatosPendientes = [];
            crearConexion();
            enviarSenal("listo", {});
            mostrarEstado("Reconectando...");
        } catch (error) {
            terminar(mensajeDeError(error));
        }
    });

    connection.onclose(() => {
        if (!terminada) {
            terminar("Se perdió la conexión con el servidor.");
        }
    });

    await connection.start();

    const info = await connection.invoke("UnirseLlamada", llamadaId);

    rol = info.Rol;
    remoteName.textContent = info.OtroNombre || "Usuario";

    crearConexion();
    enviarSenal("listo", {});

    mostrarEstado(rol === "llamante" ? "Llamando..." : "Conectando...");
}

async function iniciar() {
    if (!llamadaId) {
        terminar("No se indicó qué videollamada abrir.");
        return;
    }

    try {
        streamLocal = await navigator.mediaDevices.getUserMedia({
            video: true,
            audio: true
        });
    } catch (error) {
        console.error("No se pudo abrir cámara/micrófono:", error);
        terminar(
            "No se pudo acceder a la cámara y el micrófono. " +
            "Revisa los permisos del navegador."
        );
        return;
    }

    selfVideo.srcObject = streamLocal;

    try {
        await conectar();
    } catch (error) {
        console.error("No se pudo entrar a la llamada:", error);
        terminar(
            mensajeDeError(error) || "No se pudo entrar a la videollamada."
        );
    }
}

/* -------------------- Controles -------------------- */

toggleMic.addEventListener("click", () => {
    const pista = streamLocal && streamLocal.getAudioTracks()[0];
    if (!pista) return;

    pista.enabled = !pista.enabled;
    toggleMic.classList.toggle("off", !pista.enabled);
});

toggleCamera.addEventListener("click", () => {
    const pista = streamLocal && streamLocal.getVideoTracks()[0];
    if (!pista) return;

    pista.enabled = !pista.enabled;
    toggleCamera.classList.toggle("off", !pista.enabled);
});

async function colgar() {
    if (terminada) return;

    if (connection && connection.state === "Connected") {
        try {
            await connection.invoke("ColgarLlamada", llamadaId);
        } catch (error) {
            console.error("No se pudo avisar que se colgó:", error);
        }
    }

    terminar("Has salido de la llamada.");
}

endCall.addEventListener("click", colgar);

document.addEventListener("keydown", event => {
    if (event.key === "Escape") {
        colgar();
    }
});

iniciar();
