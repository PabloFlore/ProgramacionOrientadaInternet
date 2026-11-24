$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
Add-Type -AssemblyName System.Net.Http

Add-Type -TypeDefinition @"
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
public static class CertificadoPruebasTareas {
    public static readonly RemoteCertificateValidationCallback Aceptar =
        (remitente, certificado, cadena, errores) => true;
}
"@
[Net.ServicePointManager]::ServerCertificateValidationCallback =
    [CertificadoPruebasTareas]::Aceptar

$raizProyecto = Resolve-Path (Join-Path $PSScriptRoot "..\..\..")
$binOrigen = Resolve-Path (Join-Path $PSScriptRoot "..\bin\Debug\net8.0")
$webOrigen = Join-Path $raizProyecto "web"
$raiz = Join-Path $env:TEMP "pia_prueba_tareas_recompensas"
$base = "https://localhost:5000"
$wsHub = "wss://localhost:5000/chat"
$certificado = Join-Path $PSScriptRoot "..\certs\servidor.pfx"
$puertoStub = 5101
$enlaceStub = "http://127.0.0.1:$puertoStub/estado"

if (-not (Test-Path -LiteralPath $certificado)) {
    throw "No existe el certificado HTTPS. Genéralo con tools\generar_certificados.ps1"
}

if (-not (Test-Path -LiteralPath (Join-Path $binOrigen "ChatServer.dll"))) {
    throw "No existe ChatServer.dll. Compila primero con dotnet build."
}

if (Get-NetTCPConnection -LocalPort 5000 -State Listen -ErrorAction SilentlyContinue) {
    throw "El puerto 5000 ya está en uso."
}

if (Get-NetTCPConnection -LocalPort $puertoStub -State Listen -ErrorAction SilentlyContinue) {
    throw "El puerto $puertoStub ya está en uso."
}

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
        $json = $cuerpo | ConvertTo-Json -Depth 6 -Compress
        $params["Body"] = [System.Text.Encoding]::UTF8.GetBytes($json)
    }

    try {
        $r = Invoke-WebRequest @params
        $cuerpoRespuesta = $null
        try { $cuerpoRespuesta = $r.Content | ConvertFrom-Json } catch { }
        return [pscustomobject]@{
            Status = [int]$r.StatusCode
            Raw = $r.Content
            Body = $cuerpoRespuesta
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

function Esperar-Servidor {
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Milliseconds 500
        try {
            $x = Llamar "GET" "$base/api/usuarios"
            if ($x.Status -eq 401) { return }
        } catch { }
    }
    throw "El servidor de prueba no arrancó."
}

$script:recepciones = @{}
function Enviar-Trama($ws, [string]$texto) {
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($texto + [char]0x1e)
    $segmento = [System.ArraySegment[byte]]::new($bytes)
    [void]$ws.SendAsync(
        $segmento,
        [System.Net.WebSockets.WebSocketMessageType]::Text,
        $true,
        [System.Threading.CancellationToken]::None).GetAwaiter().GetResult()
}

function Recibir-Trama($ws, [int]$ms = 5000) {
    $estado = $script:recepciones[$ws]
    if ($null -eq $estado) {
        $buffer = New-Object byte[] 65536
        $estado = [pscustomobject]@{
            Tarea = $null
            Buffer = $buffer
            Segmento = [System.ArraySegment[byte]]::new($buffer)
            Texto = New-Object System.Text.StringBuilder
        }
        $script:recepciones[$ws] = $estado
    }

    $limite = [DateTime]::UtcNow.AddMilliseconds($ms)

    while ([DateTime]::UtcNow -lt $limite) {
        if ($null -eq $estado.Tarea) {
            $estado.Tarea = $ws.ReceiveAsync(
                $estado.Segmento,
                [System.Threading.CancellationToken]::None)
        }

        $restante = [int][Math]::Max(0, ($limite - [DateTime]::UtcNow).TotalMilliseconds)
        if (-not $estado.Tarea.Wait($restante)) { return $null }

        try {
            $r = $estado.Tarea.Result
        } catch {
            $estado.Tarea = $null
            throw $_.Exception.GetBaseException()
        }

        $estado.Tarea = $null

        if ($r.MessageType -eq [System.Net.WebSockets.WebSocketMessageType]::Close) {
            throw "El servidor cerró el WebSocket."
        }

        [void]$estado.Texto.Append(
            [System.Text.Encoding]::UTF8.GetString($estado.Buffer, 0, $r.Count))

        if ($r.EndOfMessage) {
            $texto = $estado.Texto.ToString()
            [void]$estado.Texto.Clear()
            return $texto
        }
    }

    return $null
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

function Invocar($ws, [string]$target, [object[]]$argumentos, [int]$ms = 5000) {
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
                }
            }
        }
    }

    throw "El hub no respondió a $target."
}

