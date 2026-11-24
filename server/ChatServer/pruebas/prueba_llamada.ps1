# Pruebas de videollamadas 1 a 1 (API + señalización del hub).
#
# Requisitos: haber compilado el backend (dotnet build en server/ChatServer)
# y tener la CA local instalada (tools\generar_certificados.ps1).
# Qué hace: levanta un servidor temporal con su propia base de datos, conecta
# dos clientes SignalR por WebSocket y comprueba el flujo completo:
# iniciar -> aviso -> contestar -> unirse -> señales -> colgar/rechazar.
# No transmite audio ni video: WebRTC solo funciona en el navegador; aquí se
# valida la parte del servidor.
#
# Uso:  powershell -NoProfile -ExecutionPolicy Bypass -File pruebas\prueba_llamada.ps1

$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$raizProyecto = Resolve-Path (Join-Path $PSScriptRoot "..\..\..")
$binOrigen = Resolve-Path (Join-Path $PSScriptRoot "..\bin\Debug\net8.0")
$webOrigen = Join-Path $raizProyecto "web"
$raiz = Join-Path $env:TEMP "pia_prueba_llamada"
$base = "https://localhost:5000"
$wsHub = "wss://localhost:5000/chat"

$certificado = Join-Path $PSScriptRoot "..\certs\servidor.pfx"

if (-not (Test-Path -LiteralPath $certificado)) {
    throw "No existe el certificado HTTPS. Genéralo con: powershell -File tools\generar_certificados.ps1 -Ip <IP>"
}

if (-not (Test-Path -LiteralPath (Join-Path $binOrigen "ChatServer.dll"))) {
    throw "No existe ChatServer.dll. Compila primero con: dotnet build (en server/ChatServer)."
}

if (Get-NetTCPConnection -LocalPort 5000 -State Listen -ErrorAction SilentlyContinue) {
    throw "El puerto 5000 ya está en uso. No se ejecuta la prueba para no tocar el servidor real."
}

Remove-Item $raiz -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path "$raiz\server" -Force | Out-Null
Copy-Item $webOrigen "$raiz\web" -Recurse
Copy-Item $binOrigen "$raiz\server\bin" -Recurse
Remove-Item "$raiz\server\bin\chat.db*" -Force -ErrorAction SilentlyContinue
$env:PIA_CARPETA_DATOS = "$raiz\datos"
$env:PIA_CERTIFICADO = (Resolve-Path $certificado).Path

$proc = Start-Process dotnet -ArgumentList "`"$raiz\server\bin\ChatServer.dll`"" -PassThru -WindowStyle Hidden

function Llamar([string]$metodo, [string]$url, $cuerpo = $null, [string]$token = "") {
    $cabeceras = @{}
    if ($token -ne "") { $cabeceras["Authorization"] = "Bearer $token" }

    $params = @{
        Method = $metodo
        Uri = $url
        Headers = $cabeceras
        UseBasicParsing = $true
        ContentType = "application/json; charset=utf-8"
    }

    if ($null -ne $cuerpo) {
        $json = $cuerpo | ConvertTo-Json -Depth 5 -Compress
        $params["Body"] = [System.Text.Encoding]::UTF8.GetBytes($json)
    }

    try {
        $r = Invoke-WebRequest @params
        return [pscustomobject]@{ Status = [int]$r.StatusCode; Raw = $r.Content; Body = ($r.Content | ConvertFrom-Json) }
    } catch {
        $resp = $_.Exception.Response
        if ($null -eq $resp) { throw }
        $lector = New-Object System.IO.StreamReader($resp.GetResponseStream())
        $texto = $lector.ReadToEnd()
        $cuerpoRespuesta = $null
        try { $cuerpoRespuesta = $texto | ConvertFrom-Json } catch { }
        return [pscustomobject]@{ Status = [int]$resp.StatusCode; Raw = $texto; Body = $cuerpoRespuesta }
    }
}

$resultados = New-Object System.Collections.ArrayList
function Afirmar([string]$nombre, [bool]$ok) {
    [void]$resultados.Add([pscustomobject]@{ Prueba = $nombre; Resultado = $(if ($ok) { "OK" } else { "FALLA" }) })
}

# ---- WebSocket de SignalR (protocolo JSON, registros separados por 0x1E) ----

function Enviar-Trama($ws, [string]$texto) {
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($texto + [char]0x1e)
    $segmento = [System.ArraySegment[byte]]::new($bytes)
    [void]$ws.SendAsync($segmento, [System.Net.WebSockets.WebSocketMessageType]::Text, $true,
        [System.Threading.CancellationToken]::None).GetAwaiter().GetResult()
}

# Devuelve el texto de una trama completa o $null si vence el tiempo.
function Recibir-Trama($ws, [int]$ms = 5000) {
    $buffer = New-Object byte[] 65536
    $segmento = [System.ArraySegment[byte]]::new($buffer)
    $texto = New-Object System.Text.StringBuilder
    $cts = [System.Threading.CancellationTokenSource]::new($ms)

    try {
        do {
            $r = $ws.ReceiveAsync($segmento, $cts.Token).GetAwaiter().GetResult()
            if ($r.MessageType -eq [System.Net.WebSockets.WebSocketMessageType]::Close) {
                throw "El servidor cerró el WebSocket."
            }
            [void]$texto.Append([System.Text.Encoding]::UTF8.GetString($buffer, 0, $r.Count))
        } while (-not $r.EndOfMessage)
    } catch [System.OperationCanceledException] {
        return $null
    } finally {
        $cts.Dispose()
    }

    return $texto.ToString()
}

function Convertir-Tramas([string]$texto) {
    $lista = @()
    foreach ($linea in ($texto -split [char]0x1e)) {
        if ($linea.Trim() -eq "") { continue }
        $lista += ($linea | ConvertFrom-Json)
    }
    return $lista
}

function Conectar-Hub([string]$token) {
    $ws = New-Object System.Net.WebSockets.ClientWebSocket
    $uri = [System.Uri]::new("$wsHub`?access_token=$token")
    [void]$ws.ConnectAsync($uri, [System.Threading.CancellationToken]::None).GetAwaiter().GetResult()
    Enviar-Trama $ws '{"protocol":"json","version":1}'
    $saludo = Recibir-Trama $ws
    if ($null -eq $saludo) { throw "El hub no respondió al saludo." }
    return $ws
}

