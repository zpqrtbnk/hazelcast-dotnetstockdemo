using DotNetStockDemo.Web.Hubs;
using DotNetStockDemo.Web.Services;

// inspired from basic SignalR demo at:
// https://docs.microsoft.com/en-us/aspnet/core/tutorials/signalr

// run with (change port to your liking)
// dotnet run --urls=http://localhost:7001

var builder = WebApplication.CreateBuilder(args);

// add services to the container
builder.Services.AddRazorPages();
builder.Services.AddSignalR();

// add our demo as a hosted service
builder.Services.AddHostedService<Demo>();

// configure our demo
builder.Services.AddOptions<DemoOptions>().Bind(builder.Configuration.GetSection("DotNetStockDemo"));

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts(); // default is 30 days, change for production
}

// HTTPS redirection raises warnings in dev on some platforms, ignore for now
// (see https://docs.microsoft.com/en-us/aspnet/core/security/enforcing-ssl)
//app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.UseAuthorization();

app.MapRazorPages();
app.MapHub<TradeHub>("/trades"); // register our trade SignalR hub

app.Run();
