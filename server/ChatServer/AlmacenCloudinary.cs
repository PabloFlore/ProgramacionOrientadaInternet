using System.Net.Http.Headers;
using System.Text.Json;

namespace ChatServer;

/// <summary>
/// Error de configuración del almacenamiento. El endpoint lo traduce a 503
/// para distinguirlo de un fallo del proveedor.
/// </summary>
public sealed class AlmacenNoConfiguradoException : Exception
{
    public AlmacenNoConfiguradoException(string mensaje)
        : base(mensaje)
    {
    }
}

/// <summary>
/// Error al subir el archivo a Cloudinary. El endpoint devuelve un mensaje
/// genérico y deja el detalle en el log del servidor.
/// </summary>
public sealed class AlmacenSubidaException : Exception
{
    public AlmacenSubidaException(string mensaje, Exception? interna = null)
        : base(mensaje, interna)
    {
    }
}

public sealed record ArchivoSubido(
    string Url,
    string PublicId,
    string TipoRecurso);

/// <summary>
/// Cliente mínimo de la API de subida de Cloudinary. No usa el SDK: solo
/// un POST multipart con un preset unsigned, así que el servidor no guarda
/// el API Secret.
/// </summary>
public class AlmacenCloudinary
{
    private readonly HttpClient _http;
    private readonly IConfiguration _configuracion;
    private readonly ILogger<AlmacenCloudinary> _registro;

    public AlmacenCloudinary(
        HttpClient http,
        IConfiguration configuracion,
        ILogger<AlmacenCloudinary> registro)
    {
        _http = http;
        _configuracion = configuracion;
        _registro = registro;
    }

    public void ValidarConfiguracion()
    {
        _ = LeerConfiguracion();
    }

    public async Task<ArchivoSubido> SubirAsync(
        byte[] contenido,
        string nombre,
        string mime,
        bool esImagen,
        CancellationToken cancelacion = default)
    {
        var config = LeerConfiguracion();

        var recurso = esImagen ? "image" : "raw";
        var url =
            $"{config.ApiBase.TrimEnd('/')}/v1_1/" +
            $"{Uri.EscapeDataString(config.CloudName)}/{recurso}/upload";

        using var formulario = new MultipartFormDataContent();
        using var archivo = new ByteArrayContent(contenido);
        archivo.Headers.ContentType = new MediaTypeHeaderValue(mime);
        formulario.Add(archivo, "file", nombre);
        formulario.Add(new StringContent(config.UploadPreset), "upload_preset");

        if (!string.IsNullOrWhiteSpace(config.Carpeta))
        {
            formulario.Add(new StringContent(config.Carpeta), "folder");
        }

        try
        {
            using var respuesta = await _http.PostAsync(
                url,
                formulario,
                cancelacion);

            var cuerpo = await respuesta.Content.ReadAsStringAsync(cancelacion);

            if (!respuesta.IsSuccessStatusCode)
            {
                _registro.LogError(
                    "Cloudinary rechazó la subida de {Nombre} ({Mime}): " +
                    "{Estado} {Cuerpo}",
                    nombre,
                    mime,
                    (int)respuesta.StatusCode,
                    cuerpo.Length > 500 ? cuerpo[..500] : cuerpo);

                throw new AlmacenSubidaException(
                    "No se pudo subir el archivo al almacenamiento.");
            }

            using var documento = JsonDocument.Parse(cuerpo);
            var raiz = documento.RootElement;

            var secureUrl = raiz.TryGetProperty("secure_url", out var urlJson)
                ? urlJson.GetString()
                : null;
            var publicId = raiz.TryGetProperty("public_id", out var idJson)
                ? idJson.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(secureUrl))
            {
                throw new AlmacenSubidaException(
                    "El almacenamiento no devolvió una URL válida.");
            }

            return new ArchivoSubido(
                secureUrl,
                publicId ?? "",
                recurso);
        }
        catch (AlmacenSubidaException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _registro.LogError(
                ex,
                "Falló la subida de {Nombre} ({Mime}) a Cloudinary.",
                nombre,
                mime);

            throw new AlmacenSubidaException(
                "No se pudo conectar con el almacenamiento.",
                ex);
        }
    }

    public async Task<byte[]> DescargarAsync(
        string url,
        CancellationToken cancelacion = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps &&
             uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new AlmacenSubidaException(
                "La URL del archivo almacenado no es válida.");
        }

        try
        {
            using var respuesta = await _http.GetAsync(
                uri,
                HttpCompletionOption.ResponseHeadersRead,
                cancelacion);

            if (!respuesta.IsSuccessStatusCode)
            {
                throw new AlmacenSubidaException(
                    "No se pudo descargar el archivo almacenado.");
            }

            if (respuesta.Content.Headers.ContentLength is long tamano &&
                tamano > ArchivosServicio.MaximoBytes)
            {
                throw new AlmacenSubidaException(
                    "El archivo almacenado supera el máximo de 2 MB.");
            }

            await using var flujo =
                await respuesta.Content.ReadAsStreamAsync(cancelacion);

            return await ArchivosServicio.LeerAsync(flujo, cancelacion);
        }
        catch (AlmacenSubidaException)
        {
            throw;
        }
        catch (ArchivoInvalidoException ex)
        {
            throw new AlmacenSubidaException(
                "El archivo almacenado supera el máximo de 2 MB.",
                ex);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _registro.LogError(
                ex,
                "Falló la descarga del archivo almacenado {Url}.",
                url);

            throw new AlmacenSubidaException(
                "No se pudo conectar con el almacenamiento.",
                ex);
        }
    }

    private ConfiguracionCloudinary LeerConfiguracion()
    {
        var seccion = _configuracion.GetSection("Cloudinary");

        var cloudName = (seccion["CloudName"] ?? "").Trim();
        var preset = (seccion["UploadPreset"] ?? "").Trim();
        var apiBase = (seccion["ApiBase"] ?? "https://api.cloudinary.com").Trim();
        var carpeta = (seccion["Carpeta"] ?? "musichub").Trim();

        if (string.IsNullOrWhiteSpace(cloudName) ||
            string.IsNullOrWhiteSpace(preset))
        {
            throw new AlmacenNoConfiguradoException(
                "Falta CloudName o UploadPreset en la sección Cloudinary.");
        }

        if (!Uri.TryCreate(apiBase, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps &&
             uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new AlmacenNoConfiguradoException(
                "ApiBase de Cloudinary no es una URL válida.");
        }

        return new ConfiguracionCloudinary(
            cloudName,
            preset,
            apiBase,
            carpeta);
    }

    private sealed record ConfiguracionCloudinary(
        string CloudName,
        string UploadPreset,
        string ApiBase,
        string Carpeta);
}
