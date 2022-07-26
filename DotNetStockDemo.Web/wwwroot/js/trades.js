"use strict";

var connection = new signalR.HubConnectionBuilder().withUrl("/trades").build();

// dir would be true for up, false for down - compared to last day's closing value
// amount would be the difference to last day's closing value

connection.on("ReceiveTrade", function (id, symbol, name, qty, price, dir, amount) {
    var div = document.createElement("div");
    div.innerHTML = `${symbol} (<span style="color:blue;">${name}</span>) ${qty} @ ${price} <span style="color:#999999">[${id}]</span>`;
    var ticks = document.getElementById("trades");
    ticks.insertBefore(div, ticks.firstChild);
    if (ticks.children.length > 10) ticks.lastChild.remove();
});

connection.start().then(function () {
}).catch(function (err) {
    return console.error(err.toString());
});
