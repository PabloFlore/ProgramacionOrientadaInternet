const loginForm = document.getElementById("loginForm");

if (loginForm) {

    loginForm.addEventListener("submit", function(event) {

        event.preventDefault();

        const usuario =
            document.getElementById("usuario").value.trim();

        const password =
            document.getElementById("password").value.trim();

        const error =
            document.getElementById("loginError");


        error.textContent = "";

        if (usuario === "" || password === "") {

            error.textContent =
                "No puedes iniciar sesión. Completa Usuario y Contraseña.";

            return;
        }

        alert("Inicio de sesión realizado correctamente.");

    });
}

const registerForm =
    document.getElementById("registerForm");


if (registerForm) {

    const password =
        document.getElementById("passwordRegistro");

    const confirmPassword =
        document.getElementById("confirmPassword");

    password.addEventListener("input", function() {

        const value = password.value;


        // 8 caracteres

        const hasLength = value.length >= 8;

        document.getElementById("length").textContent =
            hasLength
                ? "✓ Al menos 8 caracteres"
                : "✕ Al menos 8 caracteres";


        // Número

        const hasNumber = /[0-9]/.test(value);

        document.getElementById("number").textContent =
            hasNumber
                ? "✓ Al menos un número"
                : "✕ Al menos un número";


        // Mayúscula

        const hasUppercase = /[A-Z]/.test(value);

        document.getElementById("uppercase").textContent =
            hasUppercase
                ? "✓ Una letra mayúscula"
                : "✕ Una letra mayúscula";


        // Carácter especial

        const hasSpecial =
            /[^A-Za-z0-9]/.test(value);

        document.getElementById("special").textContent =
            hasSpecial
                ? "✓ Un carácter especial"
                : "✕ Un carácter especial";

    });

    registerForm.addEventListener("submit", function(event) {

        event.preventDefault();


        const nombre =
            document.getElementById("nombre").value.trim();

        const apellido =
            document.getElementById("apellido").value.trim();

        const usuario =
            document.getElementById("usuarioRegistro").value.trim();

        const correo =
            document.getElementById("correo").value.trim();

        const passwordValue =
            password.value;

        const confirmValue =
            confirmPassword.value;

        const error =
            document.getElementById("registerError");


        error.textContent = "";

        if (
            nombre === "" ||
            apellido === "" ||
            usuario === "" ||
            correo === "" ||
            passwordValue === "" ||
            confirmValue === ""
        ) {

            error.textContent =
                "Por favor, completa todos los campos.";

            return;
        }

        const passwordValid =
            passwordValue.length >= 8 &&
            /[0-9]/.test(passwordValue) &&
            /[A-Z]/.test(passwordValue) &&
            /[^A-Za-z0-9]/.test(passwordValue);


        if (!passwordValid) {

            error.textContent =
                "La contraseña no cumple con todos los requisitos.";

            return;
        }

        if (passwordValue !== confirmValue) {

            error.textContent =
                "Las contraseñas no coinciden.";

            return;
        }

        alert("¡Cuenta creada correctamente!");

        window.location.href =
            "iniciar-sesion.html";

    });

}