using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using ChatServer.Datos;
using ChatServer.Hubs;

namespace ChatServer.Llamadas;

/// <summary>
/// Estado de una videollamada 1 a 1 (solo en memoria, no se guarda).
/// Llamante: quien inicia. Receptor: quien recibe el aviso.
/// </summary>
public sealed class Llamada
{
    public required string Id { get; init; }
    public required int LlamanteId { get; init; }
    public required int ReceptorId { get; init; }
    public required string ChatId { get; init; }
    public bool Activa { get; set; }

    /* Grupo de SignalR solo para señalización WebRTC (oferta, respuesta,
       candidatos). Lo forman las páginas de la llamada, no las pestañas de
       chat, así que una pestaña de Home no recibe SDP. */
    public string GrupoSenal => "llamada:" + Id;
}

/// <summary>Datos que recibe la página de la llamada al unirse.</summary>
public sealed record LlamadaInfo(
    string Rol,
    bool Activa,
    string ChatId,
    int OtroUsuarioId,
    string OtroNombre);

/// <summary>
/// Reglas y estado de las videollamadas. El hub valida la sesión y el chat;
/// aquí se decide quién puede hacer cada paso y se avisa a los participantes
/// por todas sus conexiones abiertas.
/// </summary>
public sealed class LlamadasServicio
{
    public const int SegundosSinRespuesta = 30;
    public const int SegundosGraciaDesconexion = 15;
    public const int MaximoDatosSenal = 20000;

    private static readonly string[] TiposSenal = { "listo", "oferta", "respuesta", "candidato" };

    private readonly ConcurrentDictionary<string, Llamada> _llamadas = new();
    private readonly IHubContext<ChatHub> _hub;
    private readonly ILogger<LlamadasServicio> _registro;

    /* Todas las operaciones que cambian el estado van bajo este candado para
       que dos peticiones simultáneas no creen dos llamadas al mismo usuario. */
    private readonly object _candado = new();

    public LlamadasServicio(
        IHubContext<ChatHub> hub,
        ILogger<LlamadasServicio> registro)
    {
        _hub = hub;
        _registro = registro;
    }

    /* -------------------- Inicio y respuesta -------------------- */

    public async Task<string> Iniciar(int llamanteId, string nombreLlamante, string chatId)
    {
        if (!MensajesBd.EsChatPrivado(chatId))
        {
            throw new HubException("Las videollamadas solo son para chats privados.");
        }

        var par = MensajesBd.ParsearChatPrivado(chatId);
        if (par is null || (par.Value.A != llamanteId && par.Value.B != llamanteId))
        {
            throw new HubException("No perteneces a esa conversación.");
        }

        var receptorId = MensajesBd.OtroParticipante(chatId, llamanteId);

        var llamada = new Llamada
        {
            Id = Guid.NewGuid().ToString("N"),
            LlamanteId = llamanteId,
            ReceptorId = receptorId,
            ChatId = chatId
        };

        lock (_candado)
        {
            if (DeUsuario(llamanteId) is not null)
            {
                throw new HubException("Ya estás en una videollamada.");
            }

            if (DeUsuario(receptorId) is not null)
            {
                throw new HubException("El otro integrante ya está en una videollamada.");
            }

            if (!ChatHub.EstaConectado(receptorId))
            {
                throw new HubException("El otro integrante no está conectado.");
            }

            _llamadas[llamada.Id] = llamada;
        }

        await NotificarAUsuario(receptorId, "LlamadaEntrante", llamada.Id, chatId, llamanteId, nombreLlamante);

        _ = TerminarDespues(
            TimeSpan.FromSeconds(SegundosSinRespuesta),
            llamada.Id,
            "sin respuesta",
            soloSiSonando: true);

        return llamada.Id;
    }

    public async Task Contestar(string llamadaId, int usuarioId)
    {
        Llamada llamada;

        lock (_candado)
        {
            llamada = ObtenerParticipante(llamadaId, usuarioId);

            if (llamada.ReceptorId != usuarioId)
            {
                throw new HubException("Solo quien recibe la llamada puede contestarla.");
            }

            if (llamada.Activa)
            {
                return;
            }

            llamada.Activa = true;
        }

        await NotificarAParticipantes(llamada, "LlamadaAceptada", llamada.Id);
    }

    public async Task Rechazar(string llamadaId, int usuarioId)
    {
        Llamada llamada;

        lock (_candado)
        {
            llamada = ObtenerParticipante(llamadaId, usuarioId);

            if (llamada.ReceptorId != usuarioId || llamada.Activa)
            {
                throw new HubException("Solo puedes rechazar una llamada que suena.");
            }
        }

        await Terminar(llamada.Id, "rechazada", soloSiSonando: true);
    }

    /* -------------------- Unirse, señalización y cierre -------------------- */

