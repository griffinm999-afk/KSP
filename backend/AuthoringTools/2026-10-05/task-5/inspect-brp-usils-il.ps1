$ErrorActionPreference='Stop'
$game='C:\Kerbal Space Program'
$dirs=@((Join-Path $game 'KSP_x64_Data\Managed'),(Join-Path $game 'GameData\000_USITools'),(Join-Path $game 'GameData\000_Harmony'),(Join-Path $game 'GameData\UmbraSpaceIndustries\LifeSupport'),(Join-Path $game 'GameData\UmbraSpaceIndustries\MKS'),(Join-Path $game 'GameData\BackgroundResourceProcessing\Plugins'))
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
 $path=Join-Path $game 'GameData\UmbraSpaceIndustries\LifeSupport\USILifeSupport.dll'
 foreach($dependency in @('UnityEngine.CoreModule.dll','UnityEngine.dll','Assembly-CSharp.dll')){[Reflection.Assembly]::LoadFrom((Join-Path $dirs[0] $dependency))|Out-Null}
 [Reflection.Assembly]::LoadFrom((Join-Path $game 'GameData\000_USITools\USITools.dll'))|Out-Null
 [Reflection.Assembly]::LoadFrom($path)|Out-Null; $path=Join-Path $game 'GameData\BackgroundResourceProcessing\Plugins\BackgroundResourceProcessing.Integration.USILifeSupport.dll.plugin'; [Reflection.Assembly]::LoadFrom((Join-Path $game 'GameData\000_Harmony\0Harmony.dll'))|Out-Null; $assembly=[Reflection.Assembly]::LoadFrom($path)
 $flags=[Reflection.BindingFlags]'Public,NonPublic,Instance,Static,DeclaredOnly'
 $result=foreach($type in $assembly.GetTypes()){
  $methods=foreach($method in $type.GetMethods($flags)){$il=[NativeIL]::Read($method);if($true){[pscustomobject]@{name=$method.Name;signature=$method.ToString();il=$il}}}
  if($true){[pscustomobject]@{type=$type.FullName;base=$type.BaseType.FullName;fields=@($type.GetFields($flags)|ForEach-Object {$_.FieldType.FullName+' '+$_.Name});properties=@($type.GetProperties($flags)|ForEach-Object {$_.PropertyType.FullName+' '+$_.Name});methods=@($methods)}}
 }
 $result|ConvertTo-Json -Depth 7|Set-Content (Join-Path $PSScriptRoot 'installed-brp-usils-il.json')
 [pscustomobject]@{assemblyVersion=$assembly.GetName().Version.ToString();fileVersion=[Diagnostics.FileVersionInfo]::GetVersionInfo($path).FileVersion;sha256=(Get-FileHash -LiteralPath $path).Hash;types=@($result.type)}|ConvertTo-Json -Depth 4
}finally{[AppDomain]::CurrentDomain.remove_AssemblyResolve($resolver)}


