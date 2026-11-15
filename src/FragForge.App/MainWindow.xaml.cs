using System.ComponentModel;
using System.Windows;
using FragForge.UI.Core;

namespace FragForge.App;

public partial class MainWindow : Window
{
    private bool _closing;
    private bool _closeAfterMeasurement;
    public MeasurementViewModel Measurement { get; }

    public MainWindow(MeasurementViewModel measurement)
    {
        Measurement = measurement;
        InitializeComponent();
    }

    private async void CloseMeasurementTab_Click(object sender, RoutedEventArgs e)
    {
        await Measurement.FinishAsync();
        MainTabs.SelectedIndex = 0;
        MeasurementTab.Visibility = Visibility.Collapsed;
    }

    private void OpenMeasurementTab_Click(object sender, RoutedEventArgs e)
    {
        MeasurementTab.Visibility = Visibility.Visible;
        MainTabs.SelectedItem = MeasurementTab;
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_closeAfterMeasurement || Measurement.State == MeasurementState.Inactiva)
        {
            base.OnClosing(e);
            return;
        }
        e.Cancel = true;
        base.OnClosing(e);
        if (_closing) return;
        _closing = true;
        try { await Measurement.FinishAsync(); }
        finally
        {
            _closeAfterMeasurement = true;
            Close();
        }
    }
}
