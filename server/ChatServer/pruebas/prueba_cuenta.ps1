# Pruebas de cuenta, sesiones y login contra el backend.
#
# Requisitos: haber compilado el backend (dotnet build en server/ChatServer).
# Qué hace: copia web/ y el binario a una carpeta temporal con su propia
# base de datos, arranca el servidor ahí, ejecuta las pruebas y lo detiene.
# No toca server/ChatServer/bin/Debug/net8.0/chat.db.
#
# Uso:  powershell -NoProfile -ExecutionPolicy Bypass -File pruebas\prueba_cuenta.ps1

$ErrorActionPreference = "Stop"

$raizProyecto = Resolve-Path (Join-Path $PSScriptRoot "..\..\..")
$binOrigen = Resolve-Path (Join-Path $PSScriptRoot "..\bin\Debug\net8.0")
$webOrigen = Join-Path $raizProyecto "web"
$raiz = Join-Path $env:TEMP "pia_prueba_cuenta"
$base = "http://localhost:5000"

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

function Perfil([string]$token, [string]$correo, $extra) {
    $c = @{ Nombre = "Ana"; Apellido = "Uno"; Usuario = "ana"; Correo = $correo }
    foreach ($k in $extra.Keys) { $c[$k] = $extra[$k] }
    return Llamar "PATCH" "$base/api/cuenta" $c $token
}

