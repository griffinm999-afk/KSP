using System;

namespace Expanse.Domain.Colonies
{
    public static partial class ColonyStateCodec
    {
        // The adapter snapshots plain observations on its owner thread. Decode
        // on the pure worker; no observer/template/config object is shared.
        public static byte[] SerializeTickEnvironment(ColonyEnvironment environment)
        {
            if (environment == null) throw new ArgumentNullException(nameof(environment));
            return ColonyJson.Serialize(environment, ColonyLimits.MaxBytes);
        }

        public static ColonyEnvironment DeserializeTickEnvironment(byte[] bytes)
            => ColonyJson.Deserialize<ColonyEnvironment>(bytes, ColonyLimits.MaxBytes);
    }
}
