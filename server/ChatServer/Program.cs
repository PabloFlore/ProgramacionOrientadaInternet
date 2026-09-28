using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
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
   cambiado ( fue lo que dejó el botón de alias y el nombre "Tyler Joseph"
   apareciendo en pantalla ). Se exceptúa /hubs para no tocar el handshake
   de SignalR. */
app.Use(async (contexto, siguiente) =>
{
    if (!contexto.Request.Path.StartsWithSegments("/hubs"))
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

/* ======================= REGISTRO Y LOGIN (SQLite) ======================= */

var patronCorreo =
    new Regex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

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
        peticion.Correo?.Trim() ?? "";
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

    if (contrasena.Length < 8 ||
        !Regex.IsMatch(contrasena, "[0-9]") ||
        !Regex.IsMatch(contrasena, "[A-Z]") ||
        !Regex.IsMatch(contrasena, "[^A-Za-z0-9]"))
    {
        return Results.BadRequest(
            new { Error =
                "La contraseña debe tener al menos 8 caracteres, " +
                "una mayúscula, un número y un símbolo." });
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

/* .../api/login — entra con correo + contraseña */
app.MapPost("/api/login", (LoginRequest peticion) =>
{
    var correo =
        peticion.Correo?.Trim() ?? "";
    var contrasena =
        peticion.Contrasena ?? "";

    if (correo == "" || contrasena == "")
    {
        return Results.BadRequest(
            new { Error = "Escribe tu correo y tu contraseña." });
    }

    var usuario = UsuariosBd.BuscarPorCorreo(correo);

    if (usuario is null ||
        !UsuariosBd.VerificarContrasena(
            contrasena,
            usuario.HashContrasena,
            usuario.Sal))
    {
        return Results.Json(
            new { Error = "Correo o contraseña incorrectos." },
            statusCode: StatusCodes.Status401Unauthorized);
    }

    return Results.Ok(new
    {
        Nombre = usuario.Nombre,
        Apellido = usuario.Apellido,
        Usuario = usuario.NombreUsuario,
        Correo = usuario.Correo
    });
});

/* .../api/mensajes?chatId=amigos — historial guardado de un canal */
app.MapGet("/api/mensajes", (string? chatId, int? limite) =>
{
    if (string.IsNullOrWhiteSpace(chatId))
    {
        return Results.BadRequest(
            new { Error = "Falta el chatId." });
    }

    var historial = MensajesBd.ObtenerPorChat(
        chatId.Trim(),
        limite ?? 100);

    return Results.Ok(new
    {
        ChatId = chatId.Trim(),
        Total = historial.Count,
        Mensajes = historial.Select(m => new
        {
            m.Id,
            m.Usuario,
            Texto = m.Texto,
            Hora = m.FechaEnvio.ToString("HH:mm:ss"),
            Fecha = m.FechaEnvio.ToString("o")
        })
    });
});

/* .../api/cuenta — actualiza el perfil (CambiarCuenta.html).
   La cuenta se localiza por CorreoActual (el de la sesión), porque
   el propio formulario puede estar cambiando ese mismo campo. */
app.MapPatch("/api/cuenta", (ActualizarPerfilRequest peticion) =>
{
    var correoActual =
        peticion.CorreoActual?.Trim() ?? "";

    var correo =
        peticion.Correo?.Trim() ?? "";

    var nombre =
        peticion.Nombre?.Trim() ?? "";

    var apellido =
        peticion.Apellido?.Trim() ?? "";

    var usuario =
        peticion.Usuario?.Trim() ?? "";

    if (correoActual == "" || nombre == "" ||
        apellido == "" || usuario == "" || correo == "")
    {
        return Results.BadRequest(
            new { Error = "Completa todos los campos." });
    }

    if (!patronCorreo.IsMatch(correo))
    {
        return Results.BadRequest(
            new { Error = "El correo no tiene un formato válido." });
    }

    var actual = UsuariosBd.BuscarPorCorreo(correoActual);

    if (actual is null)
    {
        return Results.NotFound(
            new { Error = "No existe una cuenta con ese correo." });
    }

    if (!UsuariosBd.ActualizarPerfil(
        actual.Id, nombre, apellido, usuario, correo))
    {
        return Results.Conflict(
            new { Error = "Ese correo ya pertenece a otra cuenta." });
    }

    return Results.Ok(new
    {
        Nombre = nombre,
        Apellido = apellido,
        Usuario = usuario,
        Correo = correo
    });
});

/* .../api/cuenta/contrasena — exige la contraseña actual */
app.MapPatch("/api/cuenta/contrasena",
    (CambiarContrasenaRequest peticion) =>
{
    var correo =
        peticion.Correo?.Trim() ?? "";

    var actual = peticion.ContrasenaActual ?? "";

    var nueva = peticion.ContrasenaNueva ?? "";

    if (correo == "" || actual == "" || nueva == "")
    {
        return Results.BadRequest(
            new { Error = "Completa todos los campos." });
    }

    if (nueva.Length < 8 ||
        !Regex.IsMatch(nueva, "[0-9]") ||
        !Regex.IsMatch(nueva, "[A-Z]") ||
        !Regex.IsMatch(nueva, "[^A-Za-z0-9]"))
    {
        return Results.BadRequest(
            new { Error =
                "La contraseña debe tener al menos 8 caracteres, " +
                "una mayúscula, un número y un símbolo." });
    }

    var usuario = UsuariosBd.BuscarPorCorreo(correo);

    if (usuario is null ||
        !UsuariosBd.VerificarContrasena(
            actual,
            usuario.HashContrasena,
            usuario.Sal))
    {
        return Results.Json(
            new { Error = "La contraseña actual es incorrecta." },
            statusCode: StatusCodes.Status401Unauthorized);
    }

    var (hash, sal) =
        UsuariosBd.CrearHashContrasena(nueva);

    if (!UsuariosBd.ActualizarContrasena(usuario.Id, hash, sal))
    {
        return Results.Json(
            new { Error = "No se pudo actualizar la contraseña." },
            statusCode: StatusCodes.Status500InternalServerError);
    }

    return Results.Ok(new { Mensaje = "Contraseña actualizada." });
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

public record ActualizarPerfilRequest(
    string? CorreoActual,
    string? Nombre,
    string? Apellido,
    string? Usuario,
    string? Correo);

public record CambiarContrasenaRequest(
    string? Correo,
    string? ContrasenaActual,
    string? ContrasenaNueva);
