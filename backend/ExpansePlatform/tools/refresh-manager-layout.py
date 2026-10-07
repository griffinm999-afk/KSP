from pathlib import Path
import re

root = Path(__file__).resolve().parents[1]
path = root / 'src/Expanse.Clock.Manager/MainWindow.xaml'
backup = root / 'artifacts/ui-before-refresh'
backup.mkdir(exist_ok=True)
saved = backup / 'MainWindow.xaml'
old = saved.read_text(encoding='utf-8-sig') if saved.exists() else path.read_text(encoding='utf-8-sig')
if not saved.exists(): saved.write_text(old, encoding='utf-8')

def control(name):
    m = re.search(r'<(\w+) x:Name="'+name+r'"[^>]*>', old, re.S)
    if not m:
        raise ValueError(name)
    text=m.group(0)
    if not text.endswith('/>'):
        end=old.index('</'+m.group(1)+'>',m.end())+len(m.group(1))+3
        text=old[m.start():end]
    text=re.sub(r' Grid.Column="\d+"| Margin="[^"]*"', '', text)
    if name in ('AmountBox','LowBox','BatchBox'):
        text=text.replace('Text="1000000"','Text="1"')
    if name=='TargetBox': text=text.replace('Text="5000000"','Text="5"')
    text=text.replace('Cargo in ledger micro-units','Cargo in resource units').replace('(micro-units)','(units)')
    return text

def field(name,label):
    return f'<StackPanel Margin="0,0,14,16"><TextBlock Style="{{StaticResource FieldLabel}}" Text="{label}"/>{control(name)}</StackPanel>'

def fields(items,columns=3):
    return '<UniformGrid Columns="'+str(columns)+'">'+''.join(field(*x) for x in items)+'</UniformGrid>'

stock = re.search(r'<DataGrid x:Name="ResourceGrid".*?</DataGrid>',old,re.S).group(0)
depots = re.search(r'<DataGrid x:Name="DepotsGrid".*?</DataGrid>',old,re.S).group(0)
depots=re.sub(r'\s*<DataGridTextColumn Header="(?:ID|REVISION)"[^>]*/>', '', depots)
depots=depots.replace('FontSize="12"','FontSize="14"').replace('RowHeight="30"','RowHeight="40"').replace('ColumnHeaderHeight="30"','ColumnHeaderHeight="38"')
ops = re.search(r'<DataGrid x:Name="OperationsGrid".*?</DataGrid>',old,re.S).group(0)
ops=ops.replace('FontSize="12"','FontSize="14"').replace('RowHeight="28"','RowHeight="40"').replace('MaxHeight="220"','MaxHeight="520"').replace('ColumnHeaderHeight="30"','ColumnHeaderHeight="38"')