function Obtener-MensajeId([string]$token, [string]$chatId, [string]$tipo) {
    $url = "$base/api/mensajes?chatId=$([uri]::EscapeDataString($chatId))&limite=50"
    $r = Llamar "GET" $url $null $token
    if ($r.Status -ne 200) { return 0 }

    $mensajes = @($r.Body.Mensajes | Where-Object { $_.Tipo -eq $tipo })
    if ($mensajes.Count -eq 0) { return 0 }

    return [long](($mensajes | Sort-Object Id -Descending | Select-Object -First 1).Id)
}

function Subir-Imagen([string]$ruta, [string]$chatId, [string]$token) {
    $cliente = New-Object System.Net.Http.HttpClient
    $form = $null

    try {
        $cliente.DefaultRequestHeaders.Authorization =
            [System.Net.Http.Headers.AuthenticationHeaderValue]::new("Bearer", $token)
        $form = [System.Net.Http.MultipartFormDataContent]::new()
        $bytes = [System.IO.File]::ReadAllBytes($ruta)
        $parte = [System.Net.Http.ByteArrayContent]::new($bytes)
        $parte.Headers.ContentType =
            [System.Net.Http.Headers.MediaTypeHeaderValue]::new("image/png")
        $form.Add($parte, "archivo", "reto.png")
        $form.Add([System.Net.Http.StringContent]::new($chatId), "chatId")
        $form.Add([System.Net.Http.StringContent]::new("tmp-$([guid]::NewGuid())"), "tempId")

        $resp = $cliente.PostAsync("$base/api/archivos", $form)
        $resp.Wait()
        $resultado = $resp.Result
        $texto = $resultado.Content.ReadAsStringAsync().GetAwaiter().GetResult()

        return [pscustomobject]@{
            Status = [int]$resultado.StatusCode
            Raw = $texto
            Body = ($texto | ConvertFrom-Json)
        }
    } finally {
        if ($null -ne $form) { $form.Dispose() }
        $cliente.Dispose()
    }
}

function TareaDe($tareas, [int]$usuarioId, [int]$catalogoId) {
    $coincidencias = @($tareas | Where-Object {
        $_.AsignadoId -eq $usuarioId -and $_.CatalogoId -eq $catalogoId
    } | Select-Object -First 1)

    if ($coincidencias.Count -eq 0) { return $null }
    return $coincidencias[0]
}

function Finalizar-Tarea([int]$grupoId, [int]$tareaId, [long]$evidencia, [string]$token) {
    return Llamar "POST" `
        "$base/api/grupos/$grupoId/tareas/$tareaId/finalizar" `
        @{ EvidenciaMensajeId = $evidencia } $token
}

$resultados = New-Object System.Collections.ArrayList
function Afirmar([string]$nombre, [bool]$ok) {
    [void]$resultados.Add([pscustomobject]@{
        Prueba = $nombre
        Resultado = $(if ($ok) { "OK" } else { "FALLA" })
    })
}