# Invoca un método y espera su resultado. Descarta eventos intermedios.
function Invocar($ws, [string]$target, $argumentos = @(), [int]$ms = 5000) {
    $id = [guid]::NewGuid().ToString()
    $invocacion = @{
        type = 1
        invocationId = $id
        target = $target
        arguments = @($argumentos)
    } | ConvertTo-Json -Depth 8 -Compress

    Enviar-Trama $ws $invocacion

    $limite = [DateTime]::UtcNow.AddMilliseconds($ms)

    while ([DateTime]::UtcNow -lt $limite) {
        $texto = Recibir-Trama $ws 1000
        if ($null -eq $texto) { continue }

        foreach ($m in (Convertir-Tramas $texto)) {
            if ($m.type -eq 3 -and $m.invocationId -eq $id) {
                return [pscustomobject]@{
                    Ok = -not $m.error
                    Error = $m.error
                    Result = $m.result
                }
            }
        }
    }

    throw "El hub no respondió a $target."
}

# Igual que Invocar, pero sin esperar la confirmación: se usa cuando el propio
# emisor también recibe el evento que se va a comprobar (por ejemplo, colgar).
function Enviar-Invocacion($ws, [string]$target, $argumentos = @()) {
    $invocacion = @{
        type = 1
        invocationId = [guid]::NewGuid().ToString()
        target = $target
        arguments = @($argumentos)
    } | ConvertTo-Json -Depth 8 -Compress

    Enviar-Trama $ws $invocacion
}

# Espera un evento (type 1) del servidor. Descarta los demás.
function Esperar-Evento($ws, [string]$target, [int]$ms = 5000) {
    $limite = [DateTime]::UtcNow.AddMilliseconds($ms)

    while ([DateTime]::UtcNow -lt $limite) {
        $texto = Recibir-Trama $ws 1000
        if ($null -eq $texto) { continue }

        foreach ($m in (Convertir-Tramas $texto)) {
            if ($m.type -eq 1 -and $m.target -eq $target) {
                return $m
            }
        }
    }

    return $null
}

$wsA = $null
$wsB = $null
$wsC = $null

