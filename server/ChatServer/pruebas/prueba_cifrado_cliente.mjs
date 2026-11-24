/* Pruebas del cifrado del navegador (web/cifrado.js).
 *
 * Carga el archivo real del cliente en un contexto de Node con WebCrypto e
 * IndexedDB simulada. Comprueba que ambos extremos derivan la misma clave,
 * que el ida y vuelta funciona, que las claves nuevas no son extraibles, que
 * los pares antiguos se conservan y que una clave o un mensaje alterados
 * fallan.
 *
 * Uso:  node pruebas\prueba_cifrado_cliente.mjs
 */

import { webcrypto as crypto } from "node:crypto";
import { readFileSync } from "node:fs";
import vm from "node:vm";

const ECDH = { name: "ECDH", namedCurve: "P-256" };

const resultados = [];

function afirmar(nombre, ok) {
    resultados.push({ nombre, ok: !!ok });
}

function aBase64(buffer) {
    return Buffer.from(new Uint8Array(buffer)).toString("base64");
}

function deBase64(texto) {
    return new Uint8Array(Buffer.from(texto, "base64"));
}

function crearIndexedDbFalso(datos = new Map()) {
    function transaccion() {
        const tx = {};

        tx.objectStore = () => ({
            get(clave) {
                const solicitud = {};

                queueMicrotask(() => {
                    solicitud.result = datos.get(clave);
                    solicitud.onsuccess?.();
                });

                return solicitud;
            },
            put(valor, clave) {
                datos.set(clave, valor);
                queueMicrotask(() => tx.oncomplete?.());
            }
        });

        return tx;
    }

    return {
        open() {
            const solicitud = {};

            queueMicrotask(() => {
                solicitud.result = {
                    objectStoreNames: { contains: () => true },
                    transaction: transaccion,
                    close() {}
                };
                solicitud.onupgradeneeded?.();
                solicitud.onsuccess?.();
            });

            return solicitud;
        },
        datos
    };
}

function cargarCifrado(datos) {
    const indice = crearIndexedDbFalso(datos);
    const contexto = {
        crypto,
        indexedDB: indice,
        TextEncoder,
        TextDecoder,
        Uint8Array,
        Promise,
        Map,
        Date,
        Error,
        console,
        setTimeout,
        queueMicrotask,
        btoa: (texto) => Buffer.from(texto, "binary").toString("base64"),
        atob: (texto) => Buffer.from(texto, "base64").toString("binary")
    };

    contexto.window = contexto;
    vm.createContext(contexto);
    vm.runInContext(
        readFileSync(
            new URL("../../../web/cifrado.js", import.meta.url),
            "utf8"
        ),
        contexto
    );

    return { Cifrado: contexto.Cifrado, datos: indice.datos };
}

async function exportarPublica(par) {
    return aBase64(await crypto.subtle.exportKey("spki", par.publicKey));
}

