using System.Text;

namespace ChatServer;

/// <summary>
/// Archivo rechazado por tamaño, tipo o contenido. El endpoint usa el
/// código para responder 400, 413 o 415.
/// </summary>
public sealed class ArchivoInvalidoException : Exception
{
    public int Codigo { get; }

    public ArchivoInvalidoException(string mensaje, int codigo = 400)
        : base(mensaje)
    {
        Codigo = codigo;
    }
}

public sealed record ArchivoValidado(
    byte[] Contenido,
    string Nombre,
    string Mime,
    long Tamano,
    bool EsImagen);

/// <summary>
/// Lista blanca y validación de adjuntos. No se confía en el MIME que
/// manda el navegador: el tipo se decide por extensión y, en imágenes,
/// por la firma real de los bytes.
/// </summary>
public static class ArchivosServicio
{
    public const long MaximoBytes = 2L * 1024 * 1024;
    private const int MaximoNombre = 200;

    private static readonly UTF8Encoding Utf8Estricto = new(false, true);

    private static readonly Dictionary<string, string> MimesTexto =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["txt"] = "text/plain",
            ["md"] = "text/markdown",
            ["csv"] = "text/csv",
            ["json"] = "application/json",
            ["log"] = "text/plain"
        };

    private static readonly HashSet<string> ExtensionesImagen =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "jpg", "jpeg", "png", "webp", "gif"
        };

    public static async Task<byte[]> LeerAsync(
        Stream flujo,
        CancellationToken cancelacion = default)
    {
        using var memoria = new MemoryStream();
        var buffer = new byte[81920];

        while (true)
        {
            var leidos = await flujo.ReadAsync(
                buffer.AsMemory(0, buffer.Length),
                cancelacion);

            if (leidos == 0)
            {
                break;
            }

            if (memoria.Length + leidos > MaximoBytes)
            {
                throw new ArchivoInvalidoException(
                    "El archivo supera el máximo de 2 MB.",
                    413);
            }

            memoria.Write(buffer, 0, leidos);
        }

        return memoria.ToArray();
    }

    public static ArchivoValidado Validar(
        string nombreOriginal,
        byte[] contenido)
    {
        if (contenido.Length == 0)
        {
            throw new ArchivoInvalidoException("El archivo está vacío.");
        }

        if (contenido.Length > MaximoBytes)
        {
            throw new ArchivoInvalidoException(
                "El archivo supera el máximo de 2 MB.",
                413);
        }

        var nombre = SanearNombre(nombreOriginal);
        var extension = Path.GetExtension(nombre)
            .TrimStart('.')
            .ToLowerInvariant();

        if (MimesTexto.TryGetValue(extension, out var mimeTexto))
        {
            if (!EsTextoUtf8Valido(contenido))
            {
                throw new ArchivoInvalidoException(
                    "El archivo de texto no es UTF-8 válido o contiene " +
                    "datos binarios.",
                    415);
            }

            return new ArchivoValidado(
                contenido,
                nombre,
                mimeTexto,
                contenido.Length,
                false);
        }

        if (ExtensionesImagen.Contains(extension))
        {
            var mimeImagen = DetectarImagen(contenido);

            if (mimeImagen is null)
            {
                throw new ArchivoInvalidoException(
                    "El contenido de la imagen no es JPEG, PNG, WebP ni GIF.",
                    415);
            }

            return new ArchivoValidado(
                contenido,
                nombre,
                mimeImagen,
                contenido.Length,
                true);
        }

        throw new ArchivoInvalidoException(
            "Solo se permiten archivos de texto " +
            "(.txt, .md, .csv, .json, .log) o imágenes " +
            "(.jpg, .png, .webp, .gif).",
            415);
    }

    private static string SanearNombre(string nombreOriginal)
    {
        var nombre = (nombreOriginal ?? "").Replace('\\', '/');
        var separador = nombre.LastIndexOf('/');

        if (separador >= 0)
        {
            nombre = nombre[(separador + 1)..];
        }

        nombre = new string(
            nombre.Where(c => !char.IsControl(c)).ToArray()).Trim();

        if (nombre.Length == 0)
        {
            return "archivo";
        }

        if (nombre.Length <= MaximoNombre)
        {
            return nombre;
        }

        var extension = Path.GetExtension(nombre);
        var baseNombre = Path.GetFileNameWithoutExtension(nombre);
        var maximoBase = Math.Max(1, MaximoNombre - extension.Length);

        return baseNombre[..Math.Min(baseNombre.Length, maximoBase)] +
            extension;
    }

    private static bool EsTextoUtf8Valido(byte[] contenido)
    {
        if (Array.IndexOf(contenido, (byte)0) >= 0)
        {
            return false;
        }

        try
        {
            _ = Utf8Estricto.GetString(contenido);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static string? DetectarImagen(byte[] contenido)
    {
        if (Coincide(contenido, 0, 0xFF, 0xD8, 0xFF))
        {
            return "image/jpeg";
        }

        if (Coincide(contenido, 0, 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A))
        {
            return "image/png";
        }

        if (Coincide(contenido, 0, 0x47, 0x49, 0x46, 0x38, 0x37, 0x61) ||
            Coincide(contenido, 0, 0x47, 0x49, 0x46, 0x38, 0x39, 0x61))
        {
            return "image/gif";
        }

        if (Coincide(contenido, 0, 0x52, 0x49, 0x46, 0x46) &&
            Coincide(contenido, 8, 0x57, 0x45, 0x42, 0x50))
        {
            return "image/webp";
        }

        return null;
    }

    private static bool Coincide(byte[] contenido, int inicio, params byte[] firma)
    {
        if (contenido.Length < inicio + firma.Length)
        {
            return false;
        }

        for (var i = 0; i < firma.Length; i++)
        {
            if (contenido[inicio + i] != firma[i])
            {
                return false;
            }
        }

        return true;
    }
}
