# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project state

This repository currently contains only the unmodified output of `dotnet new blazor` (.NET 10, Blazor Web App, Server interactivity) — there is no order-book, exchange-connectivity, or dashboard logic implemented yet. Pages under `Components/Pages/` (`Home.razor`, `Counter.razor`, `Weather.razor`) are template placeholders, not real features. Expect to build the actual crypto order book functionality from scratch.

## Commands

Run all commands from the repo root (`CryptoOrderBookDashboard.slnx`) or from the `CryptoOrderBookDashboard/` project directory.

```powershell
dotnet build                                   # build
dotnet run --project CryptoOrderBookDashboard  # run (Development launch profile: http://localhost:5117, https://localhost:7289)
dotnet watch --project CryptoOrderBookDashboard # run with hot reload
```

There are no test projects in the solution yet. If tests are added, add the test project to `CryptoOrderBookDashboard.slnx` and this file should be updated with the corresponding `dotnet test` invocation.

## Architecture

- **Hosting model**: ASP.NET Core Blazor Web App using Interactive Server render mode (`AddInteractiveServerComponents()` / `AddInteractiveServerRenderMode()` in `Program.cs`), i.e. component logic executes server-side over a SignalR circuit, not WebAssembly.
- **Entry point / pipeline**: `Program.cs` wires up the standard Blazor Web App middleware pipeline (exception handler + HSTS in non-Development, static-status-code re-execution to `/not-found`, HTTPS redirection, antiforgery, static assets, Razor component mapping). Add new services/middleware here.
- **Root composition**: `Components/App.razor` is the top-level HTML document (head/body shell, asset references via `@Assets[...]`, the `<Routes />` router outlet, `<ReconnectModal />` for circuit-disconnect UX, and the Blazor web JS interop script). `Components/Routes.razor` + `Components/_Imports.razor` define routing and globally-imported usings for all components.
- **Layout**: `Components/Layout/MainLayout.razor` is the shared page chrome; `NavMenu.razor` is the sidebar nav — new pages should be linked here. `ReconnectModal.razor`/`.razor.js` handles the reconnect-UI when the SignalR circuit drops, relevant to any future real-time (order book) UI work given the server-interactive model.
- **Pages**: Routable components live in `Components/Pages/`, each declaring its route via `@page` directive.
- **Static assets**: `wwwroot/` holds CSS/JS assets; Bootstrap is vendored under `wwwroot/lib/bootstrap/`. Assets are referenced through the ASP.NET Core `Assets` map (`@Assets["path"]`), not raw paths.
- **Config**: `appsettings.json` / `appsettings.Development.json` are currently template defaults (logging levels, `AllowedHosts`) — no exchange API keys/endpoints configured yet.

## Notes for future work

Given the project name, expect upcoming work to add: a market-data/exchange connectivity layer (likely a background service or hosted service pushing order book updates), and real-time UI components consuming that data — natural fits for Blazor Server's SignalR-based interactivity already enabled in `Program.cs`. When that code lands, update this file's Architecture section rather than leaving it describing only the template scaffold.
