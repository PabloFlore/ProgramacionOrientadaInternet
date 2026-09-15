const chats = {
    amigos: {
        name: "Amigos",
        status: "2 integrantes",
        image: "https://i.pinimg.com/736x/e8/11/c3/e811c31c34cd0789d5f35939a065802c.jpg",

        messages: [
            {
                type: "received",
                user: "Arinator",
                image: "https://i.pinimg.com/736x/e8/11/c3/e811c31c34cd0789d5f35939a065802c.jpg",
                text: "Lorem ipsum dolor sit amet"
            },

            {
                type: "sent",
                user: "Tú",
                text: "Lorem ipsum dolor sit amet"
            },

            {
                type: "received",
                user: "Arinator",
                image: "https://i.pinimg.com/736x/e8/11/c3/e811c31c34cd0789d5f35939a065802c.jpg",
                text: "Lorem ipsum dolor sit amet"
            }
        ]
    },

    gaming: {
        name: "Twenty One Pilots se robo mi radio",
        status: "6 integrantes",
        image: "https://i.pinimg.com/1200x/0b/de/25/0bde25514b531ada811674223955fc04.jpg",

        messages: [

            {
                type: "received",
                user: "Santi",
                image: "https://i.pinimg.com/736x/bd/48/ab/bd48abfbc13c060ce7f8bbd86ddb9b1e.jpg",
                text: "Lorem ipsum dolor sit amet"
            },

            {
                type: "sent",
                user: "Tú",
                text: "Lorem ipsum dolor sit amet"
            },

            {
                type: "received",
                user: "Luz",
                image: "https://i.pinimg.com/736x/13/49/c2/1349c2587cf022fb1f2b3a7caa822a5b.jpg",
                text: "Lorem ipsum dolor sit amet"
            }
        ]
    },

    universidad: {
        name: "Nedsito",
        status: "2 integrantes",
        image: "https://i.pinimg.com/736x/01/39/d9/0139d91c48aeaf302e28121a46093b34.jpg",
        messages: [
            {
                type: "received",
                user: "Nedsito",
                image: "https://i.pinimg.com/736x/01/39/d9/0139d91c48aeaf302e28121a46093b34.jpg",
                text: "Lorem ipsum dolor sit amet"
            },

            {
                type: "sent",
                user: "Tú",
                text: "Lorem ipsum dolor sit amet"
            },

            {
                type: "received",
                user: "Nedsito",
                image: "https://i.pinimg.com/736x/01/39/d9/0139d91c48aeaf302e28121a46093b34.jpg",
                text: "Lorem ipsum dolor sit amet"
            }
        ]
    },

    proyecto: {
        name: "Aquí amamos a Josh Dun",
        status: "5 integrantes",
        image: "https://i.pinimg.com/1200x/6e/44/81/6e44814a20c65645359c3731e49bbf43.jpg",

        messages: [
            {
                type: "received",
                user: "Tyler Joseph",
                image: "https://i.pinimg.com/1200x/d3/0b/1e/d30b1e0a84b615d1f1198e19c3b9f5dc.jpg",
                text: "Lorem ipsum dolor sit amet"
            },

            {
                type: "sent",
                user: "Tú",
                text: "Lorem ipsum dolor sit amet"
            },

            {
                type: "received",
                user: "Mark",
                image: "https://i.pinimg.com/736x/cd/e2/3b/cde23b47783f2a66a408321ce599a84f.jpg",
                text: "Lorem ipsum dolor sit amet"
            }
        ]
    }
};

/* Moverse de chats */

const chatButtons =
    document.querySelectorAll(".chat-icon");
const currentChatName =
    document.getElementById("currentChatName");
const currentChatStatus =
    document.getElementById("currentChatStatus");
const currentChatImage =
    document.getElementById("currentChatImage");
const messagesContainer =
    document.getElementById("messages");

chatButtons.forEach(button => {
    button.addEventListener("click", () => {
        const chatId =
            button.dataset.chat;
        const chat =
            chats[chatId];
        if (!chat) {
            return;
        }

        /* Marcar chat seleccionado */
        chatButtons.forEach(item => {

            item.classList.remove("active");

        });

        button.classList.add("active");

        /* Actualizar chat */

        currentChatName.textContent =
            chat.name;

        currentChatStatus.textContent =
            chat.status;

        currentChatImage.src =
            chat.image;

        messagesContainer.innerHTML = "";

        chat.messages.forEach(message => {
            const messageElement =
                document.createElement("div");
            messageElement.classList.add(
                "message",
                message.type
            );

            let html = "";

            if (message.type === "received") {
                html += `
                    <img
                        src="${message.image}"
                        alt="${message.user}"
                    >
                `;
            }

            html += `
                <div class="message-content">

                    <strong>
                        ${message.user}
                    </strong>

                    <p>
                        ${message.text}
                    </p>

                </div>
            `;
            messageElement.innerHTML =
                html;
            messagesContainer.appendChild(
                messageElement
            );
        });
    });
});