    /// <summary>
    /// La página de la llamada se une al grupo de señalización. El receptor
    /// solo puede hacerlo cuando la llamada ya fue aceptada.
    /// </summary>
    public async Task<LlamadaInfo> UnirseAsync(string llamadaId, int usuarioId, string connectionId)
    {
        Llamada llamada;

        lock (_candado)
        {
            llamada = ObtenerParticipante(llamadaId, usuarioId);

            if (llamada.ReceptorId == usuarioId && !llamada.Activa)
            {
                throw new HubException("Aún no has contestado esta llamada.");
            }
        }

        await _hub.Groups.AddToGroupAsync(connectionId, llamada.GrupoSenal);

        var esLlamante = llamada.LlamanteId == usuarioId;
        var otroId = esLlamante ? llamada.ReceptorId : llamada.LlamanteId;
        var otro = UsuariosBd.BuscarPorId(otroId);

        return new LlamadaInfo(
            Rol: esLlamante ? "llamante" : "receptor",
            Activa: llamada.Activa,
            ChatId: llamada.ChatId,
            OtroUsuarioId: otroId,
            OtroNombre: otro?.NombreCompleto ?? "Usuario");
    }

    public async Task Senal(string llamadaId, int usuarioId, string connectionId, string tipo, string datos)
    {
        Llamada llamada;

        lock (_candado)
        {
            llamada = ObtenerParticipante(llamadaId, usuarioId);

            if (!llamada.Activa)
            {
                throw new HubException("La llamada todavía no está activa.");
            }
        }

        if (Array.IndexOf(TiposSenal, tipo) < 0)
        {
            throw new HubException("Tipo de señal no válido.");
        }

        if ((datos ?? "").Length > MaximoDatosSenal)
        {
            throw new HubException("La señal es demasiado grande.");
        }

        /* Solo al otro participante de esta llamada, nunca a otros grupos. */
        await _hub.Clients.GroupExcept(llamada.GrupoSenal, new[] { connectionId })
            .SendAsync("SenalLlamada", llamada.Id, tipo, datos ?? "");
    }

    public async Task Colgar(string llamadaId, int usuarioId)
    {
        Llamada llamada;

        lock (_candado)
        {
            llamada = ObtenerParticipante(llamadaId, usuarioId);
        }

        await Terminar(llamada.Id, "colgada", soloSiSonando: false);
    }

    /// <summary>
    /// Se llama cuando un usuario se queda sin ninguna conexión. Espera un
    /// tiempo por si la página se recarga o cambia de pantalla (por ejemplo,
    /// Home -> Videollamada); si no vuelve, la llamada termina.
    /// </summary>
    public void UsuarioSinConexion(int usuarioId)
    {
        _ = TerminarDespues(
            TimeSpan.FromSeconds(SegundosGraciaDesconexion),
            usuarioId);
    }

    private async Task TerminarDespues(
        TimeSpan espera,
        string llamadaId,
        string motivo,
        bool soloSiSonando)
    {
        try
        {
            await Task.Delay(espera);
            await Terminar(llamadaId, motivo, soloSiSonando);
        }
        catch (Exception error)
        {
            _registro.LogError(error, "No se pudo terminar la llamada {LlamadaId}.", llamadaId);
        }
    }

    private async Task TerminarDespues(TimeSpan espera, int usuarioId)
    {
        try
        {
            await Task.Delay(espera);

            if (ChatHub.EstaConectado(usuarioId))
            {
                return;
            }

            var llamada = DeUsuario(usuarioId);
            if (llamada is not null)
            {
                await Terminar(llamada.Id, "desconectado", soloSiSonando: false);
            }
        }
        catch (Exception error)
        {
            _registro.LogError(error, "No se pudo cerrar la llamada de {UsuarioId}.", usuarioId);
        }
    }

    /* -------------------- Internos -------------------- */

    private Llamada? DeUsuario(int usuarioId)
    {
        return _llamadas.Values.FirstOrDefault(l =>
            l.LlamanteId == usuarioId || l.ReceptorId == usuarioId);
    }

    /* Debe llamarse dentro del candado. */
    private Llamada ObtenerParticipante(string llamadaId, int usuarioId)
    {
        if (!_llamadas.TryGetValue(llamadaId ?? "", out var llamada) ||
            (llamada.LlamanteId != usuarioId && llamada.ReceptorId != usuarioId))
        {
            throw new HubException("La llamada no existe o ya terminó.");
        }

        return llamada;
    }

    /// <summary>
    /// Quita la llamada y avisa a ambos. Con soloSiSonando, no hace nada si
    /// la llamada ya fue aceptada (evita que un temporizador viejo la corte).
    /// </summary>
    private async Task Terminar(string llamadaId, string motivo, bool soloSiSonando)
    {
        Llamada? llamada = null;

        lock (_candado)
        {
            if (_llamadas.TryGetValue(llamadaId, out var encontrada) &&
                !(soloSiSonando && encontrada.Activa))
            {
                _llamadas.TryRemove(llamadaId, out _);
                llamada = encontrada;
            }
        }

        if (llamada is not null)
        {
            await NotificarAParticipantes(llamada, "LlamadaTerminada", llamada.Id, motivo);
        }
    }

    private async Task NotificarAParticipantes(Llamada llamada, string evento, params object[] datos)
    {
        await NotificarAUsuario(llamada.LlamanteId, evento, datos);
        await NotificarAUsuario(llamada.ReceptorId, evento, datos);
    }

    private async Task NotificarAUsuario(int usuarioId, string evento, params object[] datos)
    {
        var conexiones = ChatHub.ConexionesDe(usuarioId);

        if (conexiones.Count == 0)
        {
            return;
        }

        await _hub.Clients.Clients(conexiones).SendCoreAsync(evento, datos);
    }
}
