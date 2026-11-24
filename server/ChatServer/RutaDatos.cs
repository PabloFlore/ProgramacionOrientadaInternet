namespace ChatServer.Datos;

/// <summary>
/// Ubicación de la base de datos SQLite (chat.db). Vive fuera de bin/ para
/// que un "clean", un rebuild o una publicación no borren el historial.
///
/// Orden de resolución de la carpeta:
///   1. Variable de entorno PIA_CARPETA_DATOS (la usan las pruebas para
///      aislar su base de datos).
///   2. server/ChatServer/data/ (la carpeta del proyecto, encontrada subiendo
///      desde el binario hasta ChatServer.csproj).
///   3. data/ junto al binario, si no se encuentra el proyecto.
/// </summary>
public static class RutaDatos
{
    public const string VariableEntorno = "PIA_CARPETA_DATOS";
    public const string NombreArchivo = "chat.db";

    public static readonly string Carpeta = ResolverCarpeta();

    public static readonly string Archivo = Path.Combine(Carpeta, NombreArchivo);

    static RutaDatos()
    {
        Directory.CreateDirectory(Carpeta);
        MigrarDesdeBinario();
    }

    private static string ResolverCarpeta()
    {
        var variable = Environment.GetEnvironmentVariable(VariableEntorno);

        if (!string.IsNullOrWhiteSpace(variable))
        {
            return Path.GetFullPath(variable);
        }

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory);
             dir is not null;
             dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ChatServer.csproj")))
            {
                return Path.Combine(dir.FullName, "data");
            }
        }

        return Path.Combine(AppContext.BaseDirectory, "data");
    }

    /* Antes la base estaba en bin\...\chat.db. Si existe ahí y todavía no
       hay copia en la carpeta nueva, se copia para no perder el historial.
       El original se conserva como respaldo. */
    private static void MigrarDesdeBinario()
    {
        /* Con carpeta personalizada (pruebas) no se toca la base del binario. */
        if (!string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable(VariableEntorno)))
        {
            return;
        }

        var anterior = Path.Combine(AppContext.BaseDirectory, NombreArchivo);

        if (File.Exists(Archivo) || !File.Exists(anterior))
        {
            return;
        }

        File.Copy(anterior, Archivo);
        Console.WriteLine(
            $"Base de datos copiada desde {anterior} a {Archivo}. " +
            "El original se conserva como respaldo.");
    }
}