Remove-Item $raiz -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path "$raiz\server" -Force | Out-Null
New-Item -ItemType Directory -Path "$raiz\cloudinary" -Force | Out-Null
Copy-Item $webOrigen "$raiz\web" -Recurse
Copy-Item $binOrigen "$raiz\server\bin" -Recurse
Remove-Item "$raiz\server\bin\chat.db*" -Force -ErrorAction SilentlyContinue

$pngBase64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/lqQAAAAASUVORK5CYII="
$rutaImagen = Join-Path $raiz "reto.png"
[System.IO.File]::WriteAllBytes(
    $rutaImagen, [Convert]::FromBase64String($pngBase64))

$env:PIA_CARPETA_DATOS = "$raiz\datos"
$env:PIA_CERTIFICADO = (Resolve-Path $certificado).Path
$env:Cloudinary__CloudName = "demo"
$env:Cloudinary__UploadPreset = "musichub_unsigned"
$env:Cloudinary__Carpeta = "musichub"
$env:Cloudinary__ApiBase = "http://127.0.0.1:$puertoStub"

$script:arranques = 0
function Iniciar-Servidor {
    $script:arranques++
    $salida = Join-Path $raiz "servidor$($script:arranques).out.log"
    $errores = Join-Path $raiz "servidor$($script:arranques).err.log"
    return Start-Process dotnet -ArgumentList "`"$raiz\server\bin\ChatServer.dll`"" `
        -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput $salida -RedirectStandardError $errores
}

$stub = $null
$ws = $null
$proc = $null

try {
    $stub = Start-Job -ArgumentList $puertoStub, $enlaceStub -ScriptBlock {
        param($puerto, $urlFinal)

        function Leer-Request($stream) {
            $memoria = New-Object System.IO.MemoryStream
            $buffer = New-Object byte[] 8192
            $finCabecera = -1
            $texto = ""

            while ($finCabecera -lt 0) {
                $n = $stream.Read($buffer, 0, $buffer.Length)
                if ($n -le 0) { return $null }
                $memoria.Write($buffer, 0, $n)
                $texto = [System.Text.Encoding]::GetEncoding(28591).GetString($memoria.ToArray())
                $finCabecera = $texto.IndexOf("`r`n`r`n")
            }

            $lineas = $texto.Substring(0, $finCabecera) -split "`r`n"
            $partes = $lineas[0] -split " "
            $cabeceras = @{}
            for ($i = 1; $i -lt $lineas.Count; $i++) {
                $indice = $lineas[$i].IndexOf(":")
                if ($indice -gt 0) {
                    $cabeceras[$lineas[$i].Substring(0, $indice).ToLowerInvariant()] =
                        $lineas[$i].Substring($indice + 1).Trim()
                }
            }

            $longitud = 0
            if ($cabeceras.ContainsKey("content-length")) {
                $longitud = [int]$cabeceras["content-length"]
            }

            $todos = $memoria.ToArray()
            $inicioCuerpo = $finCabecera + 4
            $restantes = $longitud - [Math]::Max(0, $todos.Length - $inicioCuerpo)

            while ($restantes -gt 0) {
                $n = $stream.Read($buffer, 0, [Math]::Min($buffer.Length, $restantes))
                if ($n -le 0) { break }
                $restantes -= $n
            }

            return [pscustomobject]@{ Metodo = $partes[0]; Ruta = $partes[1] }
        }

        function Responder($stream, [int]$codigo, [string]$mime, [string]$cuerpo) {
            $bytesCuerpo = [System.Text.Encoding]::UTF8.GetBytes($cuerpo)
            $motivo = if ($codigo -eq 200) { "OK" } else { "Not Found" }
            $cabecera = "HTTP/1.1 $codigo $motivo`r`n" +
                "Content-Type: $mime`r`n" +
                "Content-Length: $($bytesCuerpo.Length)`r`n" +
                "Connection: close`r`n`r`n"
            $bytes = [System.Text.Encoding]::ASCII.GetBytes($cabecera)
            $stream.Write($bytes, 0, $bytes.Length)
            $stream.Write($bytesCuerpo, 0, $bytesCuerpo.Length)
            $stream.Flush()
        }

        $listener = [System.Net.Sockets.TcpListener]::new(
            [System.Net.IPAddress]::Loopback, $puerto)
        $listener.Start()

        while ($true) {
            $cliente = $listener.AcceptTcpClient()
            try {
                $peticion = Leer-Request $cliente.GetStream()
                if ($null -eq $peticion) { continue }

                if ($peticion.Metodo -eq "GET" -and $peticion.Ruta -eq "/estado") {
                    Responder $cliente.GetStream() 200 "text/plain" "ok"
                    continue
                }

                if ($peticion.Metodo -eq "POST" -and
                    $peticion.Ruta -match "^/v1_1/[^/]+/(image|raw)/upload$") {
                    $json = @{
                        secure_url = $urlFinal
                        public_id = "stub/reto"
                        bytes = 100
                        resource_type = "image"
                    } | ConvertTo-Json -Compress
                    Responder $cliente.GetStream() 200 "application/json" $json
                    continue
                }

                Responder $cliente.GetStream() 404 "text/plain" "no"
            } catch {
                try {
                    Responder $cliente.GetStream() 500 "text/plain" "error"
                } catch { }
            } finally {
                try { $cliente.Close() } catch { }
            }
        }
    }

    $stubListo = $false
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 250
        try {
            $r = Invoke-WebRequest -Uri $enlaceStub -UseBasicParsing
            if ($r.StatusCode -eq 200) { $stubListo = $true; break }
        } catch { }
    }
    if (-not $stubListo) {
        throw "El stub de Cloudinary no arrancó: $(Receive-Job $stub -Keep)"
    }

    $proc = Iniciar-Servidor
    Esperar-Servidor

    $pass = "Clave#2026x"
    $tokens = @()
    $ids = @()

    foreach ($n in 1..3) {
        $correo = "tareas$n@mail.com"
        Llamar "POST" "$base/api/registrar" @{
            Nombre = "Tareas$n"
            Apellido = "Prueba"
            Usuario = "tareas$n"
            Correo = $correo
            Contrasena = $pass
        } | Out-Null
        $l = Llamar "POST" "$base/api/login" @{ Correo = $correo; Contrasena = $pass }
        $tokens += $l.Body.Token
        $ids += [int]$l.Body.Id
    }

