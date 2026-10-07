using Expanse.Domain.Colonies;
namespace Expanse.Clock.Tests;
public sealed class ColonyPassengerPolicyTests
{
    static Dictionary<string,string> Terms()=>new(){{"id","paid-test"},{"name","Paid modeled passenger service"},{"body","Minmus"},{"fare","5000"},{"recruitmentFee","10000"},{"travelSeconds","21600"},{"concurrentSeats","4"},{"qualified","true"},{"evidence","Explicit modeled terms; physical roster and cabin readback required."},{"developmentOnly","true"}};
    [Fact] public void DevelopmentTermsCannotBecomeOperationalAndEconomicTermsBindQuote()
    {
        var terms=Terms();var route=ColonyPassengerRoutePolicy.Parse(terms);Assert.False(ColonyPassengerRoutePolicy.Available(route,false));Assert.True(ColonyPassengerRoutePolicy.Available(route,true));
        foreach(var pair in new[]{("fare","5001"),("recruitmentFee","10001"),("travelSeconds","21601"),("concurrentSeats","3"),("body","Mun"),("qualified","false"),("developmentOnly","false")})
        {var changed=Terms();changed[pair.Item1]=pair.Item2;Assert.NotEqual(route.Hash,ColonyPassengerRoutePolicy.Parse(changed).Hash);}
        terms["qualified"]="false";Assert.False(ColonyPassengerRoutePolicy.Available(ColonyPassengerRoutePolicy.Parse(terms),true));
    }
    [Fact] public void MissingFreeNonfiniteOrUnboundedServiceTermsAreRejected()
    {
        foreach(var pair in new[]{("fare","0"),("fare","-1"),("travelSeconds","NaN"),("travelSeconds","0"),("concurrentSeats","257"),("concurrentSeats","0"),("evidence","")})
        {var changed=Terms();changed[pair.Item1]=pair.Item2;Assert.ThrowsAny<Exception>(()=>ColonyPassengerRoutePolicy.Parse(changed));}
        foreach(var key in ColonyPassengerRoutePolicy.RequiredFields){var missing=Terms();missing.Remove(key);Assert.ThrowsAny<Exception>(()=>ColonyPassengerRoutePolicy.Parse(missing));}
    }
}
