using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.FileProviders;
using ChatServer;
using ChatServer.Hubs;
using ChatServer.Datos;
using ChatServer.Modelos;

var builder = WebApplication.CreateBuilder(args);

/* SignalR JS no puede mandar cabeceras en el WebSocket y envía el token en
   ?access_token=…; se admite esa forma solo en /chat. "Bearer" tiene
   prioridad y esta categoría se calla para que la URL (con el token) no
   quede en los logs del servidor. */
builder.Logging.AddFilter(
    "Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);

/* HTTPS en todas las interfaces. Con HTTP el navegador de otro dispositivo
   no da acceso a cámara ni micrófono (solo localhost y HTTPS son contextos
   seguros). El certificado lo genera tools/generar_certificados.ps1. */
var rutaCertificado = ChatServer.CertificadoHttps.Ruta();
builder.WebHost.ConfigureKestrel(opciones =>
{
    opciones.ListenAnyIP(5000, escucha => escucha.UseHttps(rutaCertificado));
});

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

/* Los objetos del hub (LlamadaInfo) usan los mismos nombres que la API. */
builder.Services.AddSignalR().AddJsonProtocol(opciones =>
{
    opciones.PayloadSerializerOptions.PropertyNamingPolicy = null;
});
builder.Services.AddSingleton<CorreoServicio>();
builder.Services.AddSingleton<ChatServer.Llamadas.LlamadasServicio>();

/* Cloudinary se consume por HTTP (preset unsigned): no hay SDK ni API
   Secret en el servidor. El timeout evita dejar la petición colgada si el
   proveedor no responde. */
builder.Services.AddHttpClient<AlmacenCloudinary>(cliente =>
{
    cliente.Timeout = TimeSpan.FromSeconds(30);
});

/* El formulario de subida se corta antes de leer el archivo completo.
   El límite real (2 MB) lo aplica ArchivosServicio; este margen deja
   espacio para las demás partes del multipart. */
builder.Services.Configure<FormOptions>(opciones =>
{
    opciones.MultipartBodyLengthLimit = 3L * 1024 * 1024;
});

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
MensajesBd.Inicializar();
GruposBd.Inicializar();
SesionesBd.LimpiarVencidas();
TareasBd.SincronizarTodo();
RecompensasBd.AsegurarPlanes();

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

/* Valida una clave pública ECDH P-256 en formato SPKI/base64. El servidor
   nunca deriva la clave del chat: solo comprueba que el texto publicado
   es una clave EC válida de 256 bits. */
bool EsClavePublicaValida(string? clave)
{
    if (string.IsNullOrWhiteSpace(clave) || clave.Length > 512)
    {
        return false;
    }

    try
    {
        var bytes = Convert.FromBase64String(clave);

        using var ecdh = ECDiffieHellman.Create();
        ecdh.ImportSubjectPublicKeyInfo(bytes, out _);

        return ecdh.KeySize == 256;
    }
    catch (Exception)
    {
        return false;
    }
}

/* ======================= REGISTRO Y LOGIN (SQLite) ======================= */

/* Límites de tamaño. La contraseña se limita a 128 caracteres; las
   imágenes son data URL (texto) de como máximo 700 000 caracteres. */
const int MaximoContrasena = 128;
const int MaximoImagen = 700_000;
const int MaximoAsuntoCorreo = 200;
const int MaximoMensajeCorreo = 5_000;
const int MaximoTituloTarea = 120;
const int MaximoDescripcionTarea = 500;

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
app.MapPost("/api/logout", async (HttpContext contexto, IHubContext<ChatHub> hub) =>
{
    var token = TokenDe(contexto);

    if (!string.IsNullOrWhiteSpace(token))
    {
        SesionesBd.Eliminar(token);
        await ChatHub.NotificarRevocacion(hub, token);
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
            Usuario = u.NombreUsuario,
            Correo = u.Correo
        })
    });
});

/* .../api/cuenta/clave (PUT) — publica la clave pública ECDH del
   dispositivo actual. Cada navegador tiene su propio par de claves. */
app.MapPut("/api/cuenta/clave",
    (HttpContext contexto, ClavePublicaRequest peticion) =>
{
    var usuario = UsuarioAutenticado(contexto);

    if (usuario is null)
    {
        return SinSesion();
    }

    var clave = peticion.ClavePublica?.Trim() ?? "";

    if (!EsClavePublicaValida(clave))
    {
        return Results.BadRequest(
            new { Error = "La clave pública no es válida." });
    }

    UsuariosBd.GuardarClavePublica(usuario.Id, clave);

    return Results.Ok(new { Mensaje = "Clave pública guardada." });
});

/* .../api/usuarios/{id}/clave (GET) — clave pública del otro extremo
   para derivar la clave AES del chat. Es información pública. */
app.MapGet("/api/usuarios/{id:int}/clave", (HttpContext contexto, int id) =>
{
    var usuario = UsuarioAutenticado(contexto);

    if (usuario is null)
    {
        return SinSesion();
    }

    var clave = UsuariosBd.ObtenerClavePublica(id);

    return Results.Ok(new
    {
        UsuarioId = id,
        ClavePublica = clave
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
            conversacion.UltimoUsuarioId,
            conversacion.UltimoCifrado,
            conversacion.UltimoTipo
        });
    }

    return Results.Ok(new { Conversaciones = resultado });
});

/* .../api/presencia?ids=1,2,3 — estado online/offline por usuario */
app.MapGet("/api/presencia", (HttpContext contexto, string? ids) =>
{
    var usuario = UsuarioAutenticado(contexto);

    if (usuario is null)
    {
        return SinSesion();
    }

    var resultado = new Dictionary<string, bool>();

    if (string.IsNullOrWhiteSpace(ids))
    {
        return Results.Ok(resultado);
    }

    var partes = ids.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    foreach (var parte in partes)
    {
        if (int.TryParse(parte, out var id))
        {
            resultado[id.ToString()] = ChatHub.EstaConectado(id);
        }
    }

    return Results.Ok(resultado);
});

