using System.Collections.Generic;

namespace Expanse.WorldBridge
{
    // Config references are shared only for the duration of one native
    // environment snapshot. GameDatabase.GetConfigNodes performs a full walk
    // for each requested type, so collect these three exact types in one pass.
    internal sealed class ColonyEnvironmentConfigObservation
    {
        ConfigNode[] backgroundConverters;
        ConfigNode[] passengerRoutes;
        ConfigNode[] logisticsSettings;
        bool observed;

        internal ConfigNode[] BackgroundConverters
        {
            get { EnsureObserved(); return backgroundConverters; }
        }

        internal ConfigNode[] PassengerRoutes
        {
            get { EnsureObserved(); return passengerRoutes; }
        }

        internal ConfigNode[] LogisticsSettings
        {
            get { EnsureObserved(); return logisticsSettings; }
        }

        void EnsureObserved()
        {
            if (observed) return;

            var database = GameDatabase.Instance;
            if (database == null)
            {
                backgroundConverters = new ConfigNode[0];
                passengerRoutes = new ConfigNode[0];
                logisticsSettings = new ConfigNode[0];
                observed = true;
                return;
            }

            var background = new List<ConfigNode>();
            var passengers = new List<ConfigNode>();
            var logistics = new List<ConfigNode>();
            // Do not publish any bucket until the complete source traversal
            // succeeds. Preserve source order, duplicates, and node references.
            foreach (var entry in database.root.AllConfigs)
            {
                switch (entry.type)
                {
                    case "BACKGROUND_CONVERTER": background.Add(entry.config); break;
                    case "EXPANSE_COLONY_PASSENGER_ROUTE": passengers.Add(entry.config); break;
                    case "LOGISTICS_SETTINGS": logistics.Add(entry.config); break;
                }
            }

            backgroundConverters = background.ToArray();
            passengerRoutes = passengers.ToArray();
            logisticsSettings = logistics.ToArray();
            observed = true;
        }
    }
}