$resultados = New-Object System.Collections.ArrayList
function Afirmar([string]$nombre, [bool]$ok) {
    [void]$resultados.Add([pscustomobject]@{ Prueba = $nombre; Resultado = $(if ($ok) { "OK" } else { "FALLA" }) })
}

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
    $img = "data:image/png;base64,iVBORw0KGgo="

    # Registro
    $a = Llamar "POST" "$base/api/registrar" @{ Nombre = "Ana"; Apellido = "Uno"; Usuario = "ana"; Correo = "Ana.Test@Mail.com"; Contrasena = $pass }
    Afirmar "registro A responde 201" ($a.Status -eq 201)

    $b = Llamar "POST" "$base/api/registrar" @{ Nombre = "Beto"; Apellido = "Dos"; Usuario = "beto"; Correo = "beto@mail.com"; Contrasena = $pass }
    Afirmar "registro B responde 201" ($b.Status -eq 201)

    $larga = "Aa1#" + ("x" * 130)
    $r = Llamar "POST" "$base/api/registrar" @{ Nombre = "L"; Apellido = "L"; Usuario = "l"; Correo = "larga@mail.com"; Contrasena = $larga }
    Afirmar "contrasena de 134 caracteres se rechaza en registro (400)" ($r.Status -eq 400)

    # Login y token
    $l1 = Llamar "POST" "$base/api/login" @{ Correo = "ANA.test@mail.com"; Contrasena = $pass }
    Afirmar "login con correo en mayusculas responde 200" ($l1.Status -eq 200)
    Afirmar "login ya no devuelve imagenes (van por GET /api/cuenta)" (-not ($l1.Raw -match "FotoPerfil|Banner"))
    $t1 = $l1.Body.Token

    $l2 = Llamar "POST" "$base/api/login" @{ Correo = "ana.test@mail.com"; Contrasena = $pass }
    $t2 = $l2.Body.Token

    # Perfil con contrasena actual incorrecta: no debe cambiar nada
    $mal = Perfil $t1 "ana.test@mail.com" @{ Nombre = "Nuevo"; ContrasenaActual = "Mala#2026x"; ContrasenaNueva = "Otra#2026y" }
    Afirmar "cambio con contrasena actual incorrecta responde 401" ($mal.Status -eq 401)

    $sinActual = Perfil $t1 "ana.test@mail.com" @{ ContrasenaNueva = "Otra#2026y" }
    Afirmar "nueva contrasena sin actual responde 400" ($sinActual.Status -eq 400)

    $debil = Perfil $t1 "ana.test@mail.com" @{ ContrasenaActual = $pass; ContrasenaNueva = "debil" }
    Afirmar "nueva contrasena debil responde 400" ($debil.Status -eq 400)

    $relog = Llamar "POST" "$base/api/login" @{ Correo = "ana.test@mail.com"; Contrasena = $pass }
    Afirmar "tras el intento fallido el perfil no cambio (Nombre sigue Ana)" ($relog.Body.Nombre -eq "Ana")
    $t3 = $relog.Body.Token

    # Foto y banner
    $p = Perfil $t1 "ana.test@mail.com" @{ FotoPerfil = $img; Banner = $img }
    Afirmar "PATCH con foto y banner responde 200" ($p.Status -eq 200)

    $cuenta = Llamar "GET" "$base/api/cuenta" $null $t1
    Afirmar "GET /api/cuenta devuelve la foto y el banner guardados" ($cuenta.Status -eq 200 -and $cuenta.Body.FotoPerfil -eq $img -and $cuenta.Body.Banner -eq $img)
    $t4 = $t1

    $usuarios = Llamar "GET" "$base/api/usuarios" $null $t1
    Afirmar "GET /api/usuarios no incluye imagenes" (-not ($usuarios.Raw -match "FotoPerfil|Banner"))

    $grande = "data:image/png;base64," + ("A" * 700000)
    $g = Perfil $t1 "ana.test@mail.com" @{ FotoPerfil = $grande }
    Afirmar "imagen de mas de 700 000 caracteres responde 400" ($g.Status -eq 400)

    $noImg = Perfil $t1 "ana.test@mail.com" @{ FotoPerfil = "hola" }
    Afirmar "texto que no es data URL responde 400" ($noImg.Status -eq 400)

    $svg = Perfil $t1 "ana.test@mail.com" @{ FotoPerfil = "data:image/svg+xml;base64,PHN2Zz48L3N2Zz4=" }
    Afirmar "SVG se rechaza (400)" ($svg.Status -eq 400)

    $b64 = Perfil $t1 "ana.test@mail.com" @{ FotoPerfil = "data:image/png;base64,@@@@" }
    Afirmar "base64 invalido se rechaza (400)" ($b64.Status -eq 400)

    # Correo duplicado sin distinguir mayusculas
    $dup = Perfil $t1 "BETO@mail.com" @{}
    Afirmar "correo de otra cuenta en mayusculas responde 409" ($dup.Status -eq 409)

    # Cambio de contrasena: cierra las demas sesiones
    $cambio = Perfil $t1 "ana.test@mail.com" @{ ContrasenaActual = $pass; ContrasenaNueva = "Nueva#2026z" }
    Afirmar "cambio de contrasena responde 200 con ContrasenaCambiada" ($cambio.Status -eq 200 -and $cambio.Body.ContrasenaCambiada -eq $true)

    $v1 = Llamar "GET" "$base/api/usuarios" $null $t1
    Afirmar "la sesion que cambio la contrasena sigue activa" ($v1.Status -eq 200)

    $cerradas = @(
        (Llamar "GET" "$base/api/usuarios" $null $t2).Status,
        (Llamar "GET" "$base/api/usuarios" $null $t3).Status
    )
    Afirmar "las otras sesiones de la cuenta quedan invalidas (401)" ($cerradas -notcontains 200)

    $viejaFunciona = Llamar "POST" "$base/api/login" @{ Correo = "ana.test@mail.com"; Contrasena = $pass }
    Afirmar "la contrasena anterior ya no sirve" ($viejaFunciona.Status -eq 401)

    $nuevaFunciona = Llamar "POST" "$base/api/login" @{ Correo = "ana.test@mail.com"; Contrasena = "Nueva#2026z" }
    Afirmar "la contrasena nueva sirve" ($nuevaFunciona.Status -eq 200)

    # Limite de intentos (cuenta B, sin afectar a A)
    for ($i = 0; $i -lt 5; $i++) {
        $f = Llamar "POST" "$base/api/login" @{ Correo = "beto@mail.com"; Contrasena = "Mala#2026x" }
    }
    Afirmar "5 fallos seguidos responden 401" ($f.Status -eq 401)

    $bloq = Llamar "POST" "$base/api/login" @{ Correo = "beto@mail.com"; Contrasena = $pass }
    Afirmar "tras 5 fallos, login correcto de B responde 429" ($bloq.Status -eq 429)

    $otra = Llamar "POST" "$base/api/login" @{ Correo = "ana.test@mail.com"; Contrasena = "Nueva#2026z" }
    Afirmar "el bloqueo de B no afecta a A" ($otra.Status -eq 200)

    # Logout
    $tokenNuevo = $otra.Body.Token
    $out = Llamar "POST" "$base/api/logout" $null $tokenNuevo
    $despues = Llamar "GET" "$base/api/usuarios" $null $tokenNuevo
    Afirmar "logout invalida el token" ($out.Status -eq 200 -and $despues.Status -eq 401)
}
finally {
    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
    Start-Sleep -Milliseconds 500
    Remove-Item $raiz -Recurse -Force -ErrorAction SilentlyContinue
}

$resultados | Format-Table -AutoSize | Out-String -Width 200
