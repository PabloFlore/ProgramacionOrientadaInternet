namespace ChatServer;

/// <summary>
/// Ubicación del certificado HTTPS del servidor (servidor.pfx), generado con
/// tools/generar_certificados.ps1. Sin certificado el servidor no arranca:
/// el navegador no daría acceso a cámara ni micrófono por HTTP.
///
/// Orden de resolución:
///   1. Variable de entorno PIA_CERTIFICADO (la usan las pruebas).
///   2. server/ChatServer/certs/servidor.pfx (subiendo desde el binario hasta
///      ChatServer.csproj).
///   3. certs/servidor.pfx junto al binario.
/// </summary>
public static class CertificadoHttps
{
    public const string VariableEntorno = "PIA_CERTIFICADO";
    public const string NombreArchivo = "servidor.pfx";

    public static string Ruta()
    {
        var variable = Environment.GetEnvironmentVariable(VariableEntorno);

        if (!string.IsNullOrWhiteSpace(variable))
        {
            return Verificar(Path.GetFullPath(variable));
        }

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory);
             dir is not null;
             dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ChatServer.csproj")))
            {
                return Verificar(Path.Combine(dir.FullName, "certs", NombreArchivo));
            }
        }

        return Verificar(Path.Combine(AppContext.BaseDirectory, "certs", NombreArchivo));
    }

    private static string Verificar(string ruta)
    {
        if (!File.Exists(ruta))
        {
            throw new InvalidOperationException(
                $"No existe el certificado HTTPS en '{ruta}'. " +
                "Genéralo con: powershell -File tools\\generar_certificados.ps1 -Ip <IP-de-la-PC>");
        }

        return ruta;
    }
}
