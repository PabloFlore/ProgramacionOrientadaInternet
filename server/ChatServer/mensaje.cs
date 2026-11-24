namespace ChatServer.Modelos;

public class Mensaje
{
    public long Id { get; set; }

    public string ChatId { get; set; } = "";

    public string Usuario { get; set; } = "";

    public string Texto { get; set; } = "";

    public DateTime FechaEnvio { get; set; }

    public int? UsuarioId { get; set; }

    /// <summary>"texto", "ubicacion" o "archivo".</summary>
    public string Tipo { get; set; } = "texto";

    /// <summary>Latitud de la ubicación compartida (solo si Tipo = ubicacion).</summary>
    public double? Latitud { get; set; }

    /// <summary>Longitud de la ubicación compartida (solo si Tipo = ubicacion).</summary>
    public double? Longitud { get; set; }

    /// <summary>Etiqueta visible de la ubicación (por ejemplo, "Mi ubicación").</summary>
    public string? Etiqueta { get; set; }

    /// <summary>URL de descarga del adjunto (solo si Tipo = archivo).</summary>
    public string? ArchivoUrl { get; set; }

    /// <summary>Nombre original del adjunto, saneado (solo si Tipo = archivo).</summary>
    public string? ArchivoNombre { get; set; }

    /// <summary>MIME canónico del adjunto (solo si Tipo = archivo).</summary>
    public string? ArchivoMime { get; set; }

    /// <summary>Tamaño del adjunto en bytes (solo si Tipo = archivo).</summary>
    public long? ArchivoTamano { get; set; }

    /// <summary>Identificador público en Cloudinary (solo si Tipo = archivo).</summary>
    public string? ArchivoPublicId { get; set; }

    /// <summary>True si Texto es ciphertext AES-GCM en base64.</summary>
    public bool Cifrado { get; set; }

    /// <summary>IV de 12 bytes en base64 (solo si Cifrado).</summary>
    public string? Iv { get; set; }

    /// <summary>Clave pública ECDH P-256 del emisor en SPKI/base64.</summary>
    public string? ClavePublica { get; set; }

    /// <summary>
    /// Clave pública ECDH P-256 del destinatario usada al cifrar, en
    /// SPKI/base64. Permite al emisor descifrar sus propios mensajes
    /// aunque el destinatario cambie de dispositivo.
    /// </summary>
    public string? ClavePublicaDestino { get; set; }
}