/* .../api/mensajes?chatId=amigos — historial guardado de un canal.
   En conversaciones privadas solo puede leer un participante. */
app.MapGet("/api/mensajes",
    (HttpContext contexto, string? chatId, int? limite, long? antesDe) =>
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

    if (!MensajesBd.EsChatValido(chat))
    {
        return Results.BadRequest(new { Error = "El chatId no es válido." });
    }

    if (!MensajesBd.PuedeLeerHistorial(chat, usuario.Id))
    {
        return Results.Json(
            new { Error = "No perteneces a esa conversación." },
            statusCode: StatusCodes.Status403Forbidden);
    }

    var limiteEfectivo = limite ?? 30;
    if (limiteEfectivo > 50) limiteEfectivo = 50;
    if (limiteEfectivo < 1) limiteEfectivo = 30;

    var filas = MensajesBd.ObtenerPorChat(
        chat,
        usuario.Id,
        limiteEfectivo + 1,
        antesDe);

    var tieneMas = filas.Count > limiteEfectivo;
    var historial = tieneMas
        ? filas.Take(limiteEfectivo).ToList()
        : filas;
    var primeroId = historial.Count > 0 ? historial[0].Id : (long?)null;

    return Results.Ok(new
    {
        ChatId = chat,
        Total = historial.Count,
        TieneMas = tieneMas,
        PrimerId = primeroId,
        Mensajes = historial.Select(m => new
        {
            m.Id,
            m.Usuario,
            Texto = m.Texto,
            Hora = m.FechaEnvio.ToString("HH:mm"),
            Fecha = m.FechaEnvio.ToString("o"),
            m.UsuarioId,
            m.Tipo,
            m.Latitud,
            m.Longitud,
            m.Etiqueta,
            m.ArchivoUrl,
            m.ArchivoNombre,
            m.ArchivoMime,
            m.ArchivoTamano,
            m.ArchivoPublicId,
            m.Cifrado,
            m.Iv,
            m.ClavePublica,
            m.ClavePublicaDestino
        })
    });
});

/* .../api/mensajes?chatId=dm:1:2 (DELETE) — borra el historial de un
   chat solo para el usuario de la sesión. Los demás participantes
   conservan sus mensajes. */
app.MapDelete("/api/mensajes", (HttpContext contexto, string? chatId) =>
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

    if (!MensajesBd.EsChatValido(chat))
    {
        return Results.BadRequest(new { Error = "El chatId no es válido." });
    }

    if (!MensajesBd.PuedeLeerHistorial(chat, usuario.Id))
    {
        return Results.Json(
            new { Error = "No perteneces a esa conversación." },
            statusCode: StatusCodes.Status403Forbidden);
    }

    var borradoHasta = MensajesBd.BorrarParaUsuario(chat, usuario.Id);

    return Results.Ok(new
    {
        ChatId = chat,
        BorradoHasta = borradoHasta
    });
});

/* ======================= ARCHIVOS ADJUNTOS ======================= */

/* .../api/archivos (POST) — sube un archivo de texto o una imagen a
   Cloudinary y lo guarda como mensaje del canal. El formulario se lee a
   mano para no depender de la validación antiforgery (la sesión viaja en
   Authorization: Bearer, no en cookies). */
