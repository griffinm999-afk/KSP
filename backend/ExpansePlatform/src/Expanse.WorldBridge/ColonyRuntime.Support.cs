using System;
using System.Globalization;
using System.Linq;
using System.Text;
using Expanse.Domain.Colonies;

namespace Expanse.WorldBridge
{
    public sealed partial class ColonyRuntime
    {
        private string supportPolicyEpoch;
        private ColonySupportEnvironment installedSupportPolicy;
        private void PopulateSupportEnvironment(ColonyEnvironment env)
        {
            if (supportPolicyEpoch != loadEpoch)
            {
                supportPolicyEpoch = loadEpoch;
                var policy = new ColonySupportEnvironment(); installedSupportPolicy = policy;
                // Early refusal paths must publish the detected owner in this
                // very observation, before any consumption/admission decision.
                env.Support = policy;
                try
                {
                    var external = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName().Name)
                        .Where(n => new[] { "USILifeSupport", "TacLifeSupport", "Kerbalism", "Snacks" }.Contains(n, StringComparer.OrdinalIgnoreCase)).OrderBy(n => n).ToArray();
                    policy.OtherLifeSupportInstalled = external.Length > 0;
                    if (external.Length > 0)
                    { policy.Reason = "External life-support owner installed: " + string.Join(", ", external) + ". Expanse consumption is disabled until its integration is qualified."; return; }
                    var nodes = GameDatabase.Instance == null ? new ConfigNode[0] : GameDatabase.Instance.GetConfigNodes("EXPANSE_COLONY_SUPPORT");
                    if (nodes.Length != 1) { policy.Reason = "Exactly one installed resident-support policy is required."; return; }
                    var node = nodes[0]; policy.PolicyId = Required(node, "id");
                    policy.MicroUnitsPerPersonDay = long.Parse(Required(node, "microUnitsPerPersonDay"), CultureInfo.InvariantCulture);
                    ColonyStateCodec.Text(policy.PolicyId, 128, true); ColonyStateCodec.Quantity(policy.MicroUnitsPerPersonDay);
                    if (policy.MicroUnitsPerPersonDay <= 0) throw new InvalidOperationException("Resident support rate must be positive.");
                    policy.PolicyHash = ColonyStateCodec.Hash(Encoding.UTF8.GetBytes(policy.PolicyId + "\nSupplies\n" + policy.MicroUnitsPerPersonDay.ToString(CultureInfo.InvariantCulture) + "\n21600\nno-retroactive-debt\nno-deaths"));
                    policy.Ready = true;
                    policy.Reason = "Expanse resident support: configurable Supplies consumption for residents and visitors; starts only after funded commissioning. Shortages pause admissions and growth, never kill Kerbals.";
                }
                catch (Exception ex) { policy.Ready = false; policy.Reason = Bound(ex.Message, 360); }
            }
            env.Support = installedSupportPolicy;
        }
    }
}
