using System.ComponentModel;

namespace Pulse.Models;

public abstract class ObservableRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private protected void Set<T>(ref T field, T value, string name)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
