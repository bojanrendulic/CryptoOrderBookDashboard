using CryptoOrderBookDashboard.Common;
using CryptoOrderBookDashboard.Components;
using CryptoOrderBookDashboard.Funding;
using CryptoOrderBookDashboard.Liquidations;
using CryptoOrderBookDashboard.OrderBooks;
using CryptoOrderBookDashboard.OrderBooks.Configuration;
using CryptoOrderBookDashboard.Persistence;
using CryptoOrderBookDashboard.Trades;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.Configure<OrderBookDashboardOptions>(
    builder.Configuration.GetSection(OrderBookDashboardOptions.SectionName));
builder.Services.AddBinance(options => { });

// SQLite persistence. The writer is registered before every feed that writes to it: hosted
// services stop in reverse order, so it stops last and can drain what the feeds queued.
builder.Services.Configure<PersistenceOptions>(builder.Configuration.GetSection(PersistenceOptions.SectionName));
builder.Services.AddSingleton<SqliteDatabase>();
builder.Services.AddSingleton<PersistenceWriter>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PersistenceWriter>());
builder.Services.AddHostedService<RetentionService>();

builder.Services.AddSingleton<OrderBookStore>();
builder.Services.AddHostedService<OrderBookHostedService>();
builder.Services.AddSingleton<TradeStore>();
builder.Services.AddHostedService<TradeFeedHostedService>();
builder.Services.AddSingleton<FundingStore>();
builder.Services.AddHostedService<FundingRateHostedService>();

builder.Services.Configure<LiquidationOptions>(builder.Configuration.GetSection(LiquidationOptions.SectionName));
builder.Services.AddSingleton<LiquidationStore>();
builder.Services.AddSingleton<LiquidationRepository>();
builder.Services.AddHostedService<LiquidationHostedService>();

// Singleton + hosted: the page calls it for on-demand fetches, the host runs its refresh loop.
builder.Services.AddSingleton<PositioningService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PositioningService>());

builder.Services.AddHostedService<RenderPulseService>();

// Singleton so its concurrency limit applies across all users/circuits.
builder.Services.AddSingleton<HistoricalTradeSearchService>();

var app = builder.Build();

// Create or migrate the database before any hosted service reads from it.
await app.Services.GetRequiredService<SqliteDatabase>().InitializeAsync();

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