$enlaceInvalido = Llamar "POST" "$base/api/grupos" @{
    Nombre = "Grupo enlace invalido"
    Miembros = @($ids[1], $ids[2])
    Enlace = "ftp://no-valido"
} $tokens[0]
Afirmar "crear grupo rechaza enlace invalido (400)" ($enlaceInvalido.Status -eq 400)

$grupo = Llamar "POST" "$base/api/grupos" @{
    Nombre = "Grupo retos"
    Miembros = @($ids[1], $ids[2])
    Enlace = $enlaceStub
} $tokens[0]
Afirmar "crear grupo con 3 integrantes y enlace (201)" (
    $grupo.Status -eq 201 -and $grupo.Body.RetosDisponibles -eq $true)
$grupoId = [int]$grupo.Body.Id
$chatId = "grupo:$grupoId"

$grupoSinEnlace = Llamar "POST" "$base/api/grupos" @{
    Nombre = "Grupo solo chat"
    Miembros = @($ids[1], $ids[2])
    Enlace = ""
} $tokens[0]
Afirmar "crear grupo sin enlace permite solo chat (201)" (
    $grupoSinEnlace.Status -eq 201 -and
    $grupoSinEnlace.Body.Enlace -eq "" -and
    $grupoSinEnlace.Body.RetosDisponibles -eq $false)
$grupoSinId = [int]$grupoSinEnlace.Body.Id

