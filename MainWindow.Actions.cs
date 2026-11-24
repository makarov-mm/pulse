using System.ComponentModel;
using System.Security;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Pulse.Localization;
using Pulse.Models;
using Pulse.Telemetry;
using Windows.Foundation;
using Windows.System;

namespace Pulse;

// Context menus that change startup entries and services.
public sealed partial class MainWindow
{
    private static MenuFlyoutItem MenuEntry(string key, bool enabled, Action action)
    {
        var item = new MenuFlyoutItem { Text = UiText.Get(key), IsEnabled = enabled };
        item.Click += (_, _) => action();
        return item;
    }

    // Opens the menu at the pointer, or at the list when it was requested from the keyboard.
    private static void ShowMenu(MenuFlyout menu, FrameworkElement list, Point? position)
    {
        if (position is { } point) menu.ShowAt(list, new FlyoutShowOptions { Position = point });
        else menu.ShowAt(list);
    }

    private void AddElevationItem(MenuFlyout menu)
    {
        if (Elevation.IsElevated) return;
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(MenuEntry("MenuElevate", true, RestartElevated));
    }

    private void RestartElevated()
    {
        try
        {
            Elevation.StartElevated();
            Close();
        }
        // The user declined the consent prompt.
        catch (Win32Exception) { }
    }

    // Runs a change, reports a failure and reloads the lists either way.
    private async Task RunActionAsync(Action action, bool background = false)
    {
        try
        {
            if (background) await Task.Run(action);
            else action();
        }
        catch (Exception error) when (error is Win32Exception or UnauthorizedAccessException or SecurityException
            or IOException or TimeoutException or InvalidOperationException)
        {
            if (!_closed) ShowError(UiText.Get("ActionError"), error is TimeoutException ? UiText.Get("ActionTimeout") : error.Message);
        }
        if (_closed) return;
        RefreshInspectorSources();
        UpdateTabRows();
    }

    private async Task<bool> ConfirmAsync(string title, string body, string action)
    {
        if (_dialogOpen) return false;
        _dialogOpen = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot, RequestedTheme = Root.ActualTheme, Language = Root.Language,
                Title = title, Content = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = action, CloseButtonText = UiText.Get("Cancel"), DefaultButton = ContentDialogButton.Close
            };
            return await dialog.ShowAsync() == ContentDialogResult.Primary && !_closed;
        }
        finally { _dialogOpen = false; }
    }

    private void StartupRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not StartupRow row) return;
        StartupList.SelectedItem = row;
        ShowStartupMenu(row, e.GetPosition(StartupList));
        e.Handled = true;
    }

    private void StartupKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Application || StartupList.SelectedItem is not StartupRow row) return;
        ShowStartupMenu(row, null);
        e.Handled = true;
    }

    private void ShowStartupMenu(StartupRow row, Point? position)
    {
        // Rows are reused, so the menu keeps the entry it was opened for.
        if (row.Item is not { } item) return;
        bool allowed = Elevation.IsElevated || !item.MachineWide;
        var menu = new MenuFlyout();
        menu.Items.Add(MenuEntry(item.Enabled ? "MenuDisable" : "MenuEnable", allowed && item.CanToggle,
            () => _ = RunActionAsync(() => StartupItems.SetEnabled(item, !item.Enabled))));
        menu.Items.Add(MenuEntry("MenuEditCommand", allowed && item.InRegistry, () => _ = EditStartupCommandAsync(item)));
        menu.Items.Add(MenuEntry("MenuDelete", allowed, () => _ = DeleteStartupAsync(item)));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(MenuEntry("MenuOpenLocation", true, () => _ = RunActionAsync(() => StartupItems.Open(item))));
        if (item.MachineWide) AddElevationItem(menu);
        ShowMenu(menu, StartupList, position);
    }

    private async Task DeleteStartupAsync(StartupItem item)
    {
        if (await ConfirmAsync(UiText.Format("ConfirmDelete", item.Name), UiText.Format("ConfirmDeleteBody", item.Location),
            UiText.Get("DeleteButton")))
            await RunActionAsync(() => StartupItems.Delete(item));
    }

    private async Task EditStartupCommandAsync(StartupItem item)
    {
        if (_dialogOpen) return;
        _dialogOpen = true;
        string? command = null;
        try
        {
            var box = new TextBox { Text = item.Command, TextWrapping = TextWrapping.Wrap, MinWidth = 420 };
            var dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot, RequestedTheme = Root.ActualTheme, Language = Root.Language,
                Title = UiText.Format("EditCommandTitle", item.Name), Content = box,
                PrimaryButtonText = UiText.Get("Save"), CloseButtonText = UiText.Get("Cancel"), DefaultButton = ContentDialogButton.Primary
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary && !_closed) command = box.Text.Trim();
        }
        finally { _dialogOpen = false; }
        if (command is not { Length: > 0 } || command == item.Command) return;
        string value = command;
        await RunActionAsync(() => StartupItems.SetCommand(item, value));
    }

    private void ServiceRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not ServiceRow row) return;
        ServiceList.SelectedItem = row;
        ShowServiceMenu(row, e.GetPosition(ServiceList));
        e.Handled = true;
    }

    private void ServiceKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Application || ServiceList.SelectedItem is not ServiceRow row) return;
        ShowServiceMenu(row, null);
        e.Handled = true;
    }

    private void ShowServiceMenu(ServiceRow row, Point? position)
    {
        if (row.Info is not { } service) return;
        bool elevated = Elevation.IsElevated;
        string name = service.Name;
        var menu = new MenuFlyout();
        menu.Items.Add(MenuEntry("MenuStart", elevated && service.State == ServiceState.Stopped,
            () => _ = RunActionAsync(() => ServiceControl.Start(name), true)));
        menu.Items.Add(MenuEntry("MenuStop", elevated && service.State == ServiceState.Running,
            () => _ = RunActionAsync(() => ServiceControl.Stop(name), true)));
        menu.Items.Add(MenuEntry("MenuRestart", elevated && service.State == ServiceState.Running,
            () => _ = RunActionAsync(() => ServiceControl.Restart(name), true)));
        menu.Items.Add(new MenuFlyoutSeparator());

        ServiceStart? current = null;
        try { current = ServiceControl.StartType(name); }
        catch (Win32Exception) { }
        var types = new MenuFlyoutSubItem { Text = UiText.Get("MenuStartType") };
        foreach (var (type, key) in new[] { (ServiceStart.Automatic, "StartAutomatic"), (ServiceStart.Manual, "StartManual"),
                     (ServiceStart.Disabled, "StartupDisabled") })
        {
            var option = new RadioMenuFlyoutItem
            {
                Text = UiText.Get(key), GroupName = "ServiceStart", IsChecked = current == type, IsEnabled = elevated
            };
            option.Click += (_, _) => _ = RunActionAsync(() => ServiceControl.SetStartType(name, type));
            types.Items.Add(option);
        }
        menu.Items.Add(types);
        if (service.Pid != 0)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(MenuEntry("MenuShowProcess", true, () => ShowProcess(service.Pid)));
        }
        AddElevationItem(menu);
        ShowMenu(menu, ServiceList, position);
    }
}
