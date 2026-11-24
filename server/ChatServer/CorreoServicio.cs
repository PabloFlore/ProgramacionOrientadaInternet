using System.Net;
using System.Net.Mail;
using System.Text;

namespace ChatServer;

/// <summary>
/// Error de configuración SMTP. El endpoint lo traduce a 503 para no
/// confundirlo con un fallo del proveedor de correo.
/// </summary>
public sealed class CorreoNoConfiguradoException : Exception
{
    public CorreoNoConfiguradoException(string mensaje)
        : base(mensaje)
    {
    }
}

/// <summary>
/// Error al construir o enviar el mensaje. El endpoint devuelve un
/// mensaje genérico y deja el detalle en el log del servidor.
/// </summary>
public sealed class CorreoEnvioException : Exception
{
    public CorreoEnvioException(string mensaje, Exception? interna = null)
        : base(mensaje, interna)
    {
    }
}

public class CorreoServicio
{
    private const int TimeoutMs = 15_000;

    private readonly IConfiguration _configuracion;
    private readonly ILogger<CorreoServicio> _registro;

    public CorreoServicio(
        IConfiguration configuracion,
        ILogger<CorreoServicio> registro)
    {
        _configuracion = configuracion;
        _registro = registro;
    }

    /// <summary>
    /// Valida la sección Correo antes de consumir el límite de envíos.
    /// Lanza CorreoNoConfiguradoException si falta algo obligatorio.
    /// </summary>
    public void ValidarConfiguracion()
    {
        _ = LeerConfiguracion();
    }

    public async Task EnviarAsync(
        string destinatarioCorreo,
        string destinatarioNombre,
        string asunto,
        string mensaje)
    {
        var config = LeerConfiguracion();

        using var cliente = new SmtpClient(config.Host, config.Puerto)
        {
            EnableSsl = config.EnableSsl,
            Timeout = TimeoutMs,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            UseDefaultCredentials = false,
            /* En un SMTP local sin autenticación hay que dejar las
               credenciales en null. DefaultNetworkCredentials intenta
               autenticar con la cuenta de Windows y suele romper el envío. */
            Credentials = string.IsNullOrWhiteSpace(config.Usuario)
                ? null
                : new NetworkCredential(config.Usuario, config.Password)
        };

        using var mail = new MailMessage
        {
            From = new MailAddress(
                config.Remitente,
                config.RemitenteNombre,
                Encoding.UTF8),
            Subject = asunto,
            Body = mensaje,
            BodyEncoding = Encoding.UTF8,
            SubjectEncoding = Encoding.UTF8,
            HeadersEncoding = Encoding.UTF8,
            IsBodyHtml = false
        };

        var nombreDest = string.IsNullOrWhiteSpace(destinatarioNombre)
            ? destinatarioCorreo
            : destinatarioNombre;

        try
        {
            mail.To.Add(new MailAddress(
                destinatarioCorreo,
                nombreDest,
                Encoding.UTF8));
        }
        catch (FormatException ex)
        {
            throw new CorreoEnvioException(
                "El destinatario no tiene una dirección de correo válida.",
                ex);
        }

        try
        {
            await cliente.SendMailAsync(mail);
        }
        catch (Exception ex)
        {
            _registro.LogError(
                ex,
                "Falló el envío SMTP a {Destinatario} con asunto {Asunto}.",
                destinatarioCorreo,
                asunto);

            throw new CorreoEnvioException(
                "No se pudo entregar el correo por SMTP.",
                ex);
        }
    }

    private ConfiguracionSmtp LeerConfiguracion()
    {
        var seccion = _configuracion.GetSection("Correo");

        var host = (seccion["Host"] ?? "").Trim();
        var puertoTexto = (seccion["Port"] ?? "587").Trim();
        var remitente = (seccion["Remitente"] ?? "").Trim();
        var remitenteNombre =
            (seccion["RemitenteNombre"] ?? "Music Hub").Trim();

        if (string.IsNullOrWhiteSpace(host) ||
            string.IsNullOrWhiteSpace(remitente))
        {
            throw new CorreoNoConfiguradoException(
                "Falta Host o Remitente en la sección Correo.");
        }

        if (!int.TryParse(puertoTexto, out var puerto) ||
            puerto < 1 ||
            puerto > 65535)
        {
            throw new CorreoNoConfiguradoException(
                "El puerto SMTP no es válido.");
        }

        if (!bool.TryParse(seccion["EnableSsl"] ?? "true", out var enableSsl))
        {
            throw new CorreoNoConfiguradoException(
                "EnableSsl debe ser true o false.");
        }

        try
        {
            _ = new MailAddress(remitente);
        }
        catch (FormatException)
        {
            throw new CorreoNoConfiguradoException(
                "El remitente SMTP no es una dirección válida.");
        }

        return new ConfiguracionSmtp(
            host,
            puerto,
            enableSsl,
            (seccion["Usuario"] ?? "").Trim(),
            seccion["Password"] ?? "",
            remitente,
            string.IsNullOrWhiteSpace(remitenteNombre)
                ? "Music Hub"
                : remitenteNombre);
    }

    private sealed record ConfiguracionSmtp(
        string Host,
        int Puerto,
        bool EnableSsl,
        string Usuario,
        string Password,
        string Remitente,
        string RemitenteNombre);
}