$tareasSinEnlace = Llamar "GET" `
    "$base/api/grupos/$grupoSinId/tareas" $null $tokens[0]
Afirmar "sin enlace el grupo no tiene retos" (
    $tareasSinEnlace.Status -eq 200 -and
    $tareasSinEnlace.Body.Disponibles -eq $false -and
    @($tareasSinEnlace.Body.Tareas).Count -eq 0)

$configSinEnlace = Llamar "GET" `
    "$base/api/grupos/$grupoSinId/recompensas" $null $tokens[0]
Afirmar "la configuracion sin enlace marca retos desactivados" (
    $configSinEnlace.Status -eq 200 -and
    $configSinEnlace.Body.RetosDisponibles -eq $false)

$finalizarSinEnlace = Llamar "POST" `
    "$base/api/grupos/$grupoSinId/tareas/1/finalizar" `
    @{ EvidenciaMensajeId = 1 } $tokens[1]
Afirmar "sin enlace no se pueden finalizar retos (400)" (
    $finalizarSinEnlace.Status -eq 400 -and
    $finalizarSinEnlace.Body.Error -match "no tiene retos habilitados")

$visitaSinEnlace = Llamar "GET" `
    "$base$($configSinEnlace.Body.MiEnlace)" $null ""
Afirmar "sin enlace la pagina de recompensa no entrega cartas" (
    $visitaSinEnlace.Status -eq 200 -and
    $visitaSinEnlace.Raw -match "no tiene retos habilitados")

$activarTardio = Llamar "PUT" `
    "$base/api/grupos/$grupoSinId/recompensas" @{
        Recompensas = @(5, 6, 7)
        Enlace = $enlaceStub
    } $tokens[0]
Afirmar "el creador puede activar retos despues (200)" (
    $activarTardio.Status -eq 200 -and
    $activarTardio.Body.RetosDisponibles -eq $true)

