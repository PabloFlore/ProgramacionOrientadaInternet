/* ============================================================
   Music Hub — ValidacionRegistro.js
   Validación en vivo de contraseña + llamadas reales al backend:
   POST /api/registrar y POST /api/login.
   Requiere: el servidor ChatServer corriendo.
   ============================================================ */

/* -------------------- Sesión -------------------- */

/* sessionStorage (no localStorage) a propósito: cada pestaña
   guarda su propia sesión, así puedes registrarte en una e
   iniciar sesión con OTRA cuenta en otra pestaña del mismo
   equipo y ver dos usuarios distintos chateando. */

const CLAVE_SESION = "musicHubUsuario";

function guardarSesion(usuario) {
    sessionStorage.setItem(
        CLAVE_SESION,
        JSON.stringify(usuario)
    );
}

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

function borrarSesion() {
    sessionStorage.removeItem(CLAVE_SESION);
}

/* -------------------- Utilidades -------------------- */

const API = `http://${window.location.hostname || "localhost"}:5000`;

async function pedir(url, metodo, cuerpo) {
    const respuesta = await fetch(url, {
        method: metodo,
        headers: {
            "Content-Type": "application/json"
        },
        body: JSON.stringify(cuerpo)
    });

    let datos = {};

    try {
        datos = await respuesta.json();
    } catch (error) {
        datos = {};
    }

    if (!respuesta.ok) {
        const mensaje = datos.Error ||
            "Ocurrio un error. Intenta de nuevo.";

        throw new Error(mensaje);
    }

    return datos;
}

function bloquear(boton, activo) {

    if (!boton) {
        return;
    }

    boton.disabled = activo;
    boton.textContent = activo
        ? "Esperando..."
        : boton.dataset.textoOriginal;

}

/* ======================= INICIAR SESIÓN ======================= */

const loginForm = document.getElementById("loginForm");

if (loginForm) {

    const correo = document.getElementById("correo");

    const loginPassword = document.getElementById("password");

    const loginError = document.getElementById("loginError");

    const loginButton = document.getElementById("loginButton");

    if (loginButton) {
        loginButton.dataset.textoOriginal = loginButton.textContent.trim();
    }

    loginForm.addEventListener("submit", async function(event) {

        event.preventDefault();

        loginError.textContent = "";

        const correoValor = correo.value.trim();

        const passwordValor = loginPassword.value;

        if (correoValor === "" || passwordValor === "") {

            loginError.textContent =
                "Escribe tu correo y tu contrasena.";

            return;

        }

        bloquear(loginButton, true);

        try {

            const usuario = await pedir(
                `${API}/api/login`,
                "POST",
                {
                    correo: correoValor,
                    contrasena: passwordValor
                }
            );

            guardarSesion(usuario);

            window.location.href = "Home.html";

        } catch (error) {

            loginError.textContent = error.message;

            bloquear(loginButton, false);

        }

    });

}

/* ========================== REGISTRO ========================== */

const registerForm = document.getElementById("registerForm");

if (registerForm) {

    const password = document.getElementById("passwordRegistro");

    const confirmPassword =
        document.getElementById("confirmPassword");

    const registerError =
        document.getElementById("registerError");

    const registerButton =
        document.getElementById("registerButton");

    if (registerButton) {
        registerButton.dataset.textoOriginal =
            registerButton.textContent.trim();
    }

    /* ---- Requisitos de contraseña en vivo ---- */

    password.addEventListener("input", function() {

        const value = password.value;

        document.getElementById("length").textContent =
            value.length >= 8
                ? "✓ Al menos 8 caracteres"
                : "✕ Al menos 8 caracteres";

        document.getElementById("number").textContent =
            /[0-9]/.test(value)
                ? "✓ Al menos un numero"
                : "✕ Al menos un numero";

        document.getElementById("uppercase").textContent =
            /[A-Z]/.test(value)
                ? "✓ Una letra mayuscula"
                : "✕ Una letra mayuscula";

        document.getElementById("special").textContent =
            /[^A-Za-z0-9]/.test(value)
                ? "✓ Un caracter especial"
                : "✕ Un caracter especial";

    });

    confirmPassword.addEventListener("input", function() {

        if (confirmPassword.value !== "") {

            const coinciden =
                password.value === confirmPassword.value;

            registerError.textContent = coinciden
                ? ""
                : "Las contrasenas no coinciden.";

        }

    });

    /* ---- Envío del registro ---- */

    registerForm.addEventListener("submit", async function(event) {

        event.preventDefault();

        registerError.textContent = "";

        const nombre =
            document.getElementById("nombre").value.trim();

        const apellido =
            document.getElementById("apellido").value.trim();

        const usuario =
            document.getElementById("usuarioRegistro").value.trim();

        const correo =
            document.getElementById("correo").value.trim();

        const passwordValue = password.value;

        const confirmValue = confirmPassword.value;

        if (nombre === "" || apellido === "" ||
            usuario === "" || correo === "" ||
            passwordValue === "" || confirmValue === "")
        {

            registerError.textContent =
                "Por favor, completa todos los campos.";

            return;

        }

        const passwordValid =
            passwordValue.length >= 8 &&
            /[0-9]/.test(passwordValue) &&
            /[A-Z]/.test(passwordValue) &&
            /[^A-Za-z0-9]/.test(passwordValue);

        if (!passwordValid) {

            registerError.textContent =
                "La contrasena no cumple con todos los requisitos.";

            return;

        }

        if (passwordValue !== confirmValue) {

            registerError.textContent =
                "Las contrasenas no coinciden.";

            return;

        }

        bloquear(registerButton, true);

        try {

            await pedir(
                `${API}/api/registrar`,
                "POST",
                {
                    nombre: nombre,
                    apellido: apellido,
                    usuario: usuario,
                    correo: correo,
                    contrasena: passwordValue
                }
            );

            /* Cuenta creada: se avisa y se manda a iniciar sesión
               con el correo recién registrado, precargado. */
            sessionStorage.setItem(
                "musicHubCorreoRegistro",
                correo
            );

            window.location.href = "IniciarSesion.html";

        } catch (error) {

            registerError.textContent = error.message;

            bloquear(registerButton, false);

        }

    });

    /* Si viene de un registro recién creado, se precarga el
       correo y el foco queda en la contraseña. */
    const correoPrellenado =
        sessionStorage.getItem("musicHubCorreoRegistro");

    if (correoPrellenado) {
        document.getElementById("correo").value = correoPrellenado;
        sessionStorage.removeItem("musicHubCorreoRegistro");
        confirmPassword.focus();
    }

}
