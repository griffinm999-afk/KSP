using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Expanse.Domain.Colonies
{
    public static class ColonyPassengerRoutePolicy
    {
        public static readonly string[] RequiredFields={"id","name","body","fare","recruitmentFee","travelSeconds","concurrentSeats","qualified","evidence","developmentOnly"};
        public static ColonyPassengerRoute Parse(IReadOnlyDictionary<string,string> values)
        {
            string Required(string key){if(!values.TryGetValue(key,out var value) || string.IsNullOrWhiteSpace(value))throw new InvalidDataException("Missing passenger route field "+key);return value;}
            var route=new ColonyPassengerRoute {Id=Required("id"),Name=Required("name"),Body=Required("body"),Fare=long.Parse(Required("fare"),CultureInfo.InvariantCulture),
                RecruitmentFee=long.Parse(Required("recruitmentFee"),CultureInfo.InvariantCulture),TravelSeconds=double.Parse(Required("travelSeconds"),CultureInfo.InvariantCulture),
                ConcurrentSeats=int.Parse(Required("concurrentSeats"),CultureInfo.InvariantCulture),Qualified=bool.Parse(Required("qualified")),Evidence=Required("evidence"),DevelopmentOnly=bool.Parse(Required("developmentOnly"))};
            ColonyStateCodec.Text(route.Id,128,true);ColonyStateCodec.Text(route.Name,160,true);ColonyStateCodec.Text(route.Body,128,true);ColonyStateCodec.Text(route.Evidence,512,true);
            ColonyStateCodec.Funds(route.Fare);ColonyStateCodec.Funds(route.RecruitmentFee);ColonyStateCodec.Range(route.TravelSeconds,1,1e9);ColonyStateCodec.Range(route.ConcurrentSeats,1,256);
            if(route.Fare==0)throw new InvalidDataException("Passenger route must declare a positive paid fare.");
            using(var stream=new MemoryStream())using(var writer=new BinaryWriter(stream,Encoding.UTF8,true))
            {writer.Write(route.Id);writer.Write(route.Body);writer.Write(route.Fare);writer.Write(route.RecruitmentFee);writer.Write(route.TravelSeconds);writer.Write(route.ConcurrentSeats);writer.Write(route.Qualified);writer.Write(route.Evidence);writer.Write(route.DevelopmentOnly);writer.Flush();route.Hash=ColonyStateCodec.Hash(stream.ToArray());}
            return route;
        }
        public static bool Available(ColonyPassengerRoute route,bool developmentAuthorized)=>route.Qualified && (!route.DevelopmentOnly || developmentAuthorized);
    }
}
