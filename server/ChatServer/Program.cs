using System.Text.RegularExpressions;
using Microsoft.Extensions.FileProviders;
using ChatServer.Hubs;
using ChatServer.Datos;
using ChatServer.Modelos;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls("http://0.0.0.0:5000");

builder.Services.AddCors(options =>
{
    options.AddPolicy("SignalR", policy =>
    {
        policy
            .AllowAnyOrigin()
            .AllowAnyMethod()
            .AllowAnyHeader();
    });
});

builder.Services.AddSignalR();

/* ASP.NET serializa por defecto en camelCase (nombre, correo, mensajes).
   El frontend está escrito con los mismos nombres que los objetos de C#
   (Nombre, Correo, Mensajes), y JavaScript distingue mayúsculas, así que
   sin esto sesionUsuario.Nombre llegaba como undefined. Se fuerza a
   conservar el nombre exacto de la propiedad. */
builder.Services.ConfigureHttpJsonOptions(opciones =>
{
    opciones.SerializerOptions.PropertyNamingPolicy = null;
});

var app = builder.Build();

/* Al arrancar se borran las sesiones vencidas (y las anteriores a la
   caducidad, que no tienen fecha de vencimiento). */
SesionesBd.LimpiarVencidas();

app.UseCors("SignalR");

/* Servir SOLO la carpeta web/ del proyecto (así puedes abrir
   http://<IP>:5000/Home.html en el celular sin CDN).

   Importante: la raíz expuesta es únicamente web/, nunca la carpeta
   del proyecto completa. Si se sirviera la raíz, quedarían accesibles
   por HTTP el chat.db de SQLite (correos, hashes y sales de todos los
   usuarios), client/node_modules y .vs/. */

/* Localiza la carpeta web/ subiendo desde el ContentRoot (dotnet run) o
   desde el binario (dotnet ChatServer.dll) hasta encontrar web/Home.html. */
var WebRaiz = BuscarCarpetaWeb();

/* Mientras se desarrolla nunca se debe cachear: si no, el navegador y el
   celular siguen mostrando el HTML/JS/CSS viejo aunque el código ya haya
   cambiado. Se exceptúa /chat para no tocar el handshake de SignalR. */
app.Use(async (contexto, siguiente) =>
{
    if (!contexto.Request.Path.StartsWithSegments("/chat"))
    {
        contexto.Response.OnStarting(() =>
        {
            contexto.Response.Headers.CacheControl =
                "no-store, no-cache, must-revalidate";
            contexto.Response.Headers.Pragma = "no-cache";
            return Task.CompletedTask;
        });
    }

    await siguiente();
});

app.UseDefaultFiles(new DefaultFilesOptions
{
    FileProvider = new PhysicalFileProvider(WebRaiz),
    DefaultFileNames = new List<string> { "Home.html" }
});

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(WebRaiz)
});

string BuscarCarpetaWeb()
{
    var origenes = new[]
    {
        app.Environment.ContentRootPath,
        AppContext.BaseDirectory
    };

    foreach (var origen in origenes)
    {
        var actual = new DirectoryInfo(origen);

        while (actual is not null)
        {
            var candidata =
                Path.Combine(actual.FullName, "web");

            if (File.Exists(Path.Combine(candidata, "Home.html")))
            {
                return candidata;
            }

            actual = actual.Parent;
        }
    }

    throw new DirectoryNotFoundException(
        "No se encontró la carpeta web/ (debe contener Home.html) " +
        "subiendo desde: " + string.Join(" | ", origenes));
}

app.MapHub<ChatHub>("/chat");

/* ======================= AUTENTICACIÓN POR TOKEN ======================= */

/* El token viaja en Authorization: Bearer <token>. Con él se resuelve
   el usuario de la sesión: los endpoints ya no confían en datos que
   mande el cliente (como CorreoActual). */
string? TokenDe(HttpContext contexto)
{
    var encabezado =
        contexto.Request.Headers.Authorization.ToString();

    if (encabezado.StartsWith(
            "Bearer ",
            StringComparison.OrdinalIgnoreCase))
    {
        return encabezado.Substring("Bearer ".Length).Trim();
    }

    return null;
}

Usuario? UsuarioAutenticado(HttpContext contexto)
{
    var token = TokenDe(contexto);

    if (string.IsNullOrWhiteSpace(token))
    {
        return null;
    }

    var id = SesionesBd.ObtenerUsuarioId(token);

    if (id is null)
    {
        return null;
    }

    return UsuariosBd.BuscarPorId(id.Value);
}

IResult SinSesion() =>
    Results.Json(
        new { Error = "Sesión no válida. Inicia sesión de nuevo." },
        statusCode: StatusCodes.Status401Unauthorized);

