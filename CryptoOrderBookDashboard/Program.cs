using CryptoOrderBookDashboard.Common;
using CryptoOrderBookDashboard.Components;
using CryptoOrderBookDashboard.OrderBooks;
using CryptoOrderBookDashboard.OrderBooks.Configuration;
using CryptoOrderBookDashboard.Trades;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.Configure<OrderBookDashboardOptions>(
    builder.Configuration.GetSection(OrderBookDashboardOptions.SectionName));
builder.Services.AddBinance(options => { });
builder.Services.AddSingleton<OrderBookStore>();
builder.Services.AddHostedService<OrderBookHostedService>();
builder.Services.AddSingleton<TradeStore>();
builder.Services.AddHostedService<TradeFeedHostedService>();
builder.Services.AddHostedService<RenderPulseService>();

// Singleton so its concurrency limit applies across all users/circuits.
builder.Services.AddSingleton<HistoricalTradeSearchService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
