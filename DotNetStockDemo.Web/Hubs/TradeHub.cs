using Microsoft.AspNetCore.SignalR;

namespace DotNetStockDemo.Web.Hubs;

public class TradeHub : Hub
{
    //public Task SendMessage(string symbol, int qty, float price, bool dir, float amount)
    //{
    //    return Clients.All.SendAsync("ReceiveTrade", symbol, qty, price, dir, amount);
    //}
}