$tareasActivadas = Llamar "GET" `
    "$base/api/grupos/$grupoSinId/tareas" $null $tokens[0]
Afirmar "activar el enlace crea los 9 retos" (
    $tareasActivadas.Status -eq 200 -and
    $tareasActivadas.Body.Disponibles -eq $true -and
    @($tareasActivadas.Body.Tareas).Count -eq 9)

$tareas = Llamar "GET" "$base/api/grupos/$grupoId/tareas" $null $tokens[0]
    Afirmar "el grupo tiene 9 retos predefinidos (3 por integrante)" (
        $tareas.Status -eq 200 -and @($tareas.Body.Tareas).Count -eq 9)

    foreach ($id in $ids) {
        $mias = @($tareas.Body.Tareas | Where-Object { $_.AsignadoId -eq $id })
        Afirmar "el usuario $id tiene 3 retos" ($mias.Count -eq 3)
        Afirmar "los 3 retos del usuario $id son distintos" (
            (@($mias | ForEach-Object { $_.CatalogoId } | Sort-Object -Unique)).Count -eq 3)
    }

    $configMal = Llamar "PUT" "$base/api/grupos/$grupoId/recompensas" @{
        Recompensas = @(5, 6, 7)
        Enlace = $enlaceStub
    } $tokens[1]
    Afirmar "solo el creador configura las recompensas (403)" ($configMal.Status -eq 403)

    $config = Llamar "PUT" "$base/api/grupos/$grupoId/recompensas" @{
        Recompensas = @(5, 6, 7)
        Enlace = $enlaceStub
    } $tokens[0]
    Afirmar "el creador adjunta 3 cartas y el enlace (200)" (
        $config.Status -eq 200 -and @($config.Body.Plan).Count -eq 3 -and
        $config.Body.Enlace -eq $enlaceStub)

    $configMalCartas = Llamar "PUT" "$base/api/grupos/$grupoId/recompensas" @{
        Recompensas = @(5, 5, 6)
        Enlace = $enlaceStub
    } $tokens[0]
    Afirmar "rechaza cartas repetidas en la configuración (400)" (
        $configMalCartas.Status -eq 400)

    $ws = Conectar-Hub $tokens[1]

    $dmId = "dm:$($ids[0]):$($ids[1])"
    $unionDm = Invocar $ws "JoinGroup" @($dmId)
    Afirmar "el usuario 2 se une al privado de la barra" ($unionDm.Ok)

    $envioDm = Invocar $ws "SendToGroup" @(
        $dmId, "ultimo mensaje privado", "tmp-dm-barra")
    Afirmar "el usuario 2 envia el ultimo mensaje privado" ($envioDm.Ok)

    $barraPrivada = Llamar "GET" "$base/api/conversaciones" $null $tokens[0]
    $entradaPrivada = @($barraPrivada.Body.Conversaciones | Where-Object {
        $_.ChatId -eq $dmId
    })
    Afirmar "la barra privada expone el ultimo mensaje" (
        $entradaPrivada.Count -eq 1 -and
        $entradaPrivada[0].UltimoMensaje -eq "ultimo mensaje privado" -and
        $entradaPrivada[0].UltimoTipo -eq "texto")

    foreach ($par in @(
        @{ Target = "SendToGroup"; Args = @($chatId, "mensaje de reto", "tmp-texto") },
        @{ Target = "SendToGroupUbicacion"; Args = @($chatId, 19.43, -99.13, "Mi ubicación", "tmp-ubi") }
    )) {
        $envio = Invocar $ws $par.Target $par.Args
        Afirmar "el usuario 2 ejecuta $($par.Target)" ($envio.Ok)
    }

    $subida = Subir-Imagen $rutaImagen $chatId $tokens[1]
    Afirmar "el usuario 2 sube una imagen al grupo (200)" (
        $subida.Status -eq 200 -and $subida.Body.Tipo -eq "archivo")

    $idTexto = Obtener-MensajeId $tokens[1] $chatId "texto"
    $idUbi = Obtener-MensajeId $tokens[1] $chatId "ubicacion"
    $idArchivo = Obtener-MensajeId $tokens[1] $chatId "archivo"
    Afirmar "se encuentran las 3 evidencias" (
        $idTexto -gt 0 -and $idUbi -gt 0 -and $idArchivo -gt 0)

    $barraGrupo1 = Llamar "GET" "$base/api/grupos" $null $tokens[1]
    $entradaGrupo1 = @($barraGrupo1.Body.Grupos | Where-Object {
        $_.Id -eq $grupoId
    })
    Afirmar "la barra de grupos expone el ultimo mensaje" (
        $entradaGrupo1.Count -eq 1 -and
        $entradaGrupo1[0].UltimoTipo -eq "archivo" -and
        -not [string]::IsNullOrEmpty([string]$entradaGrupo1[0].UltimoUsuario) -and
        -not [string]::IsNullOrEmpty([string]$entradaGrupo1[0].UltimaFecha))

    $tareaTexto = TareaDe $tareas.Body.Tareas $ids[1] 1
    $tareaArchivo = TareaDe $tareas.Body.Tareas $ids[1] 2
    $tareaUbi = TareaDe $tareas.Body.Tareas $ids[1] 3

    $evidenciaMala = Finalizar-Tarea $grupoId $tareaTexto.Id $idUbi $tokens[1]
    Afirmar "rechaza evidencia del tipo equivocado (400)" ($evidenciaMala.Status -eq 400)

    $sinEvidencia = Llamar "POST" `
        "$base/api/grupos/$grupoId/tareas/$($tareaTexto.Id)/finalizar" $null $tokens[1]
    Afirmar "rechaza finalizar sin evidencia (400)" ($sinEvidencia.Status -eq 400)

    $uno = Finalizar-Tarea $grupoId $tareaTexto.Id $idTexto $tokens[1]
    Afirmar "verifica el mensaje de texto (200)" (
        $uno.Status -eq 200 -and @($uno.Body.Recompensas).Count -eq 0)

    $dos = Finalizar-Tarea $grupoId $tareaUbi.Id $idUbi $tokens[1]
    Afirmar "verifica la ubicación (200)" (
        $dos.Status -eq 200 -and @($dos.Body.Recompensas).Count -eq 0)

    $tres = Finalizar-Tarea $grupoId $tareaArchivo.Id $idArchivo $tokens[1]
    Afirmar "verifica la imagen y entrega 2 cartas (200)" (
        $tres.Status -eq 200 -and @($tres.Body.Recompensas).Count -eq 2)

    $coleccion = Llamar "GET" "$base/api/usuarios/$($ids[1])/recompensas" $null $tokens[1]
    Afirmar "la colección tiene 2 cartas tras los 3 retos" (
        @($coleccion.Body.Recompensas).Count -eq 2)

    $ajena = Llamar "GET" "$base/api/usuarios/$($ids[1])/recompensas" $null $tokens[2]
