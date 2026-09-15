const nombre = document.getElementById("nombre");
const apellido = document.getElementById("apellido");
const usuario = document.getElementById("usuario");
const correo = document.getElementById("correo");

const nuevaPassword =
    document.getElementById("nuevaPassword");

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

function validarPassword() {

    const password =
        nuevaPassword.value;

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

    } else {

        passwordRules.style.display =
            "none";
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
formCuenta.addEventListener(
    "submit",
    function (event) {
        event.preventDefault();

        const passwordCorrecta =
            validarPassword();

        const confirmacionCorrecta =
            validarConfirmacion();

        if (!passwordCorrecta) {
            alert(
                "La contraseña no cumple con todos los requisitos."
            );
            return;
        }

        if (!confirmacionCorrecta) {
            return;
        }

        // Aquí luego ira el envio a la base de datos.

        cambiosGuardados = true;
        alert(
            "Datos guardados con éxito."
        );
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