/* ======================= REGISTRO Y LOGIN (SQLite) ======================= */

/* Límites de tamaño. La contraseña se limita a 128 caracteres; las
   imágenes son data URL (texto) de como máximo 700 000 caracteres. */
const int MaximoContrasena = 128;
const int MaximoImagen = 700_000;

var patronCorreo =
    new Regex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

/* Devuelve el motivo del rechazo, o null si la contraseña es válida. */
string? ValidarContrasena(string contrasena)
{
    if (contrasena.Length > MaximoContrasena)
    {
        return "La contraseña no puede tener más de " +
            $"{MaximoContrasena} caracteres.";
    }

    if (contrasena.Length < 8 ||
        !Regex.IsMatch(contrasena, "[0-9]") ||
        !Regex.IsMatch(contrasena, "[A-Z]") ||
        !Regex.IsMatch(contrasena, "[^A-Za-z0-9]"))
    {
        return "La contraseña debe tener al menos 8 caracteres, " +
            "una mayúscula, un número y un símbolo.";
    }

    return null;
}

/* Solo JPEG, PNG y WebP (lista blanca). SVG y otros formatos se rechazan,
   porque un SVG puede llevar scripts si se muestra como documento. */
var patronImagen = new Regex(
    @"^data:image/(jpeg|png|webp);base64,(.+)$",
    RegexOptions.Compiled | RegexOptions.IgnoreCase);

/* Vacío = sin imagen (no se valida). El tamaño se revisa primero para no
   decodificar algo enorme. */
string? ValidarImagen(string? dataUrl, string etiqueta)
{
    if (string.IsNullOrEmpty(dataUrl))
    {
        return null;
    }

    if (dataUrl.Length > MaximoImagen)
    {
        return $"{etiqueta} es demasiado grande. Usa una imagen más pequeña.";
    }

    var coincidencia = patronImagen.Match(dataUrl);

    if (!coincidencia.Success)
    {
        return $"{etiqueta} debe ser una imagen JPEG, PNG o WebP.";
    }

    try
    {
        Convert.FromBase64String(coincidencia.Groups[2].Value);
    }
    catch (FormatException)
    {
        return $"{etiqueta} no es una imagen válida.";
    }

    return null;
}

/* .../api/registrar — crea una cuenta (correo único) */
app.MapPost("/api/registrar", (RegistroRequest peticion) =>
{
    var nombre =
        peticion.Nombre?.Trim() ?? "";
    var apellido =
        peticion.Apellido?.Trim() ?? "";
    var usuario =
        peticion.Usuario?.Trim() ?? "";
    var correo =
        (peticion.Correo?.Trim() ?? "").ToLowerInvariant();
    var contrasena =
        peticion.Contrasena ?? "";

    if (nombre == "" || apellido == "" ||
        usuario == "" || correo == "")
    {
        return Results.BadRequest(
            new { Error = "Completa todos los campos." });
    }

    if (correo.Length > 254 ||
        !patronCorreo.IsMatch(correo))
    {
        return Results.BadRequest(
            new { Error = "El correo no tiene un formato válido." });
    }

    var errorContrasena = ValidarContrasena(contrasena);

    if (errorContrasena is not null)
    {
        return Results.BadRequest(new { Error = errorContrasena });
    }

    if (UsuariosBd.CorreoExiste(correo))
    {
        return Results.Conflict(
            new { Error =
                "Ese correo ya está registrado. Usa otro." });
    }

    var (hash, sal) =
        UsuariosBd.CrearHashContrasena(contrasena);

    var nuevoUsuario = new Usuario
    {
        Nombre = nombre,
        Apellido = apellido,
        NombreUsuario = usuario,
        Correo = correo,
        HashContrasena = hash,
        Sal = sal,
        FechaRegistro = DateTime.UtcNow
    };

    if (!UsuariosBd.Registrar(nuevoUsuario))
    {
        return Results.Conflict(
            new { Error =
                "Ese correo ya está registrado. Usa otro o inicia sesión." });
    }

    return Results.Created("/api/registrar",
        new
        {
            Nombre = nuevoUsuario.Nombre,
            Apellido = nuevoUsuario.Apellido,
            Usuario = nuevoUsuario.NombreUsuario,
            Correo = nuevoUsuario.Correo
        });
});

/* .../api/login — entra con correo + contraseña y emite un token.
   Tras 5 fallos en 15 minutos el correo se bloquea (429). */