/* TAREAS */

const taskLinks =
    document.querySelectorAll(".task-link");

taskLinks.forEach(link => {
    link.addEventListener("click", () => {
        const taskId =
            link.dataset.task;
        const task =
            document.getElementById(taskId);
        const status =
            task.querySelector(".task-status");
        localStorage.setItem(
            taskId,
            "visitado"
        );
        task.classList.add("completed");
        status.textContent =
            "✓ Completada";
    });
});


/* Revisar si el usuario entro al link */

taskLinks.forEach(link => {

    const taskId =
        link.dataset.task;

    const task =
        document.getElementById(taskId);

    const status =
        task.querySelector(".task-status");


    if (
        localStorage.getItem(taskId)
        === "visitado"
    ) {

        task.classList.add("completed");

        status.textContent =
            "✓ Completada";

    }

});


/* Mensajes de ejemplo (borrar cuando tengamos el server) */

const messageInput =
    document.getElementById("messageInput");

const sendButton =
    document.getElementById("sendButton");

function sendMessage() {
    const text =
        messageInput.value.trim();

    if (text === "") {
        return;
    }

    const message =
        document.createElement("div");

    message.classList.add(
        "message",
        "sent"
    );

    message.innerHTML = `

        <div class="message-content">
            <strong>
                Tú
            </strong>
            <p>
                ${text}
            </p>
        </div>
    `;

    messagesContainer.appendChild(
        message
    );

    messageInput.value = "";

    messagesContainer.scrollTop =
        messagesContainer.scrollHeight;

}

sendButton.addEventListener(
    "click",
    sendMessage
);

messageInput.addEventListener(
    "keydown",
    event => {
        if (event.key === "Enter") {
            sendMessage();
        }
    }
);

/* Carusel de cartas */

/* Cartas de ejemplo (borrar para cuando este el server) */
const pokemonCards = [
    "https://i.pinimg.com/736x/48/62/47/486247923ba212d2f45d47ff1b81f69d.jpg",
    "https://i.pinimg.com/736x/78/41/23/784123912a27a92f01d8597d1d32b069.jpg",
    "https://i.pinimg.com/736x/a4/6e/fc/a46efc5422e919ce3c8176ab3a543785.jpg"
];

let currentCard = 0;

const cardModal =
    document.getElementById("cardModal");

const largeCard =
    document.getElementById("largeCard");

const closeCard =
    document.getElementById("closeCard");

const previousCard =
    document.getElementById("previousCard");

const nextCard =
    document.getElementById("nextCard");

const indicators =
    document.querySelectorAll(".card-indicator");

function showCard(index) {

    if (index < 0) {

        index =
            pokemonCards.length - 1;

    }

    if (index >= pokemonCards.length) {

        index = 0;

    }


    currentCard = index;

    largeCard.src =
        pokemonCards[currentCard];

    indicators.forEach(
        indicator => {

            indicator.classList.remove(
                "active"
            );

        }
    );

    indicators[currentCard]
        .classList.add("active");

}

pokemonCards.forEach(
    (card, index) => {

        const button =
            document.querySelector(
                `.pokemon-card[data-card="${index}"]`
            );


        button.addEventListener(
            "click",
            () => {

                showCard(index);

                cardModal.classList.add(
                    "show"
                );

                document.body.style.overflow =
                    "hidden";
            }
        );
    }
);

function closeCardModal() {
    cardModal.classList.remove(
        "show"
    );

    document.body.style.overflow =
        "hidden";

}

closeCard.addEventListener(
    "click",
    closeCardModal
);

cardModal.addEventListener(
    "click",
    event => {
        if (
            event.target === cardModal
        ) {
            closeCardModal();
        }
    }
);

previousCard.addEventListener(
    "click",
    () => {
        showCard(
            currentCard - 1
        );
    }
);

nextCard.addEventListener(
    "click",
    () => {
        showCard(
            currentCard + 1
        );
    }
);

indicators.forEach(
    indicator => {
        indicator.addEventListener(
            "click",
            () => {

                const index =
                    Number(
                        indicator.dataset.indicator
                    );
                showCard(index);
            }
        );
    }
);

document.addEventListener(
    "keydown",
    event => {
        if (
            event.key === "Escape" &&
            cardModal.classList.contains("show")
        ) {
            closeCardModal();
        }

        if (
            event.key === "ArrowLeft" &&
            cardModal.classList.contains("show")
        ) {
            showCard(
                currentCard - 1
            );
        }

        if (
            event.key === "ArrowRight" &&
            cardModal.classList.contains("show")
        ) {
            showCard(
                currentCard + 1
            );
        }
    }
);