app.MapPost("/api/archivos",
    async (HttpContext contexto, AlmacenCloudinary almacen,
        IHubContext<ChatHub> hub, ILogger<Program> registro) =>
{
    var usuario = UsuarioAutenticado(contexto);

    if (usuario is null)
    {
        return SinSesion();
    }

    try
    {
        almacen.ValidarConfiguracion();
    }
    catch (AlmacenNoConfiguradoException ex)
    {
        return Results.Json(
            new { Error = ex.Message },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    if (!contexto.Request.HasFormContentType)
    {
        return Results.BadRequest(new
        {
            Error = "La petición debe ser multipart/form-data."
        });
    }

    IFormCollection formulario;

    try
    {
        formulario = await contexto.Request.ReadFormAsync(
            contexto.RequestAborted);
    }
    catch (Exception ex) when (
        ex is InvalidDataException or BadHttpRequestException)
    {
        return Results.Json(
            new { Error = "El formulario supera el máximo permitido." },
            statusCode: StatusCodes.Status413PayloadTooLarge);
    }

    var chatId = (formulario["chatId"].ToString() ?? "").Trim();

    if (!MensajesBd.EsChatValido(chatId))
    {
        return Results.BadRequest(new { Error = "El chatId no es válido." });
    }

    if (!MensajesBd.EsParticipante(chatId, usuario.Id))
    {
        return Results.Json(
            new { Error = "No perteneces a esa conversación." },
            statusCode: StatusCodes.Status403Forbidden);
    }

    var archivo = formulario.Files.GetFile("archivo");

    if (archivo is null || archivo.Length == 0)
    {
        return Results.BadRequest(new { Error = "Falta el archivo." });
    }

    ArchivoValidado validado;

    try
    {
        await using var flujo = archivo.OpenReadStream();
        var contenido = await ArchivosServicio.LeerAsync(
            flujo,
            contexto.RequestAborted);
        validado = ArchivosServicio.Validar(archivo.FileName, contenido);
    }
    catch (ArchivoInvalidoException ex)
    {
        return Results.Json(
            new { Error = ex.Message },
            statusCode: ex.Codigo);
    }
    catch (Exception ex) when (
        ex is InvalidDataException or BadHttpRequestException)
    {
        return Results.Json(
            new { Error = "El archivo supera el máximo de 2 MB." },
            statusCode: StatusCodes.Status413PayloadTooLarge);
    }

    ArchivoSubido subido;

    try
    {
        subido = await almacen.SubirAsync(
            validado.Contenido,
            validado.Nombre,
            validado.Mime,
            validado.EsImagen,
            contexto.RequestAborted);
    }
    catch (AlmacenNoConfiguradoException ex)
    {
        return Results.Json(
            new { Error = ex.Message },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (AlmacenSubidaException)
    {
        return Results.Json(
            new
            {
                Error = "No se pudo subir el archivo al almacenamiento. " +
                    "Inténtalo de nuevo."
            },
            statusCode: StatusCodes.Status502BadGateway);
    }
    catch (OperationCanceledException)
    {
        return Results.StatusCode(499);
    }

    Mensaje guardado;

    try
    {
        guardado = MensajesBd.RegistrarArchivo(
            chatId,
            usuario.Id,
            usuario.NombreCompleto,
            subido.Url,
            validado.Nombre,
            validado.Mime,
            validado.Tamano,
            subido.PublicId);
    }
    catch (Exception ex)
    {
        registro.LogError(
            ex,
            "No se pudo guardar el adjunto de {Usuario} en {ChatId}.",
            usuario.Id,
            chatId);

        return Results.Json(
            new { Error = "No se pudo guardar el archivo adjunto." },
            statusCode: StatusCodes.Status500InternalServerError);
    }

    var hora = guardado.FechaEnvio.ToLocalTime().ToString("HH:mm");
    var tempId = (formulario["tempId"].ToString() ?? "").Trim();

    if (tempId.Length > 100)
    {
        tempId = "";
    }

    await hub.Clients.Group(chatId).SendCoreAsync(
        "ReceiveArchivo",
        new object?[]
        {
            guardado.Usuario,
            chatId,
            guardado.UsuarioId,
            guardado.FechaEnvio.ToString("o"),
            guardado.Id,
            guardado.ArchivoUrl,
            guardado.ArchivoNombre,
            guardado.ArchivoMime,
            guardado.ArchivoTamano,
            hora,
            string.IsNullOrWhiteSpace(tempId) ? null : tempId
        });

    return Results.Ok(new
    {
        guardado.Id,
        guardado.ChatId,
        guardado.Usuario,
        Texto = "",
        Hora = hora,
        Fecha = guardado.FechaEnvio.ToString("o"),
        guardado.UsuarioId,
        guardado.Tipo,
        guardado.ArchivoUrl,
        guardado.ArchivoNombre,
        guardado.ArchivoMime,
        guardado.ArchivoTamano,
        guardado.ArchivoPublicId
    });
}).DisableAntiforgery();

/* .../api/archivos/{id} — descarga autenticada de un adjunto. La URL de
   Cloudinary nunca se entrega a quien no puede leer el chat. */
app.MapGet("/api/archivos/{id:long}",
    async (HttpContext contexto, long id, AlmacenCloudinary almacen,
        bool? descargar) =>
{
    var usuario = UsuarioAutenticado(contexto);

    if (usuario is null)
    {
        return SinSesion();
    }

    var mensaje = MensajesBd.ObtenerPorId(id);

    if (mensaje is null ||
        mensaje.Tipo != "archivo" ||
        string.IsNullOrWhiteSpace(mensaje.ArchivoUrl))
    {
        return Results.NotFound(new { Error = "El archivo no existe." });
    }

    if (!MensajesBd.PuedeLeerHistorial(mensaje.ChatId, usuario.Id))
    {
        return Results.Json(
            new { Error = "No perteneces a esa conversación." },
            statusCode: StatusCodes.Status403Forbidden);
    }

    byte[] contenido;

    try
    {
        contenido = await almacen.DescargarAsync(
            mensaje.ArchivoUrl,
            contexto.RequestAborted);
    }
    catch (AlmacenSubidaException)
    {
        return Results.Json(
            new { Error = "No se pudo descargar el archivo almacenado." },
            statusCode: StatusCodes.Status502BadGateway);
    }
    catch (OperationCanceledException)
    {
        return Results.StatusCode(499);
    }

    contexto.Response.Headers.XContentTypeOptions = "nosniff";

    var mime = string.IsNullOrWhiteSpace(mensaje.ArchivoMime)
        ? "application/octet-stream"
        : mensaje.ArchivoMime;
    var nombre = string.IsNullOrWhiteSpace(mensaje.ArchivoNombre)
        ? "archivo"
        : mensaje.ArchivoNombre;

    return descargar == true
        ? Results.File(contenido, mime, nombre)
        : Results.File(contenido, mime);
});

/* .../api/archivos/{id}/texto — vista previa de un adjunto de texto.
   Se sirve como JSON para poder avisar cuando se recorta. */
app.MapGet("/api/archivos/{id:long}/texto",
    async (HttpContext contexto, long id, AlmacenCloudinary almacen) =>
{
    var usuario = UsuarioAutenticado(contexto);

    if (usuario is null)
    {
        return SinSesion();
    }

    var mensaje = MensajesBd.ObtenerPorId(id);

    if (mensaje is null ||
        mensaje.Tipo != "archivo" ||
        string.IsNullOrWhiteSpace(mensaje.ArchivoUrl))
    {
        return Results.NotFound(new { Error = "El archivo no existe." });
    }

    if (!MensajesBd.PuedeLeerHistorial(mensaje.ChatId, usuario.Id))
    {
        return Results.Json(
            new { Error = "No perteneces a esa conversación." },
            statusCode: StatusCodes.Status403Forbidden);
    }

    if (!string.IsNullOrWhiteSpace(mensaje.ArchivoMime) &&
        mensaje.ArchivoMime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
    {
        return Results.Json(
            new { Error = "Ese adjunto no es un archivo de texto." },
            statusCode: StatusCodes.Status415UnsupportedMediaType);
    }

    byte[] contenido;

    try
    {
        contenido = await almacen.DescargarAsync(
            mensaje.ArchivoUrl,
            contexto.RequestAborted);
    }
    catch (AlmacenSubidaException)
    {
        return Results.Json(
            new { Error = "No se pudo descargar el archivo almacenado." },
            statusCode: StatusCodes.Status502BadGateway);
    }
    catch (OperationCanceledException)
    {
        return Results.StatusCode(499);
    }

    string texto;

    try
    {
        texto = new UTF8Encoding(false, true).GetString(contenido);
    }
    catch (DecoderFallbackException)
    {
        return Results.Json(
            new { Error = "El archivo no es texto UTF-8 válido." },
            statusCode: StatusCodes.Status415UnsupportedMediaType);
    }

    const int maximoVistaPrevia = 20_000;
    var truncado = texto.Length > maximoVistaPrevia;

    return Results.Ok(new
    {
        Texto = truncado ? texto[..maximoVistaPrevia] : texto,
        Truncado = truncado
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
    async (HttpContext contexto, ActualizarCuentaRequest peticion,
           IHubContext<ChatHub> hub) =>
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

    if (UsuariosBd.CorreoOcupadoPorOtro(correo, actual.Id))
    {
        return Results.Conflict(
            new { Error = "Ese correo ya pertenece a otra cuenta." });
    }

    if (!UsuariosBd.ActualizarCuenta(
            actual.Id,
            nombre,
            apellido,
            usuario,
            correo,
            nuevaClave,
            peticion.FotoPerfil,
            peticion.Banner,
            TokenDe(contexto) ?? "",
            out var sesionesRevocadas))
    {
        return Results.Conflict(
            new { Error = "Ese correo ya pertenece a otra cuenta." });
    }

    foreach (var tokenRevocado in sesionesRevocadas)
    {
        await ChatHub.NotificarRevocacion(hub, tokenRevocado);
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

/* ======================= GRUPOS ======================= */

(string Enlace, string? Error) NormalizarEnlaceRecompensa(string? enlace)
{
    var valor = (enlace ?? "").Trim();

    if (valor.Length > 500)
    {
        return (valor, "El enlace no puede tener más de 500 caracteres.");
    }

    if (valor != "" &&
        (!Uri.TryCreate(valor, UriKind.Absolute, out var uri) ||
         (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)))
    {
        return (valor, "El enlace debe empezar por http:// o https://.");
    }

    return (valor, null);
}

/* .../api/grupos (POST) — crea un grupo. Cuenta al creador: un grupo
   necesita al menos GruposBd.MinimoIntegrantes integrantes en total. */
app.MapPost("/api/grupos", (HttpContext contexto, GrupoRequest peticion) =>
{
    var creador = UsuarioAutenticado(contexto);

    if (creador is null)
    {
        return SinSesion();
    }

    var nombre = (peticion.Nombre ?? "").Trim();

    if (nombre == "")
    {
        return Results.BadRequest(
            new { Error = "Escribe un nombre para el grupo." });
    }

    if (nombre.Length > GruposBd.MaximoNombre)
    {
        return Results.BadRequest(new { Error =
            $"El nombre no puede tener más de {GruposBd.MaximoNombre} caracteres." });
    }

    var otros = (peticion.Miembros ?? new List<int>())
        .Where(id => id != creador.Id)
        .Distinct()
        .ToList();

    var total = otros.Count + 1;

    if (total < GruposBd.MinimoIntegrantes)
    {
        return Results.BadRequest(new { Error =
            $"Un grupo necesita al menos {GruposBd.MinimoIntegrantes} " +
            "integrantes, contándote a ti." });
    }

    if (total > GruposBd.MaximoIntegrantes)
    {
        return Results.BadRequest(new { Error =
            $"Un grupo puede tener como máximo {GruposBd.MaximoIntegrantes} integrantes." });
    }

    foreach (var otroId in otros)
    {
        if (UsuariosBd.BuscarPorId(otroId) is null)
        {
            return Results.BadRequest(
                new { Error = "Hay integrantes que no existen." });
        }
    }

    var (enlace, errorEnlace) = NormalizarEnlaceRecompensa(peticion.Enlace);

    if (errorEnlace is not null)
    {
        return Results.BadRequest(new { Error = errorEnlace });
    }

    var grupoId = GruposBd.Crear(nombre, creador.Id, otros, enlace);

    if (enlace != "")
    {
        TareasBd.SincronizarGrupo(grupoId);
        RecompensasBd.AsegurarPlan(grupoId);
    }

    return Results.Created($"/api/grupos/{grupoId}", new
    {
        Id = grupoId,
        Nombre = nombre,
        ChatId = $"grupo:{grupoId}",
        Integrantes = total,
        Enlace = enlace,
        RetosDisponibles = enlace != ""
    });
});

/* .../api/grupos (GET) — grupos en los que participa la sesión. */
app.MapGet("/api/grupos", (HttpContext contexto) =>
{
    var usuario = UsuarioAutenticado(contexto);

    if (usuario is null)
    {
        return SinSesion();
    }

    var grupos = GruposBd.ListarDeUsuario(usuario.Id)
        .Select(g => new
        {
            Id = g.Id,
            Nombre = g.Nombre,
            ChatId = $"grupo:{g.Id}",
            Integrantes = g.Integrantes,
            Activo = g.Activo,
            g.UltimoMensaje,
            g.UltimaFecha,
            g.UltimoTipo,
            g.UltimoUsuario
        });

    return Results.Ok(new { Grupos = grupos });
});

/* .../api/grupos/{id} (GET) — detalle y lista de integrantes.
   Solo para integrantes: a otros se les responde 403. */
app.MapGet("/api/grupos/{id:int}", (HttpContext contexto, int id) =>
{
    var usuario = UsuarioAutenticado(contexto);

    if (usuario is null)
    {
        return SinSesion();
    }

    var grupo = GruposBd.Obtener(id);

    if (grupo is null)
    {
        return Results.NotFound(new { Error = "El grupo no existe." });
    }

    if (!GruposBd.EsMiembro(id, usuario.Id))
    {
        return Results.Json(
            new { Error = "No perteneces a ese grupo." },
            statusCode: StatusCodes.Status403Forbidden);
    }

    var integrantes = new List<object>();

    foreach (var integranteId in GruposBd.IdsMiembros(id))
    {
        var integrante = UsuariosBd.BuscarPorId(integranteId);

        if (integrante is not null)
        {
            integrantes.Add(new
            {
                Id = integrante.Id,
                Nombre = integrante.NombreCompleto,
                Usuario = integrante.NombreUsuario
            });
        }
    }

    return Results.Ok(new
    {
        Id = grupo.Value.Id,
        Nombre = grupo.Value.Nombre,
        ChatId = $"grupo:{grupo.Value.Id}",
        CreadorId = grupo.Value.CreadorId,
        EnlaceRecompensa = GruposBd.ObtenerEnlaceRecompensa(id),
        Integrantes = integrantes
    });
});

/* Avisos por SignalR tras cambiar integrantes. Todos los integrantes
   actuales reciben GrupoActualizado (para refrescar su lista y el panel).
   Quien sale o es quitado se saca del grupo en todas sus pestañas y
   recibe QuitadoDeGrupo. */
async Task AvisarGrupoActualizado(IHubContext<ChatHub> hub, int grupoId)
{
    var chatId = $"grupo:{grupoId}";

    foreach (var miembro in GruposBd.IdsMiembros(grupoId))
    {
        foreach (var conexion in ChatHub.ConexionesDe(miembro))
        {
            await hub.Clients.Client(conexion)
                .SendAsync("GrupoActualizado", chatId);
        }
    }
}

async Task SacarDelGrupo(IHubContext<ChatHub> hub, int usuarioId, int grupoId)
{
    var chatId = $"grupo:{grupoId}";

    foreach (var conexion in ChatHub.ConexionesDe(usuarioId))
    {
        await hub.Groups.RemoveFromGroupAsync(conexion, chatId);
        await hub.Clients.Client(conexion)
            .SendAsync("QuitadoDeGrupo", chatId);
    }
}

/* .../api/grupos/{id}/miembros (POST) — agrega integrantes. Lo puede hacer
   cualquier integrante del grupo. */
app.MapPost("/api/grupos/{id:int}/miembros",
    async (HttpContext contexto, int id, AgregarMiembrosRequest peticion,
           IHubContext<ChatHub> hub) =>
{
    var usuario = UsuarioAutenticado(contexto);

    if (usuario is null)
    {
        return SinSesion();
    }

    if (GruposBd.Obtener(id) is null)
    {
        return Results.NotFound(new { Error = "El grupo no existe." });
    }

    if (!GruposBd.EsMiembro(id, usuario.Id))
    {
        return Results.Json(
            new { Error = "Solo los integrantes pueden agregar a otros." },
            statusCode: StatusCodes.Status403Forbidden);
    }

    var nuevos = (peticion.Miembros ?? new List<int>()).Distinct().ToList();

    if (nuevos.Count == 0)
    {
        return Results.BadRequest(
            new { Error = "Elige al menos un usuario para agregar." });
    }

    var actuales = GruposBd.IdsMiembros(id);

    if (nuevos.Any(n => actuales.Contains(n)))
    {
        return Results.BadRequest(
            new { Error = "Alguno de los usuarios ya es integrante." });
    }

    foreach (var nuevoId in nuevos)
    {
        if (UsuariosBd.BuscarPorId(nuevoId) is null)
        {
            return Results.BadRequest(
                new { Error = "Hay integrantes que no existen." });
        }
    }

    var total = actuales.Count + nuevos.Count;

    if (total > GruposBd.MaximoIntegrantes)
    {
        return Results.BadRequest(new { Error =
            $"Un grupo puede tener como máximo {GruposBd.MaximoIntegrantes} integrantes." });
    }

    GruposBd.AgregarMiembros(id, nuevos);

    if (!string.IsNullOrWhiteSpace(GruposBd.ObtenerEnlaceRecompensa(id)))
    {
        TareasBd.SincronizarGrupo(id);
    }

    await AvisarGrupoActualizado(hub, id);

    return Results.Ok(new
    {
        Id = id,
        ChatId = $"grupo:{id}",
        Integrantes = total
    });
});

/* .../api/grupos/{id}/miembros/{usuarioId} (DELETE) — quita a un integrante.
   - Quien no es creador solo puede salirse a sí mismo.
   - El creador puede quitar a otros o salirse.
   - Nunca puede quedar un grupo con menos de MinimoIntegrantes. */
app.MapDelete("/api/grupos/{id:int}/miembros/{usuarioId:int}",
    async (HttpContext contexto, int id, int usuarioId,
           IHubContext<ChatHub> hub) =>
{
    var usuario = UsuarioAutenticado(contexto);

    if (usuario is null)
    {
        return SinSesion();
    }

    var grupo = GruposBd.Obtener(id);

    if (grupo is null)
    {
        return Results.NotFound(new { Error = "El grupo no existe." });
    }

    if (!GruposBd.EsMiembro(id, usuario.Id))
    {
        return Results.Json(
            new { Error = "No perteneces a ese grupo." },
            statusCode: StatusCodes.Status403Forbidden);
    }

    var esSalida = usuarioId == usuario.Id;

    if (!esSalida && grupo.Value.CreadorId != usuario.Id)
    {
        return Results.Json(
            new { Error = "Solo quien creó el grupo puede quitar a otros integrantes. " +
                          "Puedes salir tú mismo." },
            statusCode: StatusCodes.Status403Forbidden);
    }

    if (!GruposBd.EsMiembro(id, usuarioId))
    {
        return Results.NotFound(
            new { Error = "Ese usuario no es integrante del grupo." });
    }

    var total = GruposBd.IdsMiembros(id).Count;

    if (total - 1 < GruposBd.MinimoIntegrantes)
    {
        return Results.BadRequest(new { Error =
            $"El grupo necesita al menos {GruposBd.MinimoIntegrantes} integrantes. " +
            (esSalida
                ? "Agrega a alguien antes de salir."
                : "Agrega a alguien antes de quitar a otro.") });
    }

    GruposBd.QuitarMiembro(id, usuarioId);
    TareasBd.RemoverRetosPendientes(id, usuarioId);

    if (!string.IsNullOrWhiteSpace(GruposBd.ObtenerEnlaceRecompensa(id)))
    {
        TareasBd.SincronizarGrupo(id);
    }

    await SacarDelGrupo(hub, usuarioId, id);
    await AvisarGrupoActualizado(hub, id);

    return Results.Ok(new
    {
        Id = id,
        ChatId = $"grupo:{id}",
        Integrantes = total - 1,
        Salio = esSalida
    });
});

/* ======================= TAREAS Y RECOMPENSAS ======================= */

async Task AvisarTareaActualizada(
    IHubContext<ChatHub> hub,
    int grupoId,
    Tarea tarea)
{
    var chatId = $"grupo:{grupoId}";

    foreach (var miembro in GruposBd.IdsMiembros(grupoId))
    {
        foreach (var conexion in ChatHub.ConexionesDe(miembro))
        {
            await hub.Clients.Client(conexion)
                .SendAsync("TareaActualizada", chatId, tarea);
        }
    }
}

async Task AvisarRecompensa(
    IHubContext<ChatHub> hub,
    int usuarioId,
    Recompensa? recompensa)
{
    if (recompensa is null)
    {
        return;
    }

    foreach (var conexion in ChatHub.ConexionesDe(usuarioId))
    {
        await hub.Clients.Client(conexion)
            .SendAsync("RecompensaOtorgada", recompensa);
    }
}

/* .../api/grupos/{id}/tareas (POST) — solo el creador del grupo crea tareas. */
app.MapPost("/api/grupos/{id:int}/tareas",
    async (HttpContext contexto, int id, TareaRequest peticion,
           IHubContext<ChatHub> hub) =>
{
    var usuario = UsuarioAutenticado(contexto);

    if (usuario is null)
    {
        return SinSesion();
    }

    var grupo = GruposBd.Obtener(id);

    if (grupo is null)
    {
        return Results.NotFound(new { Error = "El grupo no existe." });
    }

    if (!GruposBd.EsMiembro(id, usuario.Id))
    {
        return Results.Json(
            new { Error = "No perteneces a ese grupo." },
            statusCode: StatusCodes.Status403Forbidden);
    }

    if (grupo.Value.CreadorId != usuario.Id)
    {
        return Results.Json(
            new { Error = "Solo el creador del grupo puede crear tareas." },
            statusCode: StatusCodes.Status403Forbidden);
    }

    var titulo = peticion.Titulo?.Trim() ?? "";
    var descripcion = peticion.Descripcion?.Trim() ?? "";

    if (titulo == "")
    {
        return Results.BadRequest(new { Error = "Escribe un título." });
    }

    if (titulo.Length > MaximoTituloTarea)
    {
        return Results.BadRequest(new { Error =
            $"El título no puede tener más de {MaximoTituloTarea} caracteres." });
    }

    if (descripcion.Length > MaximoDescripcionTarea)
    {
        return Results.BadRequest(new { Error =
            $"La descripción no puede tener más de {MaximoDescripcionTarea} caracteres." });
    }

    if (!peticion.AsignadoId.HasValue || peticion.AsignadoId.Value <= 0)
    {
        return Results.BadRequest(new { Error = "Elige a un integrante asignado." });
    }

    var asignadoId = peticion.AsignadoId.Value;

    if (!GruposBd.EsMiembro(id, asignadoId))
    {
        return Results.BadRequest(new { Error =
            "El asignado debe ser integrante del grupo." });
    }

    var tareaId = TareasBd.Crear(
        id, titulo, descripcion, usuario.Id, asignadoId);
    var tarea = TareasBd.Obtener(tareaId)!;

    await AvisarTareaActualizada(hub, id, tarea);

    return Results.Created($"/api/grupos/{id}/tareas/{tareaId}", tarea);
});

/* .../api/grupos/{id}/tareas (GET) — tareas del grupo para sus integrantes. */
app.MapGet("/api/grupos/{id:int}/tareas", (HttpContext contexto, int id) =>
{
    var usuario = UsuarioAutenticado(contexto);

    if (usuario is null)
    {
        return SinSesion();
    }

    var grupo = GruposBd.Obtener(id);

    if (grupo is null)
    {
        return Results.NotFound(new { Error = "El grupo no existe." });
    }

    if (!GruposBd.EsMiembro(id, usuario.Id))
    {
        return Results.Json(
            new { Error = "No perteneces a ese grupo." },
            statusCode: StatusCodes.Status403Forbidden);
    }

    var disponibles = !string.IsNullOrWhiteSpace(
        GruposBd.ObtenerEnlaceRecompensa(id));

    return Results.Ok(new
    {
        Disponibles = disponibles,
        Tareas = disponibles ? TareasBd.Listar(id) : new List<Tarea>()
    });
});

/* .../api/grupos/{id}/tareas/{tareaId}/finalizar — solo la persona asignada. */
app.MapPost("/api/grupos/{id:int}/tareas/{tareaId:int}/finalizar",
    async (HttpContext contexto, int id, int tareaId,
           TareaFinalizarRequest? peticion, IHubContext<ChatHub> hub) =>
{
    var usuario = UsuarioAutenticado(contexto);

    if (usuario is null)
    {
        return SinSesion();
    }

    var grupo = GruposBd.Obtener(id);

    if (grupo is null)
    {
        return Results.NotFound(new { Error = "El grupo no existe." });
    }

    if (!GruposBd.EsMiembro(id, usuario.Id))
    {
        return Results.Json(
            new { Error = "No perteneces a ese grupo." },
            statusCode: StatusCodes.Status403Forbidden);
    }

    if (string.IsNullOrWhiteSpace(GruposBd.ObtenerEnlaceRecompensa(id)))
    {
        return Results.BadRequest(new
        {
            Error = "Este grupo aún no tiene retos habilitados."
        });
    }

    var tarea = TareasBd.Obtener(tareaId);

    if (tarea is null || !TareasBd.EsTareaDelGrupo(tareaId, id))
    {
        return Results.NotFound(new { Error = "La tarea no existe en ese grupo." });
    }

    if (tarea.AsignadoId != usuario.Id)
    {
        return Results.Json(
            new { Error = "Solo la persona asignada puede finalizar la tarea." },
            statusCode: StatusCodes.Status403Forbidden);
    }

    if (tarea.Finalizada)
    {
        return Results.Conflict(
            new { Error = "Esta tarea ya está finalizada." });
    }

    long? evidencia = peticion?.EvidenciaMensajeId;

    if (tarea.CatalogoId > 0)
    {
        if (!evidencia.HasValue || evidencia.Value <= 0)
        {
            return Results.BadRequest(new
            {
                Error = "Escoge el mensaje que demuestra que hiciste la tarea."
            });
        }

        var mensaje = MensajesBd.ObtenerPorId(evidencia.Value);

        if (mensaje is null || mensaje.ChatId != $"grupo:{id}" ||
            mensaje.UsuarioId != usuario.Id ||
            mensaje.Tipo != tarea.TipoVerificacion)
        {
            return Results.BadRequest(new
            {
                Error = "El mensaje no demuestra esta tarea en este grupo."
            });
        }

        if (tarea.TipoVerificacion == "archivo" &&
            string.IsNullOrWhiteSpace(mensaje.ArchivoUrl))
        {
            return Results.BadRequest(new
            {
                Error = "El mensaje no tiene una imagen adjunta."
            });
        }

        if (tarea.TipoVerificacion == "ubicacion" &&
            (!mensaje.Latitud.HasValue || !mensaje.Longitud.HasValue))
        {
            return Results.BadRequest(new
            {
                Error = "El mensaje no tiene una ubicación."
            });
        }

        if (TareasBd.EvidenciaYaUsada(id, usuario.Id, evidencia.Value))
        {
            return Results.BadRequest(new
            {
                Error = "Ese mensaje ya se usó para otra tarea."
            });
        }
    }

    var (finalizada, otorgadas) = TareasBd.FinalizarYRecompensar(
        tareaId, id, usuario.Id, tarea.CatalogoId > 0, evidencia);

    if (!finalizada)
    {
        return Results.Conflict(
            new { Error = "Esta tarea ya está finalizada." });
    }

    var actualizada = TareasBd.Obtener(tareaId)!;

    await AvisarTareaActualizada(hub, id, actualizada);

    foreach (var recompensa in otorgadas.Where(r => r is not null))
    {
        await AvisarRecompensa(hub, usuario.Id, recompensa);
    }

    return Results.Ok(new
    {
        Tarea = actualizada,
        Recompensas = otorgadas.Where(r => r is not null).ToList(),
        Recompensa = otorgadas.FirstOrDefault(r => r is not null),
        Completadas = TareasBd.TareasAutomaticasCompletas(id, usuario.Id)
    });
});

/* .../api/usuarios/{id}/recompensas — colección visible por otros usuarios. */
app.MapGet("/api/usuarios/{id:int}/recompensas", (HttpContext contexto, int id) =>
{
    var usuario = UsuarioAutenticado(contexto);

    if (usuario is null)
    {
        return SinSesion();
    }

    var destino = UsuariosBd.BuscarPorId(id);

    if (destino is null)
    {
        return Results.NotFound(new { Error = "El usuario no existe." });
    }

    return Results.Ok(new
    {
        UsuarioId = destino.Id,
        Nombre = destino.NombreCompleto,
        Usuario = destino.NombreUsuario,
        Recompensas = RecompensasBd.DeUsuario(id)
    });
});

/* Configuración de las tres photocards del grupo y del enlace final. */
app.MapGet("/api/grupos/{id:int}/recompensas", (HttpContext contexto, int id) =>
{
    var usuario = UsuarioAutenticado(contexto);

    if (usuario is null)
    {
        return SinSesion();
    }

    var grupo = GruposBd.Obtener(id);

    if (grupo is null)
    {
        return Results.NotFound(new { Error = "El grupo no existe." });
    }

    if (!GruposBd.EsMiembro(id, usuario.Id))
    {
        return Results.Json(
            new { Error = "No perteneces a ese grupo." },
            statusCode: StatusCodes.Status403Forbidden);
    }

    var token = RecompensasBd.ObtenerOCrearToken(id, usuario.Id);
    var enlaceGrupo = GruposBd.ObtenerEnlaceRecompensa(id);

    return Results.Ok(new
    {
        GrupoId = id,
        Plan = RecompensasBd.ObtenerPlan(id),
        Enlace = enlaceGrupo,
        RetosDisponibles = !string.IsNullOrWhiteSpace(enlaceGrupo),
        MiEnlace = $"/r/{token}",
        EsCreador = grupo.Value.CreadorId == usuario.Id,
        Completadas = TareasBd.TareasAutomaticasCompletas(id, usuario.Id)
    });
});

app.MapPut("/api/grupos/{id:int}/recompensas",
    async (HttpContext contexto, int id, RecompensasPlanRequest peticion,
           IHubContext<ChatHub> hub) =>
{
    var usuario = UsuarioAutenticado(contexto);

    if (usuario is null)
    {
        return SinSesion();
    }

    var grupo = GruposBd.Obtener(id);

    if (grupo is null)
    {
        return Results.NotFound(new { Error = "El grupo no existe." });
    }

    if (!GruposBd.EsMiembro(id, usuario.Id))
    {
        return Results.Json(
            new { Error = "No perteneces a ese grupo." },
            statusCode: StatusCodes.Status403Forbidden);
    }

    if (grupo.Value.CreadorId != usuario.Id)
    {
        return Results.Json(
            new { Error = "Solo el creador del grupo puede elegir las cartas." },
            statusCode: StatusCodes.Status403Forbidden);
    }

    var elegidas = (peticion.Recompensas ?? new List<int>())
        .Distinct()
        .ToList();

    if (elegidas.Count != 3 || elegidas.Any(r => !RecompensasBd.Existe(r)))
    {
        return Results.BadRequest(new
        {
            Error = "Adjunta 3 cartas distintas del catálogo."
        });
    }

    var (enlace, errorEnlace) = NormalizarEnlaceRecompensa(peticion.Enlace);

    if (errorEnlace is not null)
    {
        return Results.BadRequest(new { Error = errorEnlace });
    }

    RecompensasBd.GuardarPlan(id, elegidas);
    GruposBd.GuardarEnlaceRecompensa(id, enlace);
    RecompensasBd.AsegurarPlan(id);

    if (enlace != "")
    {
        TareasBd.SincronizarGrupo(id);
    }

    await AvisarGrupoActualizado(hub, id);

    var token = RecompensasBd.ObtenerOCrearToken(id, usuario.Id);

    return Results.Ok(new
    {
        GrupoId = id,
        Plan = RecompensasBd.ObtenerPlan(id),
        Enlace = enlace,
        RetosDisponibles = enlace != "",
        MiEnlace = $"/r/{token}",
        EsCreador = true
    });
});

app.MapGet("/r/{token}", async (string token, IHubContext<ChatHub> hub) =>
{
    var enlaceToken = RecompensasBd.BuscarPorToken(token);

    if (enlaceToken is null)
    {
        return Results.Content(
            "<h2>Este enlace ya no es válido.</h2>",
            "text/html");
    }

    var (grupoId, usuarioId) = enlaceToken.Value;
    var grupo = GruposBd.Obtener(grupoId);

    if (grupo is null || !GruposBd.EsMiembro(grupoId, usuarioId))
    {
        return Results.Content(
            "<h2>Ya no perteneces a este grupo.</h2>",
            "text/html");
    }

    var destino = GruposBd.ObtenerEnlaceRecompensa(grupoId);

    if (string.IsNullOrWhiteSpace(destino))
    {
        return Results.Content(
            "<h2>Este grupo aún no tiene retos habilitados.</h2>",
            "text/html");
    }

    if (!TareasBd.TareasAutomaticasCompletas(grupoId, usuarioId))
    {
        return Results.Content(
            "<h2>Termina las tres tareas del grupo para desbloquear " +
            "tu última photocard.</h2>",
            "text/html");
    }

    var recompensa = RecompensasBd.OtorgarDelPlan(grupoId, usuarioId, 3);
    await AvisarRecompensa(hub, usuarioId, recompensa);

    return Results.Redirect(destino);
});

/* .../api/correo/enviar — envía un correo electrónico al destinatario registrado */
app.MapPost("/api/correo/enviar",
    async (HttpContext contexto, EnviarCorreoRequest peticion, CorreoServicio correoServicio) =>
{
    var actual = UsuarioAutenticado(contexto);

    if (actual is null)
    {
        return SinSesion();
    }

    var asunto = peticion.Asunto?.Trim() ?? "";
    var mensaje = peticion.Mensaje?.Trim() ?? "";

    if (asunto == "")
    {
        return Results.BadRequest(new { Error = "Escribe un asunto." });
    }

    if (asunto.Length > MaximoAsuntoCorreo)
    {
        return Results.BadRequest(new { Error =
            $"El asunto no puede tener más de {MaximoAsuntoCorreo} caracteres." });
    }

    if (asunto.Contains('\r') || asunto.Contains('\n'))
    {
        return Results.BadRequest(new { Error =
            "El asunto no puede contener saltos de línea." });
    }

    if (mensaje == "")
    {
        return Results.BadRequest(new { Error = "Escribe un mensaje." });
    }

    if (mensaje.Length > MaximoMensajeCorreo)
    {
        return Results.BadRequest(new { Error =
            $"El mensaje no puede tener más de {MaximoMensajeCorreo} caracteres." });
    }

    Usuario? destino = null;

    if (peticion.DestinatarioId.HasValue && peticion.DestinatarioId.Value > 0)
    {
        destino = UsuariosBd.BuscarPorId(peticion.DestinatarioId.Value);
    }
    else if (!string.IsNullOrWhiteSpace(peticion.DestinatarioCorreo))
    {
        destino = UsuariosBd.BuscarPorCorreo(peticion.DestinatarioCorreo.Trim());
    }

    if (destino is null)
    {
        return Results.NotFound(new { Error = "Destinatario no encontrado." });
    }

    if (destino.Id == actual.Id)
    {
        return Results.BadRequest(new { Error = "No puedes enviarte un correo a ti mismo." });
    }

    try
    {
        correoServicio.ValidarConfiguracion();
    }
    catch (CorreoNoConfiguradoException)
    {
        return Results.Json(
            new { Error = "El envío de correo no está configurado en el servidor." },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    if (!LimiteCorreos.IntentarRegistrar(actual.Id))
    {
        contexto.Response.Headers.RetryAfter = "600";
        return Results.Json(
            new { Error = "Has enviado demasiados correos. Intenta de nuevo en unos minutos." },
            statusCode: StatusCodes.Status429TooManyRequests);
    }

    try
    {
        await correoServicio.EnviarAsync(
            destino.Correo,
            destino.NombreCompleto,
            asunto,
            mensaje);

        return Results.Ok(new
        {
            Enviado = true,
            DestinatarioId = destino.Id,
            DestinatarioNombre = destino.NombreCompleto
        });
    }
    catch (CorreoEnvioException)
    {
        return Results.Json(
            new { Error = "No se pudo enviar el correo. Intenta de nuevo más tarde." },
            statusCode: StatusCodes.Status502BadGateway);
    }
});

app.Run();

public record RegistroRequest(
    string? Nombre,
    string? Apellido,
    string? Usuario,
    string? Correo,
    string? Contrasena);

public record GrupoRequest(
    string? Nombre,
    List<int>? Miembros,
    string? Enlace);

public record AgregarMiembrosRequest(
    List<int>? Miembros);

public record TareaRequest(
    string? Titulo,
    string? Descripcion,
    int? AsignadoId);

public record TareaFinalizarRequest(
    long? EvidenciaMensajeId);

public record RecompensasPlanRequest(
    List<int>? Recompensas,
    string? Enlace);

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

public record EnviarCorreoRequest(
    int? DestinatarioId,
    string? DestinatarioCorreo,
    string? Asunto,
    string? Mensaje);

public record ClavePublicaRequest(string? ClavePublica);
