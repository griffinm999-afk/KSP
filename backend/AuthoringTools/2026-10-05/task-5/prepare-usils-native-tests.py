from pathlib import Path
p=Path('astra-missing-callbacks/Program.cs').read_text()
p=p.replace('var brp=BrpProbe.Run();','''var usiResults=new List<object>();
        foreach(double supplyAvailable in new[]{0.0,0.00125,1000.0})
        foreach(double mulchStorage in new[]{0.0,0.00125,1000.0})
        {
            var recipe=new ConversionRecipe();recipe.SetInputs(new(){new("Supplies",(double)(.0005f*5*.5f),true){FlowMode=ResourceFlowMode.ALL_VESSEL}});recipe.SetOutputs(new(){new("Mulch",(double)(.0005f*5*.5f),true){FlowMode=ResourceFlowMode.ALL_VESSEL}});recipe.SetRequirements(new());recipe.FillAmount=1;recipe.TakeAmount=1;
            var broker=new SyntheticBroker(1){Available=supplyAvailable,Storage=mulchStorage};var frame=Ledger.Enter("detached",broker,PartIdentity,"USI-supply",103,2,true);EventRequested=0;
            var native=process(new ResourceConverter(broker),2,recipe,null,null,1);if(!Ledger.Complete(frame,true,native.TimeFactor))throw new Exception("USI completion failed");
            double expectedInput=Math.Min(recipe.Inputs[0].Ratio*2,supplyAvailable);double expectedOutput=Math.Min(expectedInput,mulchStorage);
            double input=frame.Inputs.GetValueOrDefault("Supplies"),output=frame.Outputs.GetValueOrDefault("Mulch");
            if(Math.Abs(input-expectedInput)>1e-12||Math.Abs(output-expectedOutput)>1e-12)throw new Exception("USI shortage/store mismatch");
            usiResults.Add(new{channel="Supplies/Mulch",supplyAvailable,mulchStorage,seconds=2,acceptedInput=input,acceptedOutput=output,timeFactor=native.TimeFactor,pass=true});
        }
        foreach(double available in new[]{0.0,0.025,1000.0})
        {
            var recipe=new ConversionRecipe();recipe.SetInputs(new(){new("ElectricCharge",(double)(.01f*5),true){FlowMode=ResourceFlowMode.ALL_VESSEL}});recipe.SetOutputs(new());recipe.SetRequirements(new());recipe.FillAmount=1;recipe.TakeAmount=1;
            var broker=new SyntheticBroker(1){Available=available};var frame=Ledger.Enter("detached",broker,PartIdentity,"USI-EC",104,2,true);
            var native=process(new ResourceConverter(broker),2,recipe,null,null,1);if(!Ledger.Complete(frame,true,native.TimeFactor))throw new Exception("USI EC completion failed");
            double accepted=frame.Inputs.GetValueOrDefault("ElectricCharge");if(Math.Abs(accepted-Math.Min(recipe.Inputs[0].Ratio*2,available))>1e-12)throw new Exception("USI EC shortage mismatch");
            usiResults.Add(new{channel="crew EC",available,seconds=2,accepted,timeFactor=native.TimeFactor,pass=true});
        }
        int exactQualificationCases=Qualification.Run();''')
p=p.replace('results,storageResults,brp}', 'results,storageResults,usiResults,exactQualificationCases}')
p=p.replace('public double Storage=1000;', 'public double Storage=1000,Available=1000;')
p=p.replace('public double AmountAvailable(Part p,string r,double s,ResourceFlowMode f)=>1000;', 'public double AmountAvailable(Part p,string r,double s,ResourceFlowMode f)=>Available;')
p=p.replace('double returned=-amount*fraction;', 'double returned=-Math.Min(amount*fraction,Storage);')
Path('usils-work/dev/USILifeSupport.Detached/Program.cs').write_text(p)
