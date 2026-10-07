using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
namespace Expanse.Clock.Manager;
public partial class ColonyManagementView
{
    public static readonly DependencyProperty MetricColumnsProperty=DependencyProperty.Register(nameof(MetricColumns),typeof(int),typeof(ColonyManagementView),new PropertyMetadata(2));
    public static readonly DependencyProperty MetricValueSizeProperty=DependencyProperty.Register(nameof(MetricValueSize),typeof(double),typeof(ColonyManagementView),new PropertyMetadata(19d));
    public static readonly DependencyProperty MetricDetailVisibilityProperty=DependencyProperty.Register(nameof(MetricDetailVisibility),typeof(Visibility),typeof(ColonyManagementView),new PropertyMetadata(Visibility.Visible));
    public int MetricColumns=>(int)GetValue(MetricColumnsProperty);
    public double MetricValueSize=>(double)GetValue(MetricValueSizeProperty);
    public Visibility MetricDetailVisibility=>(Visibility)GetValue(MetricDetailVisibilityProperty);
    private readonly Dictionary<string,bool> recordsExpansion=new(StringComparer.Ordinal);
    private string? recordsLayoutKey;
    private bool settingRecordsExpansion;
    private bool HasLayoutControls=>RowsExpander is not null && Navigation is not null &&
        WorkflowPicker is not null && CharterFieldsGrid is not null;
    private void Layout_SizeChanged(object sender,SizeChangedEventArgs e)
    {
        if(!HasLayoutControls)return;
        bool compact=ActualHeight>0&&ActualHeight<700;
        SetValue(MetricColumnsProperty,compact&&ActualWidth>=850 ? 4 : 2);
        SetValue(MetricValueSizeProperty,compact ? 16d : 19d);
        SetValue(MetricDetailVisibilityProperty,compact ? Visibility.Collapsed : Visibility.Visible);
        if(CharterFieldsGrid is not null)CharterFieldsGrid.Columns=ActualWidth>=1050 ? 3 : 2;
        RefreshFormHeight();
    }
    private void ResizeRecords(double change)
    {
        if(RowsGrid is null || !RowsExpander.IsExpanded)return;
        double height=double.IsNaN(RowsGrid.Height) ? RowsGrid.ActualHeight : RowsGrid.Height;
        RowsGrid.MaxHeight=1200;
        RowsGrid.Height=Math.Clamp(height+change,180,1200);
    }
    private void RecordsResizeHandle_DragDelta(object sender,DragDeltaEventArgs e)=>ResizeRecords(e.VerticalChange);
    private void RecordsResizeHandle_KeyDown(object sender,KeyEventArgs e)
    {
        if(e.Key is not (Key.Up or Key.Down))return;
        ResizeRecords(e.Key==Key.Up ? -40 : 40);
        e.Handled=true;
    }
    private void RefreshFormHeight()
    {
        if(!HasLayoutControls)return;
        bool inputFocused=PageKey=="finance" || PageKey=="founding" &&
            (CurrentWorkflow is null || CurrentWorkflow.Charter || CurrentWorkflow.Fields.Length>0);
        string key=WorkflowSelectionKey+"\0"+CurrentWorkflow?.Key;
        if(recordsLayoutKey!=key)
        {
            recordsLayoutKey=key;settingRecordsExpansion=true;
            try {RowsExpander.IsExpanded=recordsExpansion.TryGetValue(key,out bool expanded) ? expanded : !inputFocused && CurrentWorkflow?.Key!="summary";}
            finally {settingRecordsExpansion=false;}
        }
    }
    private void Records_ExpansionChanged(object sender,RoutedEventArgs e)
    {
        if(!HasLayoutControls)return;
        if(!settingRecordsExpansion && ReferenceEquals(e.OriginalSource,RowsExpander) && recordsLayoutKey is not null)
        {
            recordsExpansion[recordsLayoutKey]=RowsExpander.IsExpanded;
            RefreshFormHeight();
        }
    }
}
