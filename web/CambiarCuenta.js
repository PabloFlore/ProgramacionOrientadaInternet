const nombre = document.getElementById("nombre");
const apellido = document.getElementById("apellido");
const usuario = document.getElementById("usuario");
const correo = document.getElementById("correo");

const nuevaPassword =
    document.getElementById("nuevaPassword");

const passwordActual =
    document.getElementById("passwordActual");

const confirmarPassword =
    document.getElementById("confirmarPassword");

const foto =
    document.getElementById("foto");

const banner =
    document.getElementById("banner");

const previewNombre =
    document.getElementById("previewNombre");

const previewUsuario =
    document.getElementById("previewUsuario");

const previewCorreo =
    document.getElementById("previewCorreo");

const previewFoto =
    document.getElementById("previewFoto");

const previewBanner =
    document.getElementById("previewBanner");

const passwordRules =
    document.getElementById("passwordRules");

const passwordError =
    document.getElementById("passwordError");

const formCuenta =
    document.getElementById("formCuenta");

const btnRegresar =
    document.getElementById("btnRegresar");

// PREVIEWS

let cambiosGuardados = false;

function actualizarNombre() {

    let nombreCompleto =
        nombre.value + " " + apellido.value;

    nombreCompleto =
        nombreCompleto.trim();

    if (nombreCompleto === "") {

        previewNombre.textContent =
            "Tu nombre";

    } else {

        previewNombre.textContent =
            nombreCompleto;
    }

    cambiosGuardados = false;
}

nombre.addEventListener(
    "input",
    actualizarNombre
);

apellido.addEventListener(
    "input",
    actualizarNombre
);

usuario.addEventListener(
    "input",
    function () {

        let user =
            usuario.value.trim();

        if (user === "") {

            previewUsuario.textContent =
                "@usuario";

        } else {

            if (!user.startsWith("@")) {
                user = "@" + user;
            }

            previewUsuario.textContent =
                user;
        }

        cambiosGuardados = false;
    }
);

correo.addEventListener(
    "input",
    function () {

        if (correo.value.trim() === "") {

            previewCorreo.textContent =
                "correo@ejemplo.com";

        } else {

            previewCorreo.textContent =
                correo.value;
        }

        cambiosGuardados = false;
    }
);

foto.addEventListener(
    "change",
    function () {

        if (foto.files.length === 0) {
            return;
        }

        const archivo =
            foto.files[0];

        const imagen =
            URL.createObjectURL(archivo);

        previewFoto.src = imagen;

        const nombreArchivo =
            foto.parentElement.querySelector(
                ".file-name"
            );

        nombreArchivo.textContent =
            archivo.name;

        cambiosGuardados = false;
    }
);

banner.addEventListener(
    "change",
    function () {

        if (banner.files.length === 0) {
            return;
        }

        const archivo =
            banner.files[0];

        const imagen =
            URL.createObjectURL(archivo);

        previewBanner.style.backgroundImage =
            `url("${imagen}")`;

        const nombreArchivo =
            banner.parentElement.querySelector(
                ".file-name"
            );

        nombreArchivo.textContent =
            archivo.name;

        cambiosGuardados = false;
    }
);

// VALIDACIONES CONTRASEÑA

/* La contraseña es OPCIONAL: si el campo queda vacío no se toca
   nada. Solo se valida cuando el usuario quiere cambiarla. */
function validarPassword() {

    const password =
        nuevaPassword.value;

    if (password.length === 0) {
        passwordRules.style.display = "none";
        return true;
    }

    const tieneLongitud =
        password.length >= 8;

    const tieneMayuscula =
        /[A-Z]/.test(password);

    const tieneNumero =
        /[0-9]/.test(password);

    const tieneEspecial =
        /[^A-Za-z0-9]/.test(password);
    if (password.length > 0) {

        passwordRules.style.display =
            "flex";

    }

    const reglaLongitud =
        document.getElementById(
            "reglaLongitud"
        );

    if (tieneLongitud) {

        reglaLongitud.textContent =
            "✓ Al menos 8 caracteres";

        reglaLongitud.classList.add(
            "valid"
        );

    } else {

        reglaLongitud.textContent =
            "✕ Al menos 8 caracteres";

        reglaLongitud.classList.remove(
            "valid"
        );
    }

    const reglaMayuscula =
        document.getElementById(
            "reglaMayuscula"
        );

    if (tieneMayuscula) {

        reglaMayuscula.textContent =
            "✓ Al menos una mayúscula";

        reglaMayuscula.classList.add(
            "valid"
        );

    } else {

        reglaMayuscula.textContent =
            "✕ Al menos una mayúscula";

        reglaMayuscula.classList.remove(
            "valid"
        );
    }

    const reglaNumero =
        document.getElementById(
            "reglaNumero"
        );

    if (tieneNumero) {

        reglaNumero.textContent =
            "✓ Al menos un número";

        reglaNumero.classList.add(
            "valid"
        );

    } else {

        reglaNumero.textContent =
            "✕ Al menos un número";

        reglaNumero.classList.remove(
            "valid"
        );
    }

    const reglaEspecial =
        document.getElementById(
            "reglaEspecial"
        );

    if (tieneEspecial) {
        reglaEspecial.textContent =
            "✓ Al menos un carácter especial";

        reglaEspecial.classList.add(
            "valid"
        );

    } else {
        reglaEspecial.textContent =
            "✕ Al menos un carácter especial";

        reglaEspecial.classList.remove(
            "valid"
        );
    }

    return (
        tieneLongitud &&
        tieneMayuscula &&
        tieneNumero &&
        tieneEspecial
    );
}

