using System.Reflection;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Expanse.Clock.Core;
using Expanse.Clock.Manager;
using Expanse.Domain;

if (args.Length != 1) throw new ArgumentException("Pass an output directory.");
Directory.CreateDirectory(args[0]);
var thread = new Thread(() => Run(args[0]));
thread.SetApartmentState(ApartmentState.STA);
thread.Start();
thread.Join();

static void Run(string output)
{
    var app = new App();
    app.InitializeComponent();
    var smokeToken = "ui-" + Guid.NewGuid().ToString("N");
    var window = new MainWindow(EffectsProtocol.CreateDevViewPipeName(smokeToken), EffectsProtocol.CreateDevCommandPipeName(smokeToken));
    // Initialize WPF's real viewport so DataGrid star columns receive widths.
    // Keep the fixture offscreen and prevent any polling even on its isolated pipes.
    typeof(MainWindow).GetField("_polling", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, true);
    window.ShowActivated = false; window.WindowStartupLocation = WindowStartupLocation.Manual;
    window.WindowStyle = WindowStyle.None; window.Width = 860; window.Height = 680;
    window.Left = -10000; window.Top = -10000;
    window.Show();
    ((DispatcherTimer)typeof(MainWindow).GetField("_timer", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!).Stop();
    var estimate = new ColonyPowerEstimate("nominal", "Active converter recipes only; actual output can differ.", 1200, 4, 2);
    var measured = new ColonyPowerRate("unavailable", "Facility is on rails; live power flow is unavailable.", 100, null, null, null, null);
    var vessel = new ColonyVessel(Guid.NewGuid().ToString("D"), "Minmus PDU", "Minmus", "Greater Flats", 1, 2,
        "loaded", 2, [new ColonyTank("ElectricCharge", 1500, 2000, false, false, true)], [], measured, estimate);
    var rows = Enumerable.Range(0, 80).Select(i => new WolfResource($"Resource {i:00}", 100 + i, 20, 80 + i)).ToArray();
    var wolf = new WolfSnapshot("observed", null, 100, [new WolfDepot("Minmus", "Greater Flats", true, rows)],
        rows.Select(x => x.Name).ToArray());
    var colony = new ColonySnapshot("observed", null, 100, [vessel], wolf);
    typeof(MainWindow).GetMethod("RenderColony", BindingFlags.NonPublic | BindingFlags.Instance)!
        .Invoke(window, [colony, "live", 0.2, null]);

    var root = (FrameworkElement)window.Content;
    void Layout()
    {
        root.Measure(new Size(860, 680));
        root.Arrange(new Rect(0, 0, 860, 680));
        root.UpdateLayout();
    }
    Layout();
    var colonyTab = FindLogicalTabs(window).First(x => Equals(x.Header, "Colony"));
    colonyTab.IsSelected = true;
    Layout();
    var powerTab = FindLogicalTabs(window).First(x => Equals(x.Header, "Power"));
    powerTab.IsSelected = true;
    Layout();
    var powerList = (ItemsControl)window.FindName("ColonyPowerFacilitiesList")!;
    if (powerList.Items.Count != 1) throw new Exception("Power facility is missing.");
    var drill = VisualDescendants(root).OfType<Expander>().First(x => x.Tag is string);
    drill.IsExpanded = true;
    Layout();
    var powerScroll = Ancestors(powerList).OfType<ScrollViewer>().First();
    powerScroll.ScrollToVerticalOffset(450);
    Layout();
    var labels = VisualDescendants(root).OfType<TextBlock>().Select(x => x.Text).Where(x => x is not null).ToArray();
    if (!labels.Contains("MEASURED LIVE FLOW") || !labels.Contains("MODULE-RATED ESTIMATE · NOMINAL, PARTIAL") ||
        !labels.Any(x => x!.Contains("Nominal generation 1,200")))
        throw new Exception("Power drill-down labels or nominal value are missing.");
    Capture(root, Path.Combine(output, "power-860x680.png"));

    var wolfTab = FindLogicalTabs(window).First(x => Equals(x.Header, "WOLF"));
    wolfTab.IsSelected = true;
    Layout();
    Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
    Layout();
    var wolfList = (ItemsControl)window.FindName("WolfResourcesList")!;
    if (wolfList.Items.Count != 80) throw new Exception($"Expected 80 WOLF rows; got {wolfList.Items.Count}.");
    Capture(root, Path.Combine(output, "wolf-top-860x680.png"));
    var header = VisualDescendants(root).OfType<TextBlock>().First(x => x.Text == "RESOURCE");
    var headerY = header.TransformToAncestor(root).Transform(new Point(0, 0)).Y;
    var scroll = Ancestors(wolfList).OfType<ScrollViewer>().First();
    scroll.ScrollToVerticalOffset(30);
    Layout();
    var scrolledHeaderY = header.TransformToAncestor(root).Transform(new Point(0, 0)).Y;
    if (Math.Abs(scrolledHeaderY - headerY) > 1) throw new Exception("WOLF column header moved while list scrolled.");
    if (scroll.VerticalOffset < 1) throw new Exception("WOLF list did not scroll.");
    Capture(root, Path.Combine(output, "wolf-scrolled-860x680.png"));
    // Render a local mock export state. Never submit a command or connect production pipes.
    var sourceId = "fa516623-27a6-4093-97f4-55a1b05bc495";
    var worldId = Guid.NewGuid().ToString("D");
    var sourceHash = new string('a', 64);
    var depots = new[] { new DepotSummaryView(sourceId, "Minmus Mining", 1, sourceHash, "lastObserved", "Synthetic UI observation", 0, 1,
        [new ResourceRow("Ore", "Ore", 3000, 10000)]) };
    var export = new RouteVersionRecord { RouteId = "Minmus Ore recovery", Version = 1, SourceDepotId = sourceId, SourceMembershipRevision = 1, SourceMembershipHash = sourceHash,
        DestinationKind = OreExportPolicy.VirtualDestinationKind, DestinationDepotId = OreExportPolicy.KerbinBuyerId, FundsPerUnit = 100, TravelDurationSeconds = 64800, Provenance = "modeled recovery",
        Resources = [new ResourceAmount { ResourceName = "Ore", AmountMicroUnits = 2_500_000_000 }] };
    var accepted = new AcceptedState { SchemaVersion = 2, CapsuleEncodingVersion = 4, WorldId = worldId, CheckpointId = "ui-only",
        Depots = [new DepotRecord { DepotId = sourceId, MembershipRevision = 1, MembershipHash = sourceHash }], RouteVersions = [export],
        DeliveryRules = [new DeliveryRuleRecord { RuleId = "Ore exports", Revision = 1, RouteId = export.RouteId, RouteVersion = 1, Kind = "exportStock", Enabled = true, ResourceName = "Ore", BatchSizeMicroUnits = 2_500_000_000 }],
        ActiveShipments = [new ActiveShipmentRecord { ShipmentId = "ui-ore-cargo", RouteId = export.RouteId, RouteVersion = 1, SourceDepotId = sourceId, DestinationDepotId = OreExportPolicy.KerbinBuyerId,
            DestinationKind = OreExportPolicy.VirtualDestinationKind, FundsPerUnit = 100, DepartureUt = 100, DueUt = 64900, RemainingResources = export.Resources }] };
    void SetField(string name, object value) => typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, value);
    SetField("_acceptedState", accepted); SetField("_worldId", worldId); SetField("_runId", "ui-only"); SetField("_lastDepots", depots); SetField("_lastViewStatus", "live");
    var updateExport = typeof(MainWindow).GetMethod("BuildExportRuleUpdate", BindingFlags.NonPublic | BindingFlags.Static)!;
    var historicalOrder = new DeliveryRuleRecord { RuleId = "existing exports", Revision = 3, Kind = "exportStock", RouteId = export.RouteId, RouteVersion = 1,
        Enabled = false, ResourceName = "Ore", BatchSizeMicroUnits = 1_000_000_000, WaitingRequest = true, WaitingScheduledUt = 100, WaitingCoalescedSlots = 1 };
    export.Version = 2;
    var migrated = (DeliveryRuleRecord)updateExport.Invoke(null, [historicalOrder, export])!;
    if (migrated.RuleId != historicalOrder.RuleId || migrated.Revision != 4 || migrated.Enabled || migrated.RouteVersion != 2 || migrated.BatchSizeMicroUnits != 2_500_000_000 || migrated.WaitingRequest || migrated.WaitingScheduledUt != 0 || historicalOrder.BatchSizeMicroUnits != 1_000_000_000)
        throw new Exception("Existing export edit failed to migrate quantity/version while preserving paused status and prior record.");
    export.Version = 1;
    SetField("_lastSample", new ClockSample(1, "clockSample", 1, Guid.NewGuid(), Guid.NewGuid(), "ui-only", "ui-only", "UI fixture", 100, true, "Flight", false, "Year 1", 1, WorldId: worldId, RunId: "ui-only"));
    typeof(MainWindow).GetMethod("RenderDepots", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [depots, null, false]);
    typeof(MainWindow).GetMethod("RenderOperations", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, []);
    ((ComboBox)window.FindName("ExportSourceBox")!).SelectedItem = depots[0];
    FindLogicalTabs(window).First(x => Equals(x.Header, "Delivery setup")).IsSelected = true;
    Layout();
    if (((TextBox)window.FindName("ExportDurationBox")!).Text != "3:00:00") throw new Exception("Export UI default is not three Kerbin days.");
    var quantityBox = (TextBox)window.FindName("ExportQuantityBox")!;
    var preview = (TextBlock)window.FindName("ExportPayoutPreviewText")!;
    if (quantityBox.Text != "1000" || !preview.Text.Contains("100,000")) throw new Exception("Default export quantity or 100/unit payout preview is wrong.");
    quantityBox.Text = "1.5";
    if (!preview.Text.Contains("whole Ore")) throw new Exception("Fractional export quantity was accepted by the preview.");
    quantityBox.Text = "2500";
    if (!preview.Text.Contains("250,000")) throw new Exception("Editable 2,500-Ore payout preview is wrong.");
    var parseDuration = typeof(MainWindow).GetMethod("TryTravelDuration", BindingFlags.NonPublic | BindingFlags.Static)!;
    object?[] durationArgs = ["3:00:00", 0d];
    if (!(bool)parseDuration.Invoke(null, durationArgs)! || (double)durationArgs[1]! != 64800) throw new Exception("Export duration did not parse to 64,800 game seconds.");
    if (((ComboBox)window.FindName("RuleKindBox")!).SelectedIndex != 2 || ((FrameworkElement)window.FindName("RepeatFields")!).Visibility != Visibility.Collapsed)
        throw new Exception("Export route exposes the wrong automatic-order fields.");
    Layout();
    Capture(root, Path.Combine(output, "ore-export-setup-860x680.png"));
    var savedRules = accepted.DeliveryRules; accepted.DeliveryRules = [];
    typeof(MainWindow).GetMethod("RenderOperations", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, []);
    Layout();
    if (!((TextBlock)window.FindName("ExportOrderStatusText")!).Text.Contains("inactive")) throw new Exception("Configured route incorrectly claims automatic exports are enabled.");
    Capture(root, Path.Combine(output, "ore-export-inactive-860x680.png"));
    accepted.DeliveryRules = savedRules;
    typeof(MainWindow).GetMethod("RenderOperations", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, []);
    FindLogicalTabs(window).First(x => Equals(x.Header, "Activity")).IsSelected = true;
    Layout();
    Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
    Layout();
    var activityText = VisualDescendants(root).OfType<TextBlock>().Select(x => x.Text).ToArray();
    if (!activityText.Any(x => x?.Contains("Kerbin · modeled recovery") == true) || !activityText.Any(x => x?.Contains("recover for 250,000 funds") == true))
        throw new Exception("Export activity omits modeled buyer or fixed compensation.");
    Capture(root, Path.Combine(output, "ore-export-activity-860x680.png"));
    Console.WriteLine("PASS: Editable whole Ore quantity, 100/unit payout preview, source-stock order, explicit enabled status, modeled Kerbin destination, and 64,800-second duration; isolated dev pipes only.");
    Console.WriteLine($"PASS: Power drill-down labels and nominal value visible at 860x680; WOLF rows={wolfList.Items.Count}, header Y={headerY:0.0}->{scrolledHeaderY:0.0}, scroll offset={scroll.VerticalOffset:0.0}");
    window.Close();
    app.Shutdown();
}

static IEnumerable<TabItem> FindLogicalTabs(DependencyObject node)
{
    foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
    {
        if (child is TabItem tab) yield return tab;
        foreach (var nestedTab in FindLogicalTabs(child)) yield return nestedTab;
    }
}

static IEnumerable<DependencyObject> VisualDescendants(DependencyObject node)
{
    for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
    {
        var child = VisualTreeHelper.GetChild(node, i);
        yield return child;
        foreach (var descendant in VisualDescendants(child)) yield return descendant;
    }
}

static IEnumerable<DependencyObject> Ancestors(DependencyObject node)
{
    for (var parent = VisualTreeHelper.GetParent(node); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        yield return parent;
}

static void Capture(FrameworkElement root, string path)
{
    var bitmap = new RenderTargetBitmap(860, 680, 96, 96, PixelFormats.Pbgra32);
    bitmap.Render(root);
    var encoder = new PngBitmapEncoder();
    encoder.Frames.Add(BitmapFrame.Create(bitmap));
    using var stream = File.Create(path);
    encoder.Save(stream);
}
