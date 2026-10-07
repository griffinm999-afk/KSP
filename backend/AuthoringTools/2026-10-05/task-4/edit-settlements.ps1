$root=Join-Path $PSScriptRoot 'settlement-work'
function Replace-Exact($rel,$old,$new) {
 $path=Join-Path $root $rel; $text=[IO.File]::ReadAllText($path)
 if(-not $text.Contains($old)){throw "Anchor missing: $rel $old"}
 [IO.File]::WriteAllText($path,$text.Replace($old,$new),[Text.UTF8Encoding]::new($false))
}
Replace-Exact 'src/Expanse.WorldBridge/ColonyRuntime.cs' 'base.OnLoad(node);' "base.OnLoad(node);`r`n            LoadSettlements(node);"
Replace-Exact 'src/Expanse.WorldBridge/ColonyRuntime.cs' 'base.OnSave(node);' "base.OnSave(node);`r`n            SaveSettlements(node);"
Replace-Exact 'src/Expanse.WorldBridge/ColonyRuntime.cs' 'if (!Ready) return;' "if (!Ready) return;`r`n            try { EnsureSettlements(); FlushSettlements(); } catch (Exception ex) { settlementError = Bound(ex.Message, 512); }"
Replace-Exact 'src/Expanse.WorldBridge/ColonyRuntime.cs' 'byte[] completeBytes = ColonyStateCodec.Serialize(complete); string completeHash = ColonyStateCodec.Hash(completeBytes);' "byte[] completeBytes = ColonyStateCodec.Serialize(complete); string completeHash = ColonyStateCodec.Hash(completeBytes);`r`n            var preparedSettlement = PrepareColonySettlement(complete, pending.Id);"
Replace-Exact 'src/Expanse.WorldBridge/ColonyRuntime.cs' 'state = complete; acceptedBytes = completeBytes; acceptedHash = completeHash;' "state = complete; acceptedBytes = completeBytes; acceptedHash = completeHash;`r`n                CommitSettlement(preparedSettlement);"
Replace-Exact 'src/Expanse.WorldBridge/RecoveryCapsuleModule.cs' 'internal string World;' "internal string World;`r`n            internal ColonyRuntime.PreparedSettlement Settlement;"
Replace-Exact 'src/Expanse.WorldBridge/RecoveryCapsuleModule.cs' 'Hash = AcceptedStateCodec.ComputeHash(accepted), World = accepted.WorldId };' 'Hash = AcceptedStateCodec.ComputeHash(accepted), World = accepted.WorldId, Settlement = acceptedState == null || ColonyRuntime.Current == null ? null : ColonyRuntime.Current.PrepareRecoverySettlement(acceptedState, accepted) };'
Replace-Exact 'src/Expanse.WorldBridge/RecoveryCapsuleModule.cs' 'acceptedStateHash = prepared.Hash;' "acceptedStateHash = prepared.Hash;`r`n            ColonyRuntime.CommitSettlement(prepared.Settlement);"
Replace-Exact 'src/Expanse.Domain/Colonies/ColonyManagementWire.cs' 'public ColonyCommand? Command { get; set; }' "public ColonyCommand? Command { get; set; }`r`n        public SettlementReadRequest? Settlements { get; set; }"
Replace-Exact 'src/Expanse.Domain/Colonies/ColonyManagementWire.cs' 'public ColonyResult? Result { get; set; }' "public ColonyResult? Result { get; set; }`r`n        public SettlementPage? Settlements { get; set; }"
Replace-Exact 'src/Expanse.Domain/Colonies/ColonyManagementWire.cs' 'else if (request.Kind == "submit")' "else if (request.Kind == `"settlements`") { if(request.Command!=null || request.Settlements==null)throw new InvalidDataException(`"Settlement reads require a page request and no command.`"); SettlementJournal.Validate(request.Settlements); }`r`n            else if (request.Kind == `"submit`")"
Replace-Exact 'src/Expanse.WorldBridge/ColonyManagementEndpoint.cs' 'private readonly Func<ColonyCommand, ColonyResult> submit;' "private readonly Func<ColonyCommand, ColonyResult> submit;`r`n        private readonly Func<SettlementReadRequest, SettlementPage> settlements;"
Replace-Exact 'src/Expanse.WorldBridge/ColonyManagementEndpoint.cs' 'Func<ColonyCommand, ColonyResult> submit)' 'Func<ColonyCommand, ColonyResult> submit, Func<SettlementReadRequest,SettlementPage> settlements = null)'
Replace-Exact 'src/Expanse.WorldBridge/ColonyManagementEndpoint.cs' 'ColonyManagementWire.ValidatePipeName(pipeName);' "ColonyManagementWire.ValidatePipeName(pipeName);`r`n            this.settlements = settlements;"
Replace-Exact 'src/Expanse.WorldBridge/ColonyManagementEndpoint.cs' 'else' "else" # unchanged anchor sanity
Replace-Exact 'src/Expanse.WorldBridge/ColonyManagementEndpoint.cs' 'else' 'else'
$path=Join-Path $root 'src/Expanse.WorldBridge/ColonyManagementEndpoint.cs';$text=[IO.File]::ReadAllText($path)
$anchor='if (item.Request.Kind == "snapshot") response = new ColonyManagementWireResponse { RequestId = item.Request.RequestId, Outcome = "snapshot", Snapshot = capture() };'
$text=$text.Replace($anchor,$anchor+"`r`n                    else if(item.Request.Kind == `"settlements`") response = new ColonyManagementWireResponse { RequestId=item.Request.RequestId,Outcome=`"settlements`",Settlements=settlements==null ? new SettlementPage {Reason=`"Settlement endpoint unavailable.`"} : settlements(item.Request.Settlements) };")
[IO.File]::WriteAllText($path,$text,[Text.UTF8Encoding]::new($false))
Replace-Exact 'src/Expanse.WorldBridge/ColonyRuntime.Management.cs' 'This colony runtime was superseded by a new scene/load context."});' 'This colony runtime was superseded by a new scene/load context."}, CaptureSettlements);'
