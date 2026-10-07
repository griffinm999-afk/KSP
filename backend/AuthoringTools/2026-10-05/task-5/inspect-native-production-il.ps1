$ErrorActionPreference='Stop'
$game='C:\Kerbal Space Program'
$dirs=@((Join-Path $game 'KSP_x64_Data\Managed'),(Join-Path $game 'GameData\000_USITools'),(Join-Path $game 'GameData\000_Harmony'))
$resolver=[ResolveEventHandler]{param($sender,$args) $name=[Reflection.AssemblyName]::new($args.Name).Name;foreach($dir in $dirs){$path=Join-Path $dir ($name+'.dll');if(Test-Path -LiteralPath $path){return [Reflection.Assembly]::LoadFrom($path)}}return $null}
[AppDomain]::CurrentDomain.add_AssemblyResolve($resolver)
Add-Type @'
using System;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
public static class NativeIL {
 static readonly Dictionary<short,OpCode> Codes=typeof(OpCodes).GetFields(BindingFlags.Public|BindingFlags.Static).Where(f=>f.FieldType==typeof(OpCode)).Select(f=>(OpCode)f.GetValue(null)).ToDictionary(c=>c.Value);
 public static string[] Read(MethodBase method) {
  var body=method.GetMethodBody();if(body==null)return new[]{"(no body)"};var b=body.GetILAsByteArray();var rows=new List<string>();int i=0;
  while(i<b.Length){int at=i;short n=b[i++];if(n==0xfe)n=(short)(0xfe00|b[i++]);var c=Codes[n];object value="";int size=0;
   switch(c.OperandType){
    case OperandType.InlineMethod:case OperandType.InlineField:case OperandType.InlineType:case OperandType.InlineTok:case OperandType.InlineSig:case OperandType.InlineString:
     int token=BitConverter.ToInt32(b,i);size=4;try {if(c.OperandType==OperandType.InlineString)value="<string literal>";else{var member=method.Module.ResolveMember(token,method.DeclaringType.GetGenericArguments(),method.IsGenericMethod?method.GetGenericArguments():null);value=member.DeclaringType?.FullName+"::"+member;}}catch{value="token:"+token.ToString("x8");}break;
    case OperandType.InlineBrTarget:size=4;value="IL_"+(i+4+BitConverter.ToInt32(b,i)).ToString("x4");break;
    case OperandType.ShortInlineBrTarget:size=1;value="IL_"+(i+1+(sbyte)b[i]).ToString("x4");break;
    case OperandType.InlineI:size=4;value=BitConverter.ToInt32(b,i);break;
    case OperandType.InlineI8:size=8;value=BitConverter.ToInt64(b,i);break;
    case OperandType.ShortInlineI:size=1;value=(sbyte)b[i];break;
    case OperandType.InlineR:size=8;value=BitConverter.ToDouble(b,i);break;
    case OperandType.ShortInlineR:size=4;value=BitConverter.ToSingle(b,i);break;
    case OperandType.InlineVar:size=2;value=BitConverter.ToUInt16(b,i);break;
    case OperandType.ShortInlineVar:size=1;value=b[i];break;
    case OperandType.InlineSwitch:int count=BitConverter.ToInt32(b,i);size=4+count*4;value="switch["+count+"]";break;
   }i+=size;rows.Add("IL_"+at.ToString("x4")+" "+c.Name+" "+value);
  }return rows.ToArray();
 }
}
'@
try {
 $stock=[Reflection.Assembly]::LoadFrom((Join-Path $dirs[0] 'Assembly-CSharp.dll'))
 $usi=[Reflection.Assembly]::LoadFrom((Join-Path $dirs[1] 'USITools.dll'))
 $flags=[Reflection.BindingFlags]'Public,NonPublic,Instance,Static,DeclaredOnly'
 $types=@($usi.GetType('USITools.USI_Harvester',$true),$usi.GetType('USITools.USI_Converter',$true),$stock.GetType('ModuleResourceHarvester',$true),$stock.GetType('ModuleResourceConverter',$true),$stock.GetType('BaseConverter',$true),$stock.GetType('ResourceConverter',$true))
 $result=foreach($type in $types){$methods=foreach($method in $type.GetMethods($flags)){[pscustomobject]@{name=$method.Name;signature=$method.ToString();virtual=$method.IsVirtual;baseDefinition=$method.GetBaseDefinition().DeclaringType.FullName;il=[NativeIL]::Read($method)}};[pscustomobject]@{type=$type.FullName;base=$type.BaseType.FullName;assemblyPath=$type.Assembly.Location;assemblySha256=(Get-FileHash -LiteralPath $type.Assembly.Location).Hash;methods=@($methods)}}
 $result|ConvertTo-Json -Depth 7|Set-Content -LiteralPath (Join-Path $PSScriptRoot 'installed-native-production-il.json')
 $summary=foreach($type in $result){[pscustomobject]@{type=$type.type;base=$type.base;methods=$type.methods.name;brokerCalls=@($type.methods|Where-Object {$_.il -match 'ProcessRecipe|RequestResource|StoreResource'}|Select-Object name,signature)}}
 $summary|ConvertTo-Json -Depth 5
}finally{[AppDomain]::CurrentDomain.remove_AssemblyResolve($resolver)}