Afirmar "otro integrante ve la colección y el nombre (200)" (
    $ajena.Status -eq 200 -and @($ajena.Body.Recompensas).Count -eq 2 -and
    $ajena.Body.Nombre -eq "Tareas2 Prueba")

    $reintento = Llamar "POST" `
        "$base/api/grupos/$grupoId/tareas/$($tareaArchivo.Id)/finalizar" $null $tokens[1]
    Afirmar "refinalizar no duplica recompensas" (
        $reintento.Status -eq 409 -and
        (Llamar "GET" "$base/api/usuarios/$($ids[1])/recompensas" $null $tokens[1]).Body.Recompensas.Count -eq 2)

    $configActual = Llamar "GET" "$base/api/grupos/$grupoId/recompensas" $null $tokens[1]
    Afirmar "cada integrante obtiene su propio enlace" (
        $configActual.Status -eq 200 -and $configActual.Body.MiEnlace -match "^/r/[0-9a-f]+$")

    $visita = Llamar "GET" "$base$($configActual.Body.MiEnlace)" $null ""
    Afirmar "visitar el enlace entrega la última carta" (
        $visita.Status -eq 200 -or $visita.Status -eq 302)

    $coleccionFinal = Llamar "GET" "$base/api/usuarios/$($ids[1])/recompensas" $null $tokens[1]
    $recompensasFinales = @($coleccionFinal.Body.Recompensas)
    $delEnlace = @($recompensasFinales | Where-Object { $_.Origen -eq "enlace" })
    Afirmar "la colección queda con 3 cartas" ($recompensasFinales.Count -eq 3)
    Afirmar "la tercera carta viene del enlace" ($delEnlace.Count -eq 1)

    $visitaOtra = Llamar "GET" "$base$($configActual.Body.MiEnlace)" $null ""
    $coleccionTrasOtraVisita = Llamar "GET" `
        "$base/api/usuarios/$($ids[1])/recompensas" $null $tokens[1]
    Afirmar "visitar dos veces no entrega una cuarta carta" (
        (@($coleccionTrasOtraVisita.Body.Recompensas).Count -eq 3))