app.MapPost("/api/login", (LoginRequest peticion) =>
{
    var correo =
        (peticion.Correo?.Trim() ?? "").ToLowerInvariant();
    var contrasena =
        peticion.Contrasena ?? "";

    if (correo == "" || contrasena == "")
    {
        return Results.BadRequest(
            new { Error = "Escribe tu correo y tu contraseña." });
    }

    if (LimiteIntentos.Bloqueado(correo))
    {
        return Results.Json(
            new { Error =
                "Demasiados intentos fallidos. Espera 15 minutos " +
                "e inténtalo de nuevo." },
            statusCode: StatusCodes.Status429TooManyRequests);
    }

    var usuario = UsuariosBd.BuscarPorCorreo(correo);

    var valida =
        usuario is not null &&
        contrasena.Length <= MaximoContrasena &&
        UsuariosBd.VerificarContrasena(
            contrasena,
            usuario.HashContrasena,
            usuario.Sal);

    if (!valida || usuario is null)
    {
        LimiteIntentos.RegistrarFallo(correo);

        return Results.Json(
            new { Error = "Correo o contraseña incorrectos." },
            statusCode: StatusCodes.Status401Unauthorized);
    }

    LimiteIntentos.Limpiar(correo);

    var token = SesionesBd.Crear(usuario.Id);

    return Results.Ok(new
    {
        Id = usuario.Id,
        Nombre = usuario.Nombre,
        Apellido = usuario.Apellido,
        Usuario = usuario.NombreUsuario,
        Correo = usuario.Correo,
        Token = token
    });
});

/* .../api/logout — invalida el token de la sesión actual */
app.MapPost("/api/logout", (HttpContext contexto) =>
{
    var token = TokenDe(contexto);

    if (!string.IsNullOrWhiteSpace(token))
    {
        SesionesBd.Eliminar(token);
    }

    return Results.Ok(new { Mensaje = "Sesión cerrada." });
});

/* .../api/usuarios — contacto para iniciar una conversación privada */
app.MapGet("/api/usuarios", (HttpContext contexto) =>
{
    var usuario = UsuarioAutenticado(contexto);

    if (usuario is null)
    {
        return SinSesion();
    }

    var lista = UsuariosBd.Listar(usuario.Id);

    return Results.Ok(new
    {
        Usuarios = lista.Select(u => new
        {
            u.Id,
            Nombre = u.NombreCompleto,
            Usuario = u.NombreUsuario
        })
    });
});

/* .../api/conversaciones — privadas del usuario con su último mensaje */
app.MapGet("/api/conversaciones", (HttpContext contexto) =>
{
    var usuario = UsuarioAutenticado(contexto);

    if (usuario is null)
    {
        return SinSesion();
    }

    var conversaciones =
        MensajesBd.ObtenerConversaciones(usuario.Id);

    var resultado = new List<object>();

    foreach (var conversacion in conversaciones)
    {
        var otro = UsuariosBd.BuscarPorId(
            conversacion.OtroUsuarioId);

        resultado.Add(new
        {
            conversacion.ChatId,
            OtroUsuarioId = conversacion.OtroUsuarioId,
            OtroNombre = otro?.NombreCompleto ?? "Usuario",
            OtroUsuario = otro?.NombreUsuario ?? "",
            conversacion.UltimoMensaje,
            UltimaFecha = conversacion.UltimaFecha.ToString("o"),
            conversacion.UltimoUsuarioId
        });
    }

    return Results.Ok(new { Conversaciones = resultado });
});

/* .../api/mensajes?chatId=amigos — historial guardado de un canal.
   En conversaciones privadas solo puede leer un participante. */
app.MapGet("/api/mensajes",
    (HttpContext contexto, string? chatId, int? limite) =>
{
    var usuario = UsuarioAutenticado(contexto);

    if (usuario is null)
    {
        return SinSesion();
    }

    if (string.IsNullOrWhiteSpace(chatId))
    {
        return Results.BadRequest(new { Error = "Falta el chatId." });
    }

    var chat = chatId.Trim();

    if (!MensajesBd.EsParticipante(chat, usuario.Id))
    {
        return Results.Json(
            new { Error = "No perteneces a esa conversación." },
            statusCode: StatusCodes.Status403Forbidden);
    }

    var historial = MensajesBd.ObtenerPorChat(
        chat,
        limite ?? 100);

    return Results.Ok(new
    {
        ChatId = chat,
        Total = historial.Count,
        Mensajes = historial.Select(m => new
        {
            m.Id,
            m.Usuario,
            Texto = m.Texto,
            Hora = m.FechaEnvio.ToString("HH:mm:ss"),
            Fecha = m.FechaEnvio.ToString("o"),
            m.UsuarioId
        })
    });
});

