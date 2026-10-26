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

/* Un solo lugar para mostrar errores del formulario. */
function mostrarError(mensaje) {
    passwordError.textContent = mensaje;
    passwordError.style.display = "block";
}

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

/* Imágenes que se enviarán al guardar. null = no cambian.
   Se guardan como data URL reducida (JPEG) para caber en la base. */
let fotoNueva = null;
let bannerNueva = null;

/* Mismo límite que el servidor (MaximoImagen). */
const MAX_IMAGEN_CARACTERES = 700000;

/* Reduce la imagen con canvas hasta maxLado píxeles en su lado mayor
   y la exporta como JPEG, bajando la calidad si aún es muy pesada. */
function reducirImagen(archivo, maxLado) {
    return new Promise((resolver, rechazar) => {
        const lector = new FileReader();

        lector.onerror = () =>
            rechazar(new Error("No se pudo leer la imagen."));

        lector.onload = () => {
            const img = new Image();

            img.onerror = () =>
                rechazar(new Error("El archivo no es una imagen válida."));

            img.onload = () => {
                const escala = Math.min(
                    1,
                    maxLado / Math.max(img.width, img.height)
                );

                const canvas = document.createElement("canvas");
                canvas.width = Math.round(img.width * escala);
                canvas.height = Math.round(img.height * escala);

                const ctx = canvas.getContext("2d");
                /* JPEG no tiene transparencia: se pinta fondo blanco. */
                ctx.fillStyle = "#ffffff";
                ctx.fillRect(0, 0, canvas.width, canvas.height);
                ctx.drawImage(img, 0, 0, canvas.width, canvas.height);

                for (const calidad of [0.85, 0.7, 0.5]) {
                    const dataUrl = canvas.toDataURL("image/jpeg", calidad);

                    if (dataUrl.length <= MAX_IMAGEN_CARACTERES) {
                        resolver(dataUrl);
                        return;
                    }
                }

                rechazar(new Error(
                    "La imagen es demasiado grande incluso comprimida. " +
                    "Usa una más pequeña."
                ));
            };

            img.src = lector.result;
        };

        lector.readAsDataURL(archivo);
    });
}

foto.addEventListener("change", async function () {
    if (foto.files.length === 0) {
        return;
    }

    const archivo = foto.files[0];

    const nombreArchivo =
        foto.parentElement.querySelector(".file-name");

    try {
        fotoNueva = await reducirImagen(archivo, 512);
        previewFoto.src = fotoNueva;
        nombreArchivo.textContent = archivo.name;
        cambiosGuardados = false;
    } catch (error) {
        fotoNueva = null;
        foto.value = "";
        nombreArchivo.textContent = "Ningún archivo seleccionado";
        mostrarError(error.message);
    }
});

banner.addEventListener("change", async function () {
    if (banner.files.length === 0) {
        return;
    }

    const archivo = banner.files[0];

    const nombreArchivo =
        banner.parentElement.querySelector(".file-name");

    try {
        /* El banner es más ancho: se permite más resolución. */
        bannerNueva = await reducirImagen(archivo, 1024);
        previewBanner.style.backgroundImage = `url("${bannerNueva}")`;
        nombreArchivo.textContent = archivo.name;
        cambiosGuardados = false;
    } catch (error) {
        bannerNueva = null;
        banner.value = "";
        nombreArchivo.textContent = "Ningún archivo seleccionado";
        mostrarError(error.message);
    }
});

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
        mostrarError("Las contraseñas no coinciden.");
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
        headers: {
            "Content-Type": "application/json",
            "Authorization": `Bearer ${sesion.Token || ""}`
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
        throw new Error(
            datos.Error || "Ocurrio un error. Intenta de nuevo."
        );
    }

    return datos;
}

/* Foto y banner guardados: no van en la sesión, se piden a la API. */
async function cargarImagenesGuardadas() {
    try {
        const respuesta = await fetch(`${API_CUENTA}/api/cuenta`, {
            headers: { "Authorization": `Bearer ${sesion.Token || ""}` }
        });

        if (!respuesta.ok) return;

        const datos = await respuesta.json();

        if (datos.FotoPerfil) {
            previewFoto.src = datos.FotoPerfil;
        }

        if (datos.Banner) {
            previewBanner.style.backgroundImage = `url("${datos.Banner}")`;
        }
    } catch (error) {
        console.error("No se pudieron cargar las imágenes:", error);
    }
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

    cargarImagenesGuardadas();
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
            mostrarError("Completa todos los campos.");
            return;
        }

        /* La contraseña solo se valida si el usuario quiere cambiarla. */
        const quiereCambiarPassword = nuevaPassword.value !== "";

        if (quiereCambiarPassword) {
            if (passwordActual.value === "") {
                mostrarError(
                    "Escribe tu contraseña actual para cambiarla."
                );
                return;
            }

            if (!validarPassword()) {
                mostrarError(
                    "La contraseña no cumple con todos los requisitos."
                );
                return;
            }

            if (nuevaPassword.value !== confirmarPassword.value) {
                mostrarError("Las contraseñas no coinciden.");
                return;
            }
        }

        /* Un solo PATCH: el servidor valida todo antes de escribir. */
        const cuerpo = {
            nombre: nombreValor,
            apellido: apellidoValor,
            usuario: usuarioValor,
            correo: correoValor
        };

        if (quiereCambiarPassword) {
            cuerpo.contrasenaActual = passwordActual.value;
            cuerpo.contrasenaNueva = nuevaPassword.value;
        }

        if (fotoNueva !== null) {
            cuerpo.fotoPerfil = fotoNueva;
        }

        if (bannerNueva !== null) {
            cuerpo.banner = bannerNueva;
        }

        try {
            const perfil = await pedir(
                `${API_CUENTA}/api/cuenta`,
                "PATCH",
                cuerpo
            );

            /* La sesión se actualiza conservando el token. */
            sesion.Nombre = perfil.Nombre;
            sesion.Apellido = perfil.Apellido;
            sesion.Usuario = perfil.Usuario;
            sesion.Correo = perfil.Correo;

            sessionStorage.setItem(
                CLAVE_SESION,
                JSON.stringify(sesion)
            );

            cambiosGuardados = true;

            alert("Datos guardados con éxito.");

            window.location.href = "Home.html";

        } catch (error) {
            mostrarError(error.message);
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