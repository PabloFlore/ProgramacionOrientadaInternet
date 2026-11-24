using System.Globalization;
using Microsoft.Data.Sqlite;
using ChatServer.Modelos;

namespace ChatServer.Datos;

/// <summary>
/// Historial de mensajes. El alcance es el canal (chatId): todas las
/// cuentas que están en el mismo canal ven los mismos mensajes.
/// Los canales grupales son públicos; las conversaciones privadas usan
/// el formato dm:{idMenor}:{idMayor} y solo sus participantes pueden
/// leerlas o escribir en ellas.
/// </summary>
public static class MensajesBd
{
    private const int LimitePorDefecto = 100;
    private const int LimiteMaximo = 50;
    public const int LongitudMaximaChatId = 100;

    private static string CadenaConexion =>
        new SqliteConnectionStringBuilder
        {
            DataSource = RutaDatos.Archivo,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();

    static MensajesBd()
    {
        CrearTabla();
    }

    public static void Inicializar()
    {
    }

    private static void CrearTabla()
    {
        using var conexion = new SqliteConnection(CadenaConexion);
        conexion.Open();

        using (var comando = conexion.CreateCommand())
        {
            comando.CommandText = @"
                CREATE TABLE IF NOT EXISTS Mensajes (
                    Id         INTEGER PRIMARY KEY AUTOINCREMENT,
                    ChatId     TEXT NOT NULL,
                    Usuario    TEXT NOT NULL,
                    Mensaje    TEXT NOT NULL,
                    FechaEnvio TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS IX_Mensajes_Chat
                    ON Mensajes (ChatId, Id);
                CREATE TABLE IF NOT EXISTS BorradosChat (
                    UsuarioId      INTEGER NOT NULL,
                    ChatId         TEXT NOT NULL,
                    UltimoMensajeId INTEGER NOT NULL,
                    PRIMARY KEY (UsuarioId, ChatId)
                );
            ";
            comando.ExecuteNonQuery();
        }

        /* Migración: las bases de datos creadas antes de este avance
           no tienen UsuarioId ni las columnas de cifrado. Se agregan
           las que falten. */
        var columnasExistentes = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);

        using (var pragma = conexion.CreateCommand())
        {
            pragma.CommandText = "PRAGMA table_info(Mensajes);";
            using var lector = pragma.ExecuteReader();
            while (lector.Read())
            {
                columnasExistentes.Add(lector.GetString(1));
            }
        }

        var columnasNuevas = new (string Nombre, string Definicion)[]
        {
            ("UsuarioId", "UsuarioId INTEGER NULL"),
            ("Cifrado", "Cifrado INTEGER NOT NULL DEFAULT 0"),
            ("Iv", "Iv TEXT NULL"),
            ("ClavePublica", "ClavePublica TEXT NULL"),
            ("ClavePublicaDestino", "ClavePublicaDestino TEXT NULL"),
            ("Tipo", "Tipo TEXT NOT NULL DEFAULT 'texto'"),
            ("Latitud", "Latitud REAL NULL"),
            ("Longitud", "Longitud REAL NULL"),
            ("Etiqueta", "Etiqueta TEXT NULL"),
            ("ArchivoUrl", "ArchivoUrl TEXT NULL"),
            ("ArchivoNombre", "ArchivoNombre TEXT NULL"),
            ("ArchivoMime", "ArchivoMime TEXT NULL"),
            ("ArchivoTamano", "ArchivoTamano INTEGER NULL"),
            ("ArchivoPublicId", "ArchivoPublicId TEXT NULL")
        };

        foreach (var (nombre, definicion) in columnasNuevas)
        {
            if (columnasExistentes.Contains(nombre))
            {
                continue;
            }

            using var alter = conexion.CreateCommand();
            alter.CommandText =
                $"ALTER TABLE Mensajes ADD COLUMN {definicion};";
            alter.ExecuteNonQuery();
        }

        NormalizarChatIds(conexion);
    }

    /* Migración: un chatId con el prefijo en otra capitalización (por
       ejemplo "GRUPO:1") apuntaba al mismo grupo pero guardaba los mensajes
       en otro canal. Se deja todo en minúsculas para que el historial de la
       API y el grupo de SignalR coincidan. */
    private static void NormalizarChatIds(SqliteConnection conexion)
    {
        using var transaccion = conexion.BeginTransaction();

        foreach (var tabla in new[] { "Mensajes", "BorradosChat" })
        {
            foreach (var prefijo in new[] { "grupo:", "dm:" })
            {
                using var comando = conexion.CreateCommand();
                comando.Transaction = transaccion;
                comando.CommandText = $@"
                    UPDATE {tabla}
                    SET ChatId = '{prefijo}' || substr(ChatId, {prefijo.Length + 1})
                    WHERE substr(ChatId, 1, {prefijo.Length}) <> '{prefijo}'
                      AND lower(substr(ChatId, 1, {prefijo.Length})) = '{prefijo}';
                ";
                comando.ExecuteNonQuery();
            }
        }

        transaccion.Commit();
    }

    /* -------------------- Conversaciones privadas -------------------- */

    public static string CrearChatPrivado(int a, int b)
    {
        var menor = Math.Min(a, b);
        var mayor = Math.Max(a, b);
        return $"dm:{menor}:{mayor}";
    }

    public static bool EsChatPrivado(string chatId)
    {
        return !string.IsNullOrWhiteSpace(chatId) &&
            chatId.StartsWith("dm:", StringComparison.OrdinalIgnoreCase);
    }

    public static (int A, int B)? ParsearChatPrivado(string chatId)
    {
        if (!EsChatPrivado(chatId))
        {
            return null;
        }

        var partes = chatId.Substring(3).Split(':');
        if (partes.Length != 2)
        {
            return null;
        }

        if (!int.TryParse(partes[0], out var a) ||
            !int.TryParse(partes[1], out var b))
        {
            return null;
        }

        /* Solo se acepta la forma canónica dm:{menor}:{mayor} con ids
           positivos y menor < mayor. "DM:1:2", "dm:2:1" o "dm:1:1" se
           rechazan. */
        if (a <= 0 || a >= b || CrearChatPrivado(a, b) != chatId)
        {
            return null;
        }

        return (a, b);
    }

    /* -------------------- Grupos -------------------- */

    public static bool EsChatGrupo(string chatId)
    {
        return !string.IsNullOrWhiteSpace(chatId) &&
            chatId.StartsWith("grupo:", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Id del grupo de un chatId "grupo:{id}". Solo se acepta la forma
    /// canónica (sin ceros a la izquierda y con id positivo).
    /// </summary>
    public static int? ParsearChatGrupo(string chatId)
    {
        /* EsChatGrupo solo detecta el prefijo sin distinguir mayúsculas;
           el formato canónico siempre empieza por "grupo:" en minúsculas. */
        if (!chatId.StartsWith("grupo:", StringComparison.Ordinal) ||
            !EsChatGrupo(chatId))
        {
            return null;
        }

        var texto = chatId.Substring(6);

        if (!int.TryParse(texto, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var id) ||
            id <= 0 ||
            id.ToString(System.Globalization.CultureInfo.InvariantCulture) != texto)
        {
            return null;
        }

        return id;
    }

    /// <summary>
    /// Formato aceptado para cualquier chatId: un dm:/grupo: canónico o un
    /// canal público de letras, dígitos, guion o guion bajo (máx. 100).
    /// Así no se pueden guardar mensajes en un chatId vacío o arbitrario.
    /// </summary>
    public static bool EsChatValido(string? chatId)
    {
        if (string.IsNullOrWhiteSpace(chatId) ||
            chatId.Length > LongitudMaximaChatId)
        {
            return false;
        }

        if (EsChatGrupo(chatId))
        {
            return ParsearChatGrupo(chatId) is not null;
        }

        if (EsChatPrivado(chatId))
        {
            return ParsearChatPrivado(chatId) is not null;
        }

        foreach (var c in chatId)
        {
            var letraOdigito =
                (c >= 'a' && c <= 'z') ||
                (c >= 'A' && c <= 'Z') ||
                (c >= '0' && c <= '9');

            if (!letraOdigito && c != '_' && c != '-')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Los canales públicos (amigos, gaming…) son accesibles para
    /// cualquiera (true). Un grupo solo lo usan sus integrantes. Una
    /// conversación privada solo la pueden usar sus dos participantes.
    /// Un id dm: o grupo: malformado se rechaza para todos (false).
    /// </summary>
    public static bool EsParticipante(string chatId, int usuarioId)
    {
        if (EsChatGrupo(chatId))
        {
            var grupo = ParsearChatGrupo(chatId);
            return grupo is not null &&
                GruposBd.EsMiembro(grupo.Value, usuarioId);
        }

        if (!EsChatPrivado(chatId))
        {
            return true;
        }

        var par = ParsearChatPrivado(chatId);
        if (par is null)
        {
            return false;
        }

        return par.Value.A == usuarioId || par.Value.B == usuarioId;
    }

    /// <summary>
    /// Permiso de lectura del historial. Igual que EsParticipante, pero un
    /// grupo también se puede leer si el usuario salió o fue quitado de él.
    /// Solo afecta a la lectura: el hub (JoinGroup y SendToGroup) sigue
    /// usando EsParticipante.
    /// </summary>
    public static bool PuedeLeerHistorial(string chatId, int usuarioId)
    {
        if (EsChatGrupo(chatId))
        {
            var grupo = ParsearChatGrupo(chatId);
            return grupo is not null &&
                GruposBd.FueMiembro(grupo.Value, usuarioId);
        }

        return EsParticipante(chatId, usuarioId);
    }

    public static int OtroParticipante(string chatId, int usuarioId)
    {
        var par = ParsearChatPrivado(chatId);
        if (par is null)
        {
            return 0;
        }

        return par.Value.A == usuarioId ? par.Value.B : par.Value.A;
    }

    /* -------------------- Escritura -------------------- */

    public static Mensaje Registrar(
        string chatId,
        int usuarioId,
        string usuario,
        string mensaje,
        bool cifrado = false,
        string? iv = null,
        string? clavePublica = null,
        string? clavePublicaDestino = null)
    {
        return RegistrarInterno(
            chatId,
            usuarioId,
            usuario,
            mensaje,
            "texto",
            null,
            null,
            null,
            cifrado,
            iv,
            clavePublica,
            clavePublicaDestino);
    }

    /// <summary>
    /// Guarda una ubicación compartida. El texto queda vacío: los datos
    /// viajan en columnas estructuradas para que el cliente pinte un mapa
    /// y nunca muestre latitud/longitud como texto plano.
    /// </summary>
    public static Mensaje RegistrarUbicacion(
        string chatId,
        int usuarioId,
        string usuario,
        double latitud,
        double longitud,
        string etiqueta)
    {
        return RegistrarInterno(
            chatId,
            usuarioId,
            usuario,
            "",
            "ubicacion",
            latitud,
            longitud,
            etiqueta,
            false,
            null,
            null,
            null);
    }

    /// <summary>
    /// Guarda un adjunto ya subido a Cloudinary. El texto queda vacío:
    /// los datos viajan en columnas estructuradas para que el cliente
    /// pinte una vista previa y nunca muestre la URL como texto plano.
    /// </summary>
    public static Mensaje RegistrarArchivo(
        string chatId,
        int usuarioId,
        string usuario,
        string url,
        string nombre,
        string mime,
        long tamano,
        string publicId)
    {
        return RegistrarInterno(
            chatId,
            usuarioId,
            usuario,
            "",
            "archivo",
            null,
            null,
            null,
            false,
            null,
            null,
            null,
            url,
            nombre,
            mime,
            tamano,
            publicId);
    }

    private static Mensaje RegistrarInterno(
        string chatId,
        int usuarioId,
        string usuario,
        string mensaje,
        string tipo,
        double? latitud,
        double? longitud,
        string? etiqueta,
        bool cifrado,
        string? iv,
        string? clavePublica,
        string? clavePublicaDestino,
        string? archivoUrl = null,
        string? archivoNombre = null,
        string? archivoMime = null,
        long? archivoTamano = null,
        string? archivoPublicId = null)
    {
        /* Se guarda en UTC. La hora local se calcula al mostrarla. */
        var fecha = DateTime.UtcNow;

        using var conexion = new SqliteConnection(CadenaConexion);
        conexion.Open();

        using var comando = conexion.CreateCommand();
        comando.CommandText = @"
            INSERT INTO Mensajes
                (ChatId, Usuario, Mensaje, FechaEnvio, UsuarioId,
                 Cifrado, Iv, ClavePublica, ClavePublicaDestino,
                 Tipo, Latitud, Longitud, Etiqueta, ArchivoUrl, ArchivoNombre, ArchivoMime, ArchivoTamano, ArchivoPublicId)
            VALUES
                ($chatId, $usuario, $mensaje, $fecha, $usuarioId,
                 $cifrado, $iv, $clavePublica, $clavePublicaDestino,
                 $tipo, $latitud, $longitud, $etiqueta,
                 $archivoUrl, $archivoNombre, $archivoMime,
                 $archivoTamano, $archivoPublicId);
            SELECT last_insert_rowid();
        ";
        comando.Parameters.AddWithValue("$chatId", chatId);
        comando.Parameters.AddWithValue("$usuario", usuario);
        comando.Parameters.AddWithValue("$mensaje", mensaje);
        comando.Parameters.AddWithValue("$fecha", fecha.ToString("o"));
        comando.Parameters.AddWithValue("$usuarioId", usuarioId);
        comando.Parameters.AddWithValue("$cifrado", cifrado ? 1 : 0);
        comando.Parameters.AddWithValue(
            "$iv", string.IsNullOrWhiteSpace(iv) ? DBNull.Value : iv);
        comando.Parameters.AddWithValue(
            "$clavePublica",
            string.IsNullOrWhiteSpace(clavePublica)
                ? DBNull.Value
                : clavePublica);
        comando.Parameters.AddWithValue(
            "$clavePublicaDestino",
            string.IsNullOrWhiteSpace(clavePublicaDestino)
                ? DBNull.Value
                : clavePublicaDestino);
        comando.Parameters.AddWithValue("$tipo", tipo);
        comando.Parameters.AddWithValue(
            "$latitud", latitud.HasValue ? latitud.Value : DBNull.Value);
        comando.Parameters.AddWithValue(
            "$longitud", longitud.HasValue ? longitud.Value : DBNull.Value);
        comando.Parameters.AddWithValue(
            "$etiqueta",
            string.IsNullOrWhiteSpace(etiqueta) ? DBNull.Value : etiqueta);
        comando.Parameters.AddWithValue(
            "$archivoUrl",
            string.IsNullOrWhiteSpace(archivoUrl)
                ? DBNull.Value
                : archivoUrl);
        comando.Parameters.AddWithValue(
            "$archivoNombre",
            string.IsNullOrWhiteSpace(archivoNombre)
                ? DBNull.Value
                : archivoNombre);
        comando.Parameters.AddWithValue(
            "$archivoMime",
            string.IsNullOrWhiteSpace(archivoMime)
                ? DBNull.Value
                : archivoMime);
        comando.Parameters.AddWithValue(
            "$archivoTamano",
            archivoTamano.HasValue ? archivoTamano.Value : DBNull.Value);
        comando.Parameters.AddWithValue(
            "$archivoPublicId",
            string.IsNullOrWhiteSpace(archivoPublicId)
                ? DBNull.Value
                : archivoPublicId);

        var id = (long)comando.ExecuteScalar()!;

        return new Mensaje
        {
            Id = id,
            ChatId = chatId,
            Usuario = usuario,
            Texto = mensaje,
            FechaEnvio = fecha,
            UsuarioId = usuarioId,
            Tipo = tipo,
            Latitud = latitud,
            Longitud = longitud,
            Etiqueta = etiqueta,
            Cifrado = cifrado,
            Iv = cifrado ? iv : null,
            ClavePublica = cifrado ? clavePublica : null,
            ClavePublicaDestino = cifrado ? clavePublicaDestino : null,
            ArchivoUrl = archivoUrl,
            ArchivoNombre = archivoNombre,
            ArchivoMime = archivoMime,
            ArchivoTamano = archivoTamano,
            ArchivoPublicId = archivoPublicId
        };
    }

    /* -------------------- Borrado local -------------------- */

    /// <summary>
    /// Borra el historial de un chat solo para el usuario indicado.
    /// No elimina filas: guarda el Id del último mensaje existente. Los
    /// mensajes con Id mayor (los nuevos) vuelven a aparecer.
    /// </summary>
    public static long BorrarParaUsuario(string chatId, int usuarioId)
    {
        using var conexion = new SqliteConnection(CadenaConexion);
        conexion.Open();

        using var transaccion = conexion.BeginTransaction();

        long corte;
        using (var maximo = conexion.CreateCommand())
        {
            maximo.Transaction = transaccion;
            maximo.CommandText = @"
                SELECT COALESCE(MAX(Id), 0)
                FROM Mensajes
                WHERE ChatId = $chatId;
            ";
            maximo.Parameters.AddWithValue("$chatId", chatId);
            corte = (long)maximo.ExecuteScalar()!;
        }

        using (var guardar = conexion.CreateCommand())
        {
            guardar.Transaction = transaccion;
            guardar.CommandText = @"
                INSERT INTO BorradosChat (UsuarioId, ChatId, UltimoMensajeId)
                VALUES ($usuarioId, $chatId, $corte)
                ON CONFLICT(UsuarioId, ChatId) DO UPDATE SET
                    UltimoMensajeId = MAX(
                        BorradosChat.UltimoMensajeId,
                        excluded.UltimoMensajeId);
            ";
            guardar.Parameters.AddWithValue("$usuarioId", usuarioId);
            guardar.Parameters.AddWithValue("$chatId", chatId);
            guardar.Parameters.AddWithValue("$corte", corte);
            guardar.ExecuteNonQuery();
        }

        transaccion.Commit();
        return corte;
    }

    private static long ObtenerCorte(string chatId, int usuarioId)
    {
        using var conexion = new SqliteConnection(CadenaConexion);
        conexion.Open();

        using var comando = conexion.CreateCommand();
        comando.CommandText = @"
            SELECT COALESCE(MAX(UltimoMensajeId), 0)
            FROM BorradosChat
            WHERE ChatId = $chatId AND UsuarioId = $usuarioId;
        ";
        comando.Parameters.AddWithValue("$chatId", chatId);
        comando.Parameters.AddWithValue("$usuarioId", usuarioId);

        return (long)comando.ExecuteScalar()!;
    }

    /* -------------------- Lectura -------------------- */

    /// <summary>
    /// Devuelve los últimos mensajes del canal en orden cronológico
    /// (el más antiguo primero, para poder pintarlos en orden).
    /// Si se pasa antesDe, se traen mensajes con Id < antesDe (más antiguos).
    /// Solo se devuelven los mensajes posteriores al último borrado del
    /// usuario: el borrado es local y no afecta a los demás participantes.
    /// </summary>
    public static List<Mensaje> ObtenerPorChat(
        string chatId,
        int usuarioId,
        int limite = LimitePorDefecto,
        long? antesDe = null)
    {
        /* Se permite pedir uno más para saber si existe la página siguiente. */
        if (limite <= 0 || limite > LimiteMaximo + 1)
        {
            limite = 30;
        }

        var corte = ObtenerCorte(chatId, usuarioId);

        using var conexion = new SqliteConnection(CadenaConexion);
        conexion.Open();

        using var comando = conexion.CreateCommand();

        if (antesDe.HasValue && antesDe.Value > 0)
        {
            comando.CommandText = @"
                SELECT Id, ChatId, Usuario, Mensaje, FechaEnvio, UsuarioId,
                       Cifrado, Iv, ClavePublica, ClavePublicaDestino,
                       Tipo, Latitud, Longitud, Etiqueta, ArchivoUrl, ArchivoNombre, ArchivoMime, ArchivoTamano, ArchivoPublicId
                FROM (
                    SELECT Id, ChatId, Usuario, Mensaje, FechaEnvio, UsuarioId,
                       Cifrado, Iv, ClavePublica, ClavePublicaDestino,
                       Tipo, Latitud, Longitud, Etiqueta, ArchivoUrl, ArchivoNombre, ArchivoMime, ArchivoTamano, ArchivoPublicId
                    FROM Mensajes
                    WHERE ChatId = $chatId AND Id > $corte AND Id < $antesDe
                    ORDER BY Id DESC
                    LIMIT $limite
                )
                ORDER BY Id ASC;
            ";
            comando.Parameters.AddWithValue("$chatId", chatId);
            comando.Parameters.AddWithValue("$corte", corte);
            comando.Parameters.AddWithValue("$antesDe", antesDe.Value);
            comando.Parameters.AddWithValue("$limite", limite);
        }
        else
        {
            comando.CommandText = @"
                SELECT Id, ChatId, Usuario, Mensaje, FechaEnvio, UsuarioId,
                       Cifrado, Iv, ClavePublica, ClavePublicaDestino,
                       Tipo, Latitud, Longitud, Etiqueta, ArchivoUrl, ArchivoNombre, ArchivoMime, ArchivoTamano, ArchivoPublicId
                FROM (
                    SELECT Id, ChatId, Usuario, Mensaje, FechaEnvio, UsuarioId,
                       Cifrado, Iv, ClavePublica, ClavePublicaDestino,
                       Tipo, Latitud, Longitud, Etiqueta, ArchivoUrl, ArchivoNombre, ArchivoMime, ArchivoTamano, ArchivoPublicId
                    FROM Mensajes
                    WHERE ChatId = $chatId AND Id > $corte
                    ORDER BY Id DESC
                    LIMIT $limite
                )
                ORDER BY Id ASC;
            ";
            comando.Parameters.AddWithValue("$chatId", chatId);
            comando.Parameters.AddWithValue("$corte", corte);
            comando.Parameters.AddWithValue("$limite", limite);
        }

        var lista = new List<Mensaje>();

        using var lector = comando.ExecuteReader();
        while (lector.Read())
        {
            lista.Add(LeerMensaje(lector));
        }

        return lista;
    }

    public static Mensaje? ObtenerPorId(long id)
    {
        using var conexion = new SqliteConnection(CadenaConexion);
        conexion.Open();

        using var comando = conexion.CreateCommand();
        comando.CommandText = @"
            SELECT Id, ChatId, Usuario, Mensaje, FechaEnvio, UsuarioId,
                   Cifrado, Iv, ClavePublica, ClavePublicaDestino,
                   Tipo, Latitud, Longitud, Etiqueta,
                   ArchivoUrl, ArchivoNombre, ArchivoMime,
                   ArchivoTamano, ArchivoPublicId
            FROM Mensajes
            WHERE Id = $id
            LIMIT 1;
        ";
        comando.Parameters.AddWithValue("$id", id);

        using var lector = comando.ExecuteReader();

        return lector.Read() ? LeerMensaje(lector) : null;
    }

    /// <summary>
    /// Lista de conversaciones privadas del usuario con su último
    /// mensaje, ordenadas por fecha descendente.
    /// </summary>
    public static List<Conversacion> ObtenerConversaciones(int usuarioId)
    {
        var lista = new List<Conversacion>();

        using var conexion = new SqliteConnection(CadenaConexion);
        conexion.Open();

        using var comando = conexion.CreateCommand();
        comando.CommandText = @"
            SELECT m.ChatId, m.Usuario, m.Mensaje, m.FechaEnvio,
                   m.UsuarioId, m.Cifrado, m.Tipo
            FROM Mensajes m
            INNER JOIN (
                SELECT ChatId, MAX(Id) AS UltimoId
                FROM Mensajes
                WHERE ChatId LIKE 'dm:%'
                GROUP BY ChatId
            ) u ON u.UltimoId = m.Id
            LEFT JOIN BorradosChat b
                ON b.ChatId = m.ChatId AND b.UsuarioId = $usuarioId
            WHERE m.Id > COALESCE(b.UltimoMensajeId, 0);
        ";
        comando.Parameters.AddWithValue("$usuarioId", usuarioId);

        using var lector = comando.ExecuteReader();
        while (lector.Read())
        {
            var chatId = lector.GetString(0);

            if (!EsParticipante(chatId, usuarioId))
            {
                continue;
            }

            lista.Add(new Conversacion
            {
                ChatId = chatId,
                OtroUsuarioId = OtroParticipante(chatId, usuarioId),
                UltimoMensaje = lector.GetString(2),
                UltimaFecha = ParsearFecha(lector.GetString(3)),
                UltimoUsuarioId = lector.IsDBNull(4)
                    ? null
                    : lector.GetInt32(4),
                UltimoCifrado = !lector.IsDBNull(5) && lector.GetInt32(5) != 0,
                UltimoTipo = lector.IsDBNull(6)
                    ? "texto"
                    : lector.GetString(6)
            });
        }

        lista.Sort((a, b) =>
            b.UltimaFecha.CompareTo(a.UltimaFecha));

        return lista;
    }

    private static Mensaje LeerMensaje(SqliteDataReader lector)
    {
        return new Mensaje
        {
            Id = lector.GetInt64(0),
            ChatId = lector.GetString(1),
            Usuario = lector.GetString(2),
            Texto = lector.GetString(3),
            FechaEnvio = ParsearFecha(lector.GetString(4)),
            UsuarioId = lector.IsDBNull(5)
                ? null
                : lector.GetInt32(5),
            Cifrado = !lector.IsDBNull(6) && lector.GetInt32(6) != 0,
            Iv = lector.IsDBNull(7) ? null : lector.GetString(7),
            ClavePublica = lector.IsDBNull(8) ? null : lector.GetString(8),
            ClavePublicaDestino = lector.IsDBNull(9)
                ? null
                : lector.GetString(9),
            Tipo = lector.IsDBNull(10) ? "texto" : lector.GetString(10),
            Latitud = lector.IsDBNull(11) ? null : lector.GetDouble(11),
            Longitud = lector.IsDBNull(12) ? null : lector.GetDouble(12),
            Etiqueta = lector.IsDBNull(13) ? null : lector.GetString(13),
            ArchivoUrl = lector.IsDBNull(14) ? null : lector.GetString(14),
            ArchivoNombre = lector.IsDBNull(15) ? null : lector.GetString(15),
            ArchivoMime = lector.IsDBNull(16) ? null : lector.GetString(16),
            ArchivoTamano = lector.IsDBNull(17) ? null : lector.GetInt64(17),
            ArchivoPublicId = lector.IsDBNull(18) ? null : lector.GetString(18)
        };
    }

    /* Convierte el texto guardado (ISO 8601, en UTC o con desfase) a hora
       local. Así Hora y Fecha de la API salen en la misma zona. */
    private static DateTime ParsearFecha(string texto)
    {
        if (!DateTime.TryParse(
                texto,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var fecha))
        {
            return DateTime.MinValue;
        }

        return fecha.ToLocalTime();
    }
}