async function principal() {
    const alice = cargarCifrado();
    const bob = cargarCifrado();

    const publicaAlice = await alice.Cifrado.publicaBase64();
    const publicaBob = await bob.Cifrado.publicaBase64();

    afirmar(
        "cada dispositivo exporta una clave publica SPKI P-256",
        deBase64(publicaAlice).length === 91 &&
            deBase64(publicaBob).length === 91 &&
            publicaAlice !== publicaBob
    );

    const parAlice = await alice.Cifrado.obtenerPar();
    afirmar(
        "las claves nuevas tienen la privada no extraible",
        parAlice.privada.extractable === false &&
            parAlice.publica.extractable === true
    );

    let exportacionPrivada = false;

    try {
        await crypto.subtle.exportKey("pkcs8", parAlice.privada);
    } catch (error) {
        exportacionPrivada = true;
    }

    afirmar("la clave privada no se puede exportar", exportacionPrivada);

    const claveAlice = await alice.Cifrado.derivarClave(publicaBob);
    const claveBob = await bob.Cifrado.derivarClave(publicaAlice);

    afirmar(
        "ambos extremos derivan una clave AES-256",
        claveAlice.algorithm.name === "AES-GCM" &&
            claveAlice.algorithm.length === 256 &&
            claveBob.algorithm.length === 256
    );

    const mensaje = "Hola Bob: acentos, eñes y \"comillas\".";
    const salida = await alice.Cifrado.cifrar(mensaje, claveAlice);

    afirmar(
        "el mensaje cifrado usa IV de 12 bytes",
        deBase64(salida.iv).length === alice.Cifrado.IV_BYTES
    );

    afirmar(
        "el ciphertext no contiene el texto plano",
        !salida.texto.includes("Hola") &&
            deBase64(salida.texto).length > mensaje.length
    );

    const recibido = await bob.Cifrado.descifrar(
        salida.texto,
        salida.iv,
        claveBob
    );
    afirmar("Bob descifra exactamente lo que envio Alice", recibido === mensaje);

    const propio = await alice.Cifrado.descifrar(
        salida.texto,
        salida.iv,
        claveAlice
    );
    afirmar(
        "Alice descifra su propio mensaje con la clave del destinatario",
        propio === mensaje
    );

    const otraSalida = await alice.Cifrado.cifrar(mensaje, claveAlice);
    afirmar(
        "el mismo texto cifrado dos veces produce ciphertext e IV distintos",
        otraSalida.texto !== salida.texto && otraSalida.iv !== salida.iv
    );

    const tercero = cargarCifrado();
    const publicaTercero = await tercero.Cifrado.publicaBase64();
    const claveTercero = await tercero.Cifrado.derivarClave(publicaAlice);
    let falloAjeno = false;

    try {
        await tercero.Cifrado.descifrar(salida.texto, salida.iv, claveTercero);
    } catch (error) {
        falloAjeno = true;
    }

    afirmar("un tercer dispositivo no puede descifrar", falloAjeno);
    afirmar(
        "la clave publica del tercero es distinta",
        publicaTercero !== publicaAlice && publicaTercero !== publicaBob
    );

    let falloAlterado = false;
    const alterado = deBase64(salida.texto);
    alterado[0] ^= 0xff;

    try {
        await bob.Cifrado.descifrar(aBase64(alterado), salida.iv, claveBob);
    } catch (error) {
        falloAlterado = true;
    }

    afirmar("un ciphertext alterado no descifra", falloAlterado);

    let falloIv = false;
    const ivAlterado = deBase64(salida.iv);
    ivAlterado[0] ^= 0xff;

    try {
        await bob.Cifrado.descifrar(
            salida.texto,
            aBase64(ivAlterado),
            claveBob
        );
    } catch (error) {
        falloIv = true;
    }

    afirmar("un IV alterado no descifra", falloIv);

    const vacio = await alice.Cifrado.cifrar("", claveAlice);
    const vacioDescifrado = await bob.Cifrado.descifrar(
        vacio.texto,
        vacio.iv,
        claveBob
    );
    afirmar("un mensaje vacio tambien hace ida y vuelta", vacioDescifrado === "");

    const largo = "x".repeat(5000);
    const salidaLarga = await alice.Cifrado.cifrar(largo, claveAlice);
    const largoDescifrado = await bob.Cifrado.descifrar(
        salidaLarga.texto,
        salidaLarga.iv,
        claveBob
    );
    afirmar("un mensaje largo hace ida y vuelta", largoDescifrado === largo);

    let formatoInvalido = false;

    try {
        await alice.Cifrado.derivarClave("AAAA");
    } catch (error) {
        formatoInvalido = true;
    }

    afirmar("una clave publica mal formada se rechaza", formatoInvalido);

    const otroPar = await crypto.subtle.generateKey(ECDH, true, [
        "deriveKey",
        "deriveBits"
    ]);
    const datosAntiguos = new Map([
        [
            "par-ecdh",
            { publica: otroPar.publicKey, privada: otroPar.privateKey }
        ]
    ]);
    const antiguo = cargarCifrado(datosAntiguos);
    const parAntiguo = await antiguo.Cifrado.obtenerPar();
    const publicaAntigua = await exportarPublica(otroPar);

    afirmar(
        "un par antiguo extraible se reutiliza sin regenerarlo",
        parAntiguo.publica === otroPar.publicKey &&
            parAntiguo.privada === otroPar.privateKey &&
            parAntiguo.privada.extractable === true &&
            (await antiguo.Cifrado.publicaBase64()) === publicaAntigua
    );
}

try {
    await principal();
} catch (error) {
    console.error("Error inesperado:", error);
    process.exitCode = 1;
}

for (const resultado of resultados) {
    console.log(`${resultado.ok ? "OK" : "FALLA"}  ${resultado.nombre}`);
}

const fallas = resultados.filter((resultado) => !resultado.ok);

if (fallas.length > 0) {
    console.log(`FALLARON ${fallas.length} prueba(s).`);
    process.exitCode = 1;
} else {
    console.log(`Todas las pruebas pasaron (${resultados.length}).`);
}
