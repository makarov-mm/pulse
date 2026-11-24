using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Pulse.Localization;
using Pulse.Models;
using Pulse.Telemetry;

namespace Pulse;

// The tabs of the lower pane and the Services tab.
public sealed partial class MainWindow
{
    private readonly ObservableCollection<ServiceRow> _serviceRows = [];
    private List<ServiceInfo> _services = [];
    private bool _servicesTab;

    private void TabChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        // The first selection is raised while the window is still being built.
        if (ServicesView is null || Inspector is null || TreeButton is null) return;
        _servicesTab = sender.SelectedItem is { } selected && sender.Items.IndexOf(selected) == 1;
        Inspector.Visibility = _servicesTab ? Visibility.Collapsed : Visibility.Visible;
        ServicesView.Visibility = _servicesTab ? Visibility.Visible : Visibility.Collapsed;
        TreeButton.Visibility = _servicesTab ? Visibility.Collapsed : Visibility.Visible;
        SearchBox.PlaceholderText = UiText.Get(_servicesTab ? "SearchServices" : "Search");
        RefreshServices();
        UpdateRows();
    }

    // Services are only queried while something displays them.
    private void RefreshServices()
    {
        if (!_servicesTab && !Inspector.IsPaneOpen) return;
        try { _services = ServiceSnapshot.Read(); }
        catch (Win32Exception) { _services = []; }
        UpdateServiceRows();
    }

    private void UpdateServiceRows()
    {
        if (!_servicesTab) return;
        string search = SearchBox.Text.Trim();
        var matches = _services
            .Where(s => search.Length == 0 ||
                s.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                s.DisplayName.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
                (s.Pid != 0 && s.Pid.ToString().Contains(search, StringComparison.Ordinal)))
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        for (int i = 0; i < matches.Count; i++)
        {
            if (i == _serviceRows.Count) _serviceRows.Add(new ServiceRow());
            var service = matches[i];
            _serviceRows[i].Update(service, _byPid.TryGetValue(service.Pid, out var host) ? host.Info.Name : "");
        }
        while (_serviceRows.Count > matches.Count) _serviceRows.RemoveAt(_serviceRows.Count - 1);
        CountText.Text = UiText.Format("ServiceCount", _services.Count(s => s.State == ServiceState.Running), _services.Count);
    }

    // Shows the process that hosts the service, with its parents, on the Processes tab.
    private void ServiceDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (ServiceList.SelectedItem is not ServiceRow { ProcessId: not 0 } row) return;
        SearchBox.Text = row.ProcessId.ToString();
        Tabs.SelectedItem = ProcessesTab;
    }

    private string HostedServices(int pid)
    {
        string names = string.Join(", ", _services.Where(s => s.Pid == pid).Select(s => s.Name));
        return names.Length == 0 ? "—" : names;
    }
}