/* .../api/cuenta (GET) — datos de la cuenta, con foto y banner. Las
   imágenes no viajan en el login ni en la sesión del navegador: se piden
   aquí al abrir las pantallas que las muestran. */
app.MapGet("/api/cuenta", (HttpContext contexto) =>
{
    var usuario = UsuarioAutenticado(contexto);

    if (usuario is null)
    {
        return SinSesion();
    }

    var (foto, banner) = UsuariosBd.ObtenerImagenes(usuario.Id);

    return Results.Ok(new
    {
        Nombre = usuario.Nombre,
        Apellido = usuario.Apellido,
        Usuario = usuario.NombreUsuario,
        Correo = usuario.Correo,
        FotoPerfil = foto,
        Banner = banner
    });
});

/* .../api/cuenta (PATCH) — actualiza la cuenta de la sesión en una sola petición.
   Campos de perfil (obligatorios). Opcionales:
   - ContrasenaActual + ContrasenaNueva: cambia la contraseña y cierra
     las demás sesiones del usuario.
   - FotoPerfil / Banner: data URL nueva; "" las quita; ausentes = sin cambio.
   Todo se valida antes de escribir y la escritura es una transacción. */
app.MapPatch("/api/cuenta",
    (HttpContext contexto, ActualizarCuentaRequest peticion) =>
{
    var actual = UsuarioAutenticado(contexto);

    if (actual is null)
    {
        return SinSesion();
    }

    var nombre =
        peticion.Nombre?.Trim() ?? "";

    var apellido =
        peticion.Apellido?.Trim() ?? "";

    var usuario =
        peticion.Usuario?.Trim() ?? "";

    var correo =
        (peticion.Correo?.Trim() ?? "").ToLowerInvariant();

    if (nombre == "" || apellido == "" ||
        usuario == "" || correo == "")
    {
        return Results.BadRequest(
            new { Error = "Completa todos los campos." });
    }

    if (correo.Length > 254 || !patronCorreo.IsMatch(correo))
    {
        return Results.BadRequest(
            new { Error = "El correo no tiene un formato válido." });
    }

    /* Contraseña: solo se evalúa si se escribió una nueva. */
    (string Hash, string Sal)? nuevaClave = null;

    var contrasenaNueva = peticion.ContrasenaNueva ?? "";

    if (contrasenaNueva != "")
    {
        var contrasenaActual = peticion.ContrasenaActual ?? "";

        if (contrasenaActual == "")
        {
            return Results.BadRequest(new { Error =
                "Escribe tu contraseña actual para cambiarla." });
        }

        var errorNueva = ValidarContrasena(contrasenaNueva);

        if (errorNueva is not null)
        {
            return Results.BadRequest(new { Error = errorNueva });
        }

        if (contrasenaActual.Length > MaximoContrasena ||
            !UsuariosBd.VerificarContrasena(
                contrasenaActual,
                actual.HashContrasena,
                actual.Sal))
        {
            return Results.Json(
                new { Error = "La contraseña actual es incorrecta." },
                statusCode: StatusCodes.Status401Unauthorized);
        }

        nuevaClave = UsuariosBd.CrearHashContrasena(contrasenaNueva);
    }

    var errorFoto = ValidarImagen(peticion.FotoPerfil, "La foto");

    if (errorFoto is not null)
    {
        return Results.BadRequest(new { Error = errorFoto });
    }

    var errorBanner = ValidarImagen(peticion.Banner, "El banner");

    if (errorBanner is not null)
    {
        return Results.BadRequest(new { Error = errorBanner });
    }

    if (UsuariosBd.CorreoOcupadoPorOtro(correo, actual.Id) ||
        !UsuariosBd.ActualizarCuenta(
            actual.Id,
            nombre,
            apellido,
            usuario,
            correo,
            nuevaClave,
            peticion.FotoPerfil,
            peticion.Banner,
            TokenDe(contexto) ?? ""))
    {
        return Results.Conflict(
            new { Error = "Ese correo ya pertenece a otra cuenta." });
    }

    return Results.Ok(new
    {
        Nombre = nombre,
        Apellido = apellido,
        Usuario = usuario,
        Correo = correo,
        ContrasenaCambiada = nuevaClave is not null
    });
});

app.Run();

public record RegistroRequest(
    string? Nombre,
    string? Apellido,
    string? Usuario,
    string? Correo,
    string? Contrasena);

public record LoginRequest(
    string? Correo,
    string? Contrasena);

public record ActualizarCuentaRequest(
    string? Nombre,
    string? Apellido,
    string? Usuario,
    string? Correo,
    string? ContrasenaActual,
    string? ContrasenaNueva,
    string? FotoPerfil,
    string? Banner);
