using System.IO;
using System.Globalization;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Expanse.Clock.Core;
using Expanse.Domain;

namespace Expanse.Clock.Manager;

public partial class MainWindow : Window
{
    private readonly ClockViewClient _client;
    private readonly RecoveryCommandClient _commands;
    private readonly WolfAdminClient _wolfAdmin;
    private bool _wolfCommandInFlight;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _polling;
    private ClockSample? _lastSample;
    private DepotView? _lastDepotView;
    private DepotSummaryView[]? _lastDepots;
    private ColonySnapshot? _lastColony;
    private readonly PowerRateHistory _powerRateHistory = new();
    private readonly HashSet<string> _expandedResourceKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> _expandedPowerKeys = new(StringComparer.Ordinal);
    private ColonyResourceDisplayRow[] _resourceRows = Array.Empty<ColonyResourceDisplayRow>();
    private double? _lastColonyAge;
    private AcceptedState? _acceptedState;
    private DeliveryReadinessResult? _deliveryReadiness;
    private readonly Dictionary<string, string> _transientShipmentHolds = new(StringComparer.Ordinal);
    private string? _projectionContext;
    private string? _worldId;
    private string? _runId;
    private string? _lastCommandId;
    private SubmitCommand? _pendingSendOnce;
    private long _lastProjectionAt;
    private long _lastCommandStatusAt;
    private bool _commandInFlight;
    private bool _hasRenderedView;
    private string _lastViewStatus = "waitingForKsp";
    private readonly ObservableCollection<RouteResourceEntry> _routeResources = new();
    private readonly ObservableCollection<RuleResourceEntry> _ruleResources = new();
    private string? _ruleResourceRouteKey;
    private bool _batchInProgress;
    private string? _editingRuleId;
    private long _editingRuleRevision;
    private readonly ObservableCollection<DeliveryDisplay> _deliveries = new();
    private readonly ObservableCollection<AutomaticOrderDisplay> _automaticOrders = new();
    private readonly ObservableCollection<SavedRouteDisplay> _savedRoutes = new();
    private readonly ObservableCollection<IssueDisplay> _issues = new();

    public MainWindow(string? viewPipeName = null, string? commandPipeName = null, string? colonyPipeName = null)
    {
        _client = new ClockViewClient(viewPipeName);
        _commands = new RecoveryCommandClient(commandPipeName);
        _wolfAdmin = new WolfAdminClient(commandPipeName);
        InitializeComponent();
        InitializeColonyManagement(colonyPipeName, viewPipeName is not null || commandPipeName is not null);
        RouteResourcesList.ItemsSource = _routeResources;
        RuleResourcesList.ItemsSource = _ruleResources;
        DeliveriesGrid.ItemsSource = _deliveries;
        AutomaticOrdersGrid.ItemsSource = _automaticOrders;
        SavedRoutesGrid.ItemsSource = _savedRoutes;
        IssuesGrid.ItemsSource = _issues;
        AddRouteResource("LiquidFuel", selected: true);
        AddRouteResource("Oxidizer");
        AddRouteResource("MonoPropellant");
        AddRouteResource("Ore");
        SetWritable(false);
        _timer.Tick += async (_, _) => await PollAsync();
        Loaded += async (_, _) => { await PollAsync(); _timer.Start(); };
        Closed += (_, _) => _timer.Stop();
    }

    private async Task PollAsync()
    {
        if (_polling) return;
        _polling = true;
        try
        {
            var view = await _client.GetSnapshotAsync(TimeSpan.FromSeconds(2));
            if (view.Status == "waitingForKsp" && view.Sample is null) _lastSample = null;
            if (view.Sample is not null) _lastSample = view.Sample;
        _lastDepotView = view.DepotView;
        _lastDepots = view.Depots;
        _lastColony = view.Colony;
        _lastColonyAge = view.AgeSeconds;
            _worldId = view.WorldId ?? view.Sample?.WorldId;
            _runId = view.RunId ?? view.Sample?.RunId;
            // An idle Host returns the same empty view twice a second. Rebinding
            // every grid and picker for that unchanged view makes the window flash.
            if (_hasRenderedView && view.Status == "waitingForKsp" && view.Sample is null && _lastViewStatus == "waitingForKsp" && _lastSample is null)
                return;
            Render(view.Status, view.AgeSeconds, view.Sample, view.DepotView, view.Depots, view.Colony, hostUnavailable: false);
            UpdateDeliveryCountdowns();
            if (!_batchInProgress)
            {
                await RefreshAcceptedStateAsync(view);
                if (_lastCommandId is not null && Environment.TickCount64 - _lastCommandStatusAt >= 1500) await RefreshCommandStatusAsync();
            }
        }
        catch
        {
            Render("hostUnavailable", _lastColonyAge, _lastSample, _lastDepotView, _lastDepots, _lastColony, hostUnavailable: true);
            SetWritable(false);
        }
        finally { _polling = false; }
    }

    private void Render(string status, double? age, ClockSample? sample, DepotView? depotView, DepotSummaryView[]? depots, ColonySnapshot? colony, bool hostUnavailable)
    {
        _powerRateHistory.SetContext(_worldId, _runId);
        var visible = sample ?? (status == "hostUnavailable" ? _lastSample : null);
        bool colonyConnected = managementSnapshotCurrent && managementSnapshot is { Status: "Save authority connected", State: not null };
        bool colonyOnly = status == "hostUnavailable" && colonyConnected && visible?.ActiveWorld != true;
        if (visible?.ActiveWorld == true && visible.UtSeconds is double seconds)
        {
            ClockText.Text = string.IsNullOrWhiteSpace(visible.FormattedDate) ? "KSP clock" : visible.FormattedDate;
            RawUtText.Text = $"UT {seconds.ToString("R", CultureInfo.InvariantCulture)} s";
            SaveText.Text = string.IsNullOrWhiteSpace(visible.SaveTitle)
                ? visible.SaveFolder ?? "Loaded world"
                : $"{visible.SaveTitle}  ·  {visible.SaveFolder}";
            IdentityText.Text = string.Empty;
        }
        else if (colonyOnly)
        {
            ClockText.Text = "Clock unavailable";
            RawUtText.Text = "UT —";
            SaveText.Text = "Colony save connected";
            IdentityText.Text = string.Empty;
        }
        else
        {
            ClockText.Text = "Waiting for KSP";
            RawUtText.Text = "UT —";
            SaveText.Text = visible?.ActiveWorld == false ? "Game running · no save loaded" : "No save loaded";
            IdentityText.Text = string.Empty;
        }

        string label = status switch
        {
            "hostUnavailable" => "Host unavailable",
            "waitingForKsp" => "Host connected · waiting for KSP",
            "noWorld" => "Game running · no world loaded",
            "live" => "Live clock",
            "paused" => "Paused",
            "stale" => "Stale · last sample retained",
            _ => "Host status unknown"
        };
        if (colonyOnly) label = "Colony connected · clock unavailable";
        StatusText.Text = label;
        _lastViewStatus = status;
        _hasRenderedView = true;
        StatusText.Foreground = status is "live" or "paused" ? Brushes.ForestGreen :
            status is "stale" or "hostUnavailable" ? Brushes.DarkOrange : (Brush)FindResource("MutedBrush");
        AgeText.Text = age.HasValue ? $"Received {age.Value.ToString("0.0", CultureInfo.InvariantCulture)} s ago" : string.Empty;
        RenderDepots(depots, depotView, hostUnavailable);
        RenderColony(colony, status, age, visible);
        SetWritable(!hostUnavailable && (status is "live" or "paused") && visible?.ActiveWorld == true);
    }