xaml='''<Window x:Class="Expanse.Clock.Manager.MainWindow" xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Title="Expanse Foundations" Width="1180" Height="930" MinWidth="860" MinHeight="680" Background="#EDF3F5" WindowStartupLocation="CenterScreen" FontFamily="Segoe UI" UseLayoutRounding="True">
  <Grid>
    <Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition Height="*"/><RowDefinition Height="Auto"/></Grid.RowDefinitions>
    <Border Background="#163A4B" Padding="28,22">
      <Grid MaxWidth="1180"><Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
        <StackPanel><TextBlock Text="EXPANSE FOUNDATIONS" FontSize="23" FontWeight="SemiBold" Foreground="White"/><TextBlock Text="Your outposts. Your supply network." FontSize="13" Foreground="#A8CFD5" Margin="0,5,0,0"/></StackPanel>
        <Border Grid.Column="1" Background="#265263" CornerRadius="16" Padding="14,7" VerticalAlignment="Center"><TextBlock Text="COLONY OPERATIONS" Foreground="#C5E8E8" FontSize="11" FontWeight="SemiBold"/></Border>
      </Grid>
    </Border>
    <Grid Grid.Row="1" MaxWidth="1180" Margin="24,20,24,0">
      <Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition Height="*"/></Grid.RowDefinitions>
      <Border Style="{StaticResource Card}" Background="#E3F1F1" BorderBrush="#C6E0E1" Padding="22,17">
        <StackPanel><TextBlock Text="GAME TIME" Foreground="#426674" FontSize="11" FontWeight="SemiBold"/><TextBlock x:Name="ClockText" Text="Waiting for KSP" FontSize="29" FontWeight="SemiBold" Foreground="{StaticResource InkBrush}" Margin="0,4,0,0" TextTrimming="CharacterEllipsis"/><TextBlock x:Name="SaveText" Text="Open KSP and load your save to connect." Foreground="{StaticResource MutedBrush}" FontSize="13" Margin="0,5,0,0"/>
          <Expander Header="Connection details" FontSize="11" Foreground="{StaticResource MutedBrush}" Margin="0,10,0,0"><StackPanel Margin="20,6,0,0"><TextBlock x:Name="RawUtText" Text="UT —"/><TextBlock x:Name="IdentityText" TextWrapping="Wrap" Margin="0,4,0,0"/></StackPanel></Expander>
        </StackPanel>
      </Border>
      <TabControl Grid.Row="1" Background="Transparent" BorderThickness="0" Padding="0,16,0,0">
        <TabItem Header="Depots"><ScrollViewer VerticalScrollBarVisibility="Auto"><StackPanel>
          <Border Style="{StaticResource Card}"><StackPanel>
            <DockPanel LastChildFill="False"><TextBlock Text="CURRENT DEPOT" Style="{StaticResource FieldLabel}"/><Border DockPanel.Dock="Right" Background="#E8F4EE" CornerRadius="12" Padding="12,5"><TextBlock x:Name="DepotStatusText" Text="Waiting for KSP" FontSize="12" FontWeight="SemiBold" Foreground="{StaticResource MutedBrush}"/></Border></DockPanel>
            <TextBlock x:Name="DepotNameText" Text="No depot registered" FontSize="26" FontWeight="SemiBold" Foreground="{StaticResource InkBrush}" Margin="0,4,0,0"/>
            <TextBlock x:Name="DepotFactsText" Foreground="{StaticResource MutedBrush}" FontSize="13" TextWrapping="Wrap" Margin="0,7,0,0"/>
            <TextBlock x:Name="DepotReasonText" Foreground="{StaticResource MutedBrush}" FontSize="13" TextWrapping="Wrap" Margin="0,8,0,14"/>
            STOCK
            <TextBlock Text="Resource amounts rounded to whole units" Foreground="{StaticResource MutedBrush}" FontSize="11" Margin="0,10,0,0"/>
            <Expander Header="Technical details" FontSize="11" Foreground="{StaticResource MutedBrush}" Margin="0,12,0,0"><TextBlock x:Name="DepotDiagnosticText" TextWrapping="Wrap" Margin="20,6,0,0"/></Expander>
          </StackPanel></Border>
          <Border Style="{StaticResource Card}"><StackPanel><TextBlock Text="Your depot network" FontSize="20" FontWeight="SemiBold" Foreground="{StaticResource InkBrush}"/><TextBlock Text="All depots registered in this save. Select one to inspect its stock." FontSize="13" Foreground="{StaticResource MutedBrush}" Margin="0,6,0,16" TextWrapping="Wrap"/>
            DEPOTS
            <TextBlock x:Name="SelectedDepotDetailsText" Text="No depot selected." Foreground="{StaticResource MutedBrush}" FontSize="13" TextWrapping="Wrap" Margin="0,12,0,0"/>
          </StackPanel></Border>
        </StackPanel></ScrollViewer></TabItem>
        <TabItem Header="Delivery setup"><ScrollViewer VerticalScrollBarVisibility="Auto"><StackPanel>
          <Border Background="#FFF3DB" CornerRadius="9" Padding="16" Margin="0,0,0,16"><TextBlock Text="Preview · Fuel transfers are not enabled in the regular game yet. You can prepare routes and schedules here." Foreground="#855A16" FontSize="13" TextWrapping="Wrap"/></Border>
          <TextBlock x:Name="LogisticsStatusText" Text="Connect to a loaded game to edit delivery setup." Foreground="{StaticResource MutedBrush}" FontSize="13" Margin="0,0,0,12" TextWrapping="Wrap"/>
          <Border Style="{StaticResource Card}"><StackPanel><TextBlock Text="Create a route" FontSize="21" FontWeight="SemiBold" Foreground="{StaticResource InkBrush}" Margin="0,0,0,18"/>
            ROUTEFIELDS
            SOURCEFIELDS
            <WrapPanel>SAVEROUTE</WrapPanel>
          </StackPanel></Border>
          <Border Style="{StaticResource Card}"><StackPanel><TextBlock Text="Plan deliveries" FontSize="21" FontWeight="SemiBold" Foreground="{StaticResource InkBrush}" Margin="0,0,0,18"/>
            ROUTECHOICE
            <TextBlock Text="Choose a saved route, then send once or set up a repeating order." Foreground="{StaticResource MutedBrush}" FontSize="13" Margin="0,0,0,16"/>
            RULEFIELDS
            <TextBlock Text="Keep stock sends one batch when stock falls below the trigger. The target limits the amount delivered; a single batch may not fill the depot to that target." Foreground="{StaticResource MutedBrush}" FontSize="12" TextWrapping="Wrap" Margin="0,0,0,16"/>
            <WrapPanel>RULEBUTTONS</WrapPanel>
          </StackPanel></Border>
        </StackPanel></ScrollViewer></TabItem>
        <TabItem Header="Activity"><ScrollViewer VerticalScrollBarVisibility="Auto"><Border Style="{StaticResource Card}" VerticalAlignment="Top"><StackPanel><TextBlock Text="Routes, schedules &amp; shipments" FontSize="21" FontWeight="SemiBold" Foreground="{StaticResource InkBrush}" Margin="0,0,0,8"/><TextBlock Text="Saved plans and delivery progress appear here." FontSize="13" Foreground="{StaticResource MutedBrush}" Margin="0,0,0,18"/>OPS</StackPanel></Border></ScrollViewer></TabItem>
      </TabControl>
    </Grid>
    <Border Grid.Row="2" Background="White" BorderBrush="#D8E5E9" BorderThickness="0,1,0,0" Padding="26,12"><StackPanel><TextBlock x:Name="CommandStatusText" Text="No command submitted" FontSize="12" Foreground="{StaticResource MutedBrush}" Margin="0,0,0,5" TextWrapping="Wrap"/><DockPanel LastChildFill="False"><TextBlock x:Name="StatusText" Text="Connecting…" FontSize="13" Foreground="{StaticResource MutedBrush}"/><TextBlock x:Name="AgeText" DockPanel.Dock="Right" FontSize="12" Foreground="{StaticResource MutedBrush}"/></DockPanel></StackPanel></Border>
  </Grid>
</Window>'''

for key,value in {
'STOCK':stock,'DEPOTS':depots,'OPS':ops,
'ROUTEFIELDS':fields([('RouteIdBox','Route name'),('SourceDepotBox','From depot'),('DestinationDepotBox','To depot'),('ResourceBox','Resource name'),('AmountBox','Cargo amount (units)'),('DurationBox','Travel time (game seconds)')]),
'SOURCEFIELDS':field('ProvenanceBox','Travel time notes'),
'SAVEROUTE':control('SaveRouteButton'),
'ROUTECHOICE':field('RouteVersionBox','Saved route')+control('SendOnceButton').replace('Padding="18,6"','Padding="18,10" Margin="0,0,0,18"'),
'RULEFIELDS':fields([('RuleIdBox','Schedule name'),('RuleKindBox','Order type'),('IntervalBox','Repeat every (game seconds)'),('LowBox','Order below (units)'),('TargetBox','Stock target (units)'),('BatchBox','Maximum shipment (units)')]),
'RULEBUTTONS':control('SaveRuleButton')+control('PauseRuleButton').replace('Padding="12,4"','Padding="18,10" Margin="10,0,0,0"')
}.items(): xaml=xaml.replace(key,value)
path.write_text(xaml,encoding='utf-8')
