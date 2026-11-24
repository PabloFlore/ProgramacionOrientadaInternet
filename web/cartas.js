const CARTAS = Array.from({ length: 18 }, (_, indice) => {
    const numero = indice + 1;
    const etiqueta = String(numero).padStart(2, "0");

    return {
        Id: numero,
        Codigo: `photocard-${etiqueta}`,
        Nombre: `Photocard ${etiqueta}`,
        Imagen: `fotocards/${numero}.png`
    };
});
