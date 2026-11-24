$ErrorActionPreference = "Stop"

$raizProyecto = Resolve-Path (Join-Path $PSScriptRoot "..\..\..")
$binOrigen = Resolve-Path (Join-Path $PSScriptRoot "..\bin\Debug\net8.0")
$webOrigen = Join-Path $raizProyecto "web"
$raiz = Join-Path $env:TEMP "pia_prueba_borrado_chat"
$base = "https://localhost:5000"
$wsHub = "wss://localhost:5000/chat"

$certificado = Join-Path $PSScriptRoot "..\certs\servidor.pfx"

if (-not (Test-Path -LiteralPath $certificado)) {
    throw "No existe el certificado HTTPS. Generalo con: powershell -File tools\generar_certificados.ps1 -Ip <IP>"
}

[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$env:PIA_CERTIFICADO = (Resolve-Path $certificado).Path

if (-not (Test-Path -LiteralPath (Join-Path $binOrigen "ChatServer.dll"))) {
    throw "No existe ChatServer.dll. Compila primero con: dotnet build (en server/ChatServer)."
}

if (Get-NetTCPConnection -LocalPort 5000 -State Listen -ErrorAction SilentlyContinue) {
    throw "El puerto 5000 ya esta en uso. No se ejecuta la prueba para no tocar el servidor real."
}

Remove-Item $raiz -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path "$raiz\server" -Force | Out-Null
Copy-Item $webOrigen "$raiz\web" -Recurse
Copy-Item $binOrigen "$raiz\server\bin" -Recurse
Remove-Item "$raiz\server\bin\chat.db*" -Force -ErrorAction SilentlyContinue
$env:PIA_CARPETA_DATOS = "$raiz\datos"

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
        return [pscustomobject]@{
            Status = [int]$r.StatusCode
            Raw = $r.Content
            Body = ($r.Content | ConvertFrom-Json)
        }
    } catch {
        $resp = $_.Exception.Response
        if ($null -eq $resp) { throw }
        $lector = New-Object System.IO.StreamReader($resp.GetResponseStream())
        $texto = $lector.ReadToEnd()
        $cuerpoRespuesta = $null
        try { $cuerpoRespuesta = $texto | ConvertFrom-Json } catch { }
        return [pscustomobject]@{
            Status = [int]$resp.StatusCode
            Raw = $texto
            Body = $cuerpoRespuesta
        }
    }
}

$resultados = New-Object System.Collections.ArrayList

function Afirmar([string]$nombre, [bool]$ok) {
    [void]$resultados.Add([pscustomobject]@{
        Prueba = $nombre
        Resultado = $(if ($ok) { "OK" } else { "FALLA" })
    })
}

function Arrancar {
    $p = Start-Process dotnet -ArgumentList "`"$raiz\server\bin\ChatServer.dll`"" -PassThru -WindowStyle Hidden
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Milliseconds 500
        try {
            $x = Llamar "GET" "$base/api/usuarios"
            if ($x.Status -eq 401) { return $p }
        } catch { }
    }
    throw "El servidor de prueba no arranco."
}

function Detener($p) {
    if ($p -and -not $p.HasExited) { Stop-Process -Id $p.Id -Force }
    for ($i = 0; $i -lt 40; $i++) {
        if (-not (Get-NetTCPConnection -LocalPort 5000 -State Listen -ErrorAction SilentlyContinue)) { return }
        Start-Sleep -Milliseconds 250
    }
    throw "El puerto 5000 no se libero al detener el servidor."
}

function Enviar-Trama($ws, [string]$texto) {
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($texto + [char]0x1e)
    $segmento = [System.ArraySegment[byte]]::new($bytes)
    $ws.SendAsync(
        $segmento,
        [System.Net.WebSockets.WebSocketMessageType]::Text,
        $true,
        [System.Threading.CancellationToken]::None
    ).GetAwaiter().GetResult()
}

function Recibir-Trama($ws) {
    $buffer = New-Object byte[] 65536
    $segmento = [System.ArraySegment[byte]]::new($buffer)
    $texto = New-Object System.Text.StringBuilder
    do {
        $r = $ws.ReceiveAsync(
            $segmento,
            [System.Threading.CancellationToken]::None
        ).GetAwaiter().GetResult()
        [void]$texto.Append([System.Text.Encoding]::UTF8.GetString($buffer, 0, $r.Count))
    } while (-not $r.EndOfMessage)
    return $texto.ToString()
}

function Enviar-Hub([string]$token, [string]$chatId, [string]$texto) {
    $ws = New-Object System.Net.WebSockets.ClientWebSocket
    $uri = [System.Uri]::new("$wsHub`?access_token=$token")
    $ws.ConnectAsync($uri, [System.Threading.CancellationToken]::None).GetAwaiter().GetResult()

    try {
        Enviar-Trama $ws '{"protocol":"json","version":1}'
        [void](Recibir-Trama $ws)

        $invocacion = @{
            type = 1
            invocationId = "1"
            target = "SendToGroup"
            arguments = @($chatId, $texto, "tmp-$([guid]::NewGuid())")
        } | ConvertTo-Json -Depth 5 -Compress

        Enviar-Trama $ws $invocacion

        for ($i = 0; $i -lt 10; $i++) {
            $tramas = Recibir-Trama $ws
            foreach ($linea in ($tramas -split [char]0x1e)) {
                if ($linea.Trim() -eq "") { continue }
                $mensaje = $linea | ConvertFrom-Json
                if ($mensaje.type -eq 3 -and $mensaje.invocationId -eq "1") {
                    return [pscustomobject]@{
                        Ok = -not $mensaje.error
                        Error = $mensaje.error
                    }
                }
            }
        }
        throw "El hub no respondio a SendToGroup."
    } finally {
        if ($ws.State -eq [System.Net.WebSockets.WebSocketState]::Open) {
            $ws.CloseAsync(
                [System.Net.WebSockets.WebSocketCloseStatus]::NormalClosure,
                "",
                [System.Threading.CancellationToken]::None
            ).GetAwaiter().GetResult()
        }
        $ws.Dispose()
    }
}