try {
    # Esperar a que el servidor responda
    $listo = $false
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Milliseconds 500
        try {
            $x = Llamar "GET" "$base/api/usuarios"
            if ($x.Status -eq 401) { $listo = $true; break }
        } catch { }
    }
    if (-not $listo) { throw "El servidor de prueba no arrancó." }

    $pass = "Clave#2026x"
    $cuentas = @(
        @{ Nombre = "Ana"; Apellido = "Uno"; Usuario = "ana"; Correo = "ana.llamada@mail.com" },
        @{ Nombre = "Beto"; Apellido = "Dos"; Usuario = "beto"; Correo = "beto.llamada@mail.com" },
        @{ Nombre = "Caro"; Apellido = "Tres"; Usuario = "caro"; Correo = "caro.llamada@mail.com" }
    )

    $tokens = @()
    $ids = @()

    foreach ($cuenta in $cuentas) {
        $cuerpo = @{
            Nombre = $cuenta.Nombre
            Apellido = $cuenta.Apellido
            Usuario = $cuenta.Usuario
            Correo = $cuenta.Correo
            Contrasena = $pass
        }
        $r = Llamar "POST" "$base/api/registrar" $cuerpo
        $l = Llamar "POST" "$base/api/login" @{ Correo = $cuenta.Correo; Contrasena = $pass }
        $tokens += $l.Body.Token
        $ids += [int]$l.Body.Id
    }

    Afirmar "se registran las 3 cuentas" ($ids.Count -eq 3)

    $dm = "dm:$([Math]::Min($ids[0], $ids[1])):$([Math]::Max($ids[0], $ids[1]))"

    $wsA = Conectar-Hub $tokens[0]
    $wsB = Conectar-Hub $tokens[1]
    $wsC = Conectar-Hub $tokens[2]

    # ---- Llamada normal ----

    $inicio = Invocar $wsA "IniciarLlamada" @($dm)
    Afirmar "iniciar llamada privada responde con un id" ($inicio.Ok -and -not [string]::IsNullOrWhiteSpace($inicio.Result))
    $llamadaId = $inicio.Result

    $entrante = Esperar-Evento $wsB "LlamadaEntrante"
    Afirmar "el receptor recibe LlamadaEntrante" ($null -ne $entrante -and $entrante.arguments[0] -eq $llamadaId)
    Afirmar "el aviso trae el chat y el nombre del llamante" (
        $null -ne $entrante -and
        $entrante.arguments[1] -eq $dm -and
        $entrante.arguments[2] -eq $ids[0] -and
        $entrante.arguments[3] -eq "Ana Uno"
    )

    $ajeno = Invocar $wsC "UnirseLlamada" @($llamadaId)
    Afirmar "un tercero no puede unirse a la llamada" (-not $ajeno.Ok)

    $contestar = Invocar $wsB "ContestarLlamada" @($llamadaId)
    Afirmar "el receptor contesta la llamada" ($contestar.Ok)

    $aceptada = Esperar-Evento $wsA "LlamadaAceptada"
    Afirmar "el llamante recibe LlamadaAceptada" ($null -ne $aceptada -and $aceptada.arguments[0] -eq $llamadaId)

    $unirA = Invocar $wsA "UnirseLlamada" @($llamadaId)
    Afirmar "el llamante se une como llamante" ($unirA.Ok -and $unirA.Result.Rol -eq "llamante")
    Afirmar "el llamante ve el nombre del otro" ($unirA.Result.OtroNombre -eq "Beto Dos")

    $unirB = Invocar $wsB "UnirseLlamada" @($llamadaId)
    Afirmar "el receptor se une como receptor" ($unirB.Ok -and $unirB.Result.Rol -eq "receptor")
    Afirmar "la llamada figura activa" ($unirB.Result.Activa -eq $true)

    $oferta = '{"type":"offer","sdp":"v=0"}'
    $envioOferta = Invocar $wsA "EnviarSenalLlamada" @($llamadaId, "oferta", $oferta)
    Afirmar "el llamante envía la oferta" ($envioOferta.Ok)

    $senalB = Esperar-Evento $wsB "SenalLlamada"
    Afirmar "el receptor recibe la oferta" (
        $null -ne $senalB -and
        $senalB.arguments[0] -eq $llamadaId -and
        $senalB.arguments[1] -eq "oferta" -and
        $senalB.arguments[2] -eq $oferta
    )

    $respuesta = '{"type":"answer","sdp":"v=0"}'
    $envioRespuesta = Invocar $wsB "EnviarSenalLlamada" @($llamadaId, "respuesta", $respuesta)
    Afirmar "el receptor envía la respuesta" ($envioRespuesta.Ok)

    $senalA = Esperar-Evento $wsA "SenalLlamada"
    Afirmar "el llamante recibe la respuesta" (
        $null -ne $senalA -and
        $senalA.arguments[1] -eq "respuesta" -and
        $senalA.arguments[2] -eq $respuesta
    )

    $candidato = '{"candidate":"candidate:1 1 udp 1 127.0.0.1 5000 typ host"}'
    [void](Invocar $wsB "EnviarSenalLlamada" @($llamadaId, "candidato", $candidato))
    $candA = Esperar-Evento $wsA "SenalLlamada"
    Afirmar "los candidatos ICE llegan al otro participante" (
        $null -ne $candA -and $candA.arguments[1] -eq "candidato"
    )

    $tipoMalo = Invocar $wsA "EnviarSenalLlamada" @($llamadaId, "otro", "{}")
    Afirmar "un tipo de señal desconocido se rechaza" (-not $tipoMalo.Ok)

    $grande = '{"sdp":"' + ("x" * 21000) + '"}'
    $senalGrande = Invocar $wsA "EnviarSenalLlamada" @($llamadaId, "oferta", $grande)
    Afirmar "una señal de más de 20 000 caracteres se rechaza" (-not $senalGrande.Ok)

    # ---- Reglas de una sola llamada ----

    $segunda = Invocar $wsA "IniciarLlamada" @($dm)
    Afirmar "no se puede iniciar otra llamada estando en una" (
        -not $segunda.Ok -and $segunda.Error -match "HubException: Ya "
    )

    $dmC = "dm:$([Math]::Min($ids[0], $ids[2])):$([Math]::Max($ids[0], $ids[2]))"
    $aOcupado = Invocar $wsC "IniciarLlamada" @($dmC)
    Afirmar "no se puede llamar a alguien que ya está en una llamada" (
        -not $aOcupado.Ok -and $aOcupado.Error -match "HubException: El otro integrante"
    )

    $grupo = Invocar $wsC "IniciarLlamada" @("grupo:1")
    Afirmar "no se puede llamar a un grupo" (
        -not $grupo.Ok -and $grupo.Error -match "solo son para chats privados"
    )

    # ---- Colgar ----

    Enviar-Invocacion $wsA "ColgarLlamada" @($llamadaId)

    $finA = Esperar-Evento $wsA "LlamadaTerminada"
    $finB = Esperar-Evento $wsB "LlamadaTerminada"
    Afirmar "ambos reciben LlamadaTerminada (colgada)" (
        $null -ne $finA -and $finA.arguments[1] -eq "colgada" -and
        $null -ne $finB -and $finB.arguments[1] -eq "colgada"
    )

    $unirTarde = Invocar $wsA "UnirseLlamada" @($llamadaId)
    Afirmar "una llamada terminada ya no se puede abrir" (-not $unirTarde.Ok)

    # ---- Rechazar ----

    $inicio2 = Invocar $wsA "IniciarLlamada" @($dm)
    Afirmar "se puede iniciar una llamada nueva tras colgar" ($inicio2.Ok)
    $llamada2 = $inicio2.Result

    [void](Esperar-Evento $wsB "LlamadaEntrante")

    $sinContestar = Invocar $wsB "UnirseLlamada" @($llamada2)
    Afirmar "el receptor no puede unirse antes de contestar" (-not $sinContestar.Ok)

    $unirTemprano = Invocar $wsA "UnirseLlamada" @($llamada2)
    Afirmar "el llamante sí puede esperar en la llamada sin contestar" (
        $unirTemprano.Ok -and $unirTemprano.Result.Activa -eq $false
    )

    $senalAntes = Invocar $wsA "EnviarSenalLlamada" @($llamada2, "oferta", "{}")
    Afirmar "no se puede enviar señal antes de que la llamada esté activa" (-not $senalAntes.Ok)

    $rechazo = Invocar $wsB "RechazarLlamada" @($llamada2)
    Afirmar "el receptor rechaza la llamada" ($rechazo.Ok)

    $finRechazo = Esperar-Evento $wsA "LlamadaTerminada"
    Afirmar "el llamante recibe el rechazo" (
        $null -ne $finRechazo -and $finRechazo.arguments[1] -eq "rechazada"
    )

    # ---- Reconectar / desconexión ----

    $wsC.Dispose()
    $wsC = $null
    Start-Sleep -Milliseconds 800

    $dmC = "dm:$([Math]::Min($ids[0], $ids[2])):$([Math]::Max($ids[0], $ids[2]))"
    $sinConexion = Invocar $wsA "IniciarLlamada" @($dmC)
    Afirmar "no se puede llamar a alguien desconectado" (
        -not $sinConexion.Ok -and $sinConexion.Error -match "conectado"
    )
}
finally {
    foreach ($ws in @($wsA, $wsB, $wsC)) {
        if ($null -ne $ws) {
            try { $ws.Dispose() } catch { }
        }
    }

    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
    Start-Sleep -Milliseconds 500
    Remove-Item $raiz -Recurse -Force -ErrorAction SilentlyContinue
}

$resultados | Format-Table -AutoSize | Out-String -Width 200
