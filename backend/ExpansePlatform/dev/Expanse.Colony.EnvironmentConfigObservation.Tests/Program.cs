using Expanse.WorldBridge;

var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    checks++;
    Console.WriteLine("PASS " + name);
}

GameDatabase.Instance = null;
var noDatabase = new ColonyEnvironmentConfigObservation();
Check(noDatabase.BackgroundConverters.Length == 0 && noDatabase.PassengerRoutes.Length == 0 && noDatabase.LogisticsSettings.Length == 0,
    "null database returns empty buckets");

var backgroundFirst = new ConfigNode("background-first");
var passengerFirst = new ConfigNode("passenger-first");
var logistics = new ConfigNode("logistics");
var backgroundSecond = new ConfigNode("background-second");
var passengerSecond = new ConfigNode("passenger-second");
var root = new UrlDir(new[] {
    new UrlDir.UrlConfig("BACKGROUND_CONVERTER", backgroundFirst),
    new UrlDir.UrlConfig("EXPANSE_COLONY_PASSENGER_ROUTE", passengerFirst),
    new UrlDir.UrlConfig("LOGISTICS_SETTINGS", logistics),
    new UrlDir.UrlConfig("background_converter", new ConfigNode("case-mismatch")),
    new UrlDir.UrlConfig("BACKGROUND_CONVERTER", backgroundSecond),
    new UrlDir.UrlConfig("EXPANSE_COLONY_PASSENGER_ROUTE", passengerSecond),
    new UrlDir.UrlConfig("BACKGROUND_CONVERTER", backgroundFirst)
});
GameDatabase.Instance = new GameDatabase(root);
var observation = new ColonyEnvironmentConfigObservation();
Check(root.Enumerations == 0, "observation construction is lazy");
var observedLogistics = observation.LogisticsSettings;
Check(root.Enumerations == 1, "first requested bucket performs one traversal");
var observedBackground = observation.BackgroundConverters;
var observedPassengers = observation.PassengerRoutes;
Check(root.Enumerations == 1, "all requested buckets share that traversal");
Check(observedBackground.Length == 3 && ReferenceEquals(observedBackground[0], backgroundFirst) &&
    ReferenceEquals(observedBackground[1], backgroundSecond) && ReferenceEquals(observedBackground[2], backgroundFirst),
    "background references preserve source order and duplicates");
Check(observedPassengers.Length == 2 && ReferenceEquals(observedPassengers[0], passengerFirst) &&
    ReferenceEquals(observedPassengers[1], passengerSecond), "passenger references preserve source order");
Check(observedLogistics.Length == 1 && ReferenceEquals(observedLogistics[0], logistics), "logistics reference is retained");

var newBackground = new ConfigNode("background-new");
root.Entries.Add(new UrlDir.UrlConfig("BACKGROUND_CONVERTER", newBackground));
root.Entries.Add(new UrlDir.UrlConfig("LOGISTICS_SETTINGS", new ConfigNode("logistics-new")));
var nextObservation = new ColonyEnvironmentConfigObservation();
Check(root.Enumerations == 1, "new observation remains lazy");
var nextBackgrounds = nextObservation.BackgroundConverters;
Check(root.Enumerations == 2 && nextBackgrounds.Length == 4 && ReferenceEquals(nextBackgrounds[3], newBackground),
    "separate observations see later source additions");
Check(nextObservation.LogisticsSettings.Length == 2 && root.Enumerations == 2,
    "later buckets in the new observation share its fresh traversal");

var failingRoot = new UrlDir(new[] {
    new UrlDir.UrlConfig("BACKGROUND_CONVERTER", backgroundFirst),
    new UrlDir.UrlConfig("LOGISTICS_SETTINGS", logistics),
    new UrlDir.UrlConfig("EXPANSE_COLONY_PASSENGER_ROUTE", passengerFirst)
}) { ThrowBeforeIndex = 2 };
GameDatabase.Instance = new GameDatabase(failingRoot);
var failingObservation = new ColonyEnvironmentConfigObservation();
bool threw = false;
try { _ = failingObservation.BackgroundConverters; }
catch (InvalidOperationException) { threw = true; }
Check(threw && failingRoot.Enumerations == 1, "traversal failure propagates");
failingRoot.ThrowBeforeIndex = -1;
Check(failingObservation.PassengerRoutes.Length == 1 && failingRoot.Enumerations == 2 &&
    failingObservation.BackgroundConverters.Length == 1 && failingObservation.LogisticsSettings.Length == 1,
    "failed traversal publishes no partial buckets and retries as a complete pass");

Console.WriteLine($"{checks} checks passed.");

public sealed class ConfigNode
{
    public string Name { get; }
    public ConfigNode(string name) => Name = name;
}

public sealed class GameDatabase
{
    public static GameDatabase Instance { get; set; }
    public UrlDir root { get; }
    public GameDatabase(UrlDir root) => this.root = root;
}

public sealed class UrlDir
{
    public sealed class UrlConfig
    {
        public string type { get; }
        public ConfigNode config { get; }
        public UrlConfig(string type, ConfigNode config) { this.type = type; this.config = config; }
    }

    public List<UrlConfig> Entries { get; }
    public int Enumerations { get; private set; }
    public int ThrowBeforeIndex { get; set; } = -1;
    public UrlDir(IEnumerable<UrlConfig> entries) => Entries = entries.ToList();

    public IEnumerable<UrlConfig> AllConfigs
    {
        get
        {
            Enumerations++;
            for (var i = 0; i < Entries.Count; i++)
            {
                if (i == ThrowBeforeIndex) throw new InvalidOperationException("injected traversal failure");
                yield return Entries[i];
            }
        }
    }
}
