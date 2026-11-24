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

// Context menus and the entry editor that change startup entries and services.
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
        if (StartupList.SelectedItem is not StartupRow row) return;
        if (e.Key == VirtualKey.Application) ShowStartupMenu(row, null);
        else if (e.Key == VirtualKey.Enter && row.Item is { } item) _ = EditStartupAsync(item);
        else return;
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
        menu.Items.Add(MenuEntry("MenuEdit", true, () => _ = EditStartupAsync(item)));
        menu.Items.Add(MenuEntry("MenuDelete", allowed, () => _ = DeleteStartupAsync(item)));
        if (!item.InRegistry)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(MenuEntry("MenuShowFile", true, () => _ = RunActionAsync(() => StartupItems.ShowFile(item))));
        }
        if (item.MachineWide) AddElevationItem(menu);
        ShowMenu(menu, StartupList, position);
    }

    private async Task DeleteStartupAsync(StartupItem item)
    {
        if (await ConfirmAsync(UiText.Format("ConfirmDelete", item.Name), UiText.Format("ConfirmDeleteBody", item.Location),
            UiText.Get("DeleteButton")))
            await RunActionAsync(() => StartupItems.Delete(item));
    }

    // The entry editor. Registry entries take a new name and command here, so Registry Editor is never needed.
    // Without the rights to change an entry the editor is read-only and offers to restart elevated.
    private async Task EditStartupAsync(StartupItem item)
    {
        if (_dialogOpen) return;
        _dialogOpen = true;
        bool allowed = Elevation.IsElevated || !item.MachineWide, editable = allowed && item.InRegistry, save = false;
        var name = new TextBox { Header = UiText.Get("Name"), Text = item.Name, IsReadOnly = !editable };
        var command = new TextBox
        {
            Header = UiText.Get("StartupCommand"), Text = item.Command, TextWrapping = TextWrapping.Wrap, IsReadOnly = !editable
        };
        var enabled = new ToggleSwitch
        {
            Header = UiText.Get("ServiceStatus"), OnContent = UiText.Get("StartupEnabled"), OffContent = UiText.Get("StartupDisabled"),
            IsOn = item.Enabled, IsEnabled = allowed && item.CanToggle
        };
        var location = new TextBlock
        {
            Text = item.Location, FontSize = 12, Opacity = 0.78, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true
        };
        var panel = new StackPanel { Spacing = 12, MinWidth = 440, Children = { name, command, enabled, location } };
        if (!allowed) panel.Children.Add(new TextBlock { Text = UiText.Get("StartupNeedsAdmin"), TextWrapping = TextWrapping.Wrap });
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot, RequestedTheme = Root.ActualTheme, Language = Root.Language,
                Title = UiText.Format("EditStartupTitle", item.Name), Content = panel,
                PrimaryButtonText = UiText.Get(allowed ? "Save" : "ElevateButton"),
                CloseButtonText = UiText.Get(allowed ? "Cancel" : "Close"), DefaultButton = ContentDialogButton.Primary
            };
            // An entry needs both a name and a command.
            void Validate(object sender, TextChangedEventArgs e) =>
                dialog.IsPrimaryButtonEnabled = !allowed || (name.Text.Trim().Length != 0 && command.Text.Trim().Length != 0);
            name.TextChanged += Validate;
            command.TextChanged += Validate;
            save = await dialog.ShowAsync() == ContentDialogResult.Primary && !_closed;
        }
        finally { _dialogOpen = false; }
        if (!save) return;
        if (!allowed)
        {
            RestartElevated();
            return;
        }
        // Untouched text keeps the stored value exactly, including any surrounding spaces.
        string newName = !editable || name.Text == item.Name ? item.Entry : name.Text.Trim();
        string newCommand = !editable || command.Text == item.Command ? item.Command : command.Text.Trim();
        bool on = enabled.IsOn;
        if (newName == item.Entry && newCommand == item.Command && on == item.Enabled) return;
        await RunActionAsync(() => StartupItems.Save(item, newName, newCommand, on));
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