$grupo2 = Llamar "POST" "$base/api/grupos" @{
    Nombre = "Grupo retos 2"
    Miembros = @($ids[1], $ids[2])
    Enlace = $enlaceStub
} $tokens[0]
    $grupoId2 = [int]$grupo2.Body.Id
    $chatId2 = "grupo:$grupoId2"

    Llamar "PUT" "$base/api/grupos/$grupoId2/recompensas" @{
        Recompensas = @(5, 6, 7)
        Enlace = $enlaceStub
    } $tokens[0] | Out-Null

    foreach ($par in @(
        @{ Target = "SendToGroup"; Args = @($chatId2, "mensaje del grupo 2", "tmp-texto2") },
        @{ Target = "SendToGroupUbicacion"; Args = @($chatId2, 20.1, -100.2, "Ubicación 2", "tmp-ubi2") }
    )) {
        Invocar $ws $par.Target $par.Args | Out-Null
    }
    $subida2 = Subir-Imagen $rutaImagen $chatId2 $tokens[1]

    $barraGrupos = Llamar "GET" "$base/api/grupos" $null $tokens[1]
    $entradaGrupo2 = @($barraGrupos.Body.Grupos | Where-Object {
        $_.Id -eq $grupoId2
    })
    Afirmar "el grupo 2 muestra su ultimo mensaje en la barra" (
        $entradaGrupo2.Count -eq 1 -and
        $entradaGrupo2[0].UltimoTipo -eq "archivo" -and
        -not [string]::IsNullOrEmpty([string]$entradaGrupo2[0].UltimoUsuario) -and
        -not [string]::IsNullOrEmpty([string]$entradaGrupo2[0].UltimaFecha))

    $idsBarra = @($barraGrupos.Body.Grupos | ForEach-Object { [int]$_.Id })
    $posGrupo1 = [array]::IndexOf($idsBarra, $grupoId)
    $posGrupo2 = [array]::IndexOf($idsBarra, $grupoId2)
    Afirmar "los grupos se ordenan por actividad reciente" (
        $posGrupo1 -ge 0 -and $posGrupo2 -ge 0 -and $posGrupo2 -lt $posGrupo1)

    $tareas2 = Llamar "GET" "$base/api/grupos/$grupoId2/tareas" $null $tokens[1]
    $t2Texto = TareaDe $tareas2.Body.Tareas $ids[1] 1
    $t2Archivo = TareaDe $tareas2.Body.Tareas $ids[1] 2
    $t2Ubi = TareaDe $tareas2.Body.Tareas $ids[1] 3

    Finalizar-Tarea $grupoId2 $t2Texto.Id `
        (Obtener-MensajeId $tokens[1] $chatId2 "texto") $tokens[1] | Out-Null
    Finalizar-Tarea $grupoId2 $t2Ubi.Id `
        (Obtener-MensajeId $tokens[1] $chatId2 "ubicacion") $tokens[1] | Out-Null
    Finalizar-Tarea $grupoId2 $t2Archivo.Id `
        (Obtener-MensajeId $tokens[1] $chatId2 "archivo") $tokens[1] | Out-Null

    $dosGrupos = Llamar "GET" "$base/api/usuarios/$($ids[1])/recompensas" $null $tokens[1]
    $cartasDosGrupos = @($dosGrupos.Body.Recompensas)
    Afirmar "las cartas se pueden repetir en otro grupo (5 cartas)" (
        $cartasDosGrupos.Count -eq 5)
    Afirmar "no se repite la misma carta dentro de un grupo" (
        (@($cartasDosGrupos | Where-Object {
            $_.GrupoId -eq $grupoId2
        } | ForEach-Object { $_.Id } | Sort-Object -Unique)).Count -eq 2)

    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
    Start-Sleep -Milliseconds 800
    $proc = Iniciar-Servidor
    Esperar-Servidor

    $persistenTareas = Llamar "GET" "$base/api/grupos/$grupoId/tareas" $null $tokens[0]
    Afirmar "los retos persisten tras reiniciar" (
        @($persistenTareas.Body.Tareas).Count -eq 9)

    $persistenCartas = Llamar "GET" `
        "$base/api/usuarios/$($ids[1])/recompensas" $null $tokens[1]
    Afirmar "las recompensas persisten tras reiniciar" (
        @($persistenCartas.Body.Recompensas).Count -eq 5)
}
finally {
    if ($ws) {
        try { $ws.Dispose() } catch { }
    }
    if ($proc -and -not $proc.HasExited) {
        Stop-Process -Id $proc.Id -Force
    }
    if ($stub) {
        Stop-Job $stub -ErrorAction SilentlyContinue
        Remove-Job $stub -Force -ErrorAction SilentlyContinue
    }
    Start-Sleep -Milliseconds 500
    Remove-Item $raiz -Recurse -Force -ErrorAction SilentlyContinue
}

$resultados | Format-Table -AutoSize | Out-String -Width 220
if ($resultados | Where-Object { $_.Resultado -ne "OK" }) {
    throw "Hay pruebas que fallaron."
}