nuevaPassword.addEventListener(
    "input",
    function () {
        validarPassword();
        cambiosGuardados = false;
    }
);

// CONFIRMAR CONTRASEÑA
function validarConfirmacion() {
    if (
        confirmarPassword.value !== "" &&
        nuevaPassword.value !==
        confirmarPassword.value
    ) {
        passwordError.style.display =
            "block";
        return false;
    } else {
        passwordError.style.display =
            "none";
        return true;
    }
}

confirmarPassword.addEventListener(
    "input",
    function () {
        validarConfirmacion();
        cambiosGuardados = false;
    }
);

// GUARDAR CAMBIOS

const API_CUENTA =
    `http://${window.location.hostname || "localhost"}:5000`;

const CLAVE_SESION = "musicHubUsuario";

function leerSesion() {
    try {
        return JSON.parse(
            sessionStorage.getItem(CLAVE_SESION)
        );
    } catch (error) {
        return null;
    }
}

async function pedir(url, metodo, cuerpo) {
    const respuesta = await fetch(url, {
        method: metodo,
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(cuerpo)
    });

    let datos = {};

    try {
        datos = await respuesta.json();
    } catch (error) {
        datos = {};
    }

    if (!respuesta.ok) {
        throw new Error(
            datos.Error || "Ocurrio un error. Intenta de nuevo."
        );
    }

    return datos;
}

/* Sin sesión no se edita el perfil. */
const sesion = leerSesion();

if (!sesion) {
    window.location.href = "IniciarSesion.html";
} else {
    /* Se precarga el formulario con los datos de la cuenta. */
    nombre.value = sesion.Nombre || "";
    apellido.value = sesion.Apellido || "";
    usuario.value = sesion.Usuario || "";
    correo.value = sesion.Correo || "";

    actualizarNombre();
    previewUsuario.textContent =
        "@" + (sesion.Usuario || "usuario");
    previewCorreo.textContent = sesion.Correo || "";
}

formCuenta.addEventListener(
    "submit",
    async function (event) {
        event.preventDefault();

        passwordError.style.display = "none";

        if (!sesion) {
            return;
        }

        const nombreValor = nombre.value.trim();
        const apellidoValor = apellido.value.trim();
        const usuarioValor = usuario.value.trim();
        const correoValor = correo.value.trim();

        if (nombreValor === "" || apellidoValor === "" ||
            usuarioValor === "" || correoValor === "")
        {
            passwordError.style.display = "block";
            passwordError.textContent =
                "Completa todos los campos.";
            return;
        }

        if (!validarPassword()) {
            passwordError.style.display = "block";
            passwordError.textContent =
                "La contraseña no cumple con todos los requisitos.";
            return;
        }

        if (!validarConfirmacion()) {
            return;
        }

        try {
            /* 1) Perfil */
            const perfil = await pedir(
                `${API_CUENTA}/api/cuenta`,
                "PATCH",
                {
                    correoActual: sesion.Correo,
                    nombre: nombreValor,
                    apellido: apellidoValor,
                    usuario: usuarioValor,
                    correo: correoValor
                }
            );

            /* La sesión se actualiza con lo que devolvió el
               servidor, por si cambió el correo. */
            sessionStorage.setItem(
                CLAVE_SESION,
                JSON.stringify(perfil)
            );

            /* 2) Contraseña (solo si escribió una nueva) */
            if (nuevaPassword.value !== "") {

                if (passwordActual.value === "") {
                    passwordError.style.display = "block";
                    passwordError.textContent =
                        "Escribe tu contraseña actual para " +
                        "cambiarla.";
                    return;
                }

                await pedir(
                    `${API_CUENTA}/api/cuenta/contrasena`,
                    "PATCH",
                    {
                        correo: perfil.Correo,
                        contrasenaActual: passwordActual.value,
                        contrasenaNueva: nuevaPassword.value
                    }
                );

            }

            cambiosGuardados = true;

            alert("Datos guardados con éxito.");

            window.location.href = "Home.html";

        } catch (error) {
            passwordError.style.display = "block";
            passwordError.textContent = error.message;
        }
    }
);

btnRegresar.addEventListener(
    "click",
    function () {
        if (cambiosGuardados) {

            window.location.href =
                "Home.html";

            return;
        }

        const salir =
            confirm(
                "¿Quieres salir sin guardar los cambios?"
            );

        if (salir) {

            window.location.href =
                "Home.html";
        }

    }
);