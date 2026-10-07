# Manager redesign validation — 2026-09-27

The Manager now presents the supplied Expanse Foundations logo and a light navy, teal, and gold mission-control theme. Delivery setup groups route creation, one-time sending, and automatic orders. Activity separates shipments, automatic orders, saved routes, and operational issues.

The schedule display uses accepted-state data only. Repeating orders show their persisted next due UT and an estimated arrival based on that route version's travel duration when a future departure is meaningful. A queued or overdue order has no firm arrival estimate. Stock-triggered orders have a condition instead of a fabricated departure date. KSP dates and countdowns follow the game clock; paused and disconnected states are labeled.

## Isolated preview

From the `ExpansePlatform` directory:

```powershell
dotnet build run/ui-refresh-preview/Preview.csproj -c Release
dotnet run --project run/ui-refresh-preview/Preview.csproj -c Release --no-build
```

The preview uses unused pipe names, creates in-memory depot, shipment, route, order, and issue fixtures, and does not open a production command connection or submit orders. It writes populated screenshots to `run/ui-refresh-preview/bin/Release/net8.0-windows/preview-*-1180x930.png` and `preview-*-860x680.png`, plus `preview-automatic-orders.png`, `preview-route-editor.png`, `preview-activity-bottom.png`, `preview-activity-bottom-860x680.png`, and `preview-keep-stock.png`. Its assertions cover KSP shipment and repeat timing, conditional stock orders, paused/disconnected labels, unavailable versus empty state, issue details, depot selection and stock display, manifest construction, and duration/resource parsing.

The Manager can be built independently with:

```powershell
dotnet build src/Expanse.Clock.Manager/Expanse.Clock.Manager.csproj -c Release
dotnet test tests/Expanse.Clock.Tests.csproj -c Release
```

On 2026-09-27, the Manager Release build had zero warnings and errors; all 75 project tests passed; and the isolated preview completed its assertions and screenshots. This note records source validation only. Deployment and a live game check are separate steps.

## Installation check

Installed the reviewed Manager exe/DLL into the existing 20260927-041234 Manager directory on 2026-09-27. Core and Domain dependencies match the installed binaries exactly; Host, Bridge, and saves were not replaced. All 39 installation manifest entries verified. Backup: `artifacts/deploy-backups/manager-redesign-20260927-011317`.

Opened the installed Manager with the existing launcher. The logo, tabs, layout, and clock display rendered successfully. At this check the Host supplied a live clock but no world/run attachment or depot list; Delivery setup reported "Waiting for the KSP bridge to identify the active save." Populated schedule/stock validation therefore remains the isolated preview evidence above, not a claim of live-game delivery execution in this turn.