    private void ColonyLocation_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_renderingColonySelection || sender is not System.Windows.Controls.ComboBox box) return;
        if (box.SelectedItem is ColonySiteChoice choice) _selectedColonySiteKey = choice.Key;
        RenderColony(_lastColony, _lastViewStatus, _lastColonyAge, _lastSample);
    }

    private string? _selectedColonySiteKey;
    private bool _renderingColonySelection;
    private ColonySiteChoice[] _colonySiteChoices = Array.Empty<ColonySiteChoice>();

    private void RenderColony(ColonySnapshot? colony, string viewStatus, double? hostAge, ClockSample? sample)
    {
        ColonyFreshnessText.ToolTip = null;
        var clear = viewStatus == "noWorld" || colony is null || colony.Status is "unavailable";
        var observedVessels = !clear && colony!.Status is "observed" or "truncated"
            ? colony.Vessels ?? Array.Empty<ColonyVessel>() : Array.Empty<ColonyVessel>();
        var sites = observedVessels
            .GroupBy(v => ColonySiteKey(v.Body, v.Biome), StringComparer.Ordinal)
            .Select(g => new ColonySiteChoice(g.Key, SiteLabel(g.First().Body, g.First().Biome)))
            .OrderBy(x => x.Label, StringComparer.CurrentCultureIgnoreCase).ToArray();
        if (colony?.Wolf?.Depots is { Length: > 0 } wolfDepots)
        {
            sites = sites.Concat(wolfDepots.Select(d => new ColonySiteChoice(ColonySiteKey(d.Body, d.Biome), SiteLabel(d.Body, d.Biome))))
                .GroupBy(x => x.Key, StringComparer.Ordinal).Select(g => g.First())
                .OrderBy(x => x.Label, StringComparer.CurrentCultureIgnoreCase).ToArray();
        }
        if (!_colonySiteChoices.SequenceEqual(sites))
        {
            _renderingColonySelection = true;
            try { ColonyLocationBox.ItemsSource = sites; _colonySiteChoices = sites; }
            finally { _renderingColonySelection = false; }
        }
        var selected = sites.FirstOrDefault(x => x.Key == _selectedColonySiteKey) ?? sites.FirstOrDefault();
        if ((ColonyLocationBox.SelectedItem as ColonySiteChoice)?.Key != selected?.Key)
        {
            _renderingColonySelection = true;
            try { ColonyLocationBox.SelectedItem = selected; }
            finally { _renderingColonySelection = false; }
        }
        _selectedColonySiteKey = selected?.Key;
        RenderWolfLedger(colony, viewStatus, sample, selected?.Key);
        var selectedVessels = selected is null ? Array.Empty<ColonyVessel>() : observedVessels
            .Where(v => ColonySiteKey(v.Body, v.Biome) == selected.Key)
            .OrderBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();

        ColonyTitleText.Text = selected?.Label ?? "Colony overview";
        ColonyLocationBox.IsEnabled = sites.Length > 0;
        ColonyVesselCountText.Text = clear ? "—" : selectedVessels.Length.ToString(CultureInfo.CurrentCulture);
        var crewCount = selectedVessels.Where(v => v.Crew > 0).Sum(v => v.Crew);
        ColonyCrewSummaryText.Text = clear ? "No current reading" : $"{crewCount} crew reported";
        ColonyVesselCountDetailText.Text = clear ? string.Empty : colony!.Status == "truncated" ? "Partial list · at this site" : $"{selectedVessels.Length} facilities · at this site";
        var processors = selectedVessels.SelectMany(v => v.Converters ?? Array.Empty<ColonyConverter>()).ToArray();
        var loadedVessels = selectedVessels.Where(v => v.ObservationBasis == "loaded").ToArray();
        var enabledProcessors = loadedVessels.SelectMany(v => v.Converters ?? Array.Empty<ColonyConverter>()).Count(p => p.Running == true);
        var converterReadingsAreCurrent = loadedVessels.Length > 0 && viewStatus is "live" or "paused" && colony?.Status == "observed";
        ColonyProductionText.Text = clear || !converterReadingsAreCurrent ? "—" : enabledProcessors.ToString(CultureInfo.CurrentCulture);
        ColonyProductionDetailText.Text = converterReadingsAreCurrent ? "Enabled setting · output not confirmed" : "Not observed";

        var tanks = selectedVessels.SelectMany(v => v.Tanks ?? Array.Empty<ColonyTank>()).ToArray();
        var powerFacilities = selectedVessels.Select(v =>
        {
            var ecRows = (v.Tanks ?? Array.Empty<ColonyTank>())
                .Where(t => string.Equals(t.Resource, "ElectricCharge", StringComparison.OrdinalIgnoreCase)).ToArray();
            var pairedRows = ecRows.Where(t => double.IsFinite(t.Amount) && double.IsFinite(t.Capacity)).ToArray();
            bool hasReading = pairedRows.Length > 0;
            double charge = pairedRows.Sum(t => t.Amount);
            double capacity = pairedRows.Sum(t => t.Capacity);
            string chargeText = ecRows.Length == 0 ? "No EC tank observed" : !hasReading ? "Reading unavailable" :
                FormatColonyAmount(charge) + " / " + FormatColonyAmount(capacity);
            string percentText = !hasReading || capacity <= 0 ? "Charge level unavailable" : (charge / capacity * 100).ToString("0.#", CultureInfo.CurrentCulture) + "%";
            bool currentLoaded = viewStatus == "live" && colony?.Status == "observed" && v.ObservationBasis == "loaded";
            string freshness = currentLoaded ? "Current loaded" : viewStatus == "paused" && colony?.Status == "observed" && v.ObservationBasis == "loaded" ? "Paused snapshot" : "Last reported";
            string key = (selected?.Key ?? string.Empty) + "\0" + v.VesselId;
            var rate = v.Power;
            bool hasRate = currentLoaded && rate is { Status: "partial", GenerationEcPerSecond: double generation,
                ConsumptionEcPerSecond: double consumption, NetEcPerSecond: double net,
                WindowSeconds: double seconds, SampleUt: double sampleUt } &&
                double.IsFinite(generation) && generation >= 0 && double.IsFinite(consumption) && consumption >= 0 &&
                double.IsFinite(net) && double.IsFinite(seconds) && seconds > 0 &&
                double.IsFinite(sampleUt) &&
                (colony?.ObservedUt is not double observedUt || Math.Abs(observedUt - sampleUt) <= 1);
            string flowText = hasRate
                ? $"Generation {FormatColonyAmount(rate!.GenerationEcPerSecond!.Value)} EC/s  ·  Consumption {FormatColonyAmount(rate.ConsumptionEcPerSecond!.Value)} EC/s  ·  Net {FormatSignedColonyAmount(rate.NetEcPerSecond!.Value)} EC/s"
                : "Live rate unavailable";
            string flowDetail = hasRate
                ? $"Measured over {rate!.WindowSeconds.GetValueOrDefault().ToString("0.#", CultureInfo.CurrentCulture)} s. Fulfilled EC requests only; direct changes and nearby transfers are excluded."
                : currentLoaded ? rate?.Reason ?? "Waiting for a complete loaded interval."
                : "Load this facility in KSP at 1× speed to measure flow.";
            var estimate = v.PowerEstimate;
            bool hasEstimate = (viewStatus is "live" or "paused") && colony?.Status == "observed" &&
                v.ObservationBasis == "loaded" &&
                estimate is { Status: "nominal", ModuleCount: > 0 };
            string estimateText = hasEstimate
                ? $"Nominal generation {FormatColonyAmount(estimate!.GenerationEcPerSecond)} EC/s  ·  nominal consumption {FormatColonyAmount(estimate.ConsumptionEcPerSecond)} EC/s"
                : "No module-rated estimate available";
            string estimateDetail = hasEstimate
                ? $"{estimate!.ModuleCount} active converter module{(estimate.ModuleCount == 1 ? "" : "s")}. {estimate.Reason}"
                : v.ObservationBasis == "snapshot" ? "Unloaded module recipes and operating state cannot be verified."
                : "Only active converter recipes with ElectricCharge ratios are included.";
            string storageDetail = ecRows.Length == 0 ? "No ElectricCharge tank observed." :
                $"{ecRows.Length} observed EC tank group{(ecRows.Length == 1 ? "" : "s")}; power sharing with other facilities is not verified.";
            return new ColonyPowerFacilityDisplay(key, _expandedPowerKeys.Contains(key), v.Name, chargeText, percentText,
                freshness, flowText, flowDetail, estimateText, estimateDetail, storageDetail);
        }).ToArray();
        var powerRows = selectedVessels.SelectMany(v => (v.Tanks ?? Array.Empty<ColonyTank>())
            .Where(t => string.Equals(t.Resource, "ElectricCharge", StringComparison.OrdinalIgnoreCase) && double.IsFinite(t.Amount) && double.IsFinite(t.Capacity))).ToArray();
        bool hasPowerReading = powerRows.Length > 0;
        double totalPowerCharge = powerRows.Sum(t => t.Amount);
        double totalPowerCapacity = powerRows.Sum(t => t.Capacity);
        ColonyPowerChargeText.Text = clear ? "—" : hasPowerReading ? FormatColonyAmount(totalPowerCharge) : "No EC tank observed";
        ColonyPowerCapacityText.Text = clear ? "—" : hasPowerReading ? FormatColonyAmount(totalPowerCapacity) : "No capacity observed";
        ColonyPowerPercentText.Text = clear ? "—" : !hasPowerReading || totalPowerCapacity <= 0 ? "Unavailable" : (totalPowerCharge / totalPowerCapacity * 100).ToString("0.#", CultureInfo.CurrentCulture) + "%";
        ColonyPowerFacilitiesList.ItemsSource = powerFacilities;
        ColonyPowerEmptyText.Visibility = clear || selectedVessels.Length == 0 || !hasPowerReading ? Visibility.Visible : Visibility.Collapsed;
        ColonyPowerEmptyText.Text = clear ? "No current ElectricCharge observation is available for this site." : selectedVessels.Length == 0 ? "No facilities have been observed at this site." : "No facility has an observed ElectricCharge tank at this site.";
        ColonyPowerFreshnessText.Text = viewStatus == "noWorld" ? "No save loaded" : clear ? "Observation unavailable" : viewStatus switch
        {
            "stale" => "Stale · last reported",
            "hostUnavailable" => "Host unavailable · last reported",
            "paused" => "Paused snapshot",
            "live" when powerFacilities.Any(x => x.Freshness == "Current loaded") => "Loaded readings current · unloaded last reported",
            "live" => "Last reported snapshot",
            _ => "Last reported snapshot"
        };
        RenderColonyPowerRates(colony, viewStatus, selectedVessels, selected?.Key);
        double? AmountFor(string resource, string? role = null) { var rows = tanks.Where(t => string.Equals(t.Resource, resource, StringComparison.OrdinalIgnoreCase) && (role is null || string.Equals(t.Role, role, StringComparison.OrdinalIgnoreCase))).Select(t => t.Amount).Where(double.IsFinite).ToArray(); return rows.Length == 0 ? null : rows.Sum(); }
        double? CapacityFor(string resource, string? role = null) { var rows = tanks.Where(t => string.Equals(t.Resource, resource, StringComparison.OrdinalIgnoreCase) && (role is null || string.Equals(t.Role, role, StringComparison.OrdinalIgnoreCase))).Select(t => t.Capacity).Where(double.IsFinite).ToArray(); return rows.Length == 0 ? null : rows.Sum(); }
        var ec = AmountFor("ElectricCharge");
        var ecCapacity = CapacityFor("ElectricCharge");
        ColonyPowerText.Text = clear ? "—" : ec.HasValue ? FormatColonyAmount(ec.Value) : "No tank observed";
        ColonyPowerDetailText.Text = clear ? "No reading" : ecCapacity.HasValue ? "of " + FormatColonyAmount(ecCapacity.Value) + " · stored energy" : "No capacity observed";
        var installedMachinery = AmountFor("Machinery", "installed");
        var storedMachinery = AmountFor("Machinery", "storage");
        var machineryCapacity = CapacityFor("Machinery", "storage");
        ColonyMachineryText.Text = clear ? "—" : $"{(installedMachinery.HasValue ? FormatColonyAmount(installedMachinery.Value) : "—")} installed · {(storedMachinery.HasValue ? FormatColonyAmount(storedMachinery.Value) : "—")} stored" +
            (machineryCapacity.HasValue ? " / " + FormatColonyAmount(machineryCapacity.Value) : string.Empty);

        ColonyVesselsList.ItemsSource = selectedVessels.Select(v => ToColonyVesselDisplay(v, viewStatus, colony?.Status)).ToArray();
        ColonyVesselsEmptyText.Visibility = selectedVessels.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var chains = selectedVessels.SelectMany(v => (v.Converters ?? Array.Empty<ColonyConverter>()).Select(c => ToColonyProductionDisplay(colony, viewStatus, v, c))).ToArray();
        ColonyProductionList.ItemsSource = chains;
        ColonyProductionEmptyText.Visibility = chains.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        RenderColonyResources(selectedVessels, selected?.Key, viewStatus, colony?.Status);

        if (viewStatus == "noWorld")
        {
            ColonyFreshnessText.Text = "No save is loaded. Facility readings are unavailable.";
            ColonyTitleText.Text = "Colony overview";
            ColonyAttentionHeadingText.Text = "OBSERVATION STATUS";
            ColonyAttentionText.Text = "No save is loaded; findings are unavailable.";
        }
        else if (colony is null || colony.Status == "unavailable")
        {
            ColonyFreshnessText.Text = colony?.Reason is { Length: > 0 } reason ? "Settlement observations unavailable · " + reason : "Waiting for observed settlement data.";
            ColonyAttentionHeadingText.Text = "OBSERVATION STATUS";
            ColonyAttentionText.Text = "No current facility findings are available.";
        }
        else
        {
            var capture = colony.ObservedUt is double observedUt && double.IsFinite(observedUt)
                ? "KSP UT " + observedUt.ToString("N1", CultureInfo.InvariantCulture)
                : "capture time unavailable";
            ColonyFreshnessText.ToolTip = "Colony snapshot captured at " + capture;
            string freshness = viewStatus switch
            {
                "stale" => "Stale view · last reported facility readings",
                "hostUnavailable" => "Host unavailable · last reported facility readings",
                "paused" => "KSP paused · facility readings are a snapshot",
                "live" when selectedVessels.Length > 0 && selectedVessels.All(v => v.ObservationBasis != "loaded") => "Last reported facility readings",
                "live" => "Facility readings",
                _ => "Last reported facility readings"
            };
            if (viewStatus != "hostUnavailable" && hostAge is double received && double.IsFinite(received) && received >= 0)
                freshness += " · view sample received " + FormatColonyAge(received) + " ago";
            if (colony.Status == "truncated") freshness += " · partial vessel list";
            if (!string.IsNullOrWhiteSpace(colony.Reason)) freshness += " · " + colony.Reason;
            ColonyFreshnessText.Text = freshness;
            var findings = viewStatus is "live" or "paused" && colony.Status == "observed"
                ? selectedVessels.SelectMany(v => (v.Converters ?? Array.Empty<ColonyConverter>()).SelectMany(c => ColonyDiagnosis.ForConverter(colony, v, c))).ToArray()
                : Array.Empty<ColonyFinding>();
            var facilityFreshness = viewStatus switch
            {
                "stale" => "The view is stale; current findings are unknown.",
                "hostUnavailable" => "Host unavailable; last reported readings may be outdated.",
                "paused" => "KSP is paused; readings are a paused snapshot.",
                "live" when colony.Status == "truncated" => "Partial observation; missing facilities or buffers are unknown.",
                "live" when selectedVessels.Length > 0 && selectedVessels.All(v => v.ObservationBasis != "loaded") => "These are last reported unloaded readings; current facility state is unknown.",
                "live" => "Current loaded-facility checks; unloaded facilities are last reported.",
                _ => "Last reported facility readings; current state is unknown."
            };
            var actions = findings.Count(f => f.Severity == "action");
            var checks = findings.Length - actions;
            ColonyAttentionHeadingText.Text = findings.Length == 0 ? "OBSERVATION STATUS" : $"NEEDS ATTENTION · {actions} action · {checks} check";
            ColonyAttentionText.Text = findings.Length == 0 ? facilityFreshness : findings[0].Message + (findings.Length > 1 ? $" {findings.Length - 1} more findings in Production checks." : string.Empty);
        }
        if (!clear && ColonyAttentionText.Text.Length == 0) ColonyAttentionText.Text = "Facility findings unavailable.";
    }

    private bool _wolfLedgerWritable;
    private void RenderWolfLedger(ColonySnapshot? colony, string viewStatus, ClockSample? sample, string? siteKey)
    {
        if (WolfResourceBox is null || WolfTargetBox is null || WolfAmountBox is null || WolfApplyButton is null) return;
        string? selectedResource = (WolfResourceBox.SelectedItem as WolfResourceDisplay)?.Resource;
        string draftAmount = WolfAmountBox.Text;
        var wolf = colony?.Wolf;
        var depot = wolf?.Depots?.FirstOrDefault(d => ColonySiteKey(d.Body, d.Biome) == siteKey);
        bool available = viewStatus is "live" or "paused" && wolf is not null && wolf.Status is "observed" or "truncated" && depot is not null;
        WolfFreshnessText.Text = viewStatus == "noWorld" ? "No save is loaded." : wolf is null ? "WOLF ledger not present in this observation." : !available ? (wolf.Reason ?? "WOLF ledger unavailable for this site.") : wolf.Status == "truncated" ? "Partial WOLF observation · some resources may be omitted." : "Current WOLF virtual ledger";
        var observed = available ? depot!.Resources.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase) : new Dictionary<string, WolfResource>(StringComparer.OrdinalIgnoreCase);
        var resourceNames = available ? wolf!.AllowedResources.Concat(observed.Keys).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase) : Enumerable.Empty<string>();
        var rows = resourceNames.Select(name => observed.TryGetValue(name, out var x) ? new WolfResourceDisplay(x.Name, x.Incoming.ToString("N0", CultureInfo.CurrentCulture), x.Outgoing.ToString("N0", CultureInfo.CurrentCulture), x.Available.ToString("N0", CultureInfo.CurrentCulture), x.Available < 0 ? "Deficit" : "Ready", $"Production {x.Incoming:N0} · Allocated {x.Outgoing:N0} · Available {x.Available:N0}", x.Incoming, x.Outgoing, x.Available, depot!.Body, depot.Biome) : new WolfResourceDisplay(name, "0", "0", "0", "No ledger entry", "No ledger entry", 0, 0, 0, depot!.Body, depot.Biome)).ToArray();
        WolfResourcesList.ItemsSource = rows;
        WolfResourcesEmptyText.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        WolfResourceBox.ItemsSource = rows;
        WolfResourceBox.IsEnabled = rows.Length > 0;
        WolfTargetBox.IsEnabled = rows.Length > 0;
        WolfAmountBox.IsEnabled = rows.Length > 0;
        _wolfLedgerWritable = available && depot?.Established == true && rows.Length > 0 && sample?.ActiveWorld == true && _worldId is not null && _runId is not null;
        WolfApplyButton.IsEnabled = !_wolfCommandInFlight && _wolfLedgerWritable;
        if (rows.Length == 0) WolfCommandStatusText.Text = "Select a site with an observed WOLF virtual depot to make a change.";
        else WolfResourceBox.SelectedItem = rows.FirstOrDefault(x => x.Resource.Equals(selectedResource, StringComparison.OrdinalIgnoreCase)) ?? rows[0];
        if (WolfTargetBox.SelectedIndex < 0) WolfTargetBox.SelectedIndex = 0;
        if (WolfAmountBox.Text != draftAmount) WolfAmountBox.Text = draftAmount;
        UpdateWolfPreview();
        WolfFreshnessText.ToolTip = wolf?.ObservedUt is double ut ? $"Observed at KSP UT {ut.ToString("N1", CultureInfo.InvariantCulture)}" : null;
    }

    private void WolfEditor_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => UpdateWolfPreview();
    private void WolfEditor_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => UpdateWolfPreview();
    private void UpdateWolfPreview()
    {
        if (WolfPreviewText is null) return;
        if (WolfResourceBox.SelectedItem is not WolfResourceDisplay row || WolfTargetBox.SelectedItem is not System.Windows.Controls.ComboBoxItem target || !int.TryParse(WolfAmountBox.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var amount) || amount < 0)
        { WolfPreviewText.Text = "Choose a resource and enter a non-negative whole-unit amount to preview the Incoming change."; return; }
        long next = string.Equals(target.Tag?.ToString(), "incoming", StringComparison.Ordinal) ? amount : (long)row.Incoming + amount;
        if (next > 1000000) { WolfPreviewText.Text = "Total supply must be no more than 1,000,000 units."; return; }
        WolfPreviewText.Text = next < row.Outgoing
            ? $"Supply: {row.Incoming:N0} → {next:N0} would be below the {row.Outgoing:N0} already allocated. Increase the target."
            : $"Supply: {row.Incoming:N0} → {next:N0} · Allocated stays {row.Outgoing:N0} · Available: {row.Available:N0} → {next - row.Outgoing:N0}.";
    }

    private async void WolfApply_Click(object sender, RoutedEventArgs e)
    {
        if (_wolfCommandInFlight || !_wolfLedgerWritable) return;
        if (WolfResourceBox.SelectedItem is not WolfResourceDisplay row || WolfTargetBox.SelectedItem is not System.Windows.Controls.ComboBoxItem target ||
            !int.TryParse(WolfAmountBox.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var amount) || amount < 0 || _worldId is null || _runId is null || _lastSample is not { ActiveWorld: true })
        { WolfCommandStatusText.Text = "Choose an installed resource and enter a non-negative whole-unit amount."; return; }
        if (!Guid.TryParse(_lastSample.SessionId.ToString(), out var sessionId) || !Guid.TryParse(_lastSample.LoadEpoch.ToString(), out var loadEpoch))
        { WolfCommandStatusText.Text = "The current save identity is unavailable; refresh before editing."; return; }
        WolfApplyButton.IsEnabled = false;
        _wolfCommandInFlight = true;
        WolfCommandStatusText.Text = "Applying WOLF capacity change…";
        try
        {
            var targetIncoming = string.Equals(target.Tag?.ToString(), "incoming", StringComparison.Ordinal) ? amount : checked(row.Incoming + amount);
            if (targetIncoming > 1000000) { WolfCommandStatusText.Text = "Total supply must be no more than 1,000,000 units."; return; }
            if (targetIncoming < row.Outgoing) { WolfCommandStatusText.Text = "Incoming cannot be lower than Allocated."; return; }
            var result = await _wolfAdmin.SetIncomingAsync(new WolfSetIncomingRequest
            {
                RequestId = NewRequestId("wolf"), WorldId = _worldId, RunId = _runId, SessionId = sessionId, LoadEpoch = loadEpoch,
                Body = row.Body, Biome = row.Biome, Resource = row.Resource, ExpectedIncoming = row.Incoming, ExpectedOutgoing = row.Outgoing, TargetIncoming = targetIncoming
            });
            WolfCommandStatusText.Text = result.Status is "applied" or "duplicate" ? $"WOLF change {result.Status} in the game. Incoming {result.Incoming:N0} · Allocated {result.Outgoing:N0} · Available {result.Available:N0}. Save the KSP game to retain the change." : "WOLF change rejected: " + (result.Reason ?? "the ledger changed; refresh and try again.");
            if (result.Status == "applied") await PollAsync();
        }
        catch (OperationCanceledException) { WolfCommandStatusText.Text = "WOLF change outcome is uncertain. Refresh the ledger before trying another change."; }
        catch (Exception ex) { WolfCommandStatusText.Text = "WOLF change unavailable; refresh the ledger before trying again. " + ex.Message; }
        finally { _wolfCommandInFlight = false; WolfApplyButton.IsEnabled = _wolfLedgerWritable; }
    }

    private sealed record WolfResourceDisplay(string Resource, string IncomingText, string OutgoingText, string AvailableText, string StatusText, string DetailText, int Incoming, int Outgoing, int Available, string Body, string Biome);

    private static ColonyVesselDisplay ToColonyVesselDisplay(ColonyVessel vessel, string viewStatus, string? snapshotStatus)
    {
        var tanks = (vessel.Tanks ?? Array.Empty<ColonyTank>()).ToArray();
        var resources = tanks.GroupBy(t => (t.Resource, Role: string.Equals(t.Resource, "Machinery", StringComparison.OrdinalIgnoreCase) ? (t.Role ?? string.Empty).ToLowerInvariant() : string.Empty))
            .OrderBy(g => ResourceLabel(g.Key.Resource), StringComparer.CurrentCultureIgnoreCase).ThenBy(g => g.Key.Role, StringComparer.Ordinal)
            .Select(g =>
            {
                double amount = g.Where(t => double.IsFinite(t.Amount)).Sum(t => t.Amount);
                double capacity = g.Where(t => double.IsFinite(t.Capacity)).Sum(t => t.Capacity);
                string role = string.Equals(g.Key.Resource, "Machinery", StringComparison.OrdinalIgnoreCase)
                    ? g.Key.Role switch { "installed" => " · installed", "storage" => " · stored", _ => string.Empty }
                    : string.Empty;
                return new ColonyResourceDisplay(ResourceLabel(g.Key.Resource) + role, g.Key.Resource,
                    $"{FormatColonyAmount(amount)} / {FormatColonyAmount(capacity)}", capacity > 0 ? Math.Clamp(amount / capacity * 100, 0, 100) : 0,
                    string.Join("\n", g.Select(t => $"{t.Resource} · {t.Role ?? "role unknown"}: {FormatColonyAmount(t.Amount)} / {FormatColonyAmount(t.Capacity)} · warehouse {FlagText(t.WarehousePresent)} · local {FlagText(t.LocalWarehouseOn)} · flow {FlagText(t.FlowEnabled)}")));
            }).ToArray();
        var technical = new[] { vessel.ObservationBasis == "loaded" ? "Loaded facility" : "Last reported unloaded snapshot",
            $"Position: {vessel.Latitude.ToString("0.000", CultureInfo.InvariantCulture)}°, {vessel.Longitude.ToString("0.000", CultureInfo.InvariantCulture)}°" };
        var currentLoaded = viewStatus is "live" or "paused" && snapshotStatus == "observed" && vessel.ObservationBasis == "loaded";
        return new ColonyVesselDisplay(vessel.Name, $"Crew {vessel.Crew}", currentLoaded ? "Current reading" : "Last reported",
            resources, resources.Length == 0 ? "No physical tank readings observed." : string.Empty, string.Join(" · ", technical));
    }

    private static ColonyProductionDisplay ToColonyProductionDisplay(ColonySnapshot? snapshot, string viewStatus, ColonyVessel vessel, ColonyConverter converter)
    {
        var findings = viewStatus is "live" or "paused" ? ColonyDiagnosis.ForConverter(snapshot, vessel, converter) : Array.Empty<ColonyFinding>();
        string diagnosis = viewStatus is "stale" or "hostUnavailable" ? "Current state unknown; showing the last reported switch setting."
            : snapshot?.Status == "truncated" ? "Partial observation; definitive absence checks are unavailable."
            : vessel.ObservationBasis != "loaded" ? "Last reported unloaded snapshot; converter and warehouse settings may be unknown."
            : findings.Length == 0 ? "No confirmed configuration finding. Positive production is not proven."
            : string.Join(" ", findings.Select(f => f.Message));
        string status = converter.Running switch { true => "Enabled switch", false => "Stopped switch", null => "Switch unknown" };
        string flow = string.Join(", ", (converter.Inputs ?? Array.Empty<string>()).Select(ResourceLabel)) + " → " + string.Join(", ", (converter.Outputs ?? Array.Empty<string>()).Select(ResourceLabel));
        return new ColonyProductionDisplay(ResourceLabel(converter.Recipe), vessel.Name + " · " + converter.PartName, flow, status, diagnosis);
    }

    private static string ColonySiteKey(string? body, string? biome) => (body ?? string.Empty).Trim() + "\0" + (biome ?? string.Empty).Trim();
    private static string SiteLabel(string? body, string? biome) => string.Join(" · ", new[] { body?.Trim(), biome?.Trim() }.Where(x => !string.IsNullOrWhiteSpace(x)));
    private static string FormatColonyAmount(double value) => !double.IsFinite(value) ? "—" : value != 0 && Math.Abs(value) < 0.001 ? "<0.001" : Math.Abs(value) < 1 ? value.ToString("0.###", CultureInfo.CurrentCulture) : Math.Abs(value) < 100 ? value.ToString("N2", CultureInfo.CurrentCulture) : value.ToString("N1", CultureInfo.CurrentCulture);
    private static string FormatSignedColonyAmount(double value) => value > 0 ? "+" + FormatColonyAmount(value) : FormatColonyAmount(value);
    private static string FlagText(bool? value) => value switch { true => "on", false => "off", null => "unknown" };
    private static string FormatColonyAge(double seconds) => seconds < 60 ? seconds.ToString("0.0", CultureInfo.CurrentCulture) + " s" : seconds < 3600 ? (seconds / 60).ToString("0.0", CultureInfo.CurrentCulture) + " min" : (seconds / 3600).ToString("0.0", CultureInfo.CurrentCulture) + " h";
    private sealed record ColonySiteChoice(string Key, string Label);
    private sealed record ColonyVesselDisplay(string Label, string CrewText, string StatusText, ColonyResourceDisplay[] Resources, string EmptyResourcesText, string TechnicalSummary)
    {
        public string[] TechnicalResources => Resources.SelectMany(r => new[] { $"{r.Name}: {r.Details}" }).ToArray();
    }
    private sealed record ColonyResourceDisplay(string Name, string RawName, string AmountText, double FillPercent, string Details);
    private sealed record ColonyProductionDisplay(string Name, string VesselLabel, string FlowText, string StatusText, string Diagnosis);
    private sealed record ColonyPowerFacilityDisplay(string Key, bool IsExpanded, string Name, string ChargeText,
        string PercentText, string Freshness, string FlowText, string FlowDetail,
        string EstimateText, string EstimateDetail, string StorageDetail);
    private sealed record ColonyResourceFacilityDisplay(string Name, string ObservationText, string StockText,
        string FlowText, string StorageDetailText);
    private sealed record ColonyResourceDisplayRow(string Key, bool IsExpanded, string Name, string StockText,
        string OutputCountText, string InputCountText, ColonyResourceFacilityDisplay[] Facilities);

    private void RenderColonyResources(ColonyVessel[] vessels, string? siteKey, string viewStatus, string? snapshotStatus)
    {
        var summaries = ColonyResourceProjection.ForSite(vessels);
        _resourceRows = summaries.Select(summary =>
        {
            string key = (siteKey ?? string.Empty) + "\0" + summary.Resource;
            var facilities = summary.Facilities.Select(f =>
            {
                string observation = viewStatus == "live" && snapshotStatus == "observed" && f.ObservationBasis == "loaded"
                    ? "Current loaded" : "Last reported";
                string stock = f.Amount.HasValue && f.Capacity.HasValue
                    ? $"Stock {FormatColonyAmount(f.Amount.Value)} / {FormatColonyAmount(f.Capacity.Value)}"
                    : "No physical tank observed";
                string producers = f.Producers.Length == 0 ? "No output unit observed" :
                    "Output: " + string.Join(", ", f.Producers.Select(c => ResourceLabel(c.Recipe) + " (" + ConverterState(c.Running, f.ObservationBasis, viewStatus) + ")"));
                string consumers = f.Consumers.Length == 0 ? "No input unit observed" :
                    "Input: " + string.Join(", ", f.Consumers.Select(c => ResourceLabel(c.Recipe) + " (" + ConverterState(c.Running, f.ObservationBasis, viewStatus) + ")"));
                string detail = f.Tanks.Length == 0 ? "" : string.Join(" · ", f.Tanks.Select(t =>
                    $"{t.Role ?? "tank"} {FormatColonyAmount(t.Amount)} / {FormatColonyAmount(t.Capacity)}; local warehouse {FlagText(t.LocalWarehouseOn)}; flow {FlagText(t.FlowEnabled)}"));
                return new ColonyResourceFacilityDisplay(f.Name, observation, stock, producers + "  ·  " + consumers, detail);
            }).ToArray();
            string stockText = summary.Amount.HasValue && summary.Capacity.HasValue
                ? $"{FormatColonyAmount(summary.Amount.Value)} / {FormatColonyAmount(summary.Capacity.Value)}"
                : "No tank observed";
            return new ColonyResourceDisplayRow(key, _expandedResourceKeys.Contains(key), ResourceLabel(summary.Resource), stockText,
                summary.ProducerCount.ToString(CultureInfo.CurrentCulture), summary.ConsumerCount.ToString(CultureInfo.CurrentCulture), facilities);
        }).OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        ColonyResourceSummaryText.Text = summaries.Length == 0 ? "No resources observed at this site." :
            $"{summaries.Length} resources · {summaries.Count(r => r.Amount.HasValue)} with physical stock · {vessels.Count(v => v.ObservationBasis == "loaded")}/{vessels.Length} facilities loaded" +
            (snapshotStatus == "truncated" ? " · partial facility scan" : string.Empty);
        ApplyColonyResourceFilter();
    }

    private static string ConverterState(bool? running, string observationBasis, string viewStatus) =>
        observationBasis != "loaded" || viewStatus is not ("live" or "paused") ? "state unknown" :
        running switch { true => "enabled", false => "stopped", null => "state unknown" };

    private void ColonyResourceSearch_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => ApplyColonyResourceFilter();

    private void ApplyColonyResourceFilter()
    {
        if (ColonyResourcesList is null || ColonyResourcesEmptyText is null) return;
        string filter = ColonyResourceSearchBox?.Text?.Trim() ?? string.Empty;
        var visible = string.IsNullOrEmpty(filter) ? _resourceRows :
            _resourceRows.Where(r => r.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase)).ToArray();
        ColonyResourcesList.ItemsSource = visible;
        ColonyResourcesEmptyText.Text = _resourceRows.Length == 0 ? "No resources have been observed at this site." : "No resources match this search.";
        ColonyResourcesEmptyText.Visibility = visible.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ResourceDrill_Expanded(object sender, RoutedEventArgs e)
    { if (sender is System.Windows.Controls.Expander { Tag: string key }) _expandedResourceKeys.Add(key); }
    private void ResourceDrill_Collapsed(object sender, RoutedEventArgs e)
    { if (sender is System.Windows.Controls.Expander { Tag: string key }) _expandedResourceKeys.Remove(key); }
    private void PowerDrill_Expanded(object sender, RoutedEventArgs e)
    { if (sender is System.Windows.Controls.Expander { Tag: string key }) _expandedPowerKeys.Add(key); }
    private void PowerDrill_Collapsed(object sender, RoutedEventArgs e)
    { if (sender is System.Windows.Controls.Expander { Tag: string key }) _expandedPowerKeys.Remove(key); }

    private void RenderColonyPowerRates(ColonySnapshot? colony, string viewStatus,
        ColonyVessel[] selectedVessels, string? siteKey)
    {
        const string coverageNote = "Direct resource changes and transfers are excluded.";
        string unavailable = viewStatus switch
        {
            "noWorld" => "No save loaded",
            "hostUnavailable" => "Host unavailable",
            "stale" => "Stale · current rate unavailable",
            "paused" => "Paused · current rate unavailable",
            _ when colony is null || colony.Status == "unavailable" => "Observation unavailable",
            _ when colony.Status == "truncated" => "Partial vessel list · rate unavailable",
            _ when siteKey is null || selectedVessels.Length == 0 => "No facilities at this site",
            _ => "Rate unavailable"
        };
        bool current = viewStatus == "live" && colony?.Status == "observed" &&
            siteKey is not null && selectedVessels.Length > 0;
        var usable = current ? selectedVessels
            .Where(v => v.ObservationBasis == "loaded" && v.Power is { Status: "partial", SampleUt: double sampleUt, WindowSeconds: double seconds,
                GenerationEcPerSecond: double generation, ConsumptionEcPerSecond: double consumption,
                NetEcPerSecond: double net } &&
                double.IsFinite(sampleUt) && double.IsFinite(seconds) && seconds > 0 &&
                double.IsFinite(generation) && generation >= 0 && double.IsFinite(consumption) && consumption >= 0 &&
                double.IsFinite(net) &&
                (colony!.ObservedUt is not double observedUt || Math.Abs(observedUt - sampleUt) <= 1))
            .ToArray() : Array.Empty<ColonyVessel>();
        double? latestUt = usable.Length == 0 ? null : usable.Max(v => v.Power!.SampleUt!.Value);
        var sameEnd = latestUt is double endUt ? usable.Where(v => Math.Abs(v.Power!.SampleUt!.Value - endUt) <= 0.01).ToArray() : Array.Empty<ColonyVessel>();
        double? latestWindow = sameEnd.Length == 0 ? null : sameEnd[0].Power!.WindowSeconds;
        var aligned = latestWindow is double window ? sameEnd.Where(v => Math.Abs(v.Power!.WindowSeconds!.Value - window) <= 0.05).ToArray() : Array.Empty<ColonyVessel>();

        if (aligned.Length == 0)
        {
            ColonyPowerGenerationRateText.Text = "—";
            ColonyPowerConsumptionRateText.Text = "—";
            ColonyPowerNetRateText.Text = "—";
            ColonyPowerRateFreshnessText.Text = unavailable;
            var reason = current ? selectedVessels.Select(v => v.Power?.Reason).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) : null;
            ColonyPowerRateDetailText.Text = (reason is null ? "No current tracked EC/s interval at this site." : reason.Trim()) + " " + coverageNote;
            ColonyPowerRateDetailText.ToolTip = null;
            _powerRateHistory.Interrupt(siteKey);
            DrawPowerRateHistory();
            return;
        }

        double generationTotal = aligned.Sum(v => v.Power!.GenerationEcPerSecond!.Value);
        double consumptionTotal = aligned.Sum(v => v.Power!.ConsumptionEcPerSecond!.Value);
        double netTotal = aligned.Sum(v => v.Power!.NetEcPerSecond!.Value);
        ColonyPowerGenerationRateText.Text = FormatColonyAmount(generationTotal) + " EC/s";
        ColonyPowerConsumptionRateText.Text = FormatColonyAmount(consumptionTotal) + " EC/s";
        ColonyPowerNetRateText.Text = netTotal > 0 ? "+" + FormatColonyAmount(netTotal) + " EC/s" : FormatColonyAmount(netTotal) + " EC/s";
        ColonyPowerRateFreshnessText.Text = $"Partial coverage · {aligned.Length}/{selectedVessels.Length} facilities";
        var windowMin = aligned.Min(v => v.Power!.WindowSeconds!.Value);
        var windowMax = aligned.Max(v => v.Power!.WindowSeconds!.Value);
        string windowText = Math.Abs(windowMin - windowMax) < 0.05
            ? windowMin.ToString("0.#", CultureInfo.CurrentCulture) + " s"
            : windowMin.ToString("0.#", CultureInfo.CurrentCulture) + "–" + windowMax.ToString("0.#", CultureInfo.CurrentCulture) + " s";
        int unavailableCount = selectedVessels.Length - aligned.Length;
        string excluded = unavailableCount > 0 ? $" {unavailableCount} facility reading{(unavailableCount == 1 ? "" : "s")} unavailable or out of sync." : string.Empty;
        ColonyPowerRateDetailText.Text = $"Tracked interval {windowText}.{excluded} {coverageNote}";
        ColonyPowerRateDetailText.ToolTip = $"Interval ending at KSP UT {latestUt!.Value.ToString("N1", CultureInfo.CurrentCulture)} s.";
        if (siteKey is not null)
        {
            string contributors = string.Join("\0", aligned.Select(v => v.VesselId).OrderBy(x => x, StringComparer.Ordinal));
            _powerRateHistory.Observe(siteKey, latestUt.Value, generationTotal, consumptionTotal, netTotal, contributors);
        }
        DrawPowerRateHistory();
    }

    private void ColonyPowerHistoryCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawPowerRateHistory();

    private void DrawPowerRateHistory()
    {
        var canvas = ColonyPowerHistoryCanvas;
        canvas.Children.Clear();
        var points = _powerRateHistory.ForSite(_selectedColonySiteKey);
        ColonyPowerHistoryStatusText.Text = points.Length == 0 ? "No tracked live readings" :
            $"{points.Length} partial reading{(points.Length == 1 ? "" : "s")}";
        ColonyPowerHistoryStatusText.ToolTip = points.Length == 0 ? null :
            $"Latest KSP UT {points[^1].Ut.ToString("N1", CultureInfo.CurrentCulture)} s.";
        double width = canvas.ActualWidth;
        double height = canvas.ActualHeight;
        if (width < 24 || height < 24) return;

        double top = 8, bottom = height - 18, left = 36, right = width - 9;
        if (points.Length == 0)
        {
            var empty = new System.Windows.Controls.TextBlock { Text = "Tracked flow history appears after a live rate reading.", FontSize = 11, Foreground = (Brush)FindResource("MutedBrush") };
            System.Windows.Controls.Canvas.SetLeft(empty, 9);
            System.Windows.Controls.Canvas.SetTop(empty, height / 2 - 8);
            canvas.Children.Add(empty);
            return;
        }

        double min = Math.Min(0, points.Min(p => Math.Min(p.NetEcPerSecond, Math.Min(p.GenerationEcPerSecond, p.ConsumptionEcPerSecond))));
        double max = Math.Max(0, points.Max(p => Math.Max(p.NetEcPerSecond, Math.Max(p.GenerationEcPerSecond, p.ConsumptionEcPerSecond))));
        double span = max - min;
        if (span < 0.001) span = Math.Max(1, Math.Abs(max) * 0.2);
        min -= span * 0.12;
        max += span * 0.12;
        double Y(double value) => bottom - (value - min) / (max - min) * (bottom - top);
        double firstUt = points[0].Ut, lastUt = points[^1].Ut;
        double X(int i) => lastUt <= firstUt ? (left + right) / 2 :
            left + (points[i].Ut - firstUt) * (right - left) / (lastUt - firstUt);

        void AxisLabel(string value, double x, double y)
        {
            var label = new System.Windows.Controls.TextBlock { Text = value, FontSize = 9,
                Foreground = (Brush)FindResource("MutedBrush") };
            System.Windows.Controls.Canvas.SetLeft(label, x);
            System.Windows.Controls.Canvas.SetTop(label, y);
            canvas.Children.Add(label);
        }
        AxisLabel(max.ToString("0.#", CultureInfo.CurrentCulture), 2, top - 6);
        AxisLabel("0", left + 3, Math.Max(top, Math.Min(bottom - 9, Y(0) - 6)));
        AxisLabel(min.ToString("0.#", CultureInfo.CurrentCulture), 2, bottom - 6);
        if (lastUt > firstUt)
            AxisLabel("−" + FormatColonyAge(lastUt - firstUt), left, height - 15);
        AxisLabel("Now", right - 21, height - 15);

        canvas.Children.Add(new Line { X1 = left, X2 = right, Y1 = Y(0), Y2 = Y(0), Stroke = new SolidColorBrush(Color.FromRgb(201, 215, 218)), StrokeThickness = 1 });
        void DrawSeries(Func<PowerRateHistory.Point, double> value, Color color)
        {
            var stroke = new SolidColorBrush(color);
            for (int i = 1; i < points.Length; i++)
            {
                if (points[i].StartsSegment) continue;
                canvas.Children.Add(new Line { X1 = X(i - 1), X2 = X(i), Y1 = Y(value(points[i - 1])), Y2 = Y(value(points[i])), Stroke = stroke, StrokeThickness = 1.8 });
            }
            for (int i = 0; i < points.Length; i++)
            {
                var point = points[i];
                double size = i == points.Length - 1 ? 5 : 3;
                var marker = new Ellipse { Width = size, Height = size, Fill = stroke,
                    ToolTip = $"UT {point.Ut.ToString("N1", CultureInfo.CurrentCulture)} · {value(point).ToString("+0.###;-0.###;0", CultureInfo.CurrentCulture)} EC/s" };
                System.Windows.Controls.Canvas.SetLeft(marker, X(i) - size / 2);
                System.Windows.Controls.Canvas.SetTop(marker, Y(value(point)) - size / 2);
                canvas.Children.Add(marker);
            }
        }
        DrawSeries(p => p.GenerationEcPerSecond, Color.FromRgb(36, 137, 99));
        DrawSeries(p => p.ConsumptionEcPerSecond, Color.FromRgb(187, 103, 60));
        DrawSeries(p => p.NetEcPerSecond, Color.FromRgb(8, 127, 140));
    }

    private void RenderDepots(DepotSummaryView[]? depots, DepotView? current, bool hostUnavailable)
    {
        var summaries = (depots ?? Array.Empty<DepotSummaryView>())
            .GroupBy(x => x.DepotId, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First()).ToArray();
        foreach (var name in summaries.SelectMany(x => x.Resources ?? Array.Empty<ResourceRow>()).Select(x => x.Name))
            AddRouteResource(name);
        var rows = summaries.Select(x =>
        {
            var isCurrent = current?.DepotId?.ToString("N").Equals(x.DepotId.Replace("-", ""), StringComparison.OrdinalIgnoreCase) == true;
            var useCurrent = isCurrent && current?.Status == "live";
            return StockDisplay(x.Label, x.DepotId, useCurrent ? current!.Resources : x.Resources,
                hostUnavailable ? "Last received" : useCurrent ? "Live" : StockStatus(x.Status, x.StockAgeSeconds),
                hostUnavailable ? "Host disconnected" : useCurrent ? string.Empty : x.Reason ?? string.Empty);
        }).ToList();
        if (current?.DepotId is Guid id && !summaries.Any(x => Guid.TryParse(x.DepotId, out var summaryId) && summaryId == id))
            rows.Add(StockDisplay(current.Label ?? "Depot", id.ToString("N"), current.Resources,
                hostUnavailable ? "Last received" : StockStatus(current.Status, current.StockAgeSeconds), current.Reason ?? string.Empty));
        DepotsGrid.ItemsSource = rows.OrderBy(x => x.Label, StringComparer.CurrentCultureIgnoreCase).ToArray();
        DepotListMessageText.Text = rows.Count == 0
            ? depots is null ? "Waiting for the game's depot list." : "No depots registered yet. Visit a station in KSP to register its tanks."
            : $"{rows.Count} registered {(rows.Count == 1 ? "depot" : "depots")}" + (hostUnavailable ? " · showing the last received stock" : string.Empty);
        var selectedSource = SourceDepotBox.SelectedItem as SourceChoice;
        var selectedDestination = DestinationDepotBox.SelectedItem as DepotSummaryView;
        var choices = new[] { new SourceChoice("Kerbin · external supplier (planned)", null) }
            .Concat(summaries.Select(x => new SourceChoice(x.Label, x))).ToArray();
        SourceDepotBox.ItemsSource = choices;
        DestinationDepotBox.ItemsSource = summaries;
        var exportSourceId = (ExportSourceBox.SelectedItem as DepotSummaryView)?.DepotId;
        ExportSourceBox.ItemsSource = summaries;
        ExportSourceBox.SelectedItem = summaries.FirstOrDefault(x => x.DepotId == exportSourceId);
        if (selectedSource is not null)
            SourceDepotBox.SelectedItem = choices.FirstOrDefault(x => x.Depot?.DepotId == selectedSource.Depot?.DepotId);
        if (selectedDestination is not null)
            DestinationDepotBox.SelectedItem = summaries.FirstOrDefault(x => x.DepotId == selectedDestination.DepotId);
        UpdateSourceAvailability();
        UpdateDeliveryReadiness();
    }

    private void SourceDepotBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (SourceAvailabilityText is null || SaveRouteButton is null) return;
        UpdateSourceAvailability();
        SetWritable(_lastViewStatus is "live" or "paused");
    }

    private void ExportSource_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (SaveExportRouteButton is not null) SetWritable(_lastViewStatus is "live" or "paused");
    }

    private async void SaveExportRoute_Click(object sender, RoutedEventArgs e)
    {
        if (_acceptedState is null || _worldId is null || _runId is null || ExportSourceBox.SelectedItem is not DepotSummaryView source) return;
        var routeId = ExportRouteIdBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(routeId) || routeId.Length > 128 || !TryTravelDuration(ExportDurationBox.Text, out var duration) || !TryExportQuantity(ExportQuantityBox.Text, out var quantity))
        { CommandStatusText.Text = "Enter a route name, positive travel time, and 1 to 1,000,000 whole Ore units."; return; }
        var batchMicroUnits = checked(quantity * 1_000_000L);
        var existing = _acceptedState.RouteVersions.FirstOrDefault(r => r.RouteId == routeId && r.DestinationKind == OreExportPolicy.VirtualDestinationKind &&
            r.SourceDepotId == source.DepotId && r.SourceMembershipRevision == source.MembershipRevision && r.SourceMembershipHash == source.MembershipHash && r.TravelDurationSeconds == duration &&
            r.FundsPerUnit == OreExportPolicy.DefaultFundsPerUnit && r.Resources.Length == 1 && r.Resources[0].ResourceName == "Ore" && r.Resources[0].AmountMicroUnits == batchMicroUnits);
        if (existing is not null)
        {
            RouteVersionBox.SelectedItem = (RouteVersionBox.ItemsSource as RouteChoice[])?.FirstOrDefault(x => x.Route.RouteId == existing.RouteId && x.Route.Version == existing.Version);
            CommandStatusText.Text = "This export route is already saved. Save its automatic order below.";
            return;
        }
        var route = new RouteVersionRecord { RouteId = routeId, Version = _acceptedState.RouteVersions.Where(r => r.RouteId == routeId).Select(r => r.Version).DefaultIfEmpty(0).Max() + 1,
            SourceDepotId = source.DepotId, SourceMembershipRevision = source.MembershipRevision, SourceMembershipHash = source.MembershipHash,
            DestinationKind = OreExportPolicy.VirtualDestinationKind, DestinationDepotId = OreExportPolicy.KerbinBuyerId, FundsPerUnit = OreExportPolicy.DefaultFundsPerUnit,
            TravelDurationSeconds = duration, Provenance = "Modeled routine Kerbin landing and recovery; total Ore payment 100 funds/unit",
            Resources = new[] { new ResourceAmount { ResourceName = "Ore", AmountMicroUnits = batchMicroUnits } } };
        await SubmitAsync(new SubmitCommand { ProtocolVersion = 1, MessageType = "submitCommand", ClientRequestId = NewRequestId("export-route"), WorldId = _worldId, RunId = _runId,
            CommandKind = "routeUpsert", Delivery = new DeliveryCommandPayload { Kind = "routeUpsert", RouteVersion = route } });
    }

    private static bool TryExportQuantity(string text, out long units) => long.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out units) && units >= 1 && units <= OreExportPolicy.MaxOreUnits;
    private void ExportQuantity_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (ExportPayoutPreviewText is null) return;
        ExportPayoutPreviewText.Text = TryExportQuantity(ExportQuantityBox.Text, out var units)
            ? $"{FormatUnits(units)} Ore × 100 funds = {FormatUnits(checked(units * OreExportPolicy.DefaultFundsPerUnit))} funds at recovery"
            : "Enter 1 to 1,000,000 whole Ore units.";
    }

    private void UpdateSourceAvailability() => SourceAvailabilityText.Text = SourceDepotBox.SelectedItem switch
    {
        SourceChoice { Depot: null } => "Kerbin is a planned external supplier. Routes from Kerbin cannot be saved until the external-source scheduler is built.",
        SourceChoice => "Registered depot source. Stock must be observed and available before a shipment can depart.",
        _ => "Choose a registered depot as the source, or inspect the planned Kerbin supplier."
    };

    private void AddRouteResource(string name, bool selected = false)
    {
        name = name?.Trim() ?? string.Empty;
        if (name.Length == 0 || name.Length > 128) return;
        var existing = _routeResources.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) { if (selected) existing.Selected = true; return; }
        _routeResources.Add(new RouteResourceEntry(name, selected));
    }

    private void AddResource_Click(object sender, RoutedEventArgs e)
    {
        var name = AdditionalResourceBox.Text.Trim();
        if (name.Length == 0 || name.Length > 128)
        { CommandStatusText.Text = "Enter a KSP resource name of at most 128 characters."; return; }
        AddRouteResource(name, selected: true);
        AdditionalResourceBox.Clear();
    }

    private bool TryBuildRouteManifest(out ResourceAmount[] manifest)
    {
        manifest = Array.Empty<ResourceAmount>();
        var selected = _routeResources.Where(x => x.Selected).ToArray();
        if (selected.Length == 0 || selected.Length > 32) return false;
        var rows = new List<ResourceAmount>(selected.Length);
        foreach (var row in selected)
        {
            if (!TryResourceUnits(row.AmountText, out var amount) || amount <= 0) return false;
            rows.Add(new ResourceAmount { ResourceName = row.Name, AmountMicroUnits = amount });
        }
        manifest = rows.ToArray();
        return true;
    }

    private static DepotStockDisplay StockDisplay(string label, string id, ResourceRow[]? resources, string status, string reason)
    {
        var observed = status is "Live" or "KSP snapshot" or "Last received";
        string Show(string name)
        {
            if (!observed) return "—";
            var row = (resources ?? Array.Empty<ResourceRow>()).FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            return row is null ? "—" : $"{FormatUnits(row.Amount)} / {FormatUnits(row.MaxAmount)}";
        }
        return new DepotStockDisplay(label, id, Show("LiquidFuel"), Show("Oxidizer"), Show("MonoPropellant"), Show("Ore"), status, reason);
    }

    private static string StockStatus(string status, double? age) => status switch
    {
        "live" => "Live",
        "lastObserved" => "KSP snapshot",
        "unavailable" => "Unavailable",
        _ => "No stock yet"
    };

    private async Task RefreshAcceptedStateAsync(ClockView view)
    {
        var context = _worldId is null || _runId is null ? null : _worldId + "\0" + _runId;
        if (context != _projectionContext)
        {
            ClearRuleEdit(rerender: false);
            _projectionContext = context;
            _acceptedState = null;
            _deliveryReadiness = null;
            _transientShipmentHolds.Clear();
            _lastProjectionAt = 0;
            RenderOperations();
        }
        if (_worldId is null || _runId is null || view.Status is not ("live" or "paused") || view.Sample?.ActiveWorld != true)
        {
            _deliveryReadiness = null;
            if (view.Status is "stale" or "hostUnavailable") _acceptedState = null;
            SetWritable(false);
            LogisticsStatusText.Text = _worldId is null || _runId is null ? "Waiting for the KSP bridge to identify the active save." : "Logistics is read-only until a live KSP attachment is available.";
            RenderOperations();
            return;
        }
        if (Environment.TickCount64 - _lastProjectionAt < 2000) return;
        _lastProjectionAt = Environment.TickCount64;
        try
        {
            var result = await _commands.GetAcceptedStateAsync(_worldId, _runId, TimeSpan.FromSeconds(2));
            if (result.Status != "available" || result.AcceptedCapsule is null)
            {
                _acceptedState = null;
                _transientShipmentHolds.Clear();
                LogisticsStatusText.Text = result.Reason ?? "Host has no matching accepted-state projection.";
            }
            else
            {
                var state = AcceptedStateCodec.ReadCapsule(result.AcceptedCapsule);
                if (state.WorldId != _worldId) throw new InvalidDataException("Host projection world does not match the clock context.");
                _acceptedState = state;
                _transientShipmentHolds.Clear();
                foreach (var hold in result.TransientShipmentHolds.Take(AcceptedStateV2Limits.MaxActiveShipments))
                    if (!string.IsNullOrWhiteSpace(hold.ShipmentId) && !string.IsNullOrWhiteSpace(hold.Reason))
                        _transientShipmentHolds[hold.ShipmentId] = hold.Reason;
                LogisticsStatusText.Text = state.WritesBlocked ? "An unresolved physical issue is blocking logistics orders." : "Logistics state is synchronized with the game.";
            }
            RenderOperations();
            await RefreshDeliveryReadinessAsync();
            SetWritable((view.Status is "live" or "paused") && !(_acceptedState?.WritesBlocked ?? false));
        }
        catch (Exception ex)
        {
            _acceptedState = null;
            _deliveryReadiness = null;
            _transientShipmentHolds.Clear();
            LogisticsStatusText.Text = "Accepted state unavailable: " + ex.Message;
            RenderOperations(); SetWritable(false);
        }
    }

    private void RenderOperations()
    {
        var state = _acceptedState;
        if (state is null)
        {
            ExportPaymentStatusText.Text = "Recovery payment data is unavailable.";
            _automaticOrders.Clear();
            _savedRoutes.Clear();
            _issues.Clear();
            RouteVersionBox.ItemsSource = Array.Empty<RouteChoice>();
            _deliveries.Clear();
            DeliveriesEmptyText.Text = "Delivery data is unavailable right now.";
            AutomaticOrdersEmptyText.Text = "Automatic order data is unavailable right now.";
            SavedRoutesEmptyText.Text = "Saved route data is unavailable right now.";
            IssuesEmptyText.Text = "Issue data is unavailable right now.";
            SetActivityVisibility(DeliveriesGrid, DeliveriesEmptyText, 0);
            SetActivityVisibility(AutomaticOrdersGrid, AutomaticOrdersEmptyText, 0);
            SetActivityVisibility(SavedRoutesGrid, SavedRoutesEmptyText, 0);
            SetActivityVisibility(IssuesGrid, IssuesEmptyText, 0);
            UpdateDeliveryReadiness();
            return;
        }
        var latestRecovery = state.Receipts.Where(r => r.OperationKind == "recoverySale" && r.Outcome == "accepted" && r.FundsWitness is not null).OrderByDescending(r => r.CommandSequence).FirstOrDefault();
        ExportPaymentStatusText.Text = state.EconomicFaults.Length > 0 ? "Recovery funds outcome is uncertain. Automatic logistics are blocked; inspect Activity."
            : latestRecovery?.FundsWitness is FundsSuccessWitness funds
                ? $"Latest retained recovery: +{FormatUnits(funds.IntendedDeltaFunds)} funds · game funds after payment {FormatUnits(funds.ObservedAfterFunds)}. Older receipts may have been compacted."
                : "No retained recovery payments in the selected save.";
        var routes = new List<SavedRouteDisplay>();
        foreach (var route in state.RouteVersions.Where(x => !x.LegacyOpaque && !IsStockRoute(x.RouteId)).OrderBy(x => x.RouteId, StringComparer.Ordinal).ThenBy(x => x.Version))
            routes.Add(new(route.RouteId + " v" + route.Version.ToString(CultureInfo.InvariantCulture), $"{DepotName(route.SourceDepotId)} → {DepotName(route.DestinationDepotId)} · {CargoLabel(route.Resources)} · {FormatTravelDuration(route.TravelDurationSeconds)} travel", "Saved"));
        _savedRoutes.Clear();
        foreach (var route in routes) _savedRoutes.Add(route);
        SavedRoutesEmptyText.Text = routes.Count == 0 ? "No saved routes." : $"{routes.Count} saved {(routes.Count == 1 ? "route" : "routes")}";
        SetActivityVisibility(SavedRoutesGrid, SavedRoutesEmptyText, routes.Count);

        string? selectedRuleId = (AutomaticOrdersGrid.SelectedItem as AutomaticOrderDisplay)?.RuleId;
        var orders = new List<AutomaticOrderDisplay>();
        foreach (var rule in state.DeliveryRules.OrderBy(x => x.RuleId, StringComparer.Ordinal))
        {
            var route = state.RouteVersions.FirstOrDefault(x => !x.LegacyOpaque && x.RouteId == rule.RouteId && x.Version == rule.RouteVersion);
            var detail = rule.LegacyOpaque ? "Schedule details unavailable" : rule.Kind == "exportStock"
                ? $"{rule.RouteId} v{rule.RouteVersion} · export {FormatUnits(rule.BatchSizeMicroUnits / 1_000_000d)} Ore when available · " + (route is null ? "price unavailable" : $"recover for {FormatUnits(OreExportPolicy.RecoveryFunds(route.Resources, route.FundsPerUnit))} funds ({route.FundsPerUnit}/unit)")
                : rule.Kind == "repeat"
                ? $"{rule.RouteId} v{rule.RouteVersion} · repeats every {FormatTravelDuration(rule.IntervalSeconds)}"
                : rule.LowTriggerMicroUnits == rule.TargetMicroUnits
                    ? $"{StockBaseName(rule.RouteId)} · Keep {ResourceLabel(rule.ResourceName)} full · target {FormatUnits(rule.TargetMicroUnits / 1000000d)} · up to {FormatUnits(rule.BatchSizeMicroUnits / 1000000d)} per trip"
                    : $"{StockBaseName(rule.RouteId)} · order {ResourceLabel(rule.ResourceName)} below {FormatUnits(rule.LowTriggerMicroUnits / 1000000d)} · target {FormatUnits(rule.TargetMicroUnits / 1000000d)} · up to {FormatUnits(rule.BatchSizeMicroUnits / 1000000d)} per trip";
            bool clockAvailable = _lastViewStatus is "live" or "paused" && _lastSample?.ActiveWorld == true && _lastSample.UtSeconds.HasValue;
            string orderStatus = rule.LegacyOpaque ? "Details unavailable" : !rule.Enabled ? "Paused" : !clockAvailable ? "KSP clock unavailable"
                : rule.Kind == "repeat" ? rule.WaitingRequest ? _lastViewStatus == "paused" ? "Paused · launch pending" : "Launch pending · waiting for source stock or capability" : "On"
                : _lastViewStatus == "paused" ? "Paused · stock-triggered" : StockRuleState(rule);
            string nextLaunch = "—", estimatedArrival = "—";
            if (!rule.LegacyOpaque && rule.Enabled && !clockAvailable)
            {
                nextLaunch = "KSP clock unavailable";
                estimatedArrival = "KSP clock unavailable";
            }
            else if (rule.Kind != "repeat" && !rule.LegacyOpaque && !rule.Enabled)
            {
                nextLaunch = "Paused";
                estimatedArrival = "Paused";
            }
            else if (rule.Kind != "repeat" && !rule.LegacyOpaque && _lastViewStatus == "paused")
            {
                nextLaunch = "Paused · when stock is low";
                estimatedArrival = "Paused · after dispatch";
            }
            else if (rule.Kind == "repeat" && !rule.LegacyOpaque && !rule.Enabled)
            {
                nextLaunch = "Paused";
                estimatedArrival = "Paused";
            }
            else if (rule.Kind == "repeat" && !rule.LegacyOpaque && rule.Enabled && !rule.WaitingRequest && rule.NextDueUt > (_lastSample?.UtSeconds ?? double.NegativeInfinity))
            {
                nextLaunch = FormatScheduledUt(rule.NextDueUt);
                estimatedArrival = route is null ? "Route details unavailable" : FormatScheduledUt(rule.NextDueUt + route.TravelDurationSeconds);
            }
            else if (rule.Kind == "repeat" && !rule.LegacyOpaque && rule.Enabled && !rule.WaitingRequest)
            {
                nextLaunch = _lastViewStatus == "paused" ? "Paused · due; checking after resume" : "Due · checking source";
                estimatedArrival = _lastViewStatus == "paused" ? "Paused · pending dispatch" : "Pending dispatch";
            }
            else if (rule.Kind == "repeat" && rule.WaitingRequest)
            {
                nextLaunch = _lastViewStatus == "paused" ? "Paused · launch pending" : "When source is ready";
                estimatedArrival = _lastViewStatus == "paused" ? "Paused · pending launch" : "Pending launch";
            }
            else if (rule.Kind != "repeat" && rule.Enabled)
            {
                nextLaunch = rule.Kind == "exportStock" ? $"When {FormatUnits(rule.BatchSizeMicroUnits / 1_000_000d)} Ore is available" : "When stock is low";
                estimatedArrival = "After dispatch";
            }
            orders.Add(new(rule.RuleId, IsStockRoute(rule.RuleId) ? StockBaseName(rule.RuleId) + " · " + ResourceLabel(rule.ResourceName) : rule.RuleId, detail, orderStatus, nextLaunch, estimatedArrival, rule.Enabled, rule.LegacyOpaque));
        }
        _automaticOrders.Clear();
        foreach (var order in orders) _automaticOrders.Add(order);
        AutomaticOrdersGrid.SelectedItem = _automaticOrders.FirstOrDefault(x => x.RuleId == selectedRuleId);
        AutomaticOrdersEmptyText.Text = orders.Count == 0 ? "No automatic orders saved." : $"{orders.Count} automatic {(orders.Count == 1 ? "order" : "orders")}";
        SetActivityVisibility(AutomaticOrdersGrid, AutomaticOrdersEmptyText, orders.Count);
        var desiredDeliveries = state.ActiveShipments.OrderBy(x => x.DueUt).ThenBy(x => x.ShipmentId, StringComparer.Ordinal).Select(shipment =>
            new DeliveryDisplay(shipment.ShipmentId, StockBaseName(shipment.RouteId), DepotName(shipment.DestinationDepotId),
                shipment.LegacyOpaque ? "Old delivery details unavailable" : CargoLabel(shipment.RemainingResources), shipment.DueUt,
                _transientShipmentHolds.TryGetValue(shipment.ShipmentId, out var transientReason) ? transientReason : shipment.HeldReason, shipment.LegacyOpaque)).ToArray();
        if (_deliveries.Count != desiredDeliveries.Length || _deliveries.Where((row, index) => !row.SameShipment(desiredDeliveries[index])).Any())
        {
            _deliveries.Clear();
            foreach (var delivery in desiredDeliveries) _deliveries.Add(delivery);
        }
        DeliveriesEmptyText.Text = _deliveries.Count == 0 ? "No outstanding deliveries." : $"{_deliveries.Count} outstanding {(_deliveries.Count == 1 ? "delivery" : "deliveries")}";
        SetActivityVisibility(DeliveriesGrid, DeliveriesEmptyText, _deliveries.Count);
        UpdateDeliveryCountdowns();
        var issues = state.Faults.OrderBy(x => x.DepotId, StringComparer.Ordinal).ThenBy(x => x.OperationKind, StringComparer.Ordinal)
            .Select(x => new IssueDisplay(DepotName(x.DepotId), x.OperationKind + " · " + x.Reason, "Blocking logistics orders"))
            .Concat(state.EconomicFaults.Select(x => new IssueDisplay("Kerbin recovery", x.ShipmentId + " · " + x.Result.Reason, "Uncertain funds outcome · blocking logistics orders"))).ToArray();
        _issues.Clear();
        foreach (var issue in issues) _issues.Add(issue);
        IssuesEmptyText.Text = issues.Length == 0 ? "No unresolved delivery issues." : $"{issues.Length} unresolved {(issues.Length == 1 ? "issue" : "issues")}";
        SetActivityVisibility(IssuesGrid, IssuesEmptyText, issues.Length);
        if (issues.Length > 0) LogisticsStatusText.Text = $"{issues.Length} unresolved issue(s) are blocking logistics orders.";
        var editingRule = state.DeliveryRules.FirstOrDefault(x => x.RuleId == _editingRuleId);
        var editingStockRoute = editingRule is { Kind: "keepStock" } ? state.RouteVersions.FirstOrDefault(x => x.RouteId == editingRule.RouteId && x.Version == editingRule.RouteVersion && !x.LegacyOpaque) : null;
        var routeOptions = state.RouteVersions.Where(x => !x.LegacyOpaque && (!IsStockRoute(x.RouteId) || x == editingStockRoute)).OrderBy(x => x.RouteId, StringComparer.Ordinal).ThenBy(x => x.Version)
            .Select(x => new RouteChoice($"{(x == editingStockRoute ? StockBaseName(x.RouteId) + " · " + ResourceLabel(editingRule!.ResourceName) : x.RouteId)} v{x.Version} · {DepotName(x.SourceDepotId)} → {DepotName(x.DestinationDepotId)} · {CargoLabel(x.Resources)}", x)).ToArray();
        var prior = RouteVersionBox.SelectedItem as RouteChoice;
        RouteVersionBox.ItemsSource = routeOptions;
        RouteVersionBox.SelectedItem = prior is null ? routeOptions.LastOrDefault() : routeOptions.FirstOrDefault(x => x.Route.RouteId == prior.Route.RouteId && x.Route.Version == prior.Route.Version) ?? routeOptions.LastOrDefault();
        RefreshRuleResources();
        UpdateDeliveryReadiness();
        UpdateOrderActions();
        SetWritable(_lastViewStatus is "live" or "paused");
    }

    private static bool IsStockRoute(string id) => id.Contains("::stock::", StringComparison.Ordinal);
    private static void SetActivityVisibility(System.Windows.Controls.DataGrid grid, System.Windows.Controls.TextBlock emptyText, int count)
    {
        grid.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;
        emptyText.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private static string StockBaseName(string id) { int at = id.IndexOf("::stock::", StringComparison.Ordinal); return at < 0 ? id : id[..at]; }
    private string StockRuleState(DeliveryRuleRecord rule)
    {
        if (_lastViewStatus is not ("live" or "paused") || _lastSample?.ActiveWorld != true) return "KSP clock unavailable · stock state may be outdated";
        var route = _acceptedState?.RouteVersions.FirstOrDefault(x => !x.LegacyOpaque && x.RouteId == rule.RouteId && x.Version == rule.RouteVersion);
        if (route is null) return "Waiting for saved route";
        if (rule.Kind == "exportStock")
        {
            var source = _lastDepots?.FirstOrDefault(x => x.DepotId == route.SourceDepotId);
            var ore = source?.Resources?.FirstOrDefault(x => x.Name == "Ore");
            return ore is null ? "Waiting for source Ore observation" : $"Latest observed source Ore · {FormatUnits(ore.Amount)}";
        }
        var destination = _lastDepots?.FirstOrDefault(x => string.Equals(x.DepotId, route.DestinationDepotId, StringComparison.OrdinalIgnoreCase));
        var observationIsCurrent = _lastDepotView?.DepotId?.ToString("N").Equals(route.DestinationDepotId.Replace("-", ""), StringComparison.OrdinalIgnoreCase) == true
            && _lastDepotView.Status == "live";
        var resources = observationIsCurrent ? _lastDepotView!.Resources : destination?.Resources;
        if (resources is null || (!observationIsCurrent && destination?.Status is not ("live" or "lastObserved"))) return "Waiting for destination stock";
        var stock = resources.FirstOrDefault(x => string.Equals(x.Name, rule.ResourceName, StringComparison.OrdinalIgnoreCase));
        if (stock is null) return "Waiting for destination stock";
        double inbound = (_acceptedState?.ActiveShipments ?? Array.Empty<ActiveShipmentRecord>())
            .Where(x => !x.LegacyOpaque && string.Equals(x.DestinationDepotId, route.DestinationDepotId, StringComparison.OrdinalIgnoreCase))
            .SelectMany(x => x.RemainingResources ?? Array.Empty<ResourceAmount>())
            .Where(x => string.Equals(x.ResourceName, rule.ResourceName, StringComparison.OrdinalIgnoreCase))
            .Sum(x => x.AmountMicroUnits / 1000000d);
        double effective = stock.Amount + inbound;
        return effective < rule.LowTriggerMicroUnits / 1000000d
            ? inbound > 0 ? "Latest observed below trigger · inbound cargo counted" : "Latest observed below trigger"
            : inbound > 0 ? $"Latest observed + inbound · {FormatUnits(effective)} projected" : $"Latest observed · {FormatUnits(stock.Amount)}";
    }
    private string FormatScheduledUt(double ut)
    {
        if (!double.IsFinite(ut)) return "Unavailable";
        var status = _lastViewStatus;
        var sample = _lastSample;
        if (status is not ("live" or "paused") || sample?.ActiveWorld != true || sample.UtSeconds is not double now) return "Waiting for KSP clock";
        double remaining = ut - now;
        string countdown = remaining <= 0 ? "Due now" : "in " + FormatCountdown(remaining);
        if (status == "paused") countdown = "Paused · " + countdown;
        return countdown + " · " + FormatArrivalDate(sample.FormattedDate, remaining);
    }
    private void RouteVersion_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (RuleResourcesList is not null)
        {
            _deliveryReadiness = null; RefreshRuleResources(); UpdateDeliveryReadiness();
            bool export = RouteVersionBox.SelectedItem is RouteChoice choice && choice.Route.DestinationKind == OreExportPolicy.VirtualDestinationKind;
            if (_editingRuleId is null) { RuleKindBox.IsEnabled = !export; if (export) RuleKindBox.SelectedIndex = 2; else if (RuleKindBox.SelectedIndex == 2) RuleKindBox.SelectedIndex = 0; }
            RuleKind_SelectionChanged(sender, e);
            SetWritable(_lastViewStatus is "live" or "paused");
        }
    }
    private async Task RefreshDeliveryReadinessAsync()
    {
        if (_acceptedState is null || _worldId is null || _runId is null || RouteVersionBox.SelectedItem is not RouteChoice choice)
        { _deliveryReadiness = null; UpdateDeliveryReadiness(); return; }
        string world = _worldId, run = _runId, routeId = choice.Route.RouteId;
        long version = choice.Route.Version;
        try
        {
            var result = await _commands.GetDeliveryReadinessAsync(world, run, routeId, version, TimeSpan.FromSeconds(2));
            if (_worldId == world && _runId == run && RouteVersionBox.SelectedItem is RouteChoice current &&
                current.Route.RouteId == routeId && current.Route.Version == version)
                _deliveryReadiness = result;
        }
        catch (Exception ex)
        {
            if (_worldId == world && _runId == run && RouteVersionBox.SelectedItem is RouteChoice current &&
                current.Route.RouteId == routeId && current.Route.Version == version)
                _deliveryReadiness = new DeliveryReadinessResult { Status = "held", WorldId = world, RunId = run,
                    RouteId = routeId, RouteVersion = version, Reason = "Host delivery readiness is unavailable: " + ex.Message };
        }
        UpdateDeliveryReadiness();
    }
    private bool CurrentDeliveryReady()
    {
        if (_deliveryReadiness?.Status != "ready" || RouteVersionBox.SelectedItem is not RouteChoice choice || _acceptedState is null) return false;
        return _deliveryReadiness.WorldId == _worldId && _deliveryReadiness.RunId == _runId &&
            _deliveryReadiness.RouteId == choice.Route.RouteId && _deliveryReadiness.RouteVersion == choice.Route.Version &&
            _deliveryReadiness.AcceptedSequence == _acceptedState.AcceptedSequence;
    }
    private void UpdateDeliveryReadiness()
    {
        if (DeliveryReadinessText is null) return;
        if (RouteVersionBox.SelectedItem is not RouteChoice choice)
        { DeliveryReadinessText.Text = "Choose a saved route to check delivery readiness."; return; }
        var source = _lastDepots?.FirstOrDefault(x => string.Equals(x.DepotId, choice.Route.SourceDepotId, StringComparison.OrdinalIgnoreCase));
        var shortages = new List<string>();
        if (source?.Resources is not null && source.Status is "live" or "lastObserved")
            foreach (var cargo in choice.Route.Resources)
            {
                var stock = source.Resources.FirstOrDefault(x => string.Equals(x.Name, cargo.ResourceName, StringComparison.OrdinalIgnoreCase));
                double required = cargo.AmountMicroUnits / 1000000d;
                if (stock is null || stock.Amount + 0.000001 < required)
                    shortages.Add($"{ResourceLabel(cargo.ResourceName)}: {FormatUnits(stock?.Amount ?? 0)} available / {FormatUnits(required)} needed");
            }
        string stockNote = shortages.Count > 0 ? "Source stock: short — " + string.Join("; ", shortages) + "."
            : source?.Resources is null || source.Status is not ("live" or "lastObserved") ? "Source stock: unavailable for this route."
            : "Source stock: enough for this route based on the latest observation.";
        string inTransitNote = _acceptedState is null
            ? " Outstanding delivery count unknown."
            : $" Outstanding deliveries on this route version: {_acceptedState.ActiveShipments.Count(x => !x.LegacyOpaque && string.Equals(x.RouteId, choice.Route.RouteId, StringComparison.Ordinal) && x.RouteVersion == choice.Route.Version)}.";
        string readinessText = CurrentDeliveryReady()
            ? choice.Route.DestinationKind == OreExportPolicy.VirtualDestinationKind
                ? $"Ready to export {CargoLabel(choice.Route.Resources)} for modeled recovery and {FormatUnits(OreExportPolicy.RecoveryFunds(choice.Route.Resources, choice.Route.FundsPerUnit))} funds after travel."
                : "Ready to send: fresh selected source stock and destination capacity cover this saved route."
            : (_deliveryReadiness is not null && _deliveryReadiness.WorldId == _worldId && _deliveryReadiness.RunId == _runId &&
                _deliveryReadiness.RouteId == choice.Route.RouteId && _deliveryReadiness.RouteVersion == choice.Route.Version
                    ? "Delivery held: " + (_deliveryReadiness.Reason.Length == 0 ? "Waiting for current game capability evidence." : _deliveryReadiness.Reason) + " "
                    : "Checking delivery readiness. ") + stockNote;
        DeliveryReadinessText.Text = readinessText + inTransitNote;
    }
    private void RuleKind_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (RepeatFields is null || KeepStockFields is null) return;
        bool stock = (RuleKindBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString() == "keepStock";
        bool export = RouteVersionBox.SelectedItem is RouteChoice choice && choice.Route.DestinationKind == OreExportPolicy.VirtualDestinationKind;
        RepeatFields.Visibility = stock || export ? Visibility.Collapsed : Visibility.Visible;
        KeepStockFields.Visibility = stock && !export ? Visibility.Visible : Visibility.Collapsed;
    }
    private void RefreshRuleResources()
    {
        if (RouteVersionBox.SelectedItem is not RouteChoice choice) { _ruleResources.Clear(); _ruleResourceRouteKey = null; return; }
        string key = choice.Route.RouteId + "\0" + choice.Route.Version.ToString(CultureInfo.InvariantCulture);
        if (key == _ruleResourceRouteKey) return;
        _ruleResourceRouteKey = key;
        _ruleResources.Clear();
        var depot = _lastDepots?.FirstOrDefault(x => string.Equals(x.DepotId, choice.Route.DestinationDepotId, StringComparison.OrdinalIgnoreCase) &&
            x.MembershipRevision == choice.Route.DestinationMembershipRevision && x.MembershipHash == choice.Route.DestinationMembershipHash);
        foreach (var cargo in choice.Route.Resources)
        {
            var stock = depot?.Resources?.FirstOrDefault(x => x.Name == cargo.ResourceName);
            double capacity = stock?.MaxAmount ?? 0;
            _ruleResources.Add(new RuleResourceEntry(cargo.ResourceName, cargo.AmountMicroUnits, capacity,
                capacity > 0 ? Math.Floor(capacity * .25).ToString("0", CultureInfo.InvariantCulture) : string.Empty,
                capacity > 0 ? Math.Floor(capacity * .75).ToString("0", CultureInfo.InvariantCulture) : string.Empty));
        }
    }

    private void SetWritable(bool live)
    {
        var writable = live && _acceptedState is not null && !_acceptedState.WritesBlocked && !_commandInFlight && !_batchInProgress && !string.IsNullOrWhiteSpace(_worldId) && !string.IsNullOrWhiteSpace(_runId);
        SaveRouteButton.IsEnabled = writable && SourceDepotBox.SelectedItem is SourceChoice { Depot: not null } && DestinationDepotBox.SelectedItem is DepotSummaryView;
        SaveExportRouteButton.IsEnabled = writable && ExportSourceBox.SelectedItem is DepotSummaryView;
        var pendingSendContextMatches = _pendingSendOnce is null || (_pendingSendOnce.WorldId == _worldId && _pendingSendOnce.RunId == _runId);
        SendOnceButton.Content = _pendingSendOnce is null ? "Send once" : "Retry same request";
        SendOnceButton.IsEnabled = writable && pendingSendContextMatches && (_pendingSendOnce is not null || CurrentDeliveryReady());
        SaveRuleButton.IsEnabled = writable && RouteVersionBox.SelectedItem is RouteChoice selectedRoute &&
            (_editingRuleId is not null || !IsStockRoute(selectedRoute.Route.RouteId));
        if (RouteVersionBox.SelectedItem is RouteChoice exportChoice && exportChoice.Route.DestinationKind == OreExportPolicy.VirtualDestinationKind)
        {
            SaveRuleButton.Content = _editingRuleId is null ? "Enable automatic Ore exports" : "Save automatic order";
            var enabled = _acceptedState?.DeliveryRules.Any(r => r.RouteId == exportChoice.Route.RouteId && r.RouteVersion == exportChoice.Route.Version && r.Kind == "exportStock" && r.Enabled) == true;
            ExportOrderStatusText.Text = exportChoice.Route.FundsPerUnit != OreExportPolicy.DefaultFundsPerUnit
                ? "Historical 500/unit route: departed cargo keeps its terms. Create a new 100/unit route for further exports."
                : enabled ? "Automatic export order enabled for the selected route." : "Selected route configured; automatic exports inactive until its order is enabled.";
        }
        else { SaveRuleButton.Content = "Save automatic order"; ExportOrderStatusText.Text = "Select an export route to view its automatic-order status."; }
        UpdateOrderActions();
    }

    private async void SaveRoute_Click(object sender, RoutedEventArgs e)
    {
        if (_acceptedState is null || SourceDepotBox.SelectedItem is not SourceChoice { Depot: not null } sourceChoice || DestinationDepotBox.SelectedItem is not DepotSummaryView destination) return;
        var source = sourceChoice.Depot;
        if (!TryTravelDuration(DurationBox.Text, out var duration) || !TryBuildRouteManifest(out var resources) || string.IsNullOrWhiteSpace(RouteIdBox.Text) || string.IsNullOrWhiteSpace(ProvenanceBox.Text))
        { CommandStatusText.Text = "Enter a route name, travel time as days:hours:minutes, and a positive amount for each selected resource."; return; }
        var version = _acceptedState.RouteVersions.Where(x => x.RouteId == RouteIdBox.Text.Trim() && !x.LegacyOpaque).Select(x => x.Version).DefaultIfEmpty(0).Max() + 1;
        var route = new RouteVersionRecord { RouteId = RouteIdBox.Text.Trim(), Version = version, SourceDepotId = source.DepotId, SourceMembershipRevision = source.MembershipRevision, SourceMembershipHash = source.MembershipHash, DestinationDepotId = destination.DepotId, DestinationMembershipRevision = destination.MembershipRevision, DestinationMembershipHash = destination.MembershipHash, TravelDurationSeconds = duration, Provenance = ProvenanceBox.Text.Trim(), Resources = resources };
        await SubmitAsync(new SubmitCommand { ProtocolVersion = 1, MessageType = "submitCommand", ClientRequestId = NewRequestId("route"), WorldId = _worldId!, RunId = _runId!, CommandKind = "routeUpsert", Delivery = new DeliveryCommandPayload { Kind = "routeUpsert", RouteVersion = route } });
    }

    private async void SendOnce_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingSendOnce is not null)
        {
            if (_pendingSendOnce.WorldId != _worldId || _pendingSendOnce.RunId != _runId)
            {
                CommandStatusText.Text = "A send request from another save is unresolved. It will not be retried in this save.";
                return;
            }
            await SubmitAsync(_pendingSendOnce);
            return;
        }
        if (!CurrentDeliveryReady()) { CommandStatusText.Text = _deliveryReadiness?.Reason ?? "Delivery capability is not ready for this saved route."; return; }
        if (RouteVersionBox.SelectedItem is not RouteChoice choice || _worldId is null || _runId is null) return;
        try
        {
            var fresh = await _commands.GetDeliveryReadinessAsync(_worldId, _runId, choice.Route.RouteId, choice.Route.Version, TimeSpan.FromSeconds(2));
            _deliveryReadiness = fresh;
            UpdateDeliveryReadiness(); SetWritable(_lastViewStatus is "live" or "paused");
            if (!CurrentDeliveryReady()) { CommandStatusText.Text = fresh.Reason; return; }
        }
        catch (Exception ex) { CommandStatusText.Text = "Delivery readiness changed: " + ex.Message; return; }
        _pendingSendOnce = new SubmitCommand { ProtocolVersion = 1, MessageType = "submitCommand", ClientRequestId = NewRequestId("send"), WorldId = _worldId, RunId = _runId, CommandKind = "sendOnce", Delivery = new DeliveryCommandPayload { Kind = "sendOnce", RouteId = choice.Route.RouteId, RouteVersionNumber = choice.Route.Version } };
        await SubmitAsync(_pendingSendOnce);
    }

    private async void SaveRule_Click(object sender, RoutedEventArgs e)
    {
        if (RouteVersionBox.SelectedItem is not RouteChoice choice || _worldId is null || _runId is null) return;
        if (_editingRuleId is not null) { await SaveEditedRuleAsync(choice); return; }
        var kind = (RuleKindBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString();
        var now = _lastSample?.UtSeconds ?? 0;
        string name = RuleIdBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name)) { CommandStatusText.Text = "Enter a name for this automatic order."; return; }
        if (choice.Route.DestinationKind == OreExportPolicy.VirtualDestinationKind)
        {
            if (choice.Route.FundsPerUnit != OreExportPolicy.DefaultFundsPerUnit) { CommandStatusText.Text = "Create a new export route at 100 funds per unit. Historical terms are retained for departed cargo only."; return; }
            var export = new DeliveryRuleRecord { RuleId = name, Revision = NextRuleRevision(name), Kind = "exportStock", RouteId = choice.Route.RouteId, RouteVersion = choice.Route.Version,
                Enabled = true, ResourceName = "Ore", BatchSizeMicroUnits = choice.Route.Resources.Single().AmountMicroUnits, TargetMicroUnits = 0 };
            await SubmitRuleAsync(export, "export-rule");
            return;
        }
        if (kind == "exportStock") { CommandStatusText.Text = "Select an Ore export route for the source-stock export trigger."; return; }
        if (kind != "keepStock")
        {
            if (!TryTravelDuration(IntervalBox.Text, out var interval)) { CommandStatusText.Text = "Enter the repeat interval as days:hours:minutes (KSP uses six-hour days)."; return; }
            var repeat = new DeliveryRuleRecord { RuleId = name, Revision = NextRuleRevision(name), Kind = "repeat", RouteId = choice.Route.RouteId, RouteVersion = choice.Route.Version, Enabled = true, IntervalSeconds = interval, NextDueUt = now + interval };
            await SubmitAsync(new SubmitCommand { ProtocolVersion = 1, MessageType = "submitCommand", ClientRequestId = NewRequestId("rule"), WorldId = _worldId, RunId = _runId, CommandKind = "ruleUpsert", Delivery = new DeliveryCommandPayload { Kind = "ruleUpsert", Rule = repeat } });
            return;
        }
        var selected = _ruleResources.Where(x => x.Selected).ToArray();
        if (selected.Length == 0 || selected.Length > 8) { CommandStatusText.Text = "Choose one to eight resources to keep stocked."; return; }
        var plan = new List<(RuleResourceEntry Entry, long Low, long Target)>();
        foreach (var entry in selected)
        {
            long low, target;
            if (entry.KeepFull)
            {
                low = target = entry.CapacityMicroUnits;
                if (target <= 0) { CommandStatusText.Text = $"Visit the destination to learn {entry.DisplayName} capacity before using Keep full."; return; }
            }
            else if (!TryResourceUnits(entry.LowText, out low) || !TryResourceUnits(entry.TargetText, out target) || target <= low)
            { CommandStatusText.Text = $"Set a target above the low point for {entry.DisplayName}."; return; }
            plan.Add((entry, low, target));
        }
        if (plan.Any(x => (choice.Route.RouteId + "::stock::" + x.Entry.Name).Length > 128 || (name + "::stock::" + x.Entry.Name).Length > 128))
        { CommandStatusText.Text = "The route or schedule name is too long for separate resource orders."; return; }
        int neededRoutes = plan.Count(x => FindMatchingStockRoute(choice.Route, x.Entry) is null);
        int newRules = plan.Count(x => !_acceptedState!.DeliveryRules.Any(r => r.RuleId == name + "::stock::" + x.Entry.Name));
        if (_acceptedState!.RouteVersions.Length + neededRoutes > AcceptedStateV2Limits.MaxRouteVersions || _acceptedState.DeliveryRules.Length + newRules > AcceptedStateV2Limits.MaxRules)
        { CommandStatusText.Text = "This save has reached the current route or automatic-order limit."; return; }
        _batchInProgress = true; SetWritable(_lastViewStatus is "live" or "paused");
        try
        {
            foreach (var item in plan)
            {
                var stockRoute = FindMatchingStockRoute(choice.Route, item.Entry);
                if (stockRoute is null)
                {
                    string stockRouteId = choice.Route.RouteId + "::stock::" + item.Entry.Name;
                    stockRoute = new RouteVersionRecord
                    {
                        RouteId = stockRouteId,
                        Version = _acceptedState!.RouteVersions.Where(x => x.RouteId == stockRouteId).Select(x => x.Version).DefaultIfEmpty(0).Max() + 1,
                        SourceDepotId = choice.Route.SourceDepotId, SourceMembershipRevision = choice.Route.SourceMembershipRevision, SourceMembershipHash = choice.Route.SourceMembershipHash,
                        DestinationDepotId = choice.Route.DestinationDepotId, DestinationMembershipRevision = choice.Route.DestinationMembershipRevision, DestinationMembershipHash = choice.Route.DestinationMembershipHash,
                        TravelDurationSeconds = choice.Route.TravelDurationSeconds, Provenance = choice.Route.Provenance,
                        Resources = new[] { new ResourceAmount { ResourceName = item.Entry.Name, AmountMicroUnits = item.Entry.BatchMicroUnits } }
                    };
                    bool savedRoute = await SubmitAsync(new SubmitCommand { ProtocolVersion = 1, MessageType = "submitCommand", ClientRequestId = NewRequestId("stock-route"), WorldId = _worldId, RunId = _runId, CommandKind = "routeUpsert", Delivery = new DeliveryCommandPayload { Kind = "routeUpsert", RouteVersion = stockRoute } });
                    if (!savedRoute) { CommandStatusText.Text = $"Stopped while saving the {item.Entry.DisplayName} route. Earlier resource orders may already be saved; check Activity before retrying."; return; }
                }
                string ruleId = name + "::stock::" + item.Entry.Name;
                var rule = new DeliveryRuleRecord { RuleId = ruleId, Revision = NextRuleRevision(ruleId), Kind = "keepStock", RouteId = stockRoute.RouteId, RouteVersion = stockRoute.Version,
                    Enabled = true, ResourceName = item.Entry.Name, LowTriggerMicroUnits = item.Low, TargetMicroUnits = item.Target, BatchSizeMicroUnits = item.Entry.BatchMicroUnits };
                bool savedRule = await SubmitAsync(new SubmitCommand { ProtocolVersion = 1, MessageType = "submitCommand", ClientRequestId = NewRequestId("stock-rule"), WorldId = _worldId, RunId = _runId, CommandKind = "ruleUpsert", Delivery = new DeliveryCommandPayload { Kind = "ruleUpsert", Rule = rule } });
                if (!savedRule) { CommandStatusText.Text = $"Stopped while saving the {item.Entry.DisplayName} stock rule. Earlier resource orders may already be saved; check Activity before retrying."; return; }
            }
            CommandStatusText.Text = $"Saved {plan.Count} separate stock orders. Each sends only its own resource when low.";
        }
        finally { _batchInProgress = false; SetWritable(_lastViewStatus is "live" or "paused"); }
    }

    private async Task SaveEditedRuleAsync(RouteChoice choice)
    {
        var prior = _acceptedState?.DeliveryRules.FirstOrDefault(x => x.RuleId == _editingRuleId);
        if (prior is null || prior.Revision != _editingRuleRevision || prior.LegacyOpaque)
        { CommandStatusText.Text = "This automatic order changed in the game. Select Edit again to reload it before saving."; return; }
        if (prior.RouteId != choice.Route.RouteId || (prior.Kind != "exportStock" && prior.RouteVersion != choice.Route.Version) ||
            ((RuleKindBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString() != prior.Kind))
        { CommandStatusText.Text = "The order route or type changed. Select Edit again to reload it."; return; }
        DeliveryRuleRecord updated;
        if (prior.Kind == "repeat")
        {
            if (!TryTravelDuration(IntervalBox.Text, out var interval))
            { CommandStatusText.Text = "Enter the repeat interval as days:hours:minutes (KSP uses six-hour days)."; return; }
            bool sameInterval = interval == prior.IntervalSeconds;
            updated = CopyRule(prior, prior.Enabled);
            updated.IntervalSeconds = interval;
            if (!sameInterval)
            {
                updated.NextDueUt = (_lastSample?.UtSeconds ?? 0) + interval;
                updated.WaitingRequest = false;
                updated.WaitingScheduledUt = 0;
                updated.WaitingCoalescedSlots = 0;
            }
        }
        else if (prior.Kind == "keepStock")
        {
            var entry = _ruleResources.FirstOrDefault(x => x.Name == prior.ResourceName);
            if (entry is null || !entry.Selected || _ruleResources.Count(x => x.Selected) != 1)
            { CommandStatusText.Text = "Edit the selected resource only. Select Edit again to reload this order."; return; }
            long low, target;
            if (entry.KeepFull) low = target = entry.CapacityMicroUnits;
            else if (!TryResourceUnits(entry.LowText, out low) || !TryResourceUnits(entry.TargetText, out target))
            { CommandStatusText.Text = "Enter valid stock amounts for this resource."; return; }
            if (target <= 0 || (target <= low && !entry.KeepFull && !(low == target && prior.LowTriggerMicroUnits == prior.TargetMicroUnits && low == prior.LowTriggerMicroUnits)))
            { CommandStatusText.Text = "Set a target above the low point, or use Keep full with known tank capacity."; return; }
            updated = CopyRule(prior, prior.Enabled);
            updated.LowTriggerMicroUnits = low;
            updated.TargetMicroUnits = target;
        }
        else if (prior.Kind == "exportStock")
        {
            try { updated = BuildExportRuleUpdate(prior, choice.Route); }
            catch (ArgumentException)
            { CommandStatusText.Text = "Select the current version of this Ore route with 100 funds per unit before saving the order."; return; }
        }
        else { CommandStatusText.Text = "This order type cannot be edited."; return; }
        if (await SubmitRuleAsync(updated, "edit-rule"))
        {
            ClearRuleEdit();
            CommandStatusText.Text = "Automatic order changes saved.";
        }
    }

    private static DeliveryRuleRecord BuildExportRuleUpdate(DeliveryRuleRecord prior, RouteVersionRecord route)
    {
        if (prior.Kind != "exportStock" || prior.RouteId != route.RouteId || route.Version < prior.RouteVersion ||
            route.DestinationKind != OreExportPolicy.VirtualDestinationKind || route.FundsPerUnit != OreExportPolicy.DefaultFundsPerUnit ||
            route.Resources.Length != 1 || route.Resources[0].ResourceName != "Ore" || !OreExportPolicy.IsValidBatch(route.Resources[0].AmountMicroUnits))
            throw new ArgumentException("Invalid export order route.");
        var updated = CopyRule(prior, prior.Enabled);
        updated.RouteVersion = route.Version;
        updated.BatchSizeMicroUnits = route.Resources[0].AmountMicroUnits;
        if (updated.RouteVersion != prior.RouteVersion || updated.BatchSizeMicroUnits != prior.BatchSizeMicroUnits)
        {
            updated.NextDueUt = 0;
            updated.IntervalSeconds = 0;
            updated.WaitingRequest = false;
            updated.WaitingScheduledUt = 0;
            updated.WaitingCoalescedSlots = 0;
        }
        return updated;
    }

    private static DeliveryRuleRecord CopyRule(DeliveryRuleRecord prior, bool enabled) => new()
    {
        RuleId = prior.RuleId, Revision = prior.Revision + 1, Kind = prior.Kind, RouteId = prior.RouteId, RouteVersion = prior.RouteVersion,
        Enabled = enabled, NextDueUt = prior.NextDueUt, IntervalSeconds = prior.IntervalSeconds, WaitingRequest = prior.WaitingRequest,
        WaitingScheduledUt = prior.WaitingScheduledUt, WaitingCoalescedSlots = prior.WaitingCoalescedSlots, ResourceName = prior.ResourceName,
        LowTriggerMicroUnits = prior.LowTriggerMicroUnits, TargetMicroUnits = prior.TargetMicroUnits, BatchSizeMicroUnits = prior.BatchSizeMicroUnits
    };

    private Task<bool> SubmitRuleAsync(DeliveryRuleRecord rule, string requestPrefix) =>
        SubmitAsync(BuildRuleUpsertCommand(rule, _worldId!, _runId!, NewRequestId(requestPrefix)));

    private static SubmitCommand BuildRuleUpsertCommand(DeliveryRuleRecord rule, string worldId, string runId, string requestId) => new()
    {
        ProtocolVersion = 1, MessageType = "submitCommand", ClientRequestId = requestId, WorldId = worldId, RunId = runId,
        CommandKind = "ruleUpsert", Delivery = new DeliveryCommandPayload { Kind = "ruleUpsert", Rule = rule }
    };

    private static SubmitCommand BuildRuleCancelCommand(string ruleId, string worldId, string runId, string requestId) => new()
    {
        ProtocolVersion = 1, MessageType = "submitCommand", ClientRequestId = requestId, WorldId = worldId, RunId = runId,
        CommandKind = "ruleCancel", Delivery = new DeliveryCommandPayload { Kind = "ruleCancel", RuleId = ruleId }
    };

    private long NextRuleRevision(string id) => (_acceptedState?.DeliveryRules.Where(x => x.RuleId == id).Select(x => x.Revision).DefaultIfEmpty(0).Max() ?? 0) + 1;
    private RouteVersionRecord? FindMatchingStockRoute(RouteVersionRecord source, RuleResourceEntry entry)
    {
        if (source.Resources.Length == 1 && source.Resources[0].ResourceName == entry.Name && source.Resources[0].AmountMicroUnits == entry.BatchMicroUnits) return source;
        string id = source.RouteId + "::stock::" + entry.Name;
        return _acceptedState?.RouteVersions.Where(x => !x.LegacyOpaque && x.RouteId == id && x.SourceDepotId == source.SourceDepotId && x.SourceMembershipRevision == source.SourceMembershipRevision && x.SourceMembershipHash == source.SourceMembershipHash &&
            x.DestinationDepotId == source.DestinationDepotId && x.DestinationMembershipRevision == source.DestinationMembershipRevision && x.DestinationMembershipHash == source.DestinationMembershipHash &&
            x.TravelDurationSeconds == source.TravelDurationSeconds && x.Resources.Length == 1 && x.Resources[0].ResourceName == entry.Name && x.Resources[0].AmountMicroUnits == entry.BatchMicroUnits).OrderByDescending(x => x.Version).FirstOrDefault();
    }

    private AutomaticOrderDisplay? OrderForAction(object sender) =>
        (sender as FrameworkElement)?.DataContext as AutomaticOrderDisplay ?? AutomaticOrdersGrid.SelectedItem as AutomaticOrderDisplay;

    private DeliveryRuleRecord? CurrentRule(AutomaticOrderDisplay? row) => row is null ? null :
        _acceptedState?.DeliveryRules.FirstOrDefault(x => x.RuleId == row.RuleId && !x.LegacyOpaque);

    private void AutomaticOrders_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (SelectedOrderText is not null) UpdateOrderActions();
    }

    private void AutomaticOrders_PreviewMouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        DependencyObject? target = e.OriginalSource as DependencyObject;
        while (target is not null && target is not System.Windows.Controls.DataGridRow)
            target = VisualTreeHelper.GetParent(target);
        if (target is System.Windows.Controls.DataGridRow row)
        {
            AutomaticOrdersGrid.SelectedItem = row.Item;
            AutomaticOrdersGrid.ContextMenu.IsEnabled = CurrentRule(row.Item as AutomaticOrderDisplay) is not null && CanManageOrders();
            if (AutomaticOrdersGrid.ContextMenu.Items[1] is System.Windows.Controls.MenuItem toggle && row.Item is AutomaticOrderDisplay order)
                toggle.Header = order.ToggleLabel;
        }
        else AutomaticOrdersGrid.ContextMenu.IsEnabled = false;
    }

    private void UpdateOrderActions()
    {
        if (SelectedOrderText is null) return;
        var row = AutomaticOrdersGrid.SelectedItem as AutomaticOrderDisplay;
        var rule = CurrentRule(row);
        bool writable = _lastViewStatus is "live" or "paused" && _acceptedState is { WritesBlocked: false } &&
            !_commandInFlight && !_batchInProgress && _worldId is not null && _runId is not null;
        SelectedOrderText.Text = row is null ? "Select an automatic order to manage it." :
            rule is null ? "This order's details are unavailable." : $"Selected: {row.Identity} · {row.Status}";
        EditSelectedOrderButton.IsEnabled = writable && rule is not null;
        ToggleSelectedOrderButton.IsEnabled = writable && rule is not null;
        ToggleSelectedOrderButton.Content = rule?.Enabled == false ? "Resume selected" : "Pause selected";
        CancelSelectedOrderButton.IsEnabled = writable && rule is not null;
        AutomaticOrdersGrid.ContextMenu.IsEnabled = writable && rule is not null;
        if (AutomaticOrdersGrid.ContextMenu.Items[1] is System.Windows.Controls.MenuItem toggle)
            toggle.Header = rule?.Enabled == false ? "Resume" : "Pause";
    }

    private void EditOrder_Click(object sender, RoutedEventArgs e)
    {
        var row = OrderForAction(sender);
        var rule = CurrentRule(row);
        if (rule is null) { CommandStatusText.Text = "Select an available automatic order to edit."; return; }
        var route = _acceptedState?.RouteVersions.FirstOrDefault(x => !x.LegacyOpaque && x.RouteId == rule.RouteId && x.Version == rule.RouteVersion);
        if (route is null) { CommandStatusText.Text = "This order's saved route is unavailable for editing."; return; }
        AutomaticOrdersGrid.SelectedItem = _automaticOrders.FirstOrDefault(x => x.RuleId == rule.RuleId);
        _editingRuleId = rule.RuleId;
        _editingRuleRevision = rule.Revision;
        RenderOperations();
        var choice = (RouteVersionBox.ItemsSource as IEnumerable<RouteChoice>)?.FirstOrDefault(x => x.Route.RouteId == route.RouteId && x.Route.Version == route.Version);
        if (choice is null) { ClearRuleEdit(); CommandStatusText.Text = "This order's saved route is unavailable for editing."; return; }
        RouteVersionBox.SelectedItem = choice;
        RuleKindBox.SelectedIndex = rule.Kind == "exportStock" ? 2 : rule.Kind == "keepStock" ? 1 : 0;
        RuleIdBox.Text = rule.Kind == "keepStock" ? StockBaseName(rule.RuleId) : rule.RuleId;
        if (rule.Kind == "repeat") IntervalBox.Text = FormatTravelDuration(rule.IntervalSeconds);
        else
        {
            foreach (var item in _ruleResources) item.Selected = item.Name == rule.ResourceName;
            var entry = _ruleResources.FirstOrDefault(x => x.Name == rule.ResourceName);
            if (entry is not null)
            {
                entry.KeepFull = false;
                entry.LowText = (rule.LowTriggerMicroUnits / 1000000m).ToString("0.######", CultureInfo.InvariantCulture);
                entry.TargetText = (rule.TargetMicroUnits / 1000000m).ToString("0.######", CultureInfo.InvariantCulture);
                if (rule.LowTriggerMicroUnits == rule.TargetMicroUnits && rule.TargetMicroUnits == entry.CapacityMicroUnits) entry.KeepFull = true;
            }
        }
        RuleIdBox.IsEnabled = false;
        RuleKindBox.IsEnabled = false;
        RouteVersionBox.IsEnabled = false;
        EditingRuleText.Text = $"Editing {row?.Identity}. Changes keep this order's identity and {(rule.Enabled ? "active" : "paused")} state. The route and cargo amount are fixed.";
        EditingRuleText.Visibility = Visibility.Visible;
        ClearRuleEditButton.Visibility = Visibility.Visible;
        SaveRuleButton.Content = "Save order changes";
        NavTabs.SelectedItem = DeliverySetupTab;
        SetWritable(_lastViewStatus is "live" or "paused");
    }

    private void ClearRuleEdit_Click(object sender, RoutedEventArgs e) => ClearRuleEdit();

    private void ClearRuleEdit(bool rerender = true)
    {
        _editingRuleId = null;
        _editingRuleRevision = 0;
        RuleIdBox.IsEnabled = true;
        RuleKindBox.IsEnabled = true;
        RouteVersionBox.IsEnabled = true;
        EditingRuleText.Visibility = Visibility.Collapsed;
        ClearRuleEditButton.Visibility = Visibility.Collapsed;
        SaveRuleButton.Content = "Save automatic order";
        if (rerender) RenderOperations();
        SetWritable(_lastViewStatus is "live" or "paused");
    }

    private async void ToggleOrder_Click(object sender, RoutedEventArgs e)
    {
        var row = OrderForAction(sender);
        var rule = CurrentRule(row);
        if (rule is null || !CanManageOrders()) return;
        AutomaticOrdersGrid.SelectedItem = _automaticOrders.FirstOrDefault(x => x.RuleId == rule.RuleId);
        bool resume = !rule.Enabled;
        if (await SubmitRuleAsync(CopyRule(rule, resume), resume ? "resume-rule" : "pause-rule"))
            CommandStatusText.Text = $"{row!.Identity} {(resume ? "resumed" : "paused")}.";
    }

    private async void CancelOrder_Click(object sender, RoutedEventArgs e)
    {
        var row = OrderForAction(sender);
        var rule = CurrentRule(row);
        if (rule is null || !CanManageOrders()) return;
        AutomaticOrdersGrid.SelectedItem = _automaticOrders.FirstOrDefault(x => x.RuleId == rule.RuleId);
        var response = MessageBox.Show(this,
            $"Cancel {row!.Identity}? It will stop future orders from this schedule. Deliveries already in transit will continue.",
            "Cancel automatic order", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (response != MessageBoxResult.Yes) return;
        if (!CanManageOrders()) { CommandStatusText.Text = "The game is no longer ready for order changes."; return; }
        // A projection refresh can arrive while confirmation is open. Never cancel a different revision.
        if (_acceptedState?.DeliveryRules.FirstOrDefault(x => x.RuleId == rule.RuleId)?.Revision != rule.Revision)
        { CommandStatusText.Text = "This order changed in the game. Review it and try again."; return; }
        if (await SubmitAsync(BuildRuleCancelCommand(rule.RuleId, _worldId!, _runId!, NewRequestId("cancel-rule"))))
        {
            if (_editingRuleId == rule.RuleId) ClearRuleEdit();
            CommandStatusText.Text = $"{row.Identity} canceled. Existing deliveries remain in transit.";
        }
    }

    private bool CanManageOrders() => _lastViewStatus is "live" or "paused" && _acceptedState is { WritesBlocked: false } &&
        !_commandInFlight && !_batchInProgress && _worldId is not null && _runId is not null;

    private void RuleId_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (SaveRuleButton is null) return;
        SetWritable(_lastViewStatus is "live" or "paused");
    }

    private async Task<bool> SubmitAsync(SubmitCommand request)
    {
        if (_commandInFlight || _acceptedState?.WritesBlocked == true) return false;
        _commandInFlight = true; SetWritable(_lastViewStatus is "live" or "paused");
        _lastCommandId = request.ClientRequestId;
        _lastCommandStatusAt = 0;
        try
        {
            var result = await _commands.SubmitCommandAsync(request);
            string action = request.CommandKind switch { "routeUpsert" => "Route", "ruleUpsert" or "ruleCancel" => "Automatic order", "sendOnce" => "Delivery", _ => "Request" };
            CommandStatusText.Text = result.Status == "accepted" ? action + " saved in the game." : action + ": " + result.Status + (result.Reason is null ? string.Empty : " · " + result.Reason);
            if (result.Status == "accepted" && result.AcceptedCapsule is not null) _acceptedState = AcceptedStateCodec.ReadCapsule(result.AcceptedCapsule);
            if (ReferenceEquals(request, _pendingSendOnce) && SendOnceRequestLifecycle.IsTerminal(result)) _pendingSendOnce = null;
            return result.Status == "accepted" && result.AcceptedCapsule is not null;
        }
        catch (Exception ex) { CommandStatusText.Text = "Command response unavailable. Check Activity before retrying. " + ex.Message; return false; }
        finally { _commandInFlight = false; RenderOperations(); SetWritable(_lastViewStatus is "live" or "paused"); }
    }

    private async Task RefreshCommandStatusAsync()
    {
        if (_worldId is null || _runId is null || _lastCommandId is null) return;
        _lastCommandStatusAt = Environment.TickCount64;
        try
        {
            var result = await _commands.GetCommandStatusAsync(_lastCommandId, _worldId, _runId, TimeSpan.FromSeconds(2));
            CommandStatusText.Text = result.Status == "accepted" ? "Latest request saved in the game." : "Latest request: " + result.Status + (result.Reason is null ? string.Empty : " · " + result.Reason);
            if (result.AcceptedCapsule is not null) { _acceptedState = AcceptedStateCodec.ReadCapsule(result.AcceptedCapsule); RenderOperations(); SetWritable(_lastViewStatus is "live" or "paused"); }
            if (_pendingSendOnce?.ClientRequestId == _lastCommandId && SendOnceRequestLifecycle.IsTerminal(result)) _pendingSendOnce = null;
            if (result.Status == "rejected" && result.ConfirmedTerminal) _lastCommandId = null;
        }
        catch (Exception ex) { CommandStatusText.Text = "Status unavailable: " + ex.Message; }
    }

    private static string NewRequestId(string prefix) => prefix + ":" + Guid.NewGuid().ToString("N");
    private static bool TryDouble(string text, out double value) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    private static bool TryLong(string text, out long value) => long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    private static bool TryTravelDuration(string text, out double seconds)
    {
        seconds = 0;
        var fields = text.Trim().Split(':');
        if (fields.Length != 3 || !long.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var days)
            || !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var hours)
            || !int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
            || days < 0 || hours is < 0 or >= 6 || minutes is < 0 or >= 60) return false;
        seconds = days * 21600d + hours * 3600d + minutes * 60d;
        return seconds > 0 && double.IsFinite(seconds) && seconds <= 1e15;
    }

    private static string FormatTravelDuration(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) return "—";
        var wholeMinutes = (long)Math.Round(seconds / 60d, MidpointRounding.AwayFromZero);
        var days = wholeMinutes / 360;
        var hours = (wholeMinutes % 360) / 60;
        var minutes = wholeMinutes % 60;
        return $"{days}:{hours:00}:{minutes:00}";
    }
    private string DepotName(string id) => id == OreExportPolicy.KerbinBuyerId ? "Kerbin · modeled recovery" : _lastDepots?.FirstOrDefault(x => string.Equals(x.DepotId, id, StringComparison.OrdinalIgnoreCase))?.Label
        ?? (_lastDepotView?.DepotId?.ToString("D").Equals(id, StringComparison.OrdinalIgnoreCase) == true ? _lastDepotView.Label ?? "Depot" : "Former depot");
    private static string ResourceLabel(string name) => name switch { "ElectricCharge" => "Stored energy", "EnrichedUranium" => "Nuclear fuel", "DepletedFuel" => "Spent fuel", "LiquidFuel" => "Liquid fuel", "MonoPropellant" => "Monopropellant", _ => name };
    private static string CargoLabel(ResourceAmount[] resources) => resources.Length == 0 ? "no cargo" :
        string.Join(", ", resources.Select(x => ResourceLabel(x.ResourceName) + " " + FormatUnits(x.AmountMicroUnits / 1000000d)));
    private void UpdateDeliveryCountdowns()
    {
        var sample = _lastSample;
        bool liveClock = _lastViewStatus is "live" or "paused" && sample?.ActiveWorld == true && sample.UtSeconds.HasValue;
        foreach (var delivery in _deliveries)
        {
            if (delivery.LegacyOpaque)
            {
                delivery.ArrivalDate = "Unavailable";
                delivery.Countdown = "—";
                delivery.Status = "Old delivery format";
                continue;
            }
            if (!liveClock || !double.IsFinite(delivery.DueUt))
            {
                delivery.ArrivalDate = _lastViewStatus == "paused" ? "KSP clock paused" : "Waiting for KSP";
                delivery.Countdown = _lastViewStatus == "paused" ? "Paused" : "—";
                delivery.Status = string.IsNullOrWhiteSpace(delivery.HeldReason) ? "Last known: in transit" : "Held: " + delivery.HeldReason;
                continue;
            }
            var remaining = delivery.DueUt - sample!.UtSeconds!.Value;
            delivery.ArrivalDate = FormatArrivalDate(sample.FormattedDate, remaining);
            delivery.Countdown = _lastViewStatus == "paused" ? "Paused · " + (remaining <= 0 ? "Due now" : FormatCountdown(remaining)) : remaining <= 0 ? "Due now" : FormatCountdown(remaining);
            delivery.Status = !string.IsNullOrWhiteSpace(delivery.HeldReason) ? "Held: " + delivery.HeldReason : _lastViewStatus == "paused" ? "In transit · clock paused" : remaining <= 0 ? "Arrival pending" : "In transit";
        }
    }
    private static string FormatCountdown(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) return "—";
        var whole = (long)Math.Ceiling(seconds);
        long days = whole / 21600;
        long hours = (whole % 21600) / 3600;
        long minutes = (whole % 3600) / 60;
        long secs = whole % 60;
        return $"{days}d {hours:00}h {minutes:00}m {secs:00}s";
    }
    private static string FormatArrivalDate(string? currentDate, double secondsRemaining)
    {
        if (string.IsNullOrWhiteSpace(currentDate) || !double.IsFinite(secondsRemaining)) return "KSP date unavailable";
        // Derive the due date from KSP's displayed date, preserving its initial UT offset.
        // The Expanse save uses the standard Kerbin calendar: six-hour days, 426 days/year.
        var match = Regex.Match(currentDate, @"Year\s+(\d+),\s*Day\s+(\d+).*?(\d+)h[, :]+(\d+)m(?:[, :]+(\d+)s)?", RegexOptions.IgnoreCase);
        if (!match.Success || !long.TryParse(match.Groups[1].Value, out var year) || !long.TryParse(match.Groups[2].Value, out var day)
            || !long.TryParse(match.Groups[3].Value, out var hour) || !long.TryParse(match.Groups[4].Value, out var minute)) return "KSP date unavailable";
        long second = match.Groups[5].Success && long.TryParse(match.Groups[5].Value, out var parsed) ? parsed : 0;
        if (year < 1 || day is < 1 or > 426 || hour is < 0 or >= 6 || minute is < 0 or >= 60 || second is < 0 or >= 60) return "KSP date unavailable";
        double daySeconds = (day - 1) * 21600d + hour * 3600d + minute * 60d + second + secondsRemaining;
        if (daySeconds < 0 || daySeconds > long.MaxValue / 2d) return "KSP date unavailable";
        long whole = (long)Math.Floor(daySeconds);
        year += whole / (426L * 21600L);
        whole %= 426L * 21600L;
        day = whole / 21600L + 1;
        hour = (whole % 21600L) / 3600L;
        minute = (whole % 3600L) / 60L;
        return $"Year {year}, Day {day} · {hour:00}:{minute:00}";
    }
    private static bool TryResourceUnits(string text, out long value)
    {
        value = 0;
        if (!decimal.TryParse(text, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite, CultureInfo.InvariantCulture, out var units) || units < 0 || units > long.MaxValue / 1000000m) return false;
        var scaled = units * 1000000m;
        if (scaled != decimal.Truncate(scaled)) return false;
        value = (long)scaled;
        return true;
    }

    // KSP and the Host retain the full value; this is only the readable stock display.
    private static string FormatUnits(double value) => value.ToString("N0", CultureInfo.CurrentCulture);
    private static string Abbreviate(Guid value) => value.ToString("N")[..8];
    private sealed class RouteResourceEntry : INotifyPropertyChanged
    {
        private bool _selected;
        public string Name { get; }
        public string DisplayName => Name switch { "LiquidFuel" => "Liquid Fuel", "MonoPropellant" => "Monopropellant", _ => Name };
        public string AmountText { get; set; } = "1";
        public bool Selected
        {
            get => _selected;
            set { if (_selected == value) return; _selected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected))); }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
        public RouteResourceEntry(string name, bool selected) { Name = name; _selected = selected; }
    }
    private sealed class RuleResourceEntry : INotifyPropertyChanged
    {
        private bool _selected = true;
        private bool _keepFull;
        private string _lowText, _targetText;
        private string? _manualLow, _manualTarget;
        public string Name { get; }
        public string DisplayName => ResourceLabel(Name);
        public string LowText { get => _lowText; set { if (_lowText == value) return; _lowText = value; Changed(nameof(LowText)); } }
        public string TargetText { get => _targetText; set { if (_targetText == value) return; _targetText = value; Changed(nameof(TargetText)); } }
        public long BatchMicroUnits { get; }
        public long CapacityMicroUnits { get; }
        public bool CanKeepFull => CapacityMicroUnits > 0;
        public bool CanEditThresholds => Selected && !KeepFull;
        public string BatchText => FormatUnits(BatchMicroUnits / 1000000d) + " units";
        public bool Selected
        {
            get => _selected;
            set { if (_selected == value) return; _selected = value; Changed(nameof(Selected)); Changed(nameof(CanEditThresholds)); }
        }
        public bool KeepFull
        {
            get => _keepFull;
            set
            {
                if (_keepFull == value || value && !CanKeepFull) return;
                if (value)
                {
                    _manualLow = LowText; _manualTarget = TargetText;
                    string full = (CapacityMicroUnits / 1000000m).ToString("0.######", CultureInfo.InvariantCulture);
                    LowText = full; TargetText = full;
                }
                else { LowText = _manualLow ?? string.Empty; TargetText = _manualTarget ?? string.Empty; }
                _keepFull = value; Changed(nameof(KeepFull)); Changed(nameof(CanEditThresholds));
            }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
        private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        public RuleResourceEntry(string name, long batchMicroUnits, double capacity, string low, string target)
        {
            Name = name; BatchMicroUnits = batchMicroUnits; _lowText = low; _targetText = target;
            double micro = Math.Floor(capacity * 1000000d);
            CapacityMicroUnits = double.IsFinite(micro) && micro > 0 && micro <= long.MaxValue ? (long)micro : 0;
        }
    }
    private sealed record DepotStockDisplay(string Label, string DepotId, string LiquidFuelText, string OxidizerText, string MonopropellantText, string OreText, string StatusText, string ReasonText);
    private sealed record SourceChoice(string Label, DepotSummaryView? Depot);
    private sealed record RouteChoice(string Display, RouteVersionRecord Route);
    private sealed record SavedRouteDisplay(string Identity, string Detail, string Status);
    private sealed record AutomaticOrderDisplay(string RuleId, string Identity, string Detail, string Status, string NextLaunch, string EstimatedArrival, bool Enabled, bool LegacyOpaque)
    {
        public string ToggleLabel => Enabled ? "Pause" : "Resume";
    }
    private sealed record IssueDisplay(string Identity, string Detail, string Status);
    private sealed class DeliveryDisplay : INotifyPropertyChanged
    {
        private string _arrivalDate = "—", _countdown = "—", _status = "—";
        public string ShipmentId { get; }
        public string RouteName { get; }
        public string Destination { get; }
        public string Cargo { get; }
        public double DueUt { get; }
        public string HeldReason { get; }
        public bool LegacyOpaque { get; }
        public string ArrivalDate { get => _arrivalDate; set => Set(ref _arrivalDate, value, nameof(ArrivalDate)); }
        public string Countdown { get => _countdown; set => Set(ref _countdown, value, nameof(Countdown)); }
        public string Status { get => _status; set => Set(ref _status, value, nameof(Status)); }
        public event PropertyChangedEventHandler? PropertyChanged;
        private void Set(ref string field, string value, string name) { if (field == value) return; field = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)); }
        public bool SameShipment(DeliveryDisplay other) => ShipmentId == other.ShipmentId && RouteName == other.RouteName && Destination == other.Destination
            && Cargo == other.Cargo && DueUt == other.DueUt && HeldReason == other.HeldReason && LegacyOpaque == other.LegacyOpaque;
        public DeliveryDisplay(string shipmentId, string routeName, string destination, string cargo, double dueUt, string heldReason, bool legacyOpaque)
        { ShipmentId = shipmentId; RouteName = routeName; Destination = destination; Cargo = cargo; DueUt = dueUt; HeldReason = heldReason; LegacyOpaque = legacyOpaque; }
    }
}


