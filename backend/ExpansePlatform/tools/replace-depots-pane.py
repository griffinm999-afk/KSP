from pathlib import Path
p=Path(__file__).resolve().parents[1] / 'src/Expanse.Clock.Manager/MainWindow.xaml'
s=p.read_text(encoding='utf-8-sig')
start=s.index('        <TabItem Header="Depots">')
end=s.index('        <TabItem Header="Delivery setup">',start)
replacement='''        <TabItem Header="Depots"><ScrollViewer VerticalScrollBarVisibility="Auto"><StackPanel>
          <Border Style="{StaticResource Card}"><StackPanel>
            <TextBlock Text="Depot stock" FontSize="23" FontWeight="SemiBold" Foreground="{StaticResource InkBrush}"/>
            <TextBlock Text="One row per registered depot. Each fuel column shows available units / capacity." FontSize="13" Foreground="{StaticResource MutedBrush}" TextWrapping="Wrap" Margin="0,7,0,18"/>
            <DataGrid x:Name="DepotsGrid" AutoGenerateColumns="False" CanUserAddRows="False" CanUserDeleteRows="False" CanUserReorderColumns="False" CanUserResizeRows="False" IsReadOnly="True" SelectionMode="Single" SelectionUnit="FullRow" GridLinesVisibility="Horizontal" HorizontalGridLinesBrush="#E7ECF1" Background="White" BorderBrush="#DCE9EC" RowHeaderWidth="0" MinHeight="100" MaxHeight="520" RowHeight="48" ColumnHeaderHeight="38" FontSize="14" AlternatingRowBackground="#F5F8FB">
              <DataGrid.Columns>
                <DataGridTextColumn Header="DEPOT" Binding="{Binding Label}" Width="2*"/>
                <DataGridTextColumn Header="LF" Binding="{Binding LiquidFuelText}" Width="1.2*"/>
                <DataGridTextColumn Header="OX" Binding="{Binding OxidizerText}" Width="1.2*"/>
                <DataGridTextColumn Header="MP" Binding="{Binding MonopropellantText}" Width="1.2*"/>
                <DataGridTextColumn Header="STOCK" Binding="{Binding StatusText}" Width="1.1*"/>
              </DataGrid.Columns>
            </DataGrid>
            <TextBlock x:Name="DepotListMessageText" Text="Waiting for KSP." Foreground="{StaticResource MutedBrush}" FontSize="13" TextWrapping="Wrap" Margin="0,14,0,0"/>
            <TextBlock Text="LF = Liquid Fuel   ·   OX = Oxidizer   ·   MP = Monopropellant" Foreground="{StaticResource MutedBrush}" FontSize="12" Margin="0,15,0,0"/>
            <TextBlock Text="Stocks update when a depot can be observed in KSP; an unavailable row does not mean its tanks are empty." Foreground="{StaticResource MutedBrush}" FontSize="12" TextWrapping="Wrap" Margin="0,7,0,0"/>
          </StackPanel></Border>
        </StackPanel></ScrollViewer></TabItem>
'''
s=s[:start]+replacement+s[end:]
s=s.replace('DisplayMemberPath="Label" ToolTip="Source depot"','DisplayMemberPath="Label" ToolTip="Supply source" SelectionChanged="SourceDepotBox_SelectionChanged"')
s=s.replace('Text="From depot"','Text="From"')
s=s.replace('Text="To depot"','Text="To depot"')
s=s.replace('            <UniformGrid Columns="3"><StackPanel Margin="0,0,14,16"><TextBlock Style="{StaticResource FieldLabel}" Text="Route name"',
'''            <TextBlock x:Name="SourceAvailabilityText" Text="Kerbin can be selected as a planned supplier. Purchased Kerbin shipments need the external-source scheduler before routes can be saved." Foreground="#855A16" FontSize="12" TextWrapping="Wrap" Margin="0,0,0,14"/>
            <UniformGrid Columns="3"><StackPanel Margin="0,0,14,16"><TextBlock Style="{StaticResource FieldLabel}" Text="Route name"''')
p.write_text(s,encoding='utf-8')
