using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Expanse.Domain.Colonies;

namespace Expanse.Clock.Manager;

public partial class MainWindow
{
    private static ColonyManagementPresentation PresentPhysicalStock(ColonyManagementPresentation view,ColonyManagementSnapshot snapshot,IEnumerable<ColonyManagementAction> actions)=>ColonyPhysicalStockPresentation.Present(view,snapshot,actions);
    private static ColonyPhysicalTransferQuote ReviewPhysicalTransfer(ColonyManagementSnapshot snapshot,ColonyManagementRequest request,Dictionary<string,string> fields)
    {
        if(!fields.TryGetValue("LocalStockId",out var tank) || !fields.TryGetValue("Direction",out var direction) || !fields.TryGetValue("TransferAmount",out var raw) || !TryResourceAmount(raw,out var amount) || amount<=0)throw new InvalidOperationException("Choose a current physical tank, direction and positive resource amount with at most six decimal places.");
        var quote=ColonyEngine.QuotePhysicalTransfer(snapshot.State!,request.ColonyId ?? "",tank,direction,amount,PlanningEnvironment(snapshot));
        if(!quote.CanApprove)throw new InvalidOperationException(quote.Reason);
        fields.Clear();fields["LocalStockId"]=tank;fields["Direction"]=direction;fields["AmountMicroUnits"]=amount.ToString(CultureInfo.InvariantCulture);return quote;
    }
    private static bool TryResourceAmount(string raw,out long amount)
    {
        amount=0;if(!decimal.TryParse(raw,NumberStyles.Number,CultureInfo.InvariantCulture,out var units) || units<0 || units>long.MaxValue/(decimal)ColonyLimits.Units)return false;
        decimal exact=units*ColonyLimits.Units;if(exact!=decimal.Truncate(exact))return false;amount=(long)exact;return true;
    }
}