function Historial([string]$token, [string]$chat) {
    $r = Llamar "GET" "$base/api/mensajes?chatId=$chat&limite=50" $null $token
    if ($r.Status -ne 200) { return @("HTTP $($r.Status)") }
    return @($r.Body.Mensajes | ForEach-Object { $_.Texto })
}

function TieneConversacion([string]$token, [string]$chat) {
    $r = Llamar "GET" "$base/api/conversaciones" $null $token
    if ($r.Status -ne 200) { return $false }
    return @($r.Body.Conversaciones | Where-Object { $_.ChatId -eq $chat }).Count -gt 0
}

function TieneGrupo([string]$token, [string]$chat) {
    $r = Llamar "GET" "$base/api/grupos" $null $token
    if ($r.Status -ne 200) { return $false }
    return @($r.Body.Grupos | Where-Object { $_.ChatId -eq $chat }).Count -gt 0
}

function Chat-Dm([int]$a, [int]$b) {
    $menor = [Math]::Min($a, $b)
    $mayor = [Math]::Max($a, $b)
    return "dm:$menor`:$mayor"
}

$proc = $null
try {
    $proc = Arrancar

    $pass = "Clave#2026x"
    $tokens = @{}
    $ids = @{}
    foreach ($n in 1..4) {
        $correo = "borrado$n@mail.com"
        Llamar "POST" "$base/api/registrar" @{
            Nombre = "Persona$n"
            Apellido = "Prueba"
            Usuario = "b$n"
            Correo = $correo
            Contrasena = $pass
        } | Out-Null

        $l = Llamar "POST" "$base/api/login" @{
            Correo = $correo
            Contrasena = $pass
        }
        $tokens[$n] = $l.Body.Token
        $ids[$n] = $l.Body.Id
    }

    $dm = Chat-Dm $ids[1] $ids[2]

    Enviar-Hub $tokens[1] $dm "dm-a1" | Out-Null
    Enviar-Hub $tokens[2] $dm "dm-b1" | Out-Null
    Enviar-Hub $tokens[1] $dm "dm-a2" | Out-Null

    $sinSesion = Llamar "DELETE" "$base/api/mensajes?chatId=$dm"
    Afirmar "sin sesion responde 401" ($sinSesion.Status -eq 401)

    $sinChat = Llamar "DELETE" "$base/api/mensajes" $null $tokens[1]
    Afirmar "sin chatId responde 400" ($sinChat.Status -eq 400)

    $ajeno = Llamar "DELETE" "$base/api/mensajes?chatId=$dm" $null $tokens[4]
    Afirmar "un tercero no puede borrar el DM (403)" ($ajeno.Status -eq 403)

    $borrarA = Llamar "DELETE" "$base/api/mensajes?chatId=$dm" $null $tokens[1]
    Afirmar "el participante borra su DM (200)" ($borrarA.Status -eq 200)
    Afirmar "el borrado devuelve el corte aplicado" ($borrarA.Body.BorradoHasta -gt 0)

    $dmA = Historial $tokens[1] $dm
    $dmB = Historial $tokens[2] $dm
    Afirmar "el DM queda vacio para quien lo borro" ($dmA.Count -eq 0)
    Afirmar "el otro participante conserva los 3 mensajes" ($dmB.Count -eq 3)
    Afirmar "el DM desaparece de las conversaciones de quien lo borro" (-not (TieneConversacion $tokens[1] $dm))
    Afirmar "el DM sigue en las conversaciones del otro participante" (TieneConversacion $tokens[2] $dm)

    Enviar-Hub $tokens[2] $dm "dm-b2" | Out-Null
    $dmANuevo = Historial $tokens[1] $dm
    $dmBNuevo = Historial $tokens[2] $dm
    Afirmar "un mensaje nuevo vuelve a aparecer para quien borro" (($dmANuevo -join "|") -eq "dm-b2")
    Afirmar "el otro participante ve los 4 mensajes" ($dmBNuevo.Count -eq 4)
    Afirmar "la conversacion reaparece en la lista de quien borro" (TieneConversacion $tokens[1] $dm)

    $borrarA2 = Llamar "DELETE" "$base/api/mensajes?chatId=$dm" $null $tokens[1]
    Enviar-Hub $tokens[2] $dm "dm-b3" | Out-Null
    $dmAFinal = Historial $tokens[1] $dm
    Afirmar "el segundo borrado es monotono y oculta lo anterior" ($borrarA2.Status -eq 200 -and (($dmAFinal -join "|") -eq "dm-b3"))

    Enviar-Hub $tokens[1] $dm "dm-a3" | Out-Null
    $dmAPropio = Historial $tokens[1] $dm
    Afirmar "quien borro puede seguir enviando y ve lo nuevo" (($dmAPropio -join "|") -eq "dm-b3|dm-a3")

    $grupo = Llamar "POST" "$base/api/grupos" @{
        Nombre = "Borrado"
        Miembros = @($ids[2], $ids[3], $ids[4])
    } $tokens[1]
    Afirmar "se crea el grupo de prueba (201)" ($grupo.Status -eq 201)
    $chatGrupo = "grupo:$($grupo.Body.Id)"

    Enviar-Hub $tokens[1] $chatGrupo "grp-a1" | Out-Null
    Enviar-Hub $tokens[2] $chatGrupo "grp-b1" | Out-Null
    Enviar-Hub $tokens[3] $chatGrupo "grp-c1" | Out-Null

    $borrarGrupo = Llamar "DELETE" "$base/api/mensajes?chatId=$chatGrupo" $null $tokens[1]
    Afirmar "el integrante borra el historial del grupo (200)" ($borrarGrupo.Status -eq 200)

    $grupoA = Historial $tokens[1] $chatGrupo
    $grupoB = Historial $tokens[2] $chatGrupo
    Afirmar "el grupo queda vacio para quien lo borro" ($grupoA.Count -eq 0)
    Afirmar "los demas integrantes conservan los 3 mensajes" ($grupoB.Count -eq 3)
    Afirmar "el grupo sigue visible en la lista de quien borro" (TieneGrupo $tokens[1] $chatGrupo)

    Enviar-Hub $tokens[2] $chatGrupo "grp-b2" | Out-Null
    $grupoANuevo = Historial $tokens[1] $chatGrupo
    Afirmar "el grupo muestra solo los mensajes posteriores al borrado" (($grupoANuevo -join "|") -eq "grp-b2")

    $sale = Llamar "DELETE" "$base/api/grupos/$($grupo.Body.Id)/miembros/$($ids[3])" $null $tokens[3]
    Afirmar "un integrante sale del grupo (200)" ($sale.Status -eq 200)

    $borrarEx = Llamar "DELETE" "$base/api/mensajes?chatId=$chatGrupo" $null $tokens[3]
    $exHistorial = Historial $tokens[3] $chatGrupo
    Afirmar "quien salio puede borrar su propio historial (200)" ($borrarEx.Status -eq 200)
    Afirmar "quien salio deja de ver el historial tras borrarlo" ($exHistorial.Count -eq 0)

    $grupoC = Historial $tokens[2] $chatGrupo
    Afirmar "el borrado de quien salio no afecta a los integrantes" ($grupoC.Count -eq 4)

    $dm23 = Chat-Dm $ids[2] $ids[3]
    Enviar-Hub $tokens[2] $dm23 "dm-23" | Out-Null
    $borrarAjeno = Llamar "DELETE" "$base/api/mensajes?chatId=$dm23" $null $tokens[1]
    Afirmar "un tercero no puede borrar otro DM (403)" ($borrarAjeno.Status -eq 403)
}
finally {
    Detener $proc
    Start-Sleep -Milliseconds 500
    Remove-Item $raiz -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item Env:\PIA_CARPETA_DATOS -ErrorAction SilentlyContinue
}

$resultados | Format-Table -AutoSize | Out-String -Width 200

$fallas = @($resultados | Where-Object { $_.Resultado -ne "OK" }).Count
if ($fallas -gt 0) {
    exit 1